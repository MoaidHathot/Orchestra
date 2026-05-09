namespace Orchestra.Engine;

/// <summary>
/// Null implementation of <see cref="IHumanInputWaiter"/>. <see cref="WaitAsync"/> blocks
/// on the supplied cancellation token until the token fires (it never receives a response),
/// and <see cref="TryComplete"/> always returns false. Used by tests and by the engine
/// when no host waiter is wired up.
/// </summary>
public sealed class NullHumanInputWaiter : IHumanInputWaiter
{
	public static readonly NullHumanInputWaiter Instance = new();

	public Task<UserInputResponse> WaitAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken)
	{
		// Block until cancellation. Avoids returning silently which would let an Approval
		// step succeed with empty content even when no waiter is wired up.
		var tcs = new TaskCompletionSource<UserInputResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
		return tcs.Task.ContinueWith(t =>
		{
			registration.Dispose();
			return t.GetAwaiter().GetResult();
		}, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	public bool TryComplete(string orchestrationName, string runId, string stepName, UserInputResponse response) => false;

	public bool TryCancel(string orchestrationName, string runId, string stepName) => false;

	public void BeginWait(string runId, string stepName) { }

	public void EndWait(string runId, string stepName) { }
}
