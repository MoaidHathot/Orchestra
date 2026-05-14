namespace Orchestra.Engine;

/// <summary>
/// Categorizes why an orchestration run was cancelled.
/// </summary>
public enum CancellationCauseKind
{
	/// <summary>
	/// The cause could not be determined.
	/// </summary>
	Unknown = 0,

	/// <summary>
	/// Cancellation was requested by the caller via the external <see cref="CancellationToken"/>
	/// (for example a user pressed Ctrl+C, the host shut down, or an API client cancelled the request).
	/// </summary>
	External = 1,

	/// <summary>
	/// The orchestration's own <c>timeoutSeconds</c> elapsed before the run finished.
	/// </summary>
	OrchestrationTimeout = 2,

	/// <summary>
	/// A sync invocation wrapper around the engine (typically the MCP data-plane
	/// <c>orchestra_run_orchestration</c> tool with <c>mode: sync</c>) hit its hard timeout
	/// before the engine completed. The engine's own <c>timeoutSeconds</c> did not fire.
	/// </summary>
	SyncInvokeTimeout = 3,

	/// <summary>
	/// A step explicitly requested early completion via the <c>orchestra_complete</c> engine tool.
	/// </summary>
	OrchestrationComplete = 4,

	/// <summary>
	/// The host process is stopping while the orchestration is still running. This is treated
	/// as an interruption, not a user-requested cancellation, so a durable checkpoint can be resumed.
	/// </summary>
	HostShutdown = 5,

	/// <summary>
	/// A human-in-the-loop wait timed out before a response was received. Set on Approval
	/// steps and engine-tool waits when the configured per-step or per-orchestration
	/// timeout fired while a <see cref="PendingInputRecord"/> was outstanding.
	/// </summary>
	AwaitingInputTimeout = 6,

	/// <summary>
	/// The host process was shut down while a human-in-the-loop wait was outstanding for
	/// an engine-tool request (LLM-decided pause). The agent session is in-memory and
	/// cannot be re-attached, so the run is marked failed; authors retry from the
	/// previous step's checkpoint.
	/// </summary>
	HostShutdownDuringWait = 7,

	/// <summary>
	/// The MCP transport layer aborted the request that launched (or was awaiting) this run.
	/// This is effectively a transport-layer timeout: the engine's own <c>timeoutSeconds</c>
	/// and <see cref="SyncInvokeTimeout"/> did not fire, but the upstream MCP client closed
	/// its HTTP/SSE request to <c>/mcp/data</c> (usually because <c>mcps[].timeoutSeconds</c>
	/// on the calling orchestration is smaller than the sync <c>timeoutSeconds</c> argument
	/// passed to <c>invoke_orchestration</c>). Distinct from <see cref="External"/> so
	/// authors can recognise a structural transport mismatch instead of treating the cancel
	/// as a user action.
	/// </summary>
	McpRequestAborted = 8,

	/// <summary>
	/// The orchestration YAML/JSON file backing this run was changed on disk while it was
	/// running, and the host's file-watcher cancelled the in-flight execution so the new
	/// definition can be loaded. Distinct from <see cref="External"/> so dashboards can
	/// surface a hot-reload separately from a user-driven cancel.
	/// </summary>
	ConfigReload = 9,
}

/// <summary>
/// Summary of the orchestration's progress at the moment cancellation was applied.
/// Persisted on cancelled run records so callers can immediately see whether the run was
/// "almost complete" without having to inspect every per-step record.
/// </summary>
public sealed class CancellationProgressSummary
{
	/// <summary>Total number of steps declared on the orchestration.</summary>
	public required int TotalSteps { get; init; }

	/// <summary>Number of steps that finished with <see cref="ExecutionStatus.Succeeded"/>.</summary>
	public required int StepsCompleted { get; init; }

	/// <summary>Number of steps that ended in <see cref="ExecutionStatus.Cancelled"/>.</summary>
	public required int StepsCancelled { get; init; }

	/// <summary>Number of steps that ended in <see cref="ExecutionStatus.Failed"/>.</summary>
	public required int StepsFailed { get; init; }

	/// <summary>Number of steps that ended in <see cref="ExecutionStatus.Skipped"/> or <see cref="ExecutionStatus.NoAction"/>.</summary>
	public required int StepsSkippedOrNoAction { get; init; }

	/// <summary>Number of steps that have no execution record at all (never reached).</summary>
	public required int StepsNotStarted { get; init; }

	/// <summary>The name of the most recently completed step, or null if none.</summary>
	public string? LastCompletedStep { get; init; }

	/// <summary>The completion timestamp of <see cref="LastCompletedStep"/>, or null.</summary>
	public DateTimeOffset? LastCompletedAt { get; init; }

	/// <summary>
	/// Names of steps that ended in <see cref="ExecutionStatus.Cancelled"/>. Includes both
	/// steps that were actively running when cancellation hit and steps that cascaded
	/// (their dependency cancelled, so they never produced their own success). Both are
	/// useful diagnostic signals for "what did not complete".
	/// </summary>
	public IReadOnlyList<string> CancelledSteps { get; init; } = [];
}

/// <summary>
/// Structured description of why an orchestration run ended in <see cref="ExecutionStatus.Cancelled"/>.
/// Persisted on <see cref="OrchestrationRunRecord"/> and surfaced via API/SSE/MCP responses so
/// callers can distinguish a user cancel from a timeout (and which timeout) without inspecting timestamps.
/// </summary>
public sealed class CancellationDetails
{
	/// <summary>
	/// What caused the cancellation.
	/// </summary>
	public required CancellationCauseKind Kind { get; init; }

	/// <summary>
	/// The configured timeout in seconds when <see cref="Kind"/> is one of the timeout variants.
	/// Null for non-timeout causes.
	/// </summary>
	public int? TimeoutSeconds { get; init; }

	/// <summary>
	/// Free-form short identifier of the cancellation source for diagnostics
	/// (e.g. <c>"orchestration"</c>, <c>"sync-invoke"</c>, <c>"caller"</c>, <c>"orchestra_complete"</c>).
	/// </summary>
	public string? Source { get; init; }

	/// <summary>
	/// Optional caller-supplied detail (e.g. the reason passed to <c>orchestra_complete</c>).
	/// </summary>
	public string? Detail { get; init; }

	/// <summary>
	/// When set, the wall-clock moment at which cancellation was requested by the source
	/// (e.g. the moment <c>cts.Cancel()</c> was called by an API handler or MCP tool).
	/// Distinct from when the engine actually observed the token and stopped, which is
	/// reflected in <see cref="OrchestrationRunRecord.CompletedAt"/>. Null on legacy
	/// records where the source did not capture it.
	/// </summary>
	public DateTimeOffset? RequestedAt { get; init; }

	/// <summary>
	/// Snapshot of step progress at the moment cancellation was applied. Helps diagnostics
	/// distinguish "cancelled almost immediately" from "cancelled with N of M steps done".
	/// </summary>
	public CancellationProgressSummary? Progress { get; init; }

	/// <summary>
	/// A short human-readable summary (e.g. <c>"timed out after 1800s (sync-invoke)"</c>).
	/// Suitable for embedding in error messages and run summaries.
	/// </summary>
	public string Reason
	{
		get
		{
			return Kind switch
			{
				CancellationCauseKind.OrchestrationTimeout =>
					TimeoutSeconds is { } s
						? $"orchestration timed out after {s}s"
						: "orchestration timed out",
				CancellationCauseKind.SyncInvokeTimeout =>
					TimeoutSeconds is { } s
						? $"sync invocation timed out after {s}s"
						: "sync invocation timed out",
				CancellationCauseKind.External =>
					!string.IsNullOrWhiteSpace(Detail) ? $"cancelled by caller: {Detail}" : "cancelled by caller",
				CancellationCauseKind.HostShutdown =>
					!string.IsNullOrWhiteSpace(Detail) ? $"interrupted by host shutdown: {Detail}" : "interrupted by host shutdown",
				CancellationCauseKind.OrchestrationComplete =>
					!string.IsNullOrWhiteSpace(Detail)
						? $"completed early: {Detail}"
						: "completed early via orchestra_complete",
				CancellationCauseKind.AwaitingInputTimeout =>
					TimeoutSeconds is { } s
						? $"awaiting-input timed out after {s}s without a response"
						: "awaiting-input timed out without a response",
				CancellationCauseKind.HostShutdownDuringWait =>
					!string.IsNullOrWhiteSpace(Detail)
						? $"host shutdown while awaiting input: {Detail}"
						: "host shutdown while awaiting input",
				CancellationCauseKind.McpRequestAborted =>
					TimeoutSeconds is { } mcpSecs
						? !string.IsNullOrWhiteSpace(Detail)
							? $"MCP transport request aborted after {mcpSecs}s: {Detail}"
							: $"MCP transport request aborted after {mcpSecs}s"
						: !string.IsNullOrWhiteSpace(Detail)
							? $"MCP transport request aborted: {Detail}"
							: "MCP transport request aborted",
				CancellationCauseKind.ConfigReload =>
					!string.IsNullOrWhiteSpace(Detail)
						? $"orchestration definition reloaded: {Detail}"
						: "orchestration definition reloaded",
				CancellationCauseKind.Unknown => "cancelled",
				_ => "cancelled",
			};
		}
	}

	/// <summary>
	/// True when <see cref="Kind"/> is any of the timeout variants.
	/// </summary>
	public bool IsTimeout => Kind is CancellationCauseKind.OrchestrationTimeout
		or CancellationCauseKind.SyncInvokeTimeout
		or CancellationCauseKind.AwaitingInputTimeout
		or CancellationCauseKind.McpRequestAborted;

	/// <summary>
	/// Convenience constructor for an orchestration-level timeout.
	/// </summary>
	public static CancellationDetails OrchestrationTimeout(int timeoutSeconds) => new()
	{
		Kind = CancellationCauseKind.OrchestrationTimeout,
		TimeoutSeconds = timeoutSeconds,
		Source = "orchestration",
	};

	/// <summary>
	/// Convenience constructor for a sync invocation timeout (typically an MCP data-plane sync call).
	/// </summary>
	public static CancellationDetails SyncInvokeTimeout(int timeoutSeconds, string? source = null) => new()
	{
		Kind = CancellationCauseKind.SyncInvokeTimeout,
		TimeoutSeconds = timeoutSeconds,
		Source = source ?? "sync-invoke",
	};

	/// <summary>
	/// Convenience constructor for caller-driven cancellation.
	/// </summary>
	public static CancellationDetails External(string? detail = null) => new()
	{
		Kind = CancellationCauseKind.External,
		Source = "caller",
		Detail = detail,
	};

	/// <summary>
	/// Convenience constructor for host-process shutdown interruption.
	/// </summary>
	public static CancellationDetails HostShutdown(string? detail = null) => new()
	{
		Kind = CancellationCauseKind.HostShutdown,
		Source = "host-shutdown",
		Detail = detail,
	};

	/// <summary>
	/// Convenience constructor for early completion via the <c>orchestra_complete</c> engine tool.
	/// </summary>
	public static CancellationDetails OrchestrationComplete(string? reason = null, string? completedByStep = null) => new()
	{
		Kind = CancellationCauseKind.OrchestrationComplete,
		Source = completedByStep is null ? "orchestra_complete" : $"orchestra_complete:{completedByStep}",
		Detail = reason,
	};

	/// <summary>
	/// Convenience constructor for an awaiting-input timeout (Approval step or engine-tool wait).
	/// </summary>
	public static CancellationDetails AwaitingInputTimeout(int timeoutSeconds, string? stepName = null) => new()
	{
		Kind = CancellationCauseKind.AwaitingInputTimeout,
		TimeoutSeconds = timeoutSeconds,
		Source = stepName is null ? "awaiting-input" : $"awaiting-input:{stepName}",
	};

	/// <summary>
	/// Convenience constructor for a host shutdown that interrupted an outstanding
	/// engine-tool human-input wait. The agent session cannot be resumed, so the run
	/// is marked failed and must be retried from the previous step's checkpoint.
	/// </summary>
	public static CancellationDetails HostShutdownDuringWait(string? stepName = null, string? detail = null) => new()
	{
		Kind = CancellationCauseKind.HostShutdownDuringWait,
		Source = stepName is null ? "host-shutdown-during-wait" : $"host-shutdown-during-wait:{stepName}",
		Detail = detail,
	};

	/// <summary>
	/// Convenience constructor for cancellation caused by the MCP transport aborting the
	/// request that owned this run. Use this when the engine observes external cancellation
	/// originating from the <see cref="CancellationToken"/> parameter on a server-side MCP
	/// tool handler (typically because <c>mcps[].timeoutSeconds</c> on the calling
	/// orchestration is smaller than the sync <c>timeoutSeconds</c> argument).
	/// </summary>
	public static CancellationDetails McpRequestAborted(int? transportTimeoutSeconds = null, string? source = null, string? detail = null) => new()
	{
		Kind = CancellationCauseKind.McpRequestAborted,
		TimeoutSeconds = transportTimeoutSeconds,
		Source = source ?? "mcp-transport",
		Detail = detail,
	};

	/// <summary>
	/// Convenience constructor for cancellation caused by the orchestration's definition
	/// being reloaded on disk while the run was in flight.
	/// </summary>
	public static CancellationDetails ConfigReload(string? source = null, string? detail = null) => new()
	{
		Kind = CancellationCauseKind.ConfigReload,
		Source = source ?? "config-reload",
		Detail = detail,
	};

	public override string ToString() =>
		TimeoutSeconds is { } s
			? $"{Kind} ({Source ?? "?"}, {s}s)"
			: $"{Kind}{(Source is null ? "" : $" ({Source})")}";
}

/// <summary>
/// Resolves the cancellation cause when the engine observes an external cancellation
/// (i.e. its own <c>timeoutSeconds</c> did not fire). Wrappers around the engine — such as
/// <see cref="ChildOrchestrationLauncher"/>'s sync-invoke timeout — supply this delegate so
/// the engine can record a precise <see cref="CancellationDetails"/> on the run record
/// instead of a generic <see cref="CancellationCauseKind.External"/>.
/// </summary>
/// <returns>
/// The cause if the wrapper detects it owns the cancellation; otherwise <c>null</c>
/// (the engine will fall back to <see cref="CancellationCauseKind.External"/>).
/// </returns>
public delegate CancellationDetails? ResolveCancellationCauseDelegate();
