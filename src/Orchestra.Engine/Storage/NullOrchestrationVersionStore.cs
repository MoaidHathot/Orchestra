namespace Orchestra.Engine;

/// <summary>
/// A no-op version store that discards all snapshots. Used when no version persistence is configured.
/// </summary>
public class NullOrchestrationVersionStore : IOrchestrationVersionStore
{
	public static readonly NullOrchestrationVersionStore Instance = new();

	public Task SaveVersionAsync(string orchestrationId, OrchestrationVersionEntry version, string orchestrationJson, CancellationToken cancellationToken = default)
		=> Task.CompletedTask;

	public Task<IReadOnlyList<OrchestrationVersionEntry>> ListVersionsAsync(string orchestrationId, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<OrchestrationVersionEntry>>([]);

	public Task<string?> GetSnapshotAsync(string orchestrationId, string contentHash, CancellationToken cancellationToken = default)
		=> Task.FromResult<string?>(null);

	public Task<OrchestrationVersionEntry?> GetLatestVersionAsync(string orchestrationId, CancellationToken cancellationToken = default)
		=> Task.FromResult<OrchestrationVersionEntry?>(null);

	public Task DeleteAllVersionsAsync(string orchestrationId, CancellationToken cancellationToken = default)
		=> Task.CompletedTask;
}
