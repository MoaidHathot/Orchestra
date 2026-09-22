namespace Orchestra.Engine;

/// <summary>
/// Reasoning effort requested from the model for a Prompt step. Forwarded verbatim (lower-cased)
/// as the provider's <c>reasoning_effort</c>; each model advertises the subset it accepts
/// (e.g. GPT and Claude Opus 4.7+ models accept all five, older models stop at <see cref="High"/>).
/// </summary>
public enum ReasoningLevel
{
	Low,
	Medium,
	High,
	/// <summary>Extra-high effort (<c>xhigh</c>).</summary>
	XHigh,
	/// <summary>Maximum effort the model supports (<c>max</c>).</summary>
	Max,
}
