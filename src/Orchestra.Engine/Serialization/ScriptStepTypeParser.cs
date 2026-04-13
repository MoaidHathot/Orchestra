using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Script step type JSON.
/// Handles deserialization of <see cref="ScriptOrchestrationStep"/> from orchestration JSON.
/// Validates that exactly one of <c>script</c> or <c>scriptFile</c> is provided
/// and that <c>shell</c> is specified.
/// </summary>
public sealed class ScriptStepTypeParser : IStepTypeParser
{
	public string TypeName => "Script";

	public OrchestrationStep Parse(JsonElement root, StepParseContext context)
	{
		// Validate that shell is provided
		if (!root.TryGetProperty("shell", out var shellProp) || string.IsNullOrWhiteSpace(shellProp.GetString()))
			throw new JsonException("Script step requires a 'shell' property (e.g., 'pwsh', 'bash', 'python').");

		var hasScript = root.TryGetProperty("script", out var scriptProp) && scriptProp.ValueKind == JsonValueKind.String;
		var hasScriptFile = root.TryGetProperty("scriptFile", out var scriptFileProp) && scriptFileProp.ValueKind == JsonValueKind.String;

		if (!hasScript && !hasScriptFile)
			throw new JsonException("Script step requires either 'script' (inline) or 'scriptFile' (path to file).");

		if (hasScript && hasScriptFile)
			throw new JsonException("Script step cannot have both 'script' and 'scriptFile'. Use one or the other.");

		// Resolve scriptFile path relative to orchestration file directory
		string? scriptFile = null;
		if (hasScriptFile)
		{
			scriptFile = scriptFileProp.GetString()!;

			// Resolve relative path from orchestration file directory (same pattern as systemPromptFile)
			if (context.BaseDirectory is not null && !Path.IsPathRooted(scriptFile))
			{
				scriptFile = Path.GetFullPath(Path.Combine(context.BaseDirectory, scriptFile));
			}
		}

		return new ScriptOrchestrationStep
		{
			Name = root.GetProperty("name").GetString()!,
			Type = OrchestrationStepType.Script,
			DependsOn = root.TryGetProperty("dependsOn", out var deps)
				? deps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Enabled = !root.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
			Shell = shellProp.GetString()!,
			Script = hasScript ? scriptProp.GetString() : null,
			ScriptFile = scriptFile,
			Arguments = root.TryGetProperty("arguments", out var args)
				? args.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			WorkingDirectory = root.TryGetProperty("workingDirectory", out var wd)
				? wd.GetString()
				: null,
			Environment = root.TryGetProperty("environment", out var env)
				? env.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
				: [],
			IncludeStdErr = root.TryGetProperty("includeStdErr", out var ise)
				&& ise.GetBoolean(),
			Stdin = root.TryGetProperty("stdin", out var stdin)
				? stdin.GetString()
				: null,
			TimeoutSeconds = root.TryGetProperty("timeoutSeconds", out var ts)
				? ts.GetInt32()
				: null,
			Retry = root.TryGetProperty("retry", out var retry)
				? PromptStepTypeParser.DeserializeRetryPolicy(retry)
				: null,
			Parameters = root.TryGetProperty("parameters", out var parameters)
				? parameters.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
		};
	}
}
