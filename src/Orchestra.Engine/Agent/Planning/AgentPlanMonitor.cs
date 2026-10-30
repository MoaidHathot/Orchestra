using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>Coalesces provider signals without blocking transport callbacks. Disposal takes a bounded final snapshot.</summary>
public sealed partial class AgentPlanMonitor : IAsyncDisposable
{
	private readonly Func<CancellationToken, Task<IReadOnlyList<AgentPlanSnapshot>>> _read;
	private readonly Action<AgentPlanSnapshot> _publish;
	private readonly ILogger _logger;
	private readonly Action<string>? _diagnostic;
	private readonly TimeSpan _readTimeout;
	private Task<IReadOnlyList<AgentPlanSnapshot>>? _pendingRead;
	private int _disposed;
	private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _worker;

	public AgentPlanMonitor(Func<CancellationToken, Task<IReadOnlyList<AgentPlanSnapshot>>> read, Action<AgentPlanSnapshot> publish, ILogger logger, Action<string>? diagnostic = null, TimeSpan? readTimeout = null)
	{
		_read = read;
		_publish = publish;
		_logger = logger;
		_diagnostic = diagnostic;
		_readTimeout = readTimeout ?? TimeSpan.FromSeconds(2);
		_worker = RunAsync();
		RequestRefresh();
	}

	public void RequestRefresh() => _signals.Writer.TryWrite(true);

	private async Task RunAsync()
	{
		try
		{
			await foreach (var _ in _signals.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
				await ReadOnceAsync(_stop.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
	}

	private async Task ReadOnceAsync(CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_readTimeout);
		try
		{
			// A non-cooperative RPC may still be running after the previous deadline. Do not
			// start overlapping reads, including during disposal, or publish its stale result.
			if (_pendingRead is { IsCompleted: false } previous)
			{
				try
				{ await previous.WaitAsync(timeout.Token).ConfigureAwait(false); }
				catch (Exception) when (previous.IsCompleted && !timeout.IsCancellationRequested) { }
			}
			_ = _pendingRead?.Exception;
			timeout.Token.ThrowIfCancellationRequested();
			_pendingRead = _read(timeout.Token);
			var plans = await _pendingRead.WaitAsync(timeout.Token).ConfigureAwait(false);
			foreach (var plan in plans)
				_publish(plan);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex) { LogReadFailed(_logger, ex); _diagnostic?.Invoke(ex.Message); }
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		_signals.Writer.TryComplete();
		await _stop.CancelAsync().ConfigureAwait(false);
		await _worker.ConfigureAwait(false);
		await ReadOnceAsync(CancellationToken.None).ConfigureAwait(false);
		_stop.Dispose();
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Provider plan refresh failed; retaining the last observed plan")]
	private static partial void LogReadFailed(ILogger logger, Exception exception);
}
