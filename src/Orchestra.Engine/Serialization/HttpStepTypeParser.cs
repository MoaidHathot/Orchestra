using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Http step type JSON.
/// Handles deserialization of <see cref="HttpOrchestrationStep"/> from orchestration JSON.
/// </summary>
public sealed class HttpStepTypeParser : IStepTypeParser
{
	public string TypeName => "Http";

	public OrchestrationStep Parse(JsonElement root, StepParseContext context)
	{
		var baseFields = StepBaseFields.Parse(root);

		return new HttpOrchestrationStep
		{
			Name = baseFields.Name,
			Type = OrchestrationStepType.Http,
			DependsOn = baseFields.DependsOn,
			Enabled = baseFields.Enabled,
			When = baseFields.When,
			TimeoutSeconds = baseFields.TimeoutSeconds,
			Retry = baseFields.Retry,
			Parameters = StepBaseFields.ParseParameterNames(root),
			Method = root.TryGetProperty("method", out var method)
				? method.GetString()!
				: "GET",
			Url = root.GetProperty("url").GetString()!,
			Headers = root.TryGetProperty("headers", out var headers)
				? headers.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
				: [],
			Body = root.TryGetProperty("body", out var body)
				? body.GetString()
				: null,
			ContentType = root.TryGetProperty("contentType", out var ct)
				? ct.GetString() ?? "application/json"
				: "application/json",
			MaxOutputBytes = StepBaseFields.ParseMaxOutputBytes(root, baseFields.Name),
		};
	}
}
