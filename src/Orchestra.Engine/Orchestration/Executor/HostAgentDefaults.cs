namespace Orchestra.Engine;

/// <summary>
/// Host-wide fallbacks for Prompt-step agent settings. The host loads these from its own
/// configuration (for example <c>defaultModel</c> / <c>defaultReasoningLevel</c> in
/// <c>orchestra.json</c>) and hands them to the <see cref="OrchestrationExecutor"/>, which
/// applies them only when neither the step nor the orchestration specifies a value.
/// </summary>
/// <remarks>
/// Resolution precedence per Prompt step:
/// <list type="number">
/// <item>the step's own <c>model</c> / <c>reasoningLevel</c></item>
/// <item>the orchestration's <c>defaultModel</c> / <c>defaultReasoningLevel</c></item>
/// <item>these host defaults</item>
/// <item>for the model: an error (a Prompt step must resolve to some model);
/// for the reasoning level: the provider/model default</item>
/// </list>
/// </remarks>
public sealed record HostAgentDefaults
{
	/// <summary>Shared instance with no fallbacks (the engine's historical behaviour).</summary>
	public static HostAgentDefaults None { get; } = new();

	/// <summary>Model applied to Prompt steps when neither the step nor the orchestration sets one.</summary>
	public string? Model { get; init; }

	/// <summary>
	/// Reasoning effort applied to Prompt steps when neither the step nor the orchestration sets one.
	/// </summary>
	public ReasoningLevel? ReasoningLevel { get; init; }

	/// <summary>
	/// Upper bound on steps executing at the same time within one run, applied when the
	/// orchestration does not set its own <c>maxConcurrency</c>. Null means unbounded (every
	/// ready step starts immediately). Approval steps and async Orchestration steps do not
	/// occupy a slot. Sourced from <c>defaultMaxConcurrency</c> in <c>orchestra.json</c>.
	/// </summary>
	public int? MaxConcurrency { get; init; }
}
