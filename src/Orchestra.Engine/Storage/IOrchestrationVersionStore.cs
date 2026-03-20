namespace Orchestra.Engine;

/// <summary>
/// Abstraction for persisting and retrieving orchestration version history.
/// Each orchestration has a series of version snapshots identified by content hash.
/// Implementations determine storage (file system, database, etc.).
/// </summary>
public interface IOrchestrationVersionStore
{
	/// <summary>
	/// Saves a version snapshot. If a snapshot with the same content hash already exists
	/// for this orchestration, the call is idempotent (no duplicate is created).
	/// </summary>
	Task SaveVersionAsync(string orchestrationId, OrchestrationVersionEntry version, string orchestrationJson, CancellationToken cancellationToken = default);

	/// <summary>
	/// Lists all version snapshots for an orchestration, ordered by timestamp descending (newest first).
	/// </summary>
	Task<IReadOnlyList<OrchestrationVersionEntry>> ListVersionsAsync(string orchestrationId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the full orchestration JSON snapshot for a specific version by content hash.
	/// Returns null if the version is not found.
	/// </summary>
	Task<string?> GetSnapshotAsync(string orchestrationId, string contentHash, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the latest version entry for an orchestration.
	/// Returns null if no versions exist.
	/// </summary>
	Task<OrchestrationVersionEntry?> GetLatestVersionAsync(string orchestrationId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes all version history for an orchestration.
	/// </summary>
	Task DeleteAllVersionsAsync(string orchestrationId, CancellationToken cancellationToken = default);
}
