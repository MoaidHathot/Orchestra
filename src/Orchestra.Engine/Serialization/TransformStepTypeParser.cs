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
		var baseFields = StepBaseFields.Parse(root);

		return new TransformOrchestrationStep
		{
			Name = baseFields.Name,
			Type = OrchestrationStepType.Transform,
			DependsOn = baseFields.DependsOn,
			Enabled = baseFields.Enabled,
			When = baseFields.When,
			TimeoutSeconds = baseFields.TimeoutSeconds,
			Retry = baseFields.Retry,
			Parameters = StepBaseFields.ParseParameterNames(root),
			Template = root.GetProperty("template").GetString()!,
			ContentType = root.TryGetProperty("contentType", out var ct)
				? ct.GetString() ?? "text/plain"
				: "text/plain",
		};
	}
}
