namespace Orchestra.Engine;

/// <summary>
/// Verbosity of the model's reasoning summary surfaced during a Prompt step.
/// Maps onto the Copilot SDK's <c>ReasoningSummary</c> (none / concise / detailed).
/// When null, the provider/model default applies.
/// </summary>
public enum ReasoningSummaryLevel
{
	None,
	Concise,
	Detailed,
}
