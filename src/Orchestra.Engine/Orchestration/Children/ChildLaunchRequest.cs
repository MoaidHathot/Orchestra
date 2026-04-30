namespace Orchestra.Engine;

/// <summary>
/// Request describing how a child orchestration should be launched.
/// Passed to <see cref="IChildOrchestrationLauncher.LaunchAsync"/>.
/// </summary>
public sealed class ChildLaunchRequest
{
	/// <summary>
	/// The orchestration ID (registry key) of the child to invoke.
	/// </summary>
	public required string OrchestrationId { get; init; }

	/// <summary>
	/// Optional explicit path to the orchestration file. When set, the launcher parses this
	/// file directly instead of resolving the path through the orchestration registry. This
	/// is the path that triggers (which hold their own path independently of the registry)
	/// pass through. Other callers leave this null and the launcher resolves the path via
	/// the registry's <c>Get</c> lookup.
	/// </summary>
	public string? OrchestrationPath { get; init; }

	/// <summary>
	/// Optional parameters for the child orchestration. Values are validated against
	/// the child's declared <c>inputs</c> by the engine.
	/// </summary>
	public Dictionary<string, string>? Parameters { get; init; }

	/// <summary>
	/// Whether the caller wants to await completion or fire-and-forget.
	/// In both modes the launcher returns a <see cref="ChildOrchestrationHandle"/> as soon
	/// as the child is registered; the handle's <c>Completion</c> task represents the run.
	/// </summary>
	public ChildLaunchMode Mode { get; init; } = ChildLaunchMode.Sync;

	/// <summary>
	/// Optional hard timeout (in seconds) applied on top of the orchestration's own timeout.
	/// In sync mode the launcher links this into the run's cancellation token. In async mode
	/// this value is ignored — only the orchestration's own <c>timeoutSeconds</c> applies.
	/// </summary>
	public int? TimeoutSeconds { get; init; }

	/// <summary>
	/// Free-form label describing what triggered this run (e.g. "manual", "mcp", "scheduler").
	/// Stored on <c>ActiveExecutionInfo</c> and the persisted run record.
	/// </summary>
	public string TriggeredBy { get; init; } = "manual";

	/// <summary>
	/// Optional trigger ID for runs initiated by a registered trigger.
	/// </summary>
	public string? TriggerId { get; init; }

	/// <summary>
	/// Lineage of the parent execution. When set, the child is recorded as a nested run and
	/// its cancellation token is linked to the parent's so cancelling the parent cancels the child.
	/// </summary>
	public ParentExecutionContext? ParentContext { get; init; }

	/// <summary>
	/// Free-form metadata stored alongside the active execution record. Useful for correlation
	/// IDs, ticket numbers, etc.
	/// </summary>
	public Dictionary<string, string>? UserMetadata { get; init; }

	/// <summary>
	/// Optional pre-execution parameter transform. Invoked by the executor inside the run scope
	/// (so the transform shares the orchestration's CLI process). When non-null and it returns a
	/// non-null dictionary, the returned values replace the supplied <see cref="Parameters"/>.
	/// </summary>
	public Func<CancellationToken, Task<Dictionary<string, string>?>>? PreExecutionParameterTransform { get; init; }

	/// <summary>
	/// Optional reporter to use for this run. When null, the launcher creates one via the registered
	/// <see cref="IOrchestrationReporterFactory"/>. Callers that need to subscribe before any events
	/// fire should construct the reporter themselves and pass it here.
	/// </summary>
	public IOrchestrationReporter? Reporter { get; init; }
}
