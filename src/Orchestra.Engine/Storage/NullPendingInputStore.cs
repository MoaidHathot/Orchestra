namespace Orchestra.Engine;

/// <summary>
/// Null implementation of <see cref="IPendingInputStore"/>. Discards saves and returns
/// empty results for reads. Used by tests and by the engine when no host is wired up.
/// </summary>
public sealed class NullPendingInputStore : IPendingInputStore
{
	public static readonly NullPendingInputStore Instance = new();

	public Task SaveAsync(PendingInputRecord record, CancellationToken cancellationToken = default)
		=> Task.CompletedTask;

	public Task<PendingInputRecord?> GetAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
		=> Task.FromResult<PendingInputRecord?>(null);

	public Task<IReadOnlyList<PendingInputRecord>> ListAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<PendingInputRecord>>([]);

	public Task DeleteAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
		=> Task.CompletedTask;
}
