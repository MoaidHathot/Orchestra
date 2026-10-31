using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Workspaces;

public sealed record SetupModel(string Id, string Name);
public sealed record SetupMethod(string Id, string Type, string Label, JsonElement? Form = null);
public sealed record SetupIntegration(string Id, string Name, SetupMethod[] Methods, bool Connected);
public sealed record ProviderReadiness(string Provider, bool Installed, bool Ready, string Detail,
	SetupModel[] Models, SetupIntegration[] Integrations);
public sealed record ProviderOperation(string Action, string? IntegrationId = null, string? MethodId = null,
	string? Key = null, Dictionary<string, object?>? Answer = null, string? Model = null);
public sealed record SetupProgress(string Message, string? AuthorizationUrl = null, string? UserCode = null, bool RequiresCode = false);
public sealed record SetupJob(string Id, string Provider, string Action, string State, SetupProgress Progress,
	ProviderReadiness? Result = null, DateTimeOffset? FinishedAt = null);

public interface IProviderSetupBackend
{
	ProviderReadiness[] Inspect();
	Task<ProviderReadiness> ExecuteAsync(string provider, ProviderOperation operation, Action<SetupProgress> progress,
		Func<CancellationToken, Task<string>> readCode, CancellationToken token);
}

/// <summary>One bounded operation per provider, independently of HTTP request lifetime.</summary>
public sealed partial class ProviderSetupJobs(IProviderSetupBackend backend, IHostApplicationLifetime lifetime,
	ILogger<ProviderSetupJobs> logger) : IAsyncDisposable
{
	private sealed class Running(SetupJob state, CancellationTokenSource cancellation)
	{
		public SetupJob State = state;
		public CancellationTokenSource Cancellation = cancellation;
		public TaskCompletionSource<string> Code = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task Work = Task.CompletedTask;
	}
	private readonly ConcurrentDictionary<string, Running> _jobs = new();
	private readonly Lock _gate = new();
	private bool _disposed;
	private Task? _stopping;
	public object Inspect() => new { providers = backend.Inspect(), jobs = _jobs.Values.Select(j => j.State).ToArray() };
	public SetupJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? Volatile.Read(ref job.State) : null;
	public SetupJob Start(string provider, ProviderOperation operation)
	{
		if (provider is not ("copilot" or "opencode")) throw new ArgumentException("Unknown provider.");
		if (operation.Action is not ("check" or "install" or "login" or "test")) throw new ArgumentException("Unknown setup operation.");
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			var active = _jobs.Values.FirstOrDefault(j => j.State.Provider == provider && !j.Work.IsCompleted);
			if (active is not null) return active.State;
			foreach (var old in _jobs.Values.Where(j => j.Work.IsCompleted).OrderByDescending(j => j.State.FinishedAt).Skip(15).ToArray())
				if (_jobs.TryRemove(old.State.Id, out _)) old.Cancellation.Dispose();
			var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
			cts.CancelAfter(operation.Action == "login" ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(3));
			var state = new SetupJob(Guid.NewGuid().ToString("N"), provider, operation.Action, "running", new("Starting…"));
			var running = new Running(state, cts);
			_jobs[state.Id] = running;
			running.Work = Task.Run(async () =>
			{
				try
				{
					var result = await backend.ExecuteAsync(provider, operation,
						p => Volatile.Write(ref running.State, running.State with { Progress = p }),
						t => running.Code.Task.WaitAsync(t), cts.Token);
					running.State = running.State with { State = "succeeded", Result = result, Progress = new(result.Detail), FinishedAt = DateTimeOffset.UtcNow };
				}
				catch (OperationCanceledException)
				{ running.State = running.State with { State = "cancelled", Progress = new("Setup cancelled or timed out. You can retry."), FinishedAt = DateTimeOffset.UtcNow }; }
				catch (Exception ex)
				{
					var message = operation.Key is { Length: > 0 } key ? ex.Message.Replace(key, "[redacted]") : ex.Message;
					running.State = running.State with { State = "failed", Progress = new(message), FinishedAt = DateTimeOffset.UtcNow };
					LogFailed(provider, operation.Action); // Never log credentials or raw provider payloads.
				}
			});
			return state;
		}
	}
	public bool SubmitCode(string id, string code) => _jobs.TryGetValue(id, out var job) && job.State.Progress.RequiresCode && job.Code.TrySetResult(code);
	public bool Cancel(string id)
	{
		lock (_gate) { if (!_jobs.TryGetValue(id, out var job)) return false; job.Cancellation.Cancel(); return true; }
	}
	public ValueTask DisposeAsync()
	{
		lock (_gate) { _disposed = true; return new(_stopping ??= StopAsync()); }
	}
	private async Task StopAsync()
	{
		Running[] jobs;
		lock (_gate) { jobs = _jobs.Values.ToArray(); foreach (var job in jobs) job.Cancellation.Cancel(); }
		await Task.WhenAll(jobs.Select(j => j.Work));
		foreach (var job in jobs) job.Cancellation.Dispose();
	}
	[LoggerMessage(Level = LogLevel.Warning, Message = "Provider setup failed: {Provider}, {Operation}")]
	private partial void LogFailed(string provider, string operation);
}
