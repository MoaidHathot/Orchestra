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
///   {{stepName.output}}    — output content from a completed dependency step
///   {{stepName.rawOutput}} — raw output from a completed dependency step
/// </summary>
public static partial class TemplateResolver
{
	[GeneratedRegex(@"\{\{(?<expr>[^}]+)\}\}", RegexOptions.Compiled)]
	private static partial Regex TemplatePattern();

	private static readonly string[] s_validOrchestrationProperties = ["name", "version", "runid", "startedat"];
	private static readonly string[] s_validStepProperties = ["name", "type"];

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
		return Resolve(template, parameters, context, dependsOn, currentStep, resolvingVars: null);
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
		HashSet<string>? resolvingVars)
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
				return ResolveOrchestrationProperty(property, context.OrchestrationInfo);
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
				return ResolveVariable(varName, parameters, context, dependsOn, currentStep, resolvingVars, match.Value);
			}

			// {{env.VAR_NAME}} — environment variable
			if (expr.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
			{
				var envVarName = expr["env.".Length..];
				var envValue = Environment.GetEnvironmentVariable(envVarName);
				return envValue ?? match.Value;
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
				else
				{
					var outputs = context.GetDependencyOutputs(dependsOn);
					if (outputs.TryGetValue(stepName, out var output))
						return output;
				}

				// Also check non-dependency steps by getting direct result
				var result = context.TryGetResult(stepName);
				if (result is not null)
				{
					return property.Equals("rawOutput", StringComparison.OrdinalIgnoreCase)
						? result.RawContent ?? result.Content
						: result.Content;
				}
			}

			// Not resolvable — leave as-is
			return match.Value;
		});
	}

	/// <summary>
	/// Resolves a built-in orchestration property by name.
	/// Throws on unknown properties since the orchestration.* namespace is fixed.
	/// </summary>
	private static string ResolveOrchestrationProperty(string property, OrchestrationInfo info)
	{
		if (property.Equals("name", StringComparison.OrdinalIgnoreCase))
			return info.Name;
		if (property.Equals("version", StringComparison.OrdinalIgnoreCase))
			return info.Version;
		if (property.Equals("runId", StringComparison.OrdinalIgnoreCase))
			return info.RunId;
		if (property.Equals("startedAt", StringComparison.OrdinalIgnoreCase))
			return info.StartedAt.ToString("o");

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
	/// Resolves a user-defined variable, recursively expanding any template expressions
	/// in the variable's value. Detects circular references via a resolution stack.
	/// </summary>
	private static string ResolveVariable(
		string varName,
		Dictionary<string, string> parameters,
		OrchestrationExecutionContext context,
		string[] dependsOn,
		OrchestrationStep currentStep,
		HashSet<string>? resolvingVars,
		string originalMatch)
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

		// Recursively resolve any template expressions within the variable value
		var resolved = Resolve(rawValue, parameters, context, dependsOn, currentStep, stack);

		// Pop from resolution stack
		stack.Remove(varName);

		return resolved;
	}
}
