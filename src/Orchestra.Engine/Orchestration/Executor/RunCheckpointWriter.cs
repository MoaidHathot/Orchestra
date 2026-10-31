namespace Orchestra.Engine;

/// <summary>
/// Publishes only settled step results. A run has one writer, so overlapping completions
/// cannot replace a newer checkpoint with an older one. Completions arriving during I/O
/// share the next snapshot; every caller waits until its own revision has been persisted.
/// </summary>
internal sealed class RunCheckpointWriter(
	IReadOnlyDictionary<string, CheckpointStepResult>? restored,
	Func<Dictionary<string, CheckpointStepResult>, CancellationToken, Task> save) : IAsyncDisposable
{
	private readonly Lock _stateLock = new();
	private readonly SemaphoreSlim _saveGate = new(1, 1);
	private readonly Dictionary<string, CheckpointStepResult> _completed = restored is null ? [] : new(restored);
	private long _revision;
	private long _savedRevision;
	private int _savedCount;
	private bool _closed;

	public async Task<int?> SaveAsync(string stepName, ExecutionResult result,
		KeyValuePair<string, ExecutionResult>? revisedTarget, CancellationToken cancellationToken)
	{
		long requestedRevision;
		lock (_stateLock)
		{
			if (_closed)
				return null;
			var changed = Update(stepName, result);
			if (revisedTarget is { } target)
				changed |= Update(target.Key, target.Value);
			if (!changed)
				return null;
			requestedRevision = ++_revision;
		}

		await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Dictionary<string, CheckpointStepResult> snapshot;
			long snapshotRevision;
			lock (_stateLock)
			{
				if (_closed)
					return null;
				if (_savedRevision >= requestedRevision)
					return _savedCount;
				snapshot = new(_completed);
				snapshotRevision = _revision;
			}

			await save(snapshot, cancellationToken).ConfigureAwait(false);
			lock (_stateLock)
			{
				_savedRevision = snapshotRevision;
				_savedCount = snapshot.Count;
			}
			return snapshot.Count;
		}
		finally
		{
			_saveGate.Release();
		}
	}

	// Called only under _stateLock. Loop target/checker changes enter the same snapshot.
	private bool Update(string name, ExecutionResult result)
	{
		if (result.Status != ExecutionStatus.Succeeded)
			return _completed.Remove(name);
		_completed[name] = CheckpointStepResult.FromExecutionResult(result);
		return true;
	}

	public async Task CloseAsync()
	{
		lock (_stateLock)
			_closed = true;
		// Join an in-flight write before the owner deletes the checkpoint. A late worker
		// may call SaveAsync after closure, but can no longer recreate the run's checkpoint.
		await _saveGate.WaitAsync().ConfigureAwait(false);
		_saveGate.Release();
	}

	public ValueTask DisposeAsync() => new(CloseAsync());
}
