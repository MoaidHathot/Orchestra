using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Orchestra.ProcessHost;

/// <summary>
/// Wraps a <see cref="System.Diagnostics.Process"/> to provide managed lifecycle control,
/// stdout/stderr capture, readiness detection, and graceful shutdown.
/// </summary>
public sealed partial class ManagedProcess : IAsyncDisposable
{
	private readonly ProcessService _config;
	private readonly ILogger _logger;
	private Process? _process;
	private TaskCompletionSource<bool>? _readinessTcs;
	private Regex? _readinessRegex;
	private volatile ProcessState _state = ProcessState.Pending;

	/// <summary>
	/// Fired for each stdout/stderr line received from the process.
	/// Used internally for readiness regex matching.
	/// </summary>
	internal event Action<string>? OnOutputLine;

	public ManagedProcess(ProcessService config, ILogger logger)
	{
		_config = config;
		_logger = logger;
	}

	/// <summary>
	/// Gets the current state of the managed process.
	/// </summary>
	public ProcessState State => _state;

	/// <summary>
	/// Gets the process exit code, or null if the process has not exited.
	/// </summary>
	public int? ExitCode { get; private set; }

	/// <summary>
	/// Gets the name of the managed service.
	/// </summary>
	public string Name => _config.Name;

	/// <summary>
	/// Gets the underlying process configuration.
	/// </summary>
	internal ProcessService Config => _config;

	/// <summary>
	/// Starts the process and optionally waits for readiness.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token for startup.</param>
	/// <returns>True if the process started (and is ready, if readiness is configured); false otherwise.</returns>
	public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
	{
		_state = ProcessState.Starting;

		var startInfo = BuildProcessStartInfo();

		_process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

		// Set up readiness tracking before starting the process
		if (_config.Readiness?.StdoutPattern is not null)
		{
			_readinessRegex = new Regex(_config.Readiness.StdoutPattern, RegexOptions.Compiled);
			_readinessTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		_process.OutputDataReceived += OnProcessOutputData;
		_process.ErrorDataReceived += OnProcessErrorData;

		try
		{
			if (!_process.Start())
			{
				LogProcessStartFailed(_config.Name, _config.Command);
				_state = ProcessState.Failed;
				return false;
			}

			_process.BeginOutputReadLine();
			_process.BeginErrorReadLine();

			LogProcessStarted(_config.Name, _config.Command, _process.Id);

			// If no readiness check configured, transition directly to Running
			if (_config.Readiness is null)
			{
				_state = ProcessState.Running;
				return true;
			}

			// Wait for readiness
			return await WaitForReadyAsync(cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			LogProcessStartException(_config.Name, ex);
			_state = ProcessState.Failed;
			return false;
		}
	}

	/// <summary>
	/// Waits for the process to signal readiness via stdout pattern or HTTP health check.
	/// </summary>
	private async Task<bool> WaitForReadyAsync(CancellationToken cancellationToken)
	{
		var readiness = _config.Readiness!;
		using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(readiness.TimeoutSeconds));
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

		var tasks = new List<Task<bool>>();

		// Stdout pattern readiness
		if (_readinessTcs is not null)
		{
			tasks.Add(WaitForStdoutReadyAsync(linkedCts.Token));
		}

		// HTTP health check readiness
		if (readiness.HealthCheckUrl is not null)
		{
			tasks.Add(WaitForHealthCheckAsync(readiness.HealthCheckUrl, readiness.IntervalMs, linkedCts.Token));
		}

		try
		{
			// Wait for ANY readiness signal to succeed
			var completedTask = await Task.WhenAny(tasks);
			var result = await completedTask;
			if (result)
			{
				_state = ProcessState.Ready;
				LogProcessReady(_config.Name);
				return true;
			}
		}
		catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
		{
			LogProcessReadinessTimeout(_config.Name, readiness.TimeoutSeconds);
			// If not required, downgrade to Running state with a warning
			if (!_config.Required)
			{
				_state = ProcessState.Running;
				return true;
			}
			_state = ProcessState.Failed;
			return false;
		}
		catch (OperationCanceledException)
		{
			throw; // External cancellation, propagate
		}

		_state = ProcessState.Failed;
		return false;
	}

	/// <summary>
	/// Waits for a stdout/stderr line that matches the configured readiness regex.
	/// </summary>
	private async Task<bool> WaitForStdoutReadyAsync(CancellationToken cancellationToken)
	{
		if (_readinessTcs is null)
			return false;

		await using var registration = cancellationToken.Register(
			() => _readinessTcs.TrySetCanceled(cancellationToken));

		return await _readinessTcs.Task;
	}

	/// <summary>
	/// Polls an HTTP health check endpoint until it returns 200 OK.
	/// </summary>
	private static async Task<bool> WaitForHealthCheckAsync(string url, int intervalMs, CancellationToken cancellationToken)
	{
		using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				var response = await httpClient.GetAsync(url, cancellationToken);
				if (response.IsSuccessStatusCode)
					return true;
			}
			catch (Exception) when (!cancellationToken.IsCancellationRequested)
			{
				// Expected during startup — the service may not be listening yet
			}

			await Task.Delay(intervalMs, cancellationToken);
		}

		cancellationToken.ThrowIfCancellationRequested();
		return false;
	}

	/// <summary>
	/// Gracefully stops the process, then force-kills if the timeout is exceeded.
	/// </summary>
	/// <param name="timeoutSeconds">Seconds to wait for graceful exit before force-killing.
	/// If null, uses the configured <see cref="ProcessService.ShutdownTimeoutSeconds"/>.</param>
	public async Task StopAsync(int? timeoutSeconds = null)
	{
		if (_process is null || _state is ProcessState.Stopped or ProcessState.Pending)
			return;

		_state = ProcessState.Stopping;
		var timeout = timeoutSeconds ?? _config.ShutdownTimeoutSeconds;

		try
		{
			if (!_process.HasExited)
			{
				// Attempt graceful shutdown
				KillProcessGracefully(_process);

				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
				try
				{
					await _process.WaitForExitAsync(cts.Token);
					LogProcessStopped(_config.Name, _process.ExitCode);
				}
				catch (OperationCanceledException)
				{
					// Timeout expired — force kill the entire process tree
					KillProcessTree(_process);
					LogProcessForceKilled(_config.Name);
				}
			}

			ExitCode = _process.HasExited ? _process.ExitCode : null;
		}
		catch (InvalidOperationException)
		{
			// Process already exited between our check and the operation
		}

		_state = ProcessState.Stopped;
	}

	/// <summary>
	/// Waits for the process to exit. Returns the exit code.
	/// </summary>
	public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
	{
		if (_process is null)
			throw new InvalidOperationException("Process has not been started.");

		await _process.WaitForExitAsync(cancellationToken);
		ExitCode = _process.ExitCode;
		return _process.ExitCode;
	}

	/// <summary>
	/// Returns true if the underlying process has exited.
	/// </summary>
	public bool HasExited
	{
		get
		{
			try
			{
				return _process?.HasExited ?? true;
			}
			catch (InvalidOperationException)
			{
				return true;
			}
		}
	}

	/// <summary>
	/// Builds the <see cref="ProcessStartInfo"/> for the managed process.
	/// Starts the command directly (no cmd.exe/sh wrapper) so that stdin close
	/// propagates to the process for graceful shutdown. On Windows, resolves
	/// shell shims (.cmd/.bat) by finding the actual file on PATH.
	/// </summary>
	private ProcessStartInfo BuildProcessStartInfo()
	{
		var resolvedCommand = ResolveCommand(_config.Command);

		var startInfo = new ProcessStartInfo
		{
			FileName = resolvedCommand,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		foreach (var arg in _config.Arguments)
			startInfo.ArgumentList.Add(arg);

		if (_config.WorkingDirectory is not null)
			startInfo.WorkingDirectory = _config.WorkingDirectory;

		// Remove inherited ASP.NET Core / Kestrel env vars that would cause child
		// processes to bind to the parent's URLs/ports instead of their own config.
		foreach (var key in InheritedEnvVarsToRemove)
		{
			if (startInfo.Environment.ContainsKey(key))
				startInfo.Environment.Remove(key);
		}

		if (_config.Env is not null)
		{
			foreach (var (key, value) in _config.Env)
			{
				if (value.Length > 0)
					startInfo.Environment[key] = value;
				else
					startInfo.Environment.Remove(key);
			}
		}

		return startInfo;
	}

	/// <summary>
	/// Environment variables inherited from the parent process that should not
	/// propagate to managed child processes. These are ASP.NET Core host-binding
	/// variables that would cause a child server to bind to the parent's URLs/ports.
	/// </summary>
	private static readonly string[] InheritedEnvVarsToRemove =
	[
		"ASPNETCORE_URLS",
		"DOTNET_URLS",
	];

	/// <summary>
	/// Resolves a command name to a full path. On Windows, searches PATH for
	/// .cmd, .bat, .exe, and .com extensions so that shell shims (e.g. dnx.cmd,
	/// npx.cmd) can be started directly without a cmd.exe wrapper.
	/// </summary>
	private static string ResolveCommand(string command)
	{
		// If the command is already a rooted path or contains a path separator, use as-is
		if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar)
			|| command.Contains(Path.AltDirectorySeparatorChar))
			return command;

		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return command;

		// On Windows, search PATH for the command with common executable extensions
		var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
		var pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
		var extensions = new[] { ".cmd", ".bat", ".exe", ".com" };

		foreach (var dir in pathDirs)
		{
			foreach (var ext in extensions)
			{
				var candidate = Path.Combine(dir, command + ext);
				if (File.Exists(candidate))
					return candidate;
			}

			// Also check without extension (might already have one)
			var exact = Path.Combine(dir, command);
			if (File.Exists(exact))
				return exact;
		}

		// Fall back to the original command and let Process.Start handle it
		return command;
	}

	private void OnProcessOutputData(object sender, DataReceivedEventArgs e)
	{
		if (e.Data is null) return;

		LogProcessOutput(_config.Name, e.Data);
		OnOutputLine?.Invoke(e.Data);

		// Check readiness regex
		if (_readinessRegex is not null && _readinessTcs is not null && !_readinessTcs.Task.IsCompleted)
		{
			if (_readinessRegex.IsMatch(e.Data))
				_readinessTcs.TrySetResult(true);
		}
	}

	private void OnProcessErrorData(object sender, DataReceivedEventArgs e)
	{
		if (e.Data is null) return;

		LogProcessStderr(_config.Name, e.Data);
		OnOutputLine?.Invoke(e.Data);

		// Also check readiness regex on stderr
		if (_readinessRegex is not null && _readinessTcs is not null && !_readinessTcs.Task.IsCompleted)
		{
			if (_readinessRegex.IsMatch(e.Data))
				_readinessTcs.TrySetResult(true);
		}
	}

	/// <summary>
	/// Attempts graceful shutdown of the process. Closes stdin first (some frameworks
	/// like Node.js detect stdin EOF), then kills the process directly. Since the
	/// process is started without a cmd.exe/sh wrapper, the kill signal goes to the
	/// actual process, not an intermediary shell.
	/// </summary>
	private void KillProcessGracefully(Process process)
	{
		try
		{
			// Close stdin first — gives processes that watch for stdin EOF
			// (e.g. Node.js MCP servers) a chance to begin cleanup.
			try { process.StandardInput.Close(); }
			catch { /* stdin may already be closed */ }

			// Kill the process directly. Since we start the command without a
			// cmd.exe/sh wrapper, this terminates the actual process (not a shell).
			process.Kill();
		}
		catch (InvalidOperationException)
		{
			// Already exited
		}
	}

	/// <summary>
	/// Force-kills the entire process tree.
	/// </summary>
	private static void KillProcessTree(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already exited
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_process is not null)
		{
			if (!HasExited)
			{
				await StopAsync();
			}

			_process.OutputDataReceived -= OnProcessOutputData;
			_process.ErrorDataReceived -= OnProcessErrorData;
			_process.Dispose();
			_process = null;
		}
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Information,
		Message = "Service '{ServiceName}': started process '{Command}' (PID {ProcessId})")]
	private partial void LogProcessStarted(string serviceName, string command, int processId);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Error,
		Message = "Service '{ServiceName}': failed to start process '{Command}'")]
	private partial void LogProcessStartFailed(string serviceName, string command);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Error,
		Message = "Service '{ServiceName}': exception starting process")]
	private partial void LogProcessStartException(string serviceName, Exception ex);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Information,
		Message = "Service '{ServiceName}': process is ready")]
	private partial void LogProcessReady(string serviceName);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Warning,
		Message = "Service '{ServiceName}': readiness check timed out after {TimeoutSeconds}s")]
	private partial void LogProcessReadinessTimeout(string serviceName, int timeoutSeconds);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Debug,
		Message = "Service '{ServiceName}': {Line}")]
	private partial void LogProcessOutput(string serviceName, string line);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Debug,
		Message = "Service '{ServiceName}' [stderr]: {Line}")]
	private partial void LogProcessStderr(string serviceName, string line);

	[LoggerMessage(
		EventId = 8,
		Level = LogLevel.Information,
		Message = "Service '{ServiceName}': process stopped with exit code {ExitCode}")]
	private partial void LogProcessStopped(string serviceName, int exitCode);

	[LoggerMessage(
		EventId = 9,
		Level = LogLevel.Warning,
		Message = "Service '{ServiceName}': process did not exit gracefully, force-killed")]
	private partial void LogProcessForceKilled(string serviceName);

	#endregion
}
