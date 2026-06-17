using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Orchestra.OpenCode;

/// <summary>
/// Resolves how to obtain an OpenCode server: connect to a pre-running instance
/// (<c>ORCHESTRA_OPENCODE_URL</c> / <c>opencode.serverUrl</c>) or spawn <c>opencode serve</c>
/// from an explicit path (<c>ORCHESTRA_OPENCODE_PATH</c> / <c>opencode.cliPath</c>) or the
/// <c>opencode</c> binary on PATH. Parallels <c>CopilotCliBootstrap</c> but never downloads —
/// the spawn-or-connect choice keeps the footprint small per the adapter's design.
/// </summary>
internal static class OpenCodeServerBootstrap
{
	public const string ExplicitUrlEnvVar = "ORCHESTRA_OPENCODE_URL";
	public const string ExplicitCliPathEnvVar = "ORCHESTRA_OPENCODE_PATH";

	/// <summary>
	/// Decides the connection plan for a new server instance from options + environment.
	/// </summary>
	public static OpenCodeConnectionPlan Resolve(OpenCodeAgentPoolOptions options)
	{
		var url = FirstNonEmpty(options.ServerUrl, Environment.GetEnvironmentVariable(ExplicitUrlEnvVar));
		if (!string.IsNullOrWhiteSpace(url))
			return OpenCodeConnectionPlan.Connect(url.TrimEnd('/'));

		var cliPath = FirstNonEmpty(options.CliPath, Environment.GetEnvironmentVariable(ExplicitCliPathEnvVar))
			?? "opencode";
		return OpenCodeConnectionPlan.Spawn(cliPath);
	}

	private static string? FirstNonEmpty(params string?[] values)
		=> values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

	/// <summary>
	/// Resolves a bare command (e.g. <c>opencode</c>) to a full executable path using PATH +
	/// PATHEXT. <see cref="System.Diagnostics.Process"/> with <c>UseShellExecute=false</c> does
	/// not search PATH/PATHEXT on Windows, so a bare <c>FileName</c> would otherwise fail even
	/// when the binary is installed. Returns the input unchanged when it already contains a
	/// directory separator, or when no match is found (Unix may still resolve it).
	/// </summary>
	internal static string ResolveExecutable(string command)
	{
		if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
			return command;

		var exts = OperatingSystem.IsWindows()
			? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
			: [string.Empty];
		var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

		foreach (var dir in dirs)
		{
			foreach (var ext in exts)
			{
				try
				{
					var candidate = Path.Combine(dir.Trim(), command + ext);
					if (File.Exists(candidate))
						return candidate;
				}
				catch
				{
					// Ignore malformed PATH entries.
				}
			}
		}

		return command;
	}
}

/// <summary>How a server instance is obtained: connect to a URL, or spawn a CLI binary.</summary>
internal sealed record OpenCodeConnectionPlan
{
	public required bool IsSpawn { get; init; }
	public string? Url { get; init; }
	public string? CliPath { get; init; }

	public static OpenCodeConnectionPlan Connect(string url) => new() { IsSpawn = false, Url = url };
	public static OpenCodeConnectionPlan Spawn(string cliPath) => new() { IsSpawn = true, CliPath = cliPath };
}

/// <summary>
/// One OpenCode server backing a pool worker: either a spawned <c>opencode serve</c> child
/// process or a connection to a pre-running server. Owns the process lifecycle when spawned.
/// </summary>
internal sealed partial class OpenCodeServerProcess : IAsyncDisposable
{
	private readonly OpenCodeConnectionPlan _plan;
	private readonly OpenCodeAgentPoolOptions _options;
	private readonly IOpenCodeClientFactory _clientFactory;
	private readonly ILogger _logger;
	private readonly string? _configContent;
	private Process? _process;

	public OpenCodeServerProcess(
		OpenCodeConnectionPlan plan,
		OpenCodeAgentPoolOptions options,
		IOpenCodeClientFactory clientFactory,
		ILogger logger,
		string? configContent = null)
	{
		_plan = plan;
		_options = options;
		_clientFactory = clientFactory;
		_logger = logger;
		_configContent = configContent;
	}

	public string BaseUrl { get; private set; } = string.Empty;
	public bool IsSpawned => _plan.IsSpawn;
	public IOpenCodeClient Client { get; private set; } = null!;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		if (_plan.IsSpawn)
			await SpawnAsync(cancellationToken).ConfigureAwait(false);
		else
			BaseUrl = _plan.Url!;

		Client = _clientFactory.Create(BaseUrl, _options.ServerUsername, _options.ServerPassword);
		await WaitForHealthyAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task SpawnAsync(CancellationToken cancellationToken)
	{
		var port = GetFreeTcpPort();
		BaseUrl = $"http://{_options.Hostname}:{port}";

		var psi = new ProcessStartInfo
		{
			FileName = OpenCodeServerBootstrap.ResolveExecutable(_plan.CliPath!),
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("serve");
		psi.ArgumentList.Add("--hostname");
		psi.ArgumentList.Add(_options.Hostname);
		psi.ArgumentList.Add("--port");
		psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
		if (!string.IsNullOrEmpty(_options.ServerPassword))
		{
			psi.Environment["OPENCODE_SERVER_PASSWORD"] = _options.ServerPassword;
			psi.Environment["OPENCODE_SERVER_USERNAME"] = _options.ServerUsername;
		}
		if (!string.IsNullOrWhiteSpace(_configContent))
		{
			// Inline config loaded at startup — used to register per-run agents (reasoning +
			// sub-agents), which the runtime resolves at spawn time (not via runtime PATCH).
			psi.Environment["OPENCODE_CONFIG_CONTENT"] = _configContent;
		}

		LogSpawning(_plan.CliPath!, BaseUrl);
		try
		{
			_process = Process.Start(psi)
				?? throw new OpenCodeClientUnhealthyException("(spawn)", "spawn_failed", message: $"Process.Start returned null for '{_plan.CliPath}'.");
		}
		catch (Exception ex) when (ex is not OpenCodeClientUnhealthyException)
		{
			throw new OpenCodeClientUnhealthyException(
				"(spawn)", "spawn_failed",
				probeDetails: ex.Message,
				message: $"Failed to launch '{_plan.CliPath} serve'. Ensure OpenCode is installed and on PATH, " +
						 $"set orchestra.json opencode.cliPath / {OpenCodeServerBootstrap.ExplicitCliPathEnvVar}, " +
						 $"or point at a running server with {OpenCodeServerBootstrap.ExplicitUrlEnvVar}.",
				innerException: ex);
		}

		// Drain stdout/stderr so the child never blocks on a full pipe buffer.
		DrainAsync(_process.StandardOutput, isError: false);
		DrainAsync(_process.StandardError, isError: true);
	}

	private async Task WaitForHealthyAsync(CancellationToken cancellationToken)
	{
		var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
		var sw = Stopwatch.StartNew();
		while (DateTimeOffset.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (_process is { HasExited: true })
			{
				throw new OpenCodeClientUnhealthyException(
					"(startup)", "process_exited",
					probeDetails: $"exit code {_process.ExitCode}",
					message: $"OpenCode server exited during startup (code {_process.ExitCode}).");
			}

			if (await Client.HealthAsync(cancellationToken).ConfigureAwait(false))
			{
				LogReady(BaseUrl, sw.ElapsedMilliseconds, IsSpawned);
				return;
			}

			await Task.Delay(250, cancellationToken).ConfigureAwait(false);
		}

		throw new OpenCodeClientUnhealthyException(
			"(startup)", "health_timeout",
			probeDetails: $"no healthy response within {_options.StartupTimeout.TotalSeconds:0}s",
			message: $"OpenCode server at {BaseUrl} did not become healthy within {_options.StartupTimeout.TotalSeconds:0}s.");
	}

	private async void DrainAsync(StreamReader reader, bool isError)
	{
		try
		{
			while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
			{
				if (isError)
					LogServerStderr(line);
			}
		}
		catch
		{
			// Pipe closed on shutdown — ignore.
		}
	}

	private static int GetFreeTcpPort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally
		{
			listener.Stop();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Client is not null)
			await Client.DisposeAsync().ConfigureAwait(false);

		if (_process is { } process)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
					await process.WaitForExitAsync().ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				LogKillError(ex, BaseUrl);
			}
			finally
			{
				process.Dispose();
			}
		}
	}

	[LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "OpenCode: spawning '{CliPath} serve' at {BaseUrl}")]
	private partial void LogSpawning(string cliPath, string baseUrl);

	[LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "OpenCode: server ready at {BaseUrl} in {ElapsedMs}ms (spawned={Spawned})")]
	private partial void LogReady(string baseUrl, long elapsedMs, bool spawned);

	[LoggerMessage(EventId = 202, Level = LogLevel.Debug, Message = "OpenCode[stderr]: {Line}")]
	private partial void LogServerStderr(string line);

	[LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "OpenCode: error killing server at {BaseUrl}")]
	private partial void LogKillError(Exception ex, string baseUrl);
}
