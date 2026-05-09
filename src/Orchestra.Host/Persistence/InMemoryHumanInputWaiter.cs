using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Host.Persistence;

/// <summary>
/// In-memory implementation of <see cref="IHumanInputWaiter"/>. Maintains a
/// <see cref="TaskCompletionSource{TResult}"/> per outstanding (orchestrationName, runId,
/// stepName) tuple so the executor's wait can be completed by an HTTP handler thread.
/// Thread-safe; supports concurrent waits across runs.
/// </summary>
public sealed partial class InMemoryHumanInputWaiter : IHumanInputWaiter
{
	private readonly ConcurrentDictionary<string, WaitEntry> _waits = new(StringComparer.Ordinal);
	private readonly ILogger<InMemoryHumanInputWaiter> _logger;

	public InMemoryHumanInputWaiter(ILogger<InMemoryHumanInputWaiter> logger)
	{
		_logger = logger;
	}

	private static string Key(string orchestrationName, string runId, string stepName)
		=> $"{orchestrationName}|{runId}|{stepName}";

	public Task<UserInputResponse> WaitAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken)
	{
		var key = Key(orchestrationName, runId, stepName);
		var entry = _waits.AddOrUpdate(
			key,
			_ => new WaitEntry(),
			(_, existing) =>
			{
				// If the existing wait has already resolved, replace it with a fresh entry.
				// This handles the case where a previous response/cancellation has completed
				// the TCS but the cleanup continuation hasn't yet removed the dictionary entry.
				return existing.Tcs.Task.IsCompleted ? new WaitEntry() : existing;
			});

		// Wire cancellation: when the caller's token fires, complete the TCS with cancellation
		// and remove the entry so a subsequent response cannot resolve a dead wait.
		var registration = cancellationToken.Register(() =>
		{
			if (entry.Tcs.TrySetCanceled(cancellationToken))
			{
				_waits.TryRemove(key, out _);
				LogWaitCancelled(orchestrationName, runId, stepName);
			}
		});

		// Detach the registration when the TCS resolves, but make sure we always remove the
		// entry from the map so future Wait calls for the same key get a fresh TCS.
		_ = entry.Tcs.Task.ContinueWith(static (_, state) =>
		{
			var (waiter, removalKey, reg) = ((InMemoryHumanInputWaiter, string, CancellationTokenRegistration))state!;
			reg.Dispose();
			waiter._waits.TryRemove(removalKey, out WaitEntry? _);
		}, (this, key, registration), TaskContinuationOptions.ExecuteSynchronously);

		LogWaitRegistered(orchestrationName, runId, stepName);
		return entry.Tcs.Task;
	}

	public bool TryComplete(string orchestrationName, string runId, string stepName, UserInputResponse response)
	{
		var key = Key(orchestrationName, runId, stepName);
		if (!_waits.TryGetValue(key, out var entry))
		{
			LogCompleteMiss(orchestrationName, runId, stepName);
			return false;
		}

		if (entry.Tcs.TrySetResult(response))
		{
			LogWaitCompleted(orchestrationName, runId, stepName);
			return true;
		}

		return false;
	}

	public bool TryCancel(string orchestrationName, string runId, string stepName)
	{
		var key = Key(orchestrationName, runId, stepName);
		if (!_waits.TryGetValue(key, out var entry))
			return false;
		return entry.Tcs.TrySetCanceled();
	}

	public void BeginWait(string runId, string stepName) { }

	public void EndWait(string runId, string stepName) { }

	private sealed class WaitEntry
	{
		public TaskCompletionSource<UserInputResponse> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Human input wait registered for orchestration '{OrchestrationName}', run '{RunId}', step '{StepName}'.")]
	private partial void LogWaitRegistered(string orchestrationName, string runId, string stepName);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Human input wait completed for orchestration '{OrchestrationName}', run '{RunId}', step '{StepName}'.")]
	private partial void LogWaitCompleted(string orchestrationName, string runId, string stepName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Human input wait cancelled for orchestration '{OrchestrationName}', run '{RunId}', step '{StepName}'.")]
	private partial void LogWaitCancelled(string orchestrationName, string runId, string stepName);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Received user response but no in-process wait was found for orchestration '{OrchestrationName}', run '{RunId}', step '{StepName}'.")]
	private partial void LogCompleteMiss(string orchestrationName, string runId, string stepName);
}
