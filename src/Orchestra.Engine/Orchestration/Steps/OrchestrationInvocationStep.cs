namespace Orchestra.Engine;

/// <summary>
/// Step that invokes another orchestration registered in the host. The invocation is
/// performed by an <see cref="IChildOrchestrationLauncher"/> supplied to the executor.
/// </summary>
/// <remarks>
/// YAML / JSON shape:
/// <code>
/// - name: review-pr
///   type: Orchestration
///   orchestration: pr-code-reviewer        # required, supports template expressions
///   parameters:                            # optional, values support template expressions
///     prData: "{{fetch-pr-metadata.output}}"
///   mode: sync                             # sync (default) | async
///   inputHandlerPrompt: |                  # optional LLM-based parameter shaping
///     Take the raw inputs and produce a JSON object with prData as a JSON-stringified value.
///   inputHandlerModel: claude-opus-4.6     # optional, defaults to orchestration defaultModel
///   timeoutSeconds: 14400                  # optional caller-side hard cap (sync only)
/// </code>
/// </remarks>
public class OrchestrationInvocationStep : OrchestrationStep
{
	/// <summary>
	/// The orchestration ID (registry key) to invoke. Supports template expressions, so the
	/// child name can be selected at runtime: <c>orchestration: "{{decide-target.output}}"</c>.
	/// </summary>
	public required string OrchestrationName { get; init; }

	/// <summary>
	/// Parameters to pass to the child orchestration. Each value supports template expressions.
	/// Distinct from the base <see cref="OrchestrationStep.Parameters"/> array, which lists the
	/// names of orchestration parameters this step references for static analysis.
	/// </summary>
	public Dictionary<string, string> ChildParameters { get; init; } = [];

	/// <summary>
	/// Sync (block until child completes) or Async (dispatch and return). Defaults to <see cref="OrchestrationInvocationMode.Sync"/>.
	/// </summary>
	public OrchestrationInvocationMode Mode { get; init; } = OrchestrationInvocationMode.Sync;

	/// <summary>
	/// Optional LLM-based parameter transformation prompt. When set, the resolved parameter map
	/// is JSON-serialized and passed to a one-shot LLM agent along with this prompt; the LLM is
	/// expected to return a transformed JSON object that replaces the raw parameters before the
	/// child orchestration runs. Mirrors the trigger-side <c>inputHandlerPrompt</c> mechanism.
	/// On parse failure or empty result, the original parameters are used.
	/// </summary>
	public string? InputHandlerPrompt { get; init; }

	/// <summary>
	/// Model to use when running the input handler prompt. Falls back to the orchestration's
	/// <c>defaultModel</c> when null.
	/// </summary>
	public string? InputHandlerModel { get; init; }
}
