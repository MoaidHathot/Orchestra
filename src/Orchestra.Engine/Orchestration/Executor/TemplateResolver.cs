using System.Text.RegularExpressions;

namespace Orchestra.Engine;

/// <summary>
/// Utility for resolving template expressions in strings.
/// Supports:
///   {{param.name}} — parameter value
///   {{stepName.output}} — output content from a completed dependency step
///   {{stepName.rawOutput}} — raw output from a completed dependency step
/// </summary>
public static partial class TemplateResolver
{
	[GeneratedRegex(@"\{\{(?<expr>[^}]+)\}\}", RegexOptions.Compiled)]
	private static partial Regex TemplatePattern();

	/// <summary>
	/// Resolves all template expressions in the input string.
	/// </summary>
	public static string Resolve(
		string template,
		Dictionary<string, string> parameters,
		OrchestrationExecutionContext context,
		string[] dependsOn)
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
}
