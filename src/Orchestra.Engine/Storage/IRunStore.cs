namespace Orchestra.Engine;

/// <summary>
/// Abstraction for persisting and retrieving orchestration run records.
/// Implementations determine where and how records are stored (file system, database, etc.).
/// </summary>
public interface IRunStore
{
	/// <summary>
	/// Saves a complete orchestration run record.
	/// </summary>
	Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default);

	/// <summary>
	/// Lists all run records for a specific orchestration, ordered by most recent first.
	/// </summary>
	Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(string orchestrationName, int? limit = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Lists all run records across all orchestrations, ordered by most recent first.
	/// </summary>
	Task<IReadOnlyList<OrchestrationRunRecord>> ListAllRunsAsync(int? limit = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Lists all run records for a specific trigger, ordered by most recent first.
	/// </summary>
	Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsByTriggerAsync(string triggerId, int? limit = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets a specific run record by orchestration name and run ID.
	/// </summary>
	Task<OrchestrationRunRecord?> GetRunAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes a specific run record by orchestration name and run ID.
	/// </summary>
	/// <returns>True if the record was deleted, false if it was not found.</returns>
	Task<bool> DeleteRunAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default);
}
