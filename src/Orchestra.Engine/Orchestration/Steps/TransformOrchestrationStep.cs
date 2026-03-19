namespace Orchestra.Engine;

/// <summary>
/// A step that transforms input data using a template expression.
/// No LLM call is made — this step applies string interpolation with
/// {{stepName.output}} and {{param.name}} syntax to produce its output.
/// Useful for combining outputs, formatting data, or building payloads.
/// </summary>
public class TransformOrchestrationStep : OrchestrationStep
{
	/// <summary>
	/// The template string to evaluate. Supports:
	/// - {{stepName.output}} — output from a dependency step
	/// - {{param.name}} — parameter value
	/// - {{stepName.rawOutput}} — raw (unprocessed) output from a dependency step
	/// </summary>
	public required string Template { get; init; }

	/// <summary>
	/// Optional content type hint for downstream steps (e.g., "application/json", "text/plain").
	/// Defaults to "text/plain".
	/// </summary>
	public string ContentType { get; init; } = "text/plain";
}
