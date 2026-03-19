namespace Orchestra.Engine;

/// <summary>
/// A no-op checkpoint store that discards all checkpoints. Used when no checkpoint persistence is configured.
/// </summary>
public class NullCheckpointStore : ICheckpointStore
{
	public static readonly NullCheckpointStore Instance = new();

	public Task SaveCheckpointAsync(CheckpointData checkpoint, CancellationToken cancellationToken = default)
		=> Task.CompletedTask;

	public Task<CheckpointData?> LoadCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
		=> Task.FromResult<CheckpointData?>(null);

	public Task DeleteCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
		=> Task.CompletedTask;

	public Task<IReadOnlyList<CheckpointData>> ListCheckpointsAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<CheckpointData>>([]);
}
