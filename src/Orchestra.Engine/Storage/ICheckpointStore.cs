namespace Orchestra.Engine;

/// <summary>
/// Abstraction for persisting and retrieving orchestration execution checkpoints.
/// Checkpoints capture the state of completed steps so that an interrupted execution
/// can be resumed from where it left off.
/// </summary>
public interface ICheckpointStore
{
	/// <summary>
	/// Saves a checkpoint for an in-progress execution.
	/// Called after each step completes successfully.
	/// Implementations should overwrite any previous checkpoint for the same run.
	/// </summary>
	Task SaveCheckpointAsync(CheckpointData checkpoint, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads the most recent checkpoint for a given run.
	/// Returns null if no checkpoint exists for the specified run.
	/// </summary>
	Task<CheckpointData?> LoadCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Deletes the checkpoint for a completed or abandoned run.
	/// Called when an orchestration completes (successfully or otherwise) to clean up.
	/// </summary>
	Task DeleteCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Lists all available checkpoints, optionally filtered by orchestration name.
	/// Useful for discovering incomplete runs that can be resumed.
	/// </summary>
	Task<IReadOnlyList<CheckpointData>> ListCheckpointsAsync(string? orchestrationName = null, CancellationToken cancellationToken = default);
}
