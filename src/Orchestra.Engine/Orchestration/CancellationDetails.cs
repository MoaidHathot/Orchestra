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
				CancellationCauseKind.OrchestrationComplete =>
					!string.IsNullOrWhiteSpace(Detail)
						? $"completed early: {Detail}"
						: "completed early via orchestra_complete",
				CancellationCauseKind.Unknown => "cancelled",
				_ => "cancelled",
			};
		}
	}

	/// <summary>
	/// True when <see cref="Kind"/> is any of the timeout variants.
	/// </summary>
	public bool IsTimeout => Kind is CancellationCauseKind.OrchestrationTimeout
		or CancellationCauseKind.SyncInvokeTimeout;

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
	/// Convenience constructor for early completion via the <c>orchestra_complete</c> engine tool.
	/// </summary>
	public static CancellationDetails OrchestrationComplete(string? reason = null, string? completedByStep = null) => new()
	{
		Kind = CancellationCauseKind.OrchestrationComplete,
		Source = completedByStep is null ? "orchestra_complete" : $"orchestra_complete:{completedByStep}",
		Detail = reason,
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
