namespace Orchestra.Engine;

/// <summary>
/// A no-op run store that discards all records. Used when no persistence is configured.
/// </summary>
public class NullRunStore : IRunStore
{
	public static readonly NullRunStore Instance = new();

	public Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default)
		=> Task.CompletedTask;

	public Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(string orchestrationName, int? limit = null, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>([]);

	public Task<IReadOnlyList<OrchestrationRunRecord>> ListAllRunsAsync(int? limit = null, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>([]);

	public Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsByTriggerAsync(string triggerId, int? limit = null, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>([]);

	public Task<OrchestrationRunRecord?> GetRunAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
		=> Task.FromResult<OrchestrationRunRecord?>(null);
}
