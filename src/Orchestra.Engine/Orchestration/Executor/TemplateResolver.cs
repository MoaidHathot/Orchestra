using System.Text.RegularExpressions;

namespace Orchestra.Engine;

/// <summary>
/// Utility for resolving template expressions in strings.
/// Supports:
///   {{param.name}}         — parameter value
///   {{orchestration.name}} — orchestration metadata (name, version, runId, startedAt)
///   {{step.name}}          — current step metadata (name, type)
///   {{vars.name}}          — user-defined orchestration variable (supports recursive expansion)
///   {{env.VAR_NAME}}       — environment variable value
///   {{server.url}}         — Orchestra server base URL (set by host, falls back to ORCHESTRA_SERVER_URL env var)
///   {{stepName.output}}    — output content from a completed dependency step
///   {{stepName.rawOutput}} — raw output from a completed dependency step
///   {{stepName.files}}     — JSON array of file paths saved by a step via orchestra_save_file
///   {{stepName.files[N]}}  — Nth file path (0-based) saved by a step via orchestra_save_file
///
/// For steps of type Orchestration, the following additional accessors drill into the
/// child run's data (populated by <see cref="OrchestrationStepExecutor"/>):
///   {{stepName.executionId}}             — execution ID of the child run
///   {{stepName.status}}                  — lowercase status of the child run
///   {{stepName.errorMessage}}            — top-level error message from the child run
///   {{stepName.completionReason}}        — early-completion reason (orchestra_complete)
///   {{stepName.childResult}}             — full JSON of the child run (executionId, status,
///                                          errorMessage, finalContent, completionReason,
///                                          cancellation, stepResults)
///   {{stepName.steps}}                   — JSON map of all child-step results
///   {{stepName.steps.&lt;name&gt;.output}}     — content of one child step (untruncated)
///   {{stepName.steps.&lt;name&gt;.rawOutput}}  — pre-output-handler content of one child step
///   {{stepName.steps.&lt;name&gt;.error}}      — errorMessage of one child step
///   {{stepName.steps.&lt;name&gt;.status}}     — status of one child step
///   {{stepName.steps.&lt;name&gt;.files}}      — JSON array of one child step's saved files
///   {{stepName.steps.&lt;name&gt;.files[N]}}   — indexed access to one child step's saved files
/// </summary>
public static partial class TemplateResolver
{
	[GeneratedRegex(@"\{\{(?<expr>[^}]+)\}\}", RegexOptions.Compiled)]
	private static partial Regex TemplatePattern();

	[GeneratedRegex(@"^files\[(\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
	private static partial Regex FilesIndexPattern();

	private static readonly string[] s_validOrchestrationProperties = ["name", "version", "runid", "startedat", "tempdir", "sourcepath", "sourcedirectory"];
	private static readonly string[] s_validStepProperties = ["name", "type"];
	private static readonly string[] s_validServerProperties = ["url"];

	private static readonly System.Text.Json.JsonSerializerOptions s_childResultJsonOptions = new()
	{
		PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
	};

	/// <summary>
	/// Resolves all template expressions in the input string.
	/// </summary>
	public static string Resolve(
		string template,
		Dictionary<string, string> parameters,
		OrchestrationExecutionContext context,
		string[] dependsOn,
		OrchestrationStep currentStep)
	{
		return Resolve(template, parameters, context, dependsOn, currentStep, resolvingVars: null, tracker: context.ResolutionTracker);
	}

	/// <summary>
	/// Resolves only static/orchestration-level template expressions.
	/// Handles: {{param.*}}, {{env.*}}, {{vars.*}}, {{orchestration.*}}.
	/// Step-level expressions ({{step.*}}, {{stepName.output}}, etc.) are left as-is.
	/// Use this for contexts where step outputs are not available, such as MCP configurations.
	/// </summary>
	public static string ResolveStatic(
		string template,
		Dictionary<string, string> parameters,
		OrchestrationExecutionContext context)
	{
		return ResolveStatic(template, parameters, context, resolvingVars: null, tracker: context.ResolutionTracker);
	}

	/// <summary>
	/// Creates a copy of an MCP configuration with all string fields resolved using
	/// static/orchestration-level template expressions (param, env, vars, orchestration).
	/// The <see cref="Mcp.Name"/> and <see cref="Mcp.Type"/> are preserved as-is since
	/// they are identity/structural fields used for lookup and matching.
	/// </summary>
	public static Mcp ResolveStaticMcp(
		Mcp mcp,
		Dictionary<string, string> parameters,
		OrchestrationExecutionContext context)
	{
		return mcp switch
		{
			LocalMcp local => new LocalMcp
			{
				Name = local.Name,
				Type = local.Type,
				Command = ResolveStatic(local.Command, parameters, context),
				Arguments = local.Arguments
					.Select(arg => ResolveStatic(arg, parameters, context))
					.ToArray(),
				WorkingDirectory = local.WorkingDirectory is not null
					? ResolveStatic(local.WorkingDirectory, parameters, context)
					: null,
				Timeout = local.Timeout,
			},
			RemoteMcp remote => new RemoteMcp
			{
				Name = remote.Name,
				Type = remote.Type,
				Endpoint = ResolveStatic(remote.Endpoint, parameters, context),
				Headers = remote.Headers
					.ToDictionary(h => h.Key, h => ResolveStatic(h.Value, parameters, context)),
				Timeout = remote.Timeout,
			},
			_ => mcp, // Unknown subtype — return as-is
		};
	}

	/// <summary>
	/// Internal overload that tracks which variables are currently being resolved
	/// to detect and prevent circular references.
	/// </summary>
	private static string Resolve(
		string template,
		Dictionary<string, string> parameters,
		OrchestrationExecutionContext context,
		string[] dependsOn,
		OrchestrationStep currentStep,
		HashSet<string>? resolvingVars,
		TemplateResolutionTracker? tracker = null)
	{
		return TemplatePattern().Replace(template, match =>
		{
			var expr = match.Groups["expr"].Value.Trim();

			// {{param.name}} — parameter reference
			if (expr.StartsWith("param.", StringComparison.OrdinalIgnoreCase))
			{
				var paramName = expr["param.".Length..];
				return parameters.TryGetValue(paramName, out var value) ? value : match.Value;
			}

			// {{orchestration.property}} — orchestration metadata
			if (expr.StartsWith("orchestration.", StringComparison.OrdinalIgnoreCase))
			{
				var property = expr["orchestration.".Length..];
				return ResolveOrchestrationProperty(property, context.OrchestrationInfo, context);
			}

			// {{step.property}} — current step metadata
			if (expr.StartsWith("step.", StringComparison.OrdinalIgnoreCase))
			{
				var property = expr["step.".Length..];
				return ResolveStepProperty(property, currentStep);
			}

			// {{vars.name}} — user-defined variable with recursive expansion
			if (expr.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
			{
				var varName = expr["vars.".Length..];
				return ResolveVariable(varName, parameters, context, resolvingVars, match.Value, tracker);
			}

			// {{env.VAR_NAME}} — environment variable
			if (expr.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
			{
				var envVarName = expr["env.".Length..];
				var envValue = Environment.GetEnvironmentVariable(envVarName);
				tracker?.TrackEnvironmentVariable(envVarName, envValue);
				return envValue ?? match.Value;
			}

			// {{server.property}} — server metadata
			if (expr.StartsWith("server.", StringComparison.OrdinalIgnoreCase))
			{
				var property = expr["server.".Length..];
				return ResolveServerProperty(property, context);
			}

			// {{stepName.output}} or {{stepName.rawOutput}} — dependency output reference
			var dotIndex = expr.IndexOf('.');
			if (dotIndex > 0)
			{
				var stepName = expr[..dotIndex];
				var property = expr[(dotIndex + 1)..];

				if (property.Equals("rawOutput", StringComparison.OrdinalIgnoreCase))
				{
					var rawOutputs = context.GetRawDependencyOutputs(dependsOn);
					if (rawOutputs.TryGetValue(stepName, out var rawOutput))
						return rawOutput;
				}
				else if (property.Equals("output", StringComparison.OrdinalIgnoreCase))
				{
					var outputs = context.GetDependencyOutputs(dependsOn);
					if (outputs.TryGetValue(stepName, out var output))
						return output;
				}
				else if (property.Equals("files", StringComparison.OrdinalIgnoreCase) ||
						 FilesIndexPattern().IsMatch(property))
				{
					return ResolveStepFiles(stepName, property, context);
				}

				// Orchestration-step accessors: drill into the child run's data.
				var orchProperty = ResolveOrchestrationStepProperty(stepName, property, context);
				if (orchProperty is not null)
					return orchProperty;

				// Also check non-dependency steps by getting direct result
				var result = context.TryGetResult(stepName);
				if (result is not null)
				{
					if (property.Equals("rawOutput", StringComparison.OrdinalIgnoreCase))
						return result.RawContent ?? result.Content;
					if (property.Equals("output", StringComparison.OrdinalIgnoreCase))
						return result.Content;
				}

				// Track unresolved step output reference for diagnostics
				tracker?.TrackUnresolvedExpression(match.Value, currentStep.Name);
			}

			// Not resolvable — leave as-is
			return match.Value;
		});
	}

	/// <summary>
	/// Internal static-only resolution that handles param, env, vars, and orchestration
	/// expressions. Step-level and step-output expressions are left as-is.
	/// </summary>
	private static string ResolveStatic(
		string template,
		Dictionary<string, string> parameters,
		OrchestrationExecutionContext context,
		HashSet<string>? resolvingVars,
		TemplateResolutionTracker? tracker = null)
	{
		return TemplatePattern().Replace(template, match =>
		{
			var expr = match.Groups["expr"].Value.Trim();

			// {{param.name}} — parameter reference
			if (expr.StartsWith("param.", StringComparison.OrdinalIgnoreCase))
			{
				var paramName = expr["param.".Length..];
				return parameters.TryGetValue(paramName, out var value) ? value : match.Value;
			}

			// {{orchestration.property}} — orchestration metadata
			if (expr.StartsWith("orchestration.", StringComparison.OrdinalIgnoreCase))
			{
				var property = expr["orchestration.".Length..];
				return ResolveOrchestrationProperty(property, context.OrchestrationInfo, context);
			}

			// {{vars.name}} — user-defined variable with static-only recursive expansion
			if (expr.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
			{
				var varName = expr["vars.".Length..];
				return ResolveVariable(varName, parameters, context, resolvingVars, match.Value, tracker);
			}

			// {{env.VAR_NAME}} — environment variable
			if (expr.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
			{
				var envVarName = expr["env.".Length..];
				var envValue = Environment.GetEnvironmentVariable(envVarName);
				tracker?.TrackEnvironmentVariable(envVarName, envValue);
				return envValue ?? match.Value;
			}

			// {{server.property}} — server metadata
			if (expr.StartsWith("server.", StringComparison.OrdinalIgnoreCase))
			{
				var property = expr["server.".Length..];
				return ResolveServerProperty(property, context);
			}

			// Everything else (step.*, stepName.output, etc.) — leave as-is
			return match.Value;
		});
	}

	/// <summary>
	/// Resolves a built-in orchestration property by name.
	/// Throws on unknown properties since the orchestration.* namespace is fixed.
	/// </summary>
	private static string ResolveOrchestrationProperty(string property, OrchestrationInfo info, OrchestrationExecutionContext context)
	{
		if (property.Equals("name", StringComparison.OrdinalIgnoreCase))
			return info.Name;
		if (property.Equals("version", StringComparison.OrdinalIgnoreCase))
			return info.Version;
		if (property.Equals("runId", StringComparison.OrdinalIgnoreCase))
			return info.RunId;
		if (property.Equals("startedAt", StringComparison.OrdinalIgnoreCase))
			return info.StartedAt.ToString("o");
		if (property.Equals("tempDir", StringComparison.OrdinalIgnoreCase))
			return context.TempFileStore?.TempDirectory ?? "";
		if (property.Equals("sourcePath", StringComparison.OrdinalIgnoreCase))
			return info.SourcePath ?? "";
		if (property.Equals("sourceDirectory", StringComparison.OrdinalIgnoreCase))
			return info.SourceDirectory ?? "";

		throw new InvalidOperationException(
			$"Unknown orchestration property '{{{{orchestration.{property}}}}}'. " +
			$"Valid properties: {string.Join(", ", s_validOrchestrationProperties)}.");
	}

	/// <summary>
	/// Resolves a built-in step property by name.
	/// Throws on unknown properties since the step.* namespace is fixed.
	/// </summary>
	private static string ResolveStepProperty(string property, OrchestrationStep step)
	{
		if (property.Equals("name", StringComparison.OrdinalIgnoreCase))
			return step.Name;
		if (property.Equals("type", StringComparison.OrdinalIgnoreCase))
			return step.Type.ToString();

		throw new InvalidOperationException(
			$"Unknown step property '{{{{step.{property}}}}}'. " +
			$"Valid properties: {string.Join(", ", s_validStepProperties)}.");
	}

	/// <summary>
	/// Resolves a server property by name.
	/// Falls back to the <c>ORCHESTRA_SERVER_URL</c> environment variable when
	/// <see cref="OrchestrationExecutionContext.ServerUrl"/> is not set.
	/// Returns the original template expression if the URL cannot be determined.
	/// </summary>
	private static string ResolveServerProperty(string property, OrchestrationExecutionContext context)
	{
		if (property.Equals("url", StringComparison.OrdinalIgnoreCase))
		{
			var url = context.ServerUrl
				?? Environment.GetEnvironmentVariable("ORCHESTRA_SERVER_URL");
			return url ?? "{{server.url}}";
		}

		throw new InvalidOperationException(
			$"Unknown server property '{{{{server.{property}}}}}'. " +
			$"Valid properties: {string.Join(", ", s_validServerProperties)}.");
	}

	/// <summary>
	/// Resolves a user-defined variable, recursively expanding any template expressions
	/// in the variable's value using static-only resolution.
	/// Variable values can reference other variables, parameters, environment variables,
	/// and orchestration metadata — but NOT step outputs or step metadata.
	/// Detects circular references via a resolution stack.
	/// </summary>
	private static string ResolveVariable(
		string varName,
		Dictionary<string, string> parameters,
		OrchestrationExecutionContext context,
		HashSet<string>? resolvingVars,
		string originalMatch,
		TemplateResolutionTracker? tracker = null)
	{
		if (!context.Variables.TryGetValue(varName, out var rawValue))
			return originalMatch;

		// Circular reference detection: if this variable is already being resolved
		// up the call stack, leave it as-is to break the cycle.
		if (resolvingVars is not null && resolvingVars.Contains(varName))
			return originalMatch;

		// Push onto resolution stack
		var stack = resolvingVars ?? [];
		stack.Add(varName);

		// Recursively resolve using static-only resolution.
		// Variables can only contain param, env, vars, and orchestration expressions.
		var resolved = ResolveStatic(rawValue, parameters, context, stack, tracker);

		// Pop from resolution stack
		stack.Remove(varName);

		// Track the resolved variable if it differs from the raw value
		if (tracker is not null && resolved != rawValue)
		{
			tracker.TrackResolvedVariable(varName, resolved);
		}

		return resolved;
	}

	/// <summary>
	/// Resolves step file references.
	/// <c>files</c> returns a JSON array of all file paths saved by the step.
	/// <c>files[N]</c> returns the Nth file path (0-based index).
	/// </summary>
	private static string ResolveStepFiles(string stepName, string property, OrchestrationExecutionContext context)
	{
		var files = context.TempFileStore?.GetFilesForStep(stepName) ?? [];

		if (property.Equals("files", StringComparison.OrdinalIgnoreCase))
		{
			// Return JSON array of all file paths
			return System.Text.Json.JsonSerializer.Serialize(files);
		}

		// files[N] — extract the index
		var indexMatch = FilesIndexPattern().Match(property);
		if (indexMatch.Success && int.TryParse(indexMatch.Groups[1].Value, out var index))
		{
			if (index >= 0 && index < files.Length)
			{
				return files[index];
			}
			return string.Empty; // Index out of range — return empty rather than leaving the template
		}

		return string.Empty;
	}

	/// <summary>
	/// Resolves orchestration-step accessors that drill into a child run's data.
	/// Returns <c>null</c> when the expression is not an orchestration-step accessor
	/// (so the caller can fall through to the generic unresolved-tracking path), or when
	/// the referenced step has no <see cref="ChildOrchestrationInfo"/> attached (e.g. the
	/// step is not an orchestration step, or it failed before the launcher returned).
	/// </summary>
	/// <remarks>
	/// Recognized accessor shapes:
	/// <list type="bullet">
	///   <item><c>{{stepName.executionId|status|errorMessage|completionReason|childResult|steps}}</c></item>
	///   <item><c>{{stepName.steps.&lt;childStepName&gt;}}</c> — JSON of one child step</item>
	///   <item><c>{{stepName.steps.&lt;childStepName&gt;.output|rawOutput|error|status|files|files[N]}}</c></item>
	/// </list>
	/// </remarks>
	private static string? ResolveOrchestrationStepProperty(string stepName, string property, OrchestrationExecutionContext context)
	{
		var result = context.TryGetResult(stepName);
		if (result is null)
		{
			return null;
		}

		var info = result.ChildOrchestrationInfo;
		if (info is null)
		{
			// Not an orchestration step or no child info attached. Leave for the outer
			// fall-through to handle (e.g. output/rawOutput path).
			return null;
		}

		// Top-level child accessors.
		if (property.Equals("executionId", StringComparison.OrdinalIgnoreCase))
			return info.ExecutionId;
		if (property.Equals("status", StringComparison.OrdinalIgnoreCase))
			return info.Status.ToString().ToLowerInvariant();
		if (property.Equals("errorMessage", StringComparison.OrdinalIgnoreCase))
			return info.ErrorMessage ?? string.Empty;
		if (property.Equals("completionReason", StringComparison.OrdinalIgnoreCase))
			return info.CompletionReason ?? string.Empty;
		if (property.Equals("childResult", StringComparison.OrdinalIgnoreCase))
			return SerializeChildResult(info);
		if (property.Equals("steps", StringComparison.OrdinalIgnoreCase))
			return SerializeStepResults(info.StepResults);

		// {{stepName.steps.<childStepName>...}}
		if (property.StartsWith("steps.", StringComparison.OrdinalIgnoreCase))
		{
			var tail = property["steps.".Length..];
			var nextDot = tail.IndexOf('.');
			var childStepName = nextDot < 0 ? tail : tail[..nextDot];
			var leaf = nextDot < 0 ? null : tail[(nextDot + 1)..];

			if (!info.StepResults.TryGetValue(childStepName, out var childStep))
			{
				// Child step name not found. Returning null lets the outer caller mark it
				// as unresolved, which surfaces as a diagnostic to the user.
				return null;
			}

			if (leaf is null)
			{
				return SerializeChildStep(childStep);
			}

			if (leaf.Equals("output", StringComparison.OrdinalIgnoreCase))
				return childStep.Content;
			if (leaf.Equals("rawOutput", StringComparison.OrdinalIgnoreCase))
				return childStep.RawContent ?? childStep.Content;
			if (leaf.Equals("error", StringComparison.OrdinalIgnoreCase))
				return childStep.ErrorMessage ?? string.Empty;
			if (leaf.Equals("status", StringComparison.OrdinalIgnoreCase))
				return childStep.Status.ToString().ToLowerInvariant();
			if (leaf.Equals("files", StringComparison.OrdinalIgnoreCase))
				return System.Text.Json.JsonSerializer.Serialize(childStep.SavedFiles);
			var indexMatch = FilesIndexPattern().Match(leaf);
			if (indexMatch.Success && int.TryParse(indexMatch.Groups[1].Value, out var index))
			{
				if (index >= 0 && index < childStep.SavedFiles.Count)
				{
					return childStep.SavedFiles[index];
				}
				return string.Empty;
			}

			// Unknown leaf — return null so the outer caller flags it as unresolved.
			return null;
		}

		return null;
	}

	private static string SerializeChildResult(ChildOrchestrationInfo info)
	{
		var payload = new
		{
			executionId = info.ExecutionId,
			orchestrationId = info.OrchestrationId,
			orchestrationName = info.OrchestrationName,
			status = info.Status.ToString().ToLowerInvariant(),
			errorMessage = info.ErrorMessage,
			finalContent = info.FinalContent,
			completionReason = info.CompletionReason,
			cancellation = info.Cancellation is null ? null : new
			{
				kind = info.Cancellation.Kind.ToString(),
				detail = info.Cancellation.Detail,
				reason = info.Cancellation.Reason,
				source = info.Cancellation.Source,
				isTimeout = info.Cancellation.IsTimeout,
			},
			startedAt = info.StartedAt,
			completedAt = info.CompletedAt,
			stepResults = ProjectStepResultsForJson(info.StepResults),
		};
		return System.Text.Json.JsonSerializer.Serialize(payload, s_childResultJsonOptions);
	}

	private static string SerializeStepResults(IReadOnlyDictionary<string, ChildStepInfo> stepResults)
	{
		return System.Text.Json.JsonSerializer.Serialize(ProjectStepResultsForJson(stepResults), s_childResultJsonOptions);
	}

	private static string SerializeChildStep(ChildStepInfo step)
	{
		var payload = new
		{
			status = step.Status.ToString().ToLowerInvariant(),
			output = step.Content,
			rawOutput = step.RawContent,
			error = step.ErrorMessage,
			files = step.SavedFiles,
		};
		return System.Text.Json.JsonSerializer.Serialize(payload, s_childResultJsonOptions);
	}

	private static Dictionary<string, object> ProjectStepResultsForJson(IReadOnlyDictionary<string, ChildStepInfo> stepResults)
	{
		var projected = new Dictionary<string, object>(stepResults.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var (name, step) in stepResults)
		{
			projected[name] = new
			{
				status = step.Status.ToString().ToLowerInvariant(),
				output = step.Content,
				rawOutput = step.RawContent,
				error = step.ErrorMessage,
				files = step.SavedFiles,
			};
		}
		return projected;
	}
}
