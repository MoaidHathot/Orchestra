using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Transform step type JSON.
/// Handles deserialization of <see cref="TransformOrchestrationStep"/> from orchestration JSON.
/// </summary>
public sealed class TransformStepTypeParser : IStepTypeParser
{
	public string TypeName => "Transform";

	public OrchestrationStep Parse(JsonElement root, StepParseContext context)
	{
		return new TransformOrchestrationStep
		{
			Name = root.GetProperty("name").GetString()!,
			Type = OrchestrationStepType.Transform,
			DependsOn = root.TryGetProperty("dependsOn", out var deps)
				? deps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Template = root.GetProperty("template").GetString()!,
			ContentType = root.TryGetProperty("contentType", out var ct)
				? ct.GetString() ?? "text/plain"
				: "text/plain",
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
