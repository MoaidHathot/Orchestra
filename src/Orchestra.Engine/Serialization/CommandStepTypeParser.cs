using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Command step type JSON.
/// Handles deserialization of <see cref="CommandOrchestrationStep"/> from orchestration JSON.
/// </summary>
public sealed class CommandStepTypeParser : IStepTypeParser
{
	public string TypeName => "Command";

	public OrchestrationStep Parse(JsonElement root)
	{
		return new CommandOrchestrationStep
		{
			Name = root.GetProperty("name").GetString()!,
			Type = OrchestrationStepType.Command,
			DependsOn = root.TryGetProperty("dependsOn", out var deps)
				? deps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Command = root.GetProperty("command").GetString()!,
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
