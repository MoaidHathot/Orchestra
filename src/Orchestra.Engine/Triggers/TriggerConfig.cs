namespace Orchestra.Engine;

/// <summary>
/// Base class for all trigger configurations.
/// A trigger defines how an orchestration is automatically started.
/// </summary>
public abstract class TriggerConfig
{
	public required TriggerType Type { get; init; }

	/// <summary>
	/// Whether the trigger is enabled. Defaults to true.
	/// Allows users to define a trigger but temporarily disable it.
	/// </summary>
	public bool Enabled { get; init; } = true;

	/// <summary>
	/// An optional prompt template that instructs an LLM to transform raw trigger input
	/// (webhook body, manual parameters, etc.) into the orchestration's expected parameter format.
	/// The raw input is appended to this prompt, and the LLM's JSON response becomes the parameters.
	/// </summary>
	public string? InputHandlerPrompt { get; init; }

	/// <summary>
	/// The model to use for the input handler LLM call. If null, falls back to the
	/// global default model configured in orchestra.json.
	/// </summary>
	public string? InputHandlerModel { get; init; }
}
