namespace Orchestra.Engine;

/// <summary>
/// A step that makes an HTTP request to an external API or service.
/// The response body is captured as the step output.
/// Supports templated URL, headers, and body with {{stepName.output}} and {{param.name}} syntax.
/// </summary>
public class HttpOrchestrationStep : OrchestrationStep
{
	/// <summary>
	/// The HTTP method (GET, POST, PUT, PATCH, DELETE). Defaults to GET when not specified.
	/// </summary>
	public string Method { get; init; } = "GET";

	/// <summary>
	/// The URL to send the request to. Supports template expressions.
	/// </summary>
	public required string Url { get; init; }

	/// <summary>
	/// Optional HTTP headers to include in the request.
	/// Values support template expressions.
	/// </summary>
	public Dictionary<string, string> Headers { get; init; } = [];

	/// <summary>
	/// Optional request body. Supports template expressions.
	/// Sent as-is with the Content-Type determined by the ContentType property.
	/// </summary>
	public string? Body { get; init; }

	/// <summary>
	/// Content type for the request body. Defaults to "application/json".
	/// </summary>
	public string ContentType { get; init; } = "application/json";
}
