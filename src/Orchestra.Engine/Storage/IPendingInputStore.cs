namespace Orchestra.Engine;

/// <summary>
/// Persists outstanding human-in-the-loop wait records so they survive host restarts and so
/// the host's HumanInput API can route a response to the right run/step. Implementations
/// should be thread-safe for concurrent reads/writes — the engine writes from an executor
/// thread while the API serves reads/writes from request threads.
/// </summary>
public interface IPendingInputStore
{
	/// <summary>
	/// Saves or overwrites a pending input record. Called when an Approval step or an
	/// engine-tool wait begins.
	/// </summary>
	Task SaveAsync(PendingInputRecord record, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads the pending input record for a specific run + step, or returns <c>null</c>
	/// when none exists. Used by the response endpoint to validate the wait is still
	/// outstanding before completing it.
	/// </summary>
	Task<PendingInputRecord?> GetAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default);

	/// <summary>
	/// Lists all outstanding pending input records, optionally filtered by orchestration name.
	/// Used by the host's <c>/api/runs/pending</c> endpoint and by the startup scan that
	/// reconciles orphaned engine-tool waits after a host restart.
	/// </summary>
	Task<IReadOnlyList<PendingInputRecord>> ListAsync(string? orchestrationName = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes a pending input record once the wait is satisfied or abandoned. Idempotent —
	/// deleting a non-existent record returns silently.
	/// </summary>
	Task DeleteAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default);
}
