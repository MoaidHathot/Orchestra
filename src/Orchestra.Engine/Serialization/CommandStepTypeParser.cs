using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Command step type JSON.
/// Handles deserialization of <see cref="CommandOrchestrationStep"/> from orchestration JSON.
/// </summary>
public sealed class CommandStepTypeParser : IStepTypeParser
{
	public string TypeName => "Command";

	public OrchestrationStep Parse(JsonElement root, StepParseContext context)
	{
		var baseFields = StepBaseFields.Parse(root);

		var workingDirectory = root.TryGetProperty("workingDirectory", out var wd)
			? ResolvePath(wd.GetString()!, context)
			: null;

		return new CommandOrchestrationStep
		{
			Name = baseFields.Name,
			Type = OrchestrationStepType.Command,
			DependsOn = baseFields.DependsOn,
			Enabled = baseFields.Enabled,
			When = baseFields.When,
			TimeoutSeconds = baseFields.TimeoutSeconds,
			Retry = baseFields.Retry,
			Parameters = StepBaseFields.ParseParameterNames(root),
			Command = root.GetProperty("command").GetString()!,
			Arguments = root.TryGetProperty("arguments", out var args)
				? args.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			WorkingDirectory = workingDirectory,
			Environment = root.TryGetProperty("environment", out var env)
				? env.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
				: [],
			IncludeStdErr = root.TryGetProperty("includeStdErr", out var ise)
				&& ise.GetBoolean(),
			Stdin = root.TryGetProperty("stdin", out var stdin)
				? stdin.GetString()
				: null,
			MaxOutputBytes = StepBaseFields.ParseMaxOutputBytes(root, baseFields.Name),
		};
	}

	private static string ResolvePath(string path, StepParseContext context)
	{
		return PromptStepTypeParser.ResolvePathRelativeToBaseDirectory(path, context);
	}
}
