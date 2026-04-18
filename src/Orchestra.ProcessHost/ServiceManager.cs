using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Orchestra.ProcessHost;

/// <summary>
/// Manages the lifecycle of external processes and command hooks defined in
/// <c>orchestra.services.json</c>. Starts long-running processes, monitors them
/// with configurable restart policies, runs one-shot commands at lifecycle
/// boundaries, and shuts everything down gracefully.
/// </summary>
public partial class ServiceManager : IAsyncDisposable
{
	private readonly ILogger<ServiceManager> _logger;
	private readonly ProcessTracker? _processTracker;
	private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new();
	private readonly ConcurrentDictionary<string, Task> _restartLoops = new();
	private readonly List<CommandHook> _beforeStartHooks = [];
	private readonly List<CommandHook> _afterStopHooks = [];
	private CancellationTokenSource? _shutdownCts;
	private bool _initialized;
	private bool _stopped;

	/// <summary>
	/// Maximum restart backoff delay in seconds.
	/// </summary>
	private const int MaxBackoffSeconds = 30;

	public ServiceManager(ILogger<ServiceManager> logger, ProcessTracker? processTracker = null)
	{
		_logger = logger;
		_processTracker = processTracker;
	}

	/// <summary>
	/// Gets all currently managed processes.
	/// </summary>
	public IReadOnlyDictionary<string, ManagedProcess> Processes => _processes;

	/// <summary>
	/// Gets whether the manager has been initialized.
	/// </summary>
	public bool IsInitialized => _initialized;

	/// <summary>
	/// Initializes the service manager with the given service entries.
	/// Runs beforeStart hooks, then starts long-running processes.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown if called more than once.</exception>
	/// <exception cref="ServiceInitializationException">Thrown if a required beforeStart hook fails or a required process fails readiness.</exception>
	public async Task InitializeAsync(ServiceEntry[] entries, CancellationToken cancellationToken = default)
	{
		if (_initialized)
			throw new InvalidOperationException("ServiceManager has already been initialized.");

		_initialized = true;
		_shutdownCts = new CancellationTokenSource();

		// Clean up orphaned processes from a previous session before starting anything.
		// This handles the case where Orchestra crashed without stopping its managed processes.
		_processTracker?.CleanupOrphans();

		if (entries.Length == 0)
		{
			LogNoServicesConfigured();
			return;
		}

		// Validate no duplicate names
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in entries)
		{
			if (!names.Add(entry.Name))
				throw new ArgumentException($"Duplicate service name '{entry.Name}'.");
		}

		// Categorize entries
		var processServices = new List<ProcessService>();
		foreach (var entry in entries)
		{
			switch (entry)
			{
				case CommandHook hook when hook.RunAt == HookPhase.BeforeStart:
					_beforeStartHooks.Add(hook);
					break;
				case CommandHook hook when hook.RunAt == HookPhase.AfterStop:
					_afterStopHooks.Add(hook);
					break;
				case ProcessService process:
					processServices.Add(process);
					break;
			}
		}

		LogServiceManagerInitializing(
			processServices.Count,
			_beforeStartHooks.Count,
			_afterStopHooks.Count);

		// 1. Run beforeStart hooks sequentially
		await RunBeforeStartHooksAsync(cancellationToken);

		// 2. Start all processes in parallel
		await StartProcessesAsync(processServices, cancellationToken);

		LogServiceManagerInitialized(
			_processes.Count,
			_beforeStartHooks.Count + _afterStopHooks.Count);
	}

	/// <summary>
	/// Runs all beforeStart hooks in order. Throws if a required hook fails.
	/// Links the shutdown token so that <see cref="StopAsync"/> cancels running hooks.
	/// </summary>
	private async Task RunBeforeStartHooksAsync(CancellationToken cancellationToken)
	{
		// Link the external token with the shutdown token so that either Ctrl+C
		// (via the external token) or StopAsync (via _shutdownCts) will cancel hooks.
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken, _shutdownCts!.Token);

		foreach (var hook in _beforeStartHooks)
		{
			LogRunningBeforeStartHook(hook.Name, hook.Command);
			var (exitCode, stderr) = await RunCommandAsync(hook, linkedCts.Token);

			if (exitCode == 0)
			{
				LogBeforeStartHookCompleted(hook.Name, exitCode);
			}
			else if (linkedCts.IsCancellationRequested)
			{
				// Shutdown was requested — don't throw, just stop processing hooks
				return;
			}
			else if (hook.Required)
			{
				LogBeforeStartHookFailed(hook.Name, exitCode, stderr);
				throw new ServiceInitializationException(
					$"Required beforeStart hook '{hook.Name}' failed with exit code {exitCode}: {stderr}");
			}
			else
			{
				LogBeforeStartHookFailedNonRequired(hook.Name, exitCode, stderr);
			}
		}
	}

	/// <summary>
	/// Starts all process services and waits for their readiness checks.
	/// </summary>
	private async Task StartProcessesAsync(List<ProcessService> processServices, CancellationToken cancellationToken)
	{
		var startTasks = new List<Task>();

		foreach (var config in processServices)
		{
			startTasks.Add(StartSingleProcessAsync(config, cancellationToken));
		}

		await Task.WhenAll(startTasks);
	}

	/// <summary>
	/// Starts a single process, waits for readiness, and sets up a restart loop if configured.
	/// </summary>
	private async Task StartSingleProcessAsync(ProcessService config, CancellationToken cancellationToken)
	{
		var managed = await CreateAndStartProcessAsync(config, cancellationToken);

		if (managed is null)
		{
			if (config.Required)
			{
				throw new ServiceInitializationException(
					$"Required process '{config.Name}' failed to start.");
			}
			return;
		}

		_processes[config.Name] = managed;

		// Track the process PID for orphan detection on future startups
		if (_processTracker is not null && managed.ProcessId is int pid)
			_processTracker.TrackProcess(config.Name, pid, config.Command);

		// Start restart loop if policy is not Never
		if (config.RestartPolicy != RestartPolicy.Never)
		{
			var loopTask = RestartLoopAsync(config, _shutdownCts!.Token);
			_restartLoops[config.Name] = loopTask;
		}
	}

	/// <summary>
	/// Creates and starts a <see cref="ManagedProcess"/> from a <see cref="ProcessService"/> config.
	/// Override this in tests to avoid real process spawning.
	/// </summary>
	internal virtual async Task<ManagedProcess?> CreateAndStartProcessAsync(
		ProcessService config, CancellationToken cancellationToken)
	{
		var managed = new ManagedProcess(config, _logger);
		LogStartingProcess(config.Name, config.Command);

		var started = await managed.StartAsync(cancellationToken);
		if (!started)
		{
			await managed.DisposeAsync();
			return null;
		}

		return managed;
	}

	/// <summary>
	/// Monitors a process and restarts it according to the configured restart policy
	/// with exponential backoff (1s, 2s, 4s, 8s... capped at 30s).
	/// </summary>
	private async Task RestartLoopAsync(ProcessService config, CancellationToken shutdownToken)
	{
		var attempt = 0;

		while (!shutdownToken.IsCancellationRequested)
		{
			// Wait for the current process to exit
			if (_processes.TryGetValue(config.Name, out var current) && !current.HasExited)
			{
				try
				{
					await current.WaitForExitAsync(shutdownToken);
				}
				catch (OperationCanceledException)
				{
					return; // Shutdown requested
				}
			}

			if (shutdownToken.IsCancellationRequested)
				return;

			// Check if we should restart
			var exitCode = current?.ExitCode ?? -1;
			var shouldRestart = config.RestartPolicy switch
			{
				RestartPolicy.Always => true,
				RestartPolicy.OnFailure => exitCode != 0,
				_ => false,
			};

			if (!shouldRestart)
			{
				if (exitCode != 0)
					LogProcessExitedWithError(config.Name, exitCode);
				else
					LogProcessExitedCleanly(config.Name);
				return;
			}

			// Exponential backoff
			attempt++;
			var delaySeconds = Math.Min((int)Math.Pow(2, attempt - 1), MaxBackoffSeconds);
			LogRestartingProcess(config.Name, attempt, delaySeconds);

			try
			{
				await Task.Delay(TimeSpan.FromSeconds(delaySeconds), shutdownToken);
			}
			catch (OperationCanceledException)
			{
				return; // Shutdown requested during backoff
			}

			// Dispose the old process
			if (current is not null)
				await current.DisposeAsync();

			// Start a new process
			var newProcess = await CreateAndStartProcessAsync(config, shutdownToken);
			if (newProcess is not null)
			{
				_processes[config.Name] = newProcess;

				// Track the restarted process PID for orphan detection
				if (_processTracker is not null && newProcess.ProcessId is int newPid)
					_processTracker.TrackProcess(config.Name, newPid, config.Command);

				attempt = 0; // Reset backoff on successful start
			}
			else
			{
				LogProcessRestartFailed(config.Name, attempt);
			}
		}
	}

	/// <summary>
	/// Runs a one-shot command hook and returns its exit code and stderr output.
	/// Kills the process tree on timeout or external cancellation (e.g., Ctrl+C shutdown).
	/// </summary>
	internal virtual async Task<(int ExitCode, string Stderr)> RunCommandAsync(
		CommandHook hook, CancellationToken cancellationToken)
	{
		var startInfo = BuildCommandStartInfo(hook);

		using var process = new System.Diagnostics.Process { StartInfo = startInfo };

		var stderr = new System.Text.StringBuilder();
		process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data is not null)
				stderr.AppendLine(e.Data);
		};

		using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(hook.TimeoutSeconds));
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

		try
		{
			if (!process.Start())
				return (-1, "Failed to start process");

			process.BeginErrorReadLine();

			await process.WaitForExitAsync(linkedCts.Token);
			return (process.ExitCode, stderr.ToString().TrimEnd());
		}
		catch (OperationCanceledException)
		{
			// Kill the entire process tree on any cancellation (timeout or external shutdown).
			// Without this, the child process (e.g., a server started via cmd.exe /c) would
			// be orphaned and keep running after Orchestra exits.
			try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }

			if (timeoutCts.IsCancellationRequested)
			{
				LogBeforeStartHookTimedOut(hook.Name, hook.TimeoutSeconds);
				return (-1, $"Hook timed out after {hook.TimeoutSeconds}s");
			}

			// External cancellation (e.g., Ctrl+C / shutdown)
			LogBeforeStartHookCancelled(hook.Name);
			return (-1, "Hook cancelled (shutdown requested)");
		}
	}

	/// <summary>
	/// Builds a <see cref="System.Diagnostics.ProcessStartInfo"/> for a one-shot command hook.
	/// </summary>
	private static System.Diagnostics.ProcessStartInfo BuildCommandStartInfo(CommandHook hook)
	{
		var isWindows = System.Runtime.InteropServices.RuntimeInformation
			.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

		var escapedArgs = hook.Arguments.Select(arg =>
		{
			if (isWindows)
				return arg.Contains(' ') ? $"\"" + arg + "\"" : arg;
			else
				return $"'{arg.Replace("'", "'\\''")}'";
		});

		var fullCommandLine = hook.Arguments.Length > 0
			? $"{hook.Command} {string.Join(' ', escapedArgs)}"
			: hook.Command;

		var startInfo = new System.Diagnostics.ProcessStartInfo
		{
			FileName = isWindows ? "cmd.exe" : "/bin/sh",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		if (isWindows)
		{
			startInfo.Arguments = $"/c {fullCommandLine}";
		}
		else
		{
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add(fullCommandLine);
		}

		if (hook.WorkingDirectory is not null)
			startInfo.WorkingDirectory = hook.WorkingDirectory;

		if (hook.Env is not null)
		{
			foreach (var (key, value) in hook.Env)
				startInfo.Environment[key] = value;
		}

		return startInfo;
	}

	/// <summary>
	/// Stops all managed processes and runs afterStop hooks.
	/// </summary>
	public async Task StopAsync(CancellationToken cancellationToken = default)
	{
		if (_stopped || !_initialized)
			return;

		_stopped = true;
		LogServiceManagerStopping();

		// 1. Cancel all restart loops
		_shutdownCts?.Cancel();

		// Wait for restart loops to finish
		if (_restartLoops.Count > 0)
		{
			try
			{
				await Task.WhenAll(_restartLoops.Values);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				LogRestartLoopError(ex);
			}
		}

		// 2. Stop all processes in parallel
		var stopTasks = new List<Task>();
		foreach (var (name, managed) in _processes)
		{
			LogStoppingProcess(name);
			stopTasks.Add(managed.StopAsync());
		}
		await Task.WhenAll(stopTasks);

		// 3. Run afterStop hooks sequentially
		await RunAfterStopHooksAsync(cancellationToken);

		// Dispose all managed processes
		foreach (var (_, managed) in _processes)
		{
			await managed.DisposeAsync();
		}
		_processes.Clear();

		// Clean shutdown — remove PID file so next startup doesn't see orphans
		_processTracker?.Clear();

		LogServiceManagerStopped();
	}

	/// <summary>
	/// Runs all afterStop hooks in order. Never throws — errors are logged.
	/// </summary>
	private async Task RunAfterStopHooksAsync(CancellationToken cancellationToken)
	{
		foreach (var hook in _afterStopHooks)
		{
			try
			{
				LogRunningAfterStopHook(hook.Name, hook.Command);
				var (exitCode, stderr) = await RunCommandAsync(hook, cancellationToken);

				if (exitCode != 0)
					LogAfterStopHookFailed(hook.Name, exitCode, stderr);
				else
					LogAfterStopHookCompleted(hook.Name);
			}
			catch (Exception ex)
			{
				LogAfterStopHookException(hook.Name, ex);
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (!_stopped)
			await StopAsync();

		_shutdownCts?.Dispose();
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 100,
		Level = LogLevel.Information,
		Message = "No services configured. ServiceManager will not manage any processes.")]
	private partial void LogNoServicesConfigured();

	[LoggerMessage(
		EventId = 101,
		Level = LogLevel.Information,
		Message = "ServiceManager initializing: {ProcessCount} process(es), {BeforeStartCount} beforeStart hook(s), {AfterStopCount} afterStop hook(s)")]
	private partial void LogServiceManagerInitializing(int processCount, int beforeStartCount, int afterStopCount);

	[LoggerMessage(
		EventId = 102,
		Level = LogLevel.Information,
		Message = "ServiceManager initialized: {ProcessCount} process(es) running, {HookCount} hook(s) registered")]
	private partial void LogServiceManagerInitialized(int processCount, int hookCount);

	[LoggerMessage(
		EventId = 103,
		Level = LogLevel.Information,
		Message = "Running beforeStart hook '{HookName}': {Command}")]
	private partial void LogRunningBeforeStartHook(string hookName, string command);

	[LoggerMessage(
		EventId = 104,
		Level = LogLevel.Information,
		Message = "beforeStart hook '{HookName}' completed with exit code {ExitCode}")]
	private partial void LogBeforeStartHookCompleted(string hookName, int exitCode);

	[LoggerMessage(
		EventId = 105,
		Level = LogLevel.Error,
		Message = "Required beforeStart hook '{HookName}' failed with exit code {ExitCode}: {Stderr}")]
	private partial void LogBeforeStartHookFailed(string hookName, int exitCode, string stderr);

	[LoggerMessage(
		EventId = 106,
		Level = LogLevel.Warning,
		Message = "beforeStart hook '{HookName}' failed with exit code {ExitCode}: {Stderr} (non-required, continuing)")]
	private partial void LogBeforeStartHookFailedNonRequired(string hookName, int exitCode, string stderr);

	[LoggerMessage(
		EventId = 107,
		Level = LogLevel.Error,
		Message = "beforeStart hook '{HookName}' timed out after {TimeoutSeconds}s")]
	private partial void LogBeforeStartHookTimedOut(string hookName, int timeoutSeconds);

	[LoggerMessage(
		EventId = 121,
		Level = LogLevel.Warning,
		Message = "beforeStart hook '{HookName}' cancelled (shutdown requested)")]
	private partial void LogBeforeStartHookCancelled(string hookName);

	[LoggerMessage(
		EventId = 108,
		Level = LogLevel.Information,
		Message = "Starting process '{ServiceName}': {Command}")]
	private partial void LogStartingProcess(string serviceName, string command);

	[LoggerMessage(
		EventId = 109,
		Level = LogLevel.Warning,
		Message = "Process '{ServiceName}' exited with error code {ExitCode}")]
	private partial void LogProcessExitedWithError(string serviceName, int exitCode);

	[LoggerMessage(
		EventId = 110,
		Level = LogLevel.Information,
		Message = "Process '{ServiceName}' exited cleanly")]
	private partial void LogProcessExitedCleanly(string serviceName);

	[LoggerMessage(
		EventId = 111,
		Level = LogLevel.Information,
		Message = "Restarting process '{ServiceName}' (attempt {Attempt}, delay {DelaySeconds}s)")]
	private partial void LogRestartingProcess(string serviceName, int attempt, int delaySeconds);

	[LoggerMessage(
		EventId = 112,
		Level = LogLevel.Error,
		Message = "Process '{ServiceName}' failed to restart (attempt {Attempt})")]
	private partial void LogProcessRestartFailed(string serviceName, int attempt);

	[LoggerMessage(
		EventId = 113,
		Level = LogLevel.Information,
		Message = "Stopping process '{ServiceName}'")]
	private partial void LogStoppingProcess(string serviceName);

	[LoggerMessage(
		EventId = 114,
		Level = LogLevel.Information,
		Message = "ServiceManager stopping")]
	private partial void LogServiceManagerStopping();

	[LoggerMessage(
		EventId = 115,
		Level = LogLevel.Information,
		Message = "ServiceManager stopped")]
	private partial void LogServiceManagerStopped();

	[LoggerMessage(
		EventId = 116,
		Level = LogLevel.Information,
		Message = "Running afterStop hook '{HookName}': {Command}")]
	private partial void LogRunningAfterStopHook(string hookName, string command);

	[LoggerMessage(
		EventId = 117,
		Level = LogLevel.Warning,
		Message = "afterStop hook '{HookName}' failed with exit code {ExitCode}: {Stderr}")]
	private partial void LogAfterStopHookFailed(string hookName, int exitCode, string stderr);

	[LoggerMessage(
		EventId = 118,
		Level = LogLevel.Information,
		Message = "afterStop hook '{HookName}' completed")]
	private partial void LogAfterStopHookCompleted(string hookName);

	[LoggerMessage(
		EventId = 119,
		Level = LogLevel.Error,
		Message = "afterStop hook '{HookName}' threw an exception")]
	private partial void LogAfterStopHookException(string hookName, Exception ex);

	[LoggerMessage(
		EventId = 120,
		Level = LogLevel.Error,
		Message = "Error in restart loop")]
	private partial void LogRestartLoopError(Exception ex);

	#endregion
}
