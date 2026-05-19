namespace Orchestra.Engine;

/// <summary>
/// Step that invokes another orchestration registered in the host. The invocation is
/// performed by an <see cref="IChildOrchestrationLauncher"/> supplied to the executor.
/// </summary>
/// <remarks>
/// <para>
/// Single-child YAML / JSON shape:
/// </para>
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
/// <para>
/// Fan-out (forEach) YAML / JSON shape — launches one child per element in a JSON array
/// emitted by a prior step. Eliminates the need to drive fan-out via an LLM Prompt step.
/// </para>
/// <code>
/// - name: dispatch-processor
///   type: Orchestration
///   orchestration: meeting-action-items-processor
///   forEach: "{{filter-already-processed.output}}"   # template that resolves to a JSON array
///   forEachPath: meetingsToProcess                   # optional, drill into a JSON property
///   itemParameter: meetingData                       # child parameter name carrying the raw item JSON
///   parameters:
///     actionItemsDir: "{{vars.actionItemsDir}}"
///     forceReprocess: "{{param.forceReprocess}}"
///   mode: sync
///   maxConcurrency: 4                                # optional; default unbounded
///   continueOnItemFailure: true                      # optional; default true
///   timeoutSeconds: 3000                             # per-child timeout (sync only)
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
	/// In forEach mode, these are passed to EVERY child invocation (alongside the per-item
	/// payload bound to <see cref="ItemParameter"/>).
	/// Distinct from the base <see cref="OrchestrationStep.Parameters"/> array, which lists the
	/// names of orchestration parameters this step references for static analysis.
	/// </summary>
	public Dictionary<string, string> ChildParameters { get; init; } = [];

	/// <summary>
	/// Sync (block until child completes) or Async (dispatch and return). Defaults to <see cref="OrchestrationInvocationMode.Sync"/>.
	/// In forEach mode, Async means each child is dispatched without waiting; Sync waits for all.
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

	/// <summary>
	/// When set, the step launches one child per element in the resolved JSON array. The
	/// template is evaluated with the standard <see cref="TemplateResolver"/> machinery, so
	/// it can reference any prior step's output (e.g. <c>"{{filter-already-processed.output}}"</c>).
	/// The resolved string MUST parse as a JSON array — or, when <see cref="ForEachPath"/>
	/// is set, as a JSON object containing the array at that path.
	/// Null = single-child invocation (backward compatible).
	/// </summary>
	public string? ForEach { get; init; }

	/// <summary>
	/// Optional dotted JSON path used to extract the items array out of <see cref="ForEach"/>
	/// when the template resolves to a JSON object rather than directly to an array. E.g.
	/// when the prior step's output is <c>{"meetingsToProcess":[...]}</c>, set this to
	/// <c>meetingsToProcess</c>.
	/// </summary>
	public string? ForEachPath { get; init; }

	/// <summary>
	/// Required when <see cref="ForEach"/> is set: the name of the child orchestration
	/// parameter that carries the raw per-item JSON. The item is serialized as a compact
	/// JSON string and assigned to this parameter for every child invocation, so the child
	/// orchestration can <c>ConvertFrom-Json</c> it inside its own normalize step.
	/// </summary>
	public string? ItemParameter { get; init; }

	/// <summary>
	/// Maximum number of children to run concurrently in forEach mode. Null = unbounded.
	/// Honored only in Sync mode; Async dispatch is fire-and-forget.
	/// </summary>
	public int? MaxConcurrency { get; init; }

	/// <summary>
	/// In forEach + Sync mode, controls how child failures propagate. When true (default),
	/// every dispatched child is awaited and its result captured into the step output even
	/// if some failed; the step succeeds as long as the launcher itself did not error.
	/// When false, the first failed child causes the step to fail (other children still run
	/// to completion in the background but their results are not surfaced).
	/// </summary>
	public bool ContinueOnItemFailure { get; init; } = true;
}
