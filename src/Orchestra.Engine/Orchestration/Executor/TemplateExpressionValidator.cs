using System.Text.RegularExpressions;

namespace Orchestra.Engine;

/// <summary>
/// Result of template expression validation. Contains a list of errors (if any)
/// and helpers to format them for display.
/// </summary>
public class TemplateValidationResult
{
	public bool IsValid => Errors.Count == 0;
	public List<TemplateValidationError> Errors { get; } = [];

	public string FormatErrors()
	{
		if (IsValid)
			return string.Empty;

		var lines = Errors.Select(e =>
		{
			var location = (e.StepName, e.FieldName) switch
			{
				(not null, not null) => $"[Step '{e.StepName}', Field '{e.FieldName}']",
				(not null, null) => $"[Step '{e.StepName}']",
				(null, not null) => $"[Field '{e.FieldName}']",
				_ => "[Orchestration]",
			};
			var expr = e.Expression is not null ? $" Expression: {e.Expression}." : "";
			return $"  - {location} {e.Message}{expr}";
		});

		return $"Template expression validation failed with {Errors.Count} error(s):\n{string.Join("\n", lines)}";
	}
}

/// <summary>
/// A single validation error for a template expression.
/// </summary>
/// <param name="Message">Human-readable description of the problem.</param>
/// <param name="StepName">The step where the error was found (null for orchestration-level).</param>
/// <param name="FieldName">The field containing the expression (e.g., "UserPrompt", "Command").</param>
/// <param name="Expression">The offending expression (e.g., "{{param.missing}}").</param>
public record TemplateValidationError(
	string Message,
	string? StepName = null,
	string? FieldName = null,
	string? Expression = null);

/// <summary>
/// Validates template expressions in an orchestration before execution.
/// Two validation layers:
/// <list type="bullet">
///   <item><see cref="ValidateOrchestration"/> — parse-time validation that requires no runtime context.</item>
///   <item><see cref="ValidateRuntime"/> — pre-execution validation that checks environment variables and parameter resolution.</item>
/// </list>
/// </summary>
public static partial class TemplateExpressionValidator
{
	[GeneratedRegex(@"\{\{(?<expr>[^}]+)\}\}", RegexOptions.Compiled)]
	private static partial Regex TemplatePattern();

	[GeneratedRegex(@"^files\[(\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
	private static partial Regex FilesIndexPattern();

	private static readonly HashSet<string> s_validOrchestrationProperties =
		new(["name", "version", "runid", "startedat", "tempdir"], StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> s_validStepProperties =
		new(["name", "type"], StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> s_knownNamespaces =
		new(["param", "orchestration", "step", "vars", "env", "server"], StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> s_validServerProperties =
		new(["url"], StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> s_validStepOutputSuffixes =
		new(["output", "rawoutput", "files"], StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Parse-time validation: checks all template expressions for structural correctness.
	/// Does NOT require runtime context (no parameters, no environment).
	/// </summary>
	public static TemplateValidationResult ValidateOrchestration(Orchestration orchestration)
	{
		var result = new TemplateValidationResult();
		var stepNames = new HashSet<string>(orchestration.Steps.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

		// Collect all declared parameter names:
		// - From orchestration-level Inputs (if defined, this is the authoritative source)
		// - From step-level Parameters arrays (legacy behavior, and for declaring which inputs each step needs)
		var allParamNames = new HashSet<string>(
			orchestration.Steps.SelectMany(s => s.Parameters),
			StringComparer.OrdinalIgnoreCase);

		// When Inputs is defined, include its keys as valid parameter names too
		if (orchestration.Inputs is not null)
		{
			foreach (var key in orchestration.Inputs.Keys)
				allParamNames.Add(key);
		}

		// Build reachability map: for each step, what steps can it reach via DependsOn (transitive)
		var reachability = BuildReachabilityMap(orchestration);

		// 1. Validate orchestration-level variable values (static-only context)
		foreach (var (varName, varValue) in orchestration.Variables)
		{
			ValidateExpressionsInField(result, varValue, null, $"Variables[{varName}]",
				allParamNames, orchestration.Variables, stepNames, reachability,
				isStaticOnlyContext: true);
		}

		// 2. Validate orchestration-level MCP definitions (static-only context)
		for (var i = 0; i < orchestration.Mcps.Length; i++)
		{
			ValidateMcpExpressions(result, orchestration.Mcps[i], null,
				$"Mcps[{i}]", allParamNames, orchestration.Variables, stepNames, reachability);
		}

		// 3. Validate each step's resolvable fields
		foreach (var step in orchestration.Steps)
		{
			ValidateStepExpressions(result, step, allParamNames, orchestration.Variables,
				stepNames, reachability);
		}

		// 4. Detect circular variable references
		DetectCircularVariables(result, orchestration.Variables);

		return result;
	}

	/// <summary>
	/// Pre-execution validation: checks runtime-dependent expressions.
	/// Called after <see cref="ValidateOrchestration"/> with actual parameters.
	/// </summary>
	public static TemplateValidationResult ValidateRuntime(
		Orchestration orchestration,
		Dictionary<string, string>? parameters)
	{
		var result = new TemplateValidationResult();
		var effectiveParams = parameters ?? [];

		// Collect all env expressions from the entire orchestration
		var envExpressions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Check variable values first — they can cascade env references
		foreach (var (varName, varValue) in orchestration.Variables)
		{
			CollectEnvExpressions(varValue, envExpressions);
		}

		// Check orchestration-level MCPs
		foreach (var mcp in orchestration.Mcps)
		{
			foreach (var field in GetMcpFields(mcp))
			{
				CollectEnvExpressions(field.Value, envExpressions);
			}
		}

		// Check all step fields
		foreach (var step in orchestration.Steps)
		{
			foreach (var field in GetStepFields(step))
			{
				CollectEnvExpressions(field.Value, envExpressions);
			}
		}

		// Validate all referenced env vars exist
		foreach (var envVarName in envExpressions)
		{
			var envValue = Environment.GetEnvironmentVariable(envVarName);
			if (envValue is null)
			{
				result.Errors.Add(new TemplateValidationError(
					$"Environment variable '{envVarName}' is not set.",
					FieldName: "env",
					Expression: $"{{{{env.{envVarName}}}}}"));
			}
		}

		// Validate variable values resolve with actual parameters
		// (catches cascading failures like vars referencing {{param.missing}} when params are provided)
		foreach (var (varName, varValue) in orchestration.Variables)
		{
			foreach (var match in TemplatePattern().Matches(varValue).Cast<Match>())
			{
				var expr = match.Groups["expr"].Value.Trim();
				if (expr.StartsWith("param.", StringComparison.OrdinalIgnoreCase))
				{
					var paramName = expr["param.".Length..];
					if (!effectiveParams.ContainsKey(paramName))
					{
						result.Errors.Add(new TemplateValidationError(
							$"Variable '{varName}' references parameter '{paramName}' which is not provided.",
							FieldName: $"Variables[{varName}]",
							Expression: $"{{{{{expr}}}}}"));
					}
				}
			}
		}

		return result;
	}

	/// <summary>
	/// Validates all template expressions in a single string field.
	/// </summary>
	private static void ValidateExpressionsInField(
		TemplateValidationResult result,
		string? value,
		string? stepName,
		string fieldName,
		HashSet<string> allParamNames,
		Dictionary<string, string> variables,
		HashSet<string> stepNames,
		Dictionary<string, HashSet<string>> reachability,
		bool isStaticOnlyContext)
	{
		if (string.IsNullOrEmpty(value))
			return;

		foreach (var match in TemplatePattern().Matches(value).Cast<Match>())
		{
			var expr = match.Groups["expr"].Value.Trim();
			ValidateSingleExpression(result, expr, stepName, fieldName,
				allParamNames, variables, stepNames, reachability, isStaticOnlyContext);
		}
	}

	/// <summary>
	/// Validates a single template expression.
	/// </summary>
	private static void ValidateSingleExpression(
		TemplateValidationResult result,
		string expr,
		string? stepName,
		string fieldName,
		HashSet<string> allParamNames,
		Dictionary<string, string> variables,
		HashSet<string> stepNames,
		Dictionary<string, HashSet<string>> reachability,
		bool isStaticOnlyContext)
	{
		var fullExpr = $"{{{{{expr}}}}}";

		// {{param.name}}
		if (expr.StartsWith("param.", StringComparison.OrdinalIgnoreCase))
		{
			var paramName = expr["param.".Length..];
			if (!allParamNames.Contains(paramName))
			{
				result.Errors.Add(new TemplateValidationError(
					$"Parameter '{paramName}' is not declared by any step's Parameters array.",
					stepName, fieldName, fullExpr));
			}
			return;
		}

		// {{orchestration.property}}
		if (expr.StartsWith("orchestration.", StringComparison.OrdinalIgnoreCase))
		{
			var property = expr["orchestration.".Length..];
			if (!s_validOrchestrationProperties.Contains(property))
			{
				result.Errors.Add(new TemplateValidationError(
					$"Unknown orchestration property '{property}'. Valid properties: {string.Join(", ", s_validOrchestrationProperties)}.",
					stepName, fieldName, fullExpr));
			}
			return;
		}

		// {{step.property}}
		if (expr.StartsWith("step.", StringComparison.OrdinalIgnoreCase))
		{
			if (isStaticOnlyContext)
			{
				result.Errors.Add(new TemplateValidationError(
					"Step metadata expressions are not available in static-only contexts (MCP fields, variable values).",
					stepName, fieldName, fullExpr));
				return;
			}
			var property = expr["step.".Length..];
			if (!s_validStepProperties.Contains(property))
			{
				result.Errors.Add(new TemplateValidationError(
					$"Unknown step property '{property}'. Valid properties: {string.Join(", ", s_validStepProperties)}.",
					stepName, fieldName, fullExpr));
			}
			return;
		}

		// {{server.property}}
		if (expr.StartsWith("server.", StringComparison.OrdinalIgnoreCase))
		{
			var property = expr["server.".Length..];
			if (!s_validServerProperties.Contains(property))
			{
				result.Errors.Add(new TemplateValidationError(
					$"Unknown server property '{property}'. Valid properties: {string.Join(", ", s_validServerProperties)}.",
					stepName, fieldName, fullExpr));
			}
			return;
		}

		// {{vars.name}}
		if (expr.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
		{
			var varName = expr["vars.".Length..];
			if (!variables.ContainsKey(varName))
			{
				result.Errors.Add(new TemplateValidationError(
					$"Variable '{varName}' is not defined in the orchestration's Variables.",
					stepName, fieldName, fullExpr));
			}
			return;
		}

		// {{env.VAR_NAME}} — checked at runtime, skip here
		if (expr.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		// {{stepName.output|rawOutput|files|files[N]}} — step output reference
		var dotIndex = expr.IndexOf('.');
		if (dotIndex > 0)
		{
			var refStepName = expr[..dotIndex];
			var property = expr[(dotIndex + 1)..];

			// Check if it's a valid step output reference
			var isStepOutput = s_validStepOutputSuffixes.Contains(property)
				|| FilesIndexPattern().IsMatch(property);

			if (isStepOutput)
			{
				if (isStaticOnlyContext)
				{
					result.Errors.Add(new TemplateValidationError(
						"Step output expressions are not available in static-only contexts (MCP fields, variable values).",
						stepName, fieldName, fullExpr));
					return;
				}

				// Step must exist
				if (!stepNames.Contains(refStepName))
				{
					result.Errors.Add(new TemplateValidationError(
						$"Step '{refStepName}' does not exist in the orchestration.",
						stepName, fieldName, fullExpr));
					return;
				}

				// Step must be reachable via DependsOn (direct or transitive)
				if (stepName is not null && reachability.TryGetValue(stepName, out var reachable) &&
					!reachable.Contains(refStepName))
				{
					result.Errors.Add(new TemplateValidationError(
						$"Step '{refStepName}' is not reachable via DependsOn from step '{stepName}'. " +
						$"Add it as a direct or transitive dependency.",
						stepName, fieldName, fullExpr));
				}
				return;
			}

			// Unknown property on a step name — could be a typo
			if (stepNames.Contains(refStepName))
			{
				result.Errors.Add(new TemplateValidationError(
					$"Unknown output property '{property}' on step '{refStepName}'. " +
					$"Valid properties: output, rawOutput, files, files[N].",
					stepName, fieldName, fullExpr));
				return;
			}
		}

		// Check if the namespace prefix is unknown
		var nsEnd = expr.IndexOf('.');
		if (nsEnd > 0)
		{
			var ns = expr[..nsEnd];
			if (!s_knownNamespaces.Contains(ns) && !stepNames.Contains(ns))
			{
				result.Errors.Add(new TemplateValidationError(
					$"Unknown expression namespace '{ns}'. " +
					$"Known namespaces: {string.Join(", ", s_knownNamespaces)}. " +
					$"Or use a valid step name.",
					stepName, fieldName, fullExpr));
			}
		}
		else
		{
			// No dot at all — completely unknown expression format
			result.Errors.Add(new TemplateValidationError(
				$"Invalid expression format '{expr}'. Expressions must use a namespace prefix (e.g., param.name, vars.name, env.NAME).",
				stepName, fieldName, fullExpr));
		}
	}

	/// <summary>
	/// Validates all resolvable fields for a single step.
	/// </summary>
	private static void ValidateStepExpressions(
		TemplateValidationResult result,
		OrchestrationStep step,
		HashSet<string> allParamNames,
		Dictionary<string, string> variables,
		HashSet<string> stepNames,
		Dictionary<string, HashSet<string>> reachability)
	{
		foreach (var (fieldName, value) in GetStepFields(step))
		{
			ValidateExpressionsInField(result, value, step.Name, fieldName,
				allParamNames, variables, stepNames, reachability,
				isStaticOnlyContext: false);
		}

		// Validate step-level MCP definitions (static-only context)
		if (step is PromptOrchestrationStep promptStep)
		{
			for (var i = 0; i < promptStep.Mcps.Length; i++)
			{
				ValidateMcpExpressions(result, promptStep.Mcps[i], step.Name,
					$"Mcps[{i}]", allParamNames, variables, stepNames, reachability);
			}

			// Validate subagent MCPs (static-only context)
			for (var si = 0; si < promptStep.Subagents.Length; si++)
			{
				var subagent = promptStep.Subagents[si];
				for (var mi = 0; mi < subagent.Mcps.Length; mi++)
				{
					ValidateMcpExpressions(result, subagent.Mcps[mi], step.Name,
						$"Subagents[{si}].Mcps[{mi}]", allParamNames, variables, stepNames, reachability);
				}
			}
		}
	}

	/// <summary>
	/// Validates template expressions in MCP fields (static-only context).
	/// </summary>
	private static void ValidateMcpExpressions(
		TemplateValidationResult result,
		Mcp mcp,
		string? stepName,
		string fieldPrefix,
		HashSet<string> allParamNames,
		Dictionary<string, string> variables,
		HashSet<string> stepNames,
		Dictionary<string, HashSet<string>> reachability)
	{
		foreach (var (fieldName, value) in GetMcpFields(mcp))
		{
			ValidateExpressionsInField(result, value, stepName, $"{fieldPrefix}.{fieldName}",
				allParamNames, variables, stepNames, reachability,
				isStaticOnlyContext: true);
		}
	}

	/// <summary>
	/// Detects circular references among orchestration variables using DFS.
	/// </summary>
	private static void DetectCircularVariables(
		TemplateValidationResult result,
		Dictionary<string, string> variables)
	{
		// For each variable, follow vars.* references and detect cycles
		foreach (var varName in variables.Keys)
		{
			var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var path = new List<string>();
			if (HasCircularReference(varName, variables, visited, path))
			{
				path.Add(varName); // Close the cycle
				result.Errors.Add(new TemplateValidationError(
					$"Circular variable reference detected: {string.Join(" -> ", path)}.",
					FieldName: $"Variables[{varName}]",
					Expression: $"{{{{vars.{varName}}}}}"));
			}
		}
	}

	/// <summary>
	/// DFS helper to detect circular variable references.
	/// </summary>
	private static bool HasCircularReference(
		string varName,
		Dictionary<string, string> variables,
		HashSet<string> visited,
		List<string> path)
	{
		if (!variables.TryGetValue(varName, out var value))
			return false;

		if (!visited.Add(varName))
			return true; // Cycle detected

		path.Add(varName);

		foreach (var match in TemplatePattern().Matches(value).Cast<Match>())
		{
			var expr = match.Groups["expr"].Value.Trim();
			if (expr.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
			{
				var referencedVar = expr["vars.".Length..];
				if (HasCircularReference(referencedVar, variables, visited, path))
					return true;
			}
		}

		path.RemoveAt(path.Count - 1);
		visited.Remove(varName);
		return false;
	}

	/// <summary>
	/// Builds a transitive reachability map from DependsOn relationships.
	/// For each step, computes the set of all steps reachable via DependsOn (direct + transitive).
	/// </summary>
	private static Dictionary<string, HashSet<string>> BuildReachabilityMap(Orchestration orchestration)
	{
		var directDeps = orchestration.Steps.ToDictionary(
			s => s.Name,
			s => s.DependsOn,
			StringComparer.OrdinalIgnoreCase);

		var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

		foreach (var step in orchestration.Steps)
		{
			var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var queue = new Queue<string>(step.DependsOn);
			while (queue.Count > 0)
			{
				var dep = queue.Dequeue();
				if (reachable.Add(dep) && directDeps.TryGetValue(dep, out var transitiveDeps))
				{
					foreach (var td in transitiveDeps)
						queue.Enqueue(td);
				}
			}
			result[step.Name] = reachable;
		}

		return result;
	}

	/// <summary>
	/// Collects all env variable names referenced by template expressions in a string.
	/// </summary>
	private static void CollectEnvExpressions(string? value, HashSet<string> envNames)
	{
		if (string.IsNullOrEmpty(value))
			return;

		foreach (var match in TemplatePattern().Matches(value).Cast<Match>())
		{
			var expr = match.Groups["expr"].Value.Trim();
			if (expr.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
			{
				envNames.Add(expr["env.".Length..]);
			}
		}
	}

	/// <summary>
	/// Enumerates all resolvable fields from a step (full-resolution context).
	/// Returns (fieldName, value) pairs.
	/// </summary>
	private static IEnumerable<(string FieldName, string Value)> GetStepFields(OrchestrationStep step)
	{
		return step switch
		{
			PromptOrchestrationStep ps => GetPromptStepFields(ps),
			CommandOrchestrationStep cs => GetCommandStepFields(cs),
			HttpOrchestrationStep hs => GetHttpStepFields(hs),
			TransformOrchestrationStep ts => GetTransformStepFields(ts),
			_ => [],
		};
	}

	private static IEnumerable<(string, string)> GetPromptStepFields(PromptOrchestrationStep step)
	{
		yield return ("Model", step.Model);
		yield return ("SystemPrompt", step.SystemPrompt);
		yield return ("UserPrompt", step.UserPrompt);
		if (step.InputHandlerPrompt is not null)
			yield return ("InputHandlerPrompt", step.InputHandlerPrompt);
		if (step.OutputHandlerPrompt is not null)
			yield return ("OutputHandlerPrompt", step.OutputHandlerPrompt);
		foreach (var (i, dir) in step.SkillDirectories.Select((d, i) => (i, d)))
			yield return ($"SkillDirectories[{i}]", dir);
	}

	private static IEnumerable<(string, string)> GetCommandStepFields(CommandOrchestrationStep step)
	{
		yield return ("Command", step.Command);
		foreach (var (i, arg) in step.Arguments.Select((a, i) => (i, a)))
			yield return ($"Arguments[{i}]", arg);
		if (step.WorkingDirectory is not null)
			yield return ("WorkingDirectory", step.WorkingDirectory);
		if (step.Stdin is not null)
			yield return ("Stdin", step.Stdin);
		foreach (var (key, value) in step.Environment)
			yield return ($"Environment[{key}]", value);
	}

	private static IEnumerable<(string, string)> GetHttpStepFields(HttpOrchestrationStep step)
	{
		yield return ("Url", step.Url);
		if (step.Body is not null)
			yield return ("Body", step.Body);
		foreach (var (key, value) in step.Headers)
			yield return ($"Headers[{key}]", value);
	}

	private static IEnumerable<(string, string)> GetTransformStepFields(TransformOrchestrationStep step)
	{
		yield return ("Template", step.Template);
	}

	/// <summary>
	/// Enumerates all resolvable fields from an MCP configuration (static-only context).
	/// Name and Type are excluded since they are identity/structural fields.
	/// </summary>
	private static IEnumerable<(string FieldName, string Value)> GetMcpFields(Mcp mcp)
	{
		return mcp switch
		{
			LocalMcp local => GetLocalMcpFields(local),
			RemoteMcp remote => GetRemoteMcpFields(remote),
			_ => [],
		};
	}

	private static IEnumerable<(string, string)> GetLocalMcpFields(LocalMcp mcp)
	{
		yield return ("Command", mcp.Command);
		foreach (var (i, arg) in mcp.Arguments.Select((a, i) => (i, a)))
			yield return ($"Arguments[{i}]", arg);
		if (mcp.WorkingDirectory is not null)
			yield return ("WorkingDirectory", mcp.WorkingDirectory);
	}

	private static IEnumerable<(string, string)> GetRemoteMcpFields(RemoteMcp mcp)
	{
		yield return ("Endpoint", mcp.Endpoint);
		foreach (var (key, value) in mcp.Headers)
			yield return ($"Headers[{key}]", value);
	}
}
