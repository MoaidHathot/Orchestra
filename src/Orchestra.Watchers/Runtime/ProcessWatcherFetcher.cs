using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orchestra.Watchers.Models;

namespace Orchestra.Watchers.Runtime;

/// <summary>
/// Shared child-process plumbing for the two fetcher kinds: writes the request to stdin (UTF-8),
/// captures stdout/stderr (UTF-8, capped), enforces the timeout by killing the process tree and
/// parses stdout as JSON. Mirrors the engine's Script step executor (encodings, pwsh wrapper) but
/// stays independent of it - the engine's executor is bound to step/run context and this assembly
/// must not reach into engine internals.
/// </summary>
public abstract class ProcessWatcherFetcher : IWatcherFetcher
{
	/// <summary>Stdout larger than this is a fetch failure; watcher fetchers are not meant to ship blobs.</summary>
	public const int MaxStdoutBytes = 16 * 1024 * 1024;

	/// <summary>Stderr is captured for logging only; keep it bounded as well.</summary>
	public const int MaxStderrBytes = 1 * 1024 * 1024;

	private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	public async Task<WatcherFetchResult> RunAsync(WatcherEntry entry, WatcherFetchRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentNullException.ThrowIfNull(request);

		var fetch = entry.FetchBase;
		var stopwatch = Stopwatch.StartNew();

		ProcessStartInfo startInfo;
		Action? cleanup;
		try
		{
			(startInfo, cleanup) = await BuildStartInfoAsync(entry, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return WatcherFetchResult.Failure($"could not prepare the fetcher: {ex.Message}", elapsed: stopwatch.Elapsed);
		}

		try
		{
			ApplyCommonStartInfo(startInfo, entry);

			using var process = new Process { StartInfo = startInfo };
			try
			{
				if (!process.Start())
					return WatcherFetchResult.Failure($"failed to start '{startInfo.FileName}'.", elapsed: stopwatch.Elapsed);
			}
			catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
			{
				return WatcherFetchResult.Failure($"failed to start '{startInfo.FileName}': {ex.Message}", elapsed: stopwatch.Elapsed);
			}

			var timeout = TimeSpan.FromSeconds(Math.Max(1, fetch.TimeoutSeconds));
			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutCts.CancelAfter(timeout);
			var token = timeoutCts.Token;

			var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxStdoutBytes, token);
			var stderrTask = ReadBoundedAsync(process.StandardError, MaxStderrBytes, token);

			var stdinFailure = await WriteStdinAsync(process, request.ToJson(), token);

			try
			{
				await process.WaitForExitAsync(token);
			}
			catch (OperationCanceledException)
			{
				KillQuietly(process);
				var partialOut = await SafeAwait(stdoutTask);
				var partialErr = await SafeAwait(stderrTask);
				if (cancellationToken.IsCancellationRequested)
					cancellationToken.ThrowIfCancellationRequested();

				return WatcherFetchResult.Failure(
					$"timed out after {timeout.TotalSeconds:0.#}s.",
					exitCode: -1,
					stdout: partialOut.Text,
					stderr: partialErr.Text,
					timedOut: true,
					elapsed: stopwatch.Elapsed);
			}

			var stdout = await stdoutTask;
			var stderr = await stderrTask;
			stopwatch.Stop();

			if (stdout.Truncated)
			{
				return WatcherFetchResult.Failure(
					$"stdout exceeded the {MaxStdoutBytes / (1024 * 1024)} MB cap; fetchers must return compact item lists, not blobs.",
					exitCode: process.ExitCode,
					stdout: stdout.Text,
					stderr: stderr.Text,
					elapsed: stopwatch.Elapsed);
			}

			if (process.ExitCode != 0)
			{
				var detail = string.IsNullOrWhiteSpace(stderr.Text) ? string.Empty : $" stderr: {Trim(stderr.Text, 512)}";
				var stdinNote = stdinFailure is null ? string.Empty : $" (stdin write failed: {stdinFailure})";
				return WatcherFetchResult.Failure(
					$"exit code {process.ExitCode}.{detail}{stdinNote}",
					exitCode: process.ExitCode,
					stdout: stdout.Text,
					stderr: stderr.Text,
					elapsed: stopwatch.Elapsed);
			}

			var payload = fetch.IncludeStdErr ? stdout.Text + stderr.Text : stdout.Text;
			if (string.IsNullOrWhiteSpace(payload))
			{
				return WatcherFetchResult.Failure(
					"fetcher exited 0 but produced no output; expected a JSON document such as {\"protocolVersion\":\"1\",\"items\":[]}.",
					exitCode: 0,
					stdout: stdout.Text,
					stderr: stderr.Text,
					elapsed: stopwatch.Elapsed);
			}

			JsonNode? output;
			try
			{
				output = JsonNode.Parse(payload, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
			}
			catch (JsonException ex)
			{
				return WatcherFetchResult.Failure(
					$"stdout is not valid JSON: {ex.Message}",
					exitCode: 0,
					stdout: stdout.Text,
					stderr: stderr.Text,
					elapsed: stopwatch.Elapsed);
			}

			return new WatcherFetchResult(0, stdout.Text, stderr.Text, output, FailureReason: null, TimedOut: false, Elapsed: stopwatch.Elapsed);
		}
		finally
		{
			cleanup?.Invoke();
		}
	}

	/// <summary>
	/// Builds the executable/arguments for <paramref name="entry"/>. The optional cleanup runs after
	/// the process exits (e.g. to delete a temporary inline script).
	/// </summary>
	protected abstract Task<(ProcessStartInfo StartInfo, Action? Cleanup)> BuildStartInfoAsync(WatcherEntry entry, CancellationToken cancellationToken);

	/// <summary>Resolves a possibly-relative path against the entry's base directory.</summary>
	protected static string ResolvePath(WatcherEntry entry, string path)
	{
		if (Path.IsPathRooted(path))
			return Path.GetFullPath(path);

		var baseDirectory = entry.BaseDirectory ?? Directory.GetCurrentDirectory();
		return Path.GetFullPath(Path.Combine(baseDirectory, path));
	}

	private static void ApplyCommonStartInfo(ProcessStartInfo startInfo, WatcherEntry entry)
	{
		var fetch = entry.FetchBase;

		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;
		startInfo.RedirectStandardInput = true;
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.StandardOutputEncoding = s_utf8NoBom;
		startInfo.StandardErrorEncoding = s_utf8NoBom;
		startInfo.StandardInputEncoding = s_utf8NoBom;

		startInfo.WorkingDirectory = fetch.WorkingDirectory is { Length: > 0 } wd
			? ResolvePath(entry, wd)
			: entry.BaseDirectory ?? Directory.GetCurrentDirectory();

		if (fetch.Env is { Count: > 0 })
		{
			foreach (var (key, value) in fetch.Env)
				startInfo.Environment[key] = value;
		}
	}

	private static async Task<string?> WriteStdinAsync(Process process, string payload, CancellationToken cancellationToken)
	{
		try
		{
			await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken);
			await process.StandardInput.FlushAsync(cancellationToken);
			process.StandardInput.Close();
			return null;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (IOException ex)
		{
			// The fetcher exited (or closed stdin) before reading the payload. Not fatal by itself;
			// the exit code decides. Remember the detail for the failure message.
			return ex.Message;
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
	}

	private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken cancellationToken)
	{
		var builder = new StringBuilder();
		var buffer = new char[8192];
		var truncated = false;
		long bytes = 0;

		try
		{
			while (true)
			{
				var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
				if (read == 0)
					break;

				if (truncated)
					continue; // keep draining so the child does not block on a full pipe

				bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
				if (bytes > maxBytes)
				{
					truncated = true;
					continue;
				}

				builder.Append(buffer, 0, read);
			}
		}
		catch (OperationCanceledException)
		{
			// Timeout: return whatever arrived.
		}

		return (builder.ToString(), truncated);
	}

	private static async Task<(string Text, bool Truncated)> SafeAwait(Task<(string Text, bool Truncated)> task)
	{
		try
		{
			return await task;
		}
		catch (Exception)
		{
			return (string.Empty, false);
		}
	}

	private static void KillQuietly(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
		}
		catch (System.ComponentModel.Win32Exception)
		{
		}
	}

	private static string Trim(string text, int max)
	{
		text = text.Trim();
		return text.Length <= max ? text : text[..max] + "...";
	}
}

/// <summary>
/// Fetcher for <see cref="PollCommandWatcher"/>: runs <c>fetch.command</c> with <c>fetch.arguments</c>.
/// </summary>
public sealed class CommandWatcherFetcher : ProcessWatcherFetcher
{
	protected override Task<(ProcessStartInfo StartInfo, Action? Cleanup)> BuildStartInfoAsync(WatcherEntry entry, CancellationToken cancellationToken)
	{
		if (entry is not PollCommandWatcher command)
			throw new ArgumentException($"{nameof(CommandWatcherFetcher)} only runs poll-command watchers.", nameof(entry));

		var startInfo = new ProcessStartInfo { FileName = command.Fetch.Command };
		foreach (var argument in command.Fetch.Arguments)
			startInfo.ArgumentList.Add(argument);

		return Task.FromResult<(ProcessStartInfo, Action?)>((startInfo, null));
	}
}

/// <summary>
/// Fetcher for <see cref="PollScriptWatcher"/>: runs <c>fetch.scriptFile</c> or an inline
/// <c>fetch.script</c> through <c>fetch.shell</c>.
/// </summary>
public sealed class ScriptWatcherFetcher : ProcessWatcherFetcher
{
	private static readonly UTF8Encoding s_utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

	/// <summary>
	/// Same statements the engine's Script step injects ahead of pwsh scripts: pin the child's
	/// output and input encodings to BOM-less UTF-8 so non-ASCII item fields survive the pipe on
	/// Windows (whose default is the OEM code page, which best-fit transliterates and corrupts
	/// JSON quotes).
	/// </summary>
	private const string PowerShellEncodingPrologue =
		"try { [Console]::OutputEncoding = $OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }; "
		+ "try { [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { };";

	private static readonly Dictionary<string, (string Extension, string[] RunFileArgs, bool IsPowerShell)> s_shells = new(StringComparer.OrdinalIgnoreCase)
	{
		["pwsh"] = (".ps1", ["-NoProfile", "-NonInteractive", "-File"], true),
		["powershell"] = (".ps1", ["-NoProfile", "-NonInteractive", "-File"], true),
		["bash"] = (".sh", [], false),
		["sh"] = (".sh", [], false),
		["zsh"] = (".sh", [], false),
		["python"] = (".py", [], false),
		["python3"] = (".py", [], false),
		["node"] = (".js", [], false),
	};

	protected override async Task<(ProcessStartInfo StartInfo, Action? Cleanup)> BuildStartInfoAsync(WatcherEntry entry, CancellationToken cancellationToken)
	{
		if (entry is not PollScriptWatcher script)
			throw new ArgumentException($"{nameof(ScriptWatcherFetcher)} only runs poll-script watchers.", nameof(entry));

		var fetch = script.Fetch;
		var shell = fetch.Shell.Trim();
		var known = s_shells.TryGetValue(shell, out var shellInfo);
		var extension = known ? shellInfo.Extension : ".script";
		var runFileArgs = known ? shellInfo.RunFileArgs : [];
		var isPowerShell = known && shellInfo.IsPowerShell;

		var tempFiles = new List<string>();
		string scriptPath;

		if (!string.IsNullOrWhiteSpace(fetch.Script))
		{
			scriptPath = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-{Guid.NewGuid():N}{extension}");
			var body = isPowerShell ? PowerShellEncodingPrologue + Environment.NewLine + fetch.Script : fetch.Script;
			await File.WriteAllTextAsync(scriptPath, body, isPowerShell ? s_utf8WithBom : new UTF8Encoding(false), cancellationToken);
			tempFiles.Add(scriptPath);
		}
		else
		{
			scriptPath = ResolvePath(entry, fetch.ScriptFile!);
			if (!File.Exists(scriptPath))
				throw new FileNotFoundException($"script file not found: '{scriptPath}'.", scriptPath);

			if (isPowerShell)
			{
				// The user's file is never modified; a generated wrapper applies the encoding
				// prologue, dot-sources the real script and propagates its exit code.
				var wrapperPath = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-{Guid.NewGuid():N}{extension}");
				var literalPath = scriptPath.Replace("'", "''");
				var wrapper = PowerShellEncodingPrologue + Environment.NewLine
					+ ". '" + literalPath + "' @args" + Environment.NewLine
					+ "if (-not $?) { exit $LASTEXITCODE }" + Environment.NewLine;
				await File.WriteAllTextAsync(wrapperPath, wrapper, s_utf8WithBom, cancellationToken);
				tempFiles.Add(wrapperPath);
				scriptPath = wrapperPath;
			}
		}

		var startInfo = new ProcessStartInfo { FileName = shell };
		foreach (var arg in runFileArgs)
			startInfo.ArgumentList.Add(arg);
		startInfo.ArgumentList.Add(scriptPath);
		foreach (var arg in fetch.Arguments)
			startInfo.ArgumentList.Add(arg);

		if (shell.StartsWith("python", StringComparison.OrdinalIgnoreCase) && !startInfo.Environment.ContainsKey("PYTHONIOENCODING"))
			startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

		Action cleanup = () =>
		{
			foreach (var file in tempFiles)
			{
				try
				{
					if (File.Exists(file))
						File.Delete(file);
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		};

		return (startInfo, cleanup);
	}
}
