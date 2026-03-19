using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Http step type JSON.
/// Handles deserialization of <see cref="HttpOrchestrationStep"/> from orchestration JSON.
/// </summary>
public sealed class HttpStepTypeParser : IStepTypeParser
{
	public string TypeName => "Http";

	public OrchestrationStep Parse(JsonElement root)
	{
		return new HttpOrchestrationStep
		{
			Name = root.GetProperty("name").GetString()!,
			Type = OrchestrationStepType.Http,
			DependsOn = root.TryGetProperty("dependsOn", out var deps)
				? deps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
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
