using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Orchestra.Copilot;

/// <summary>
/// First-run bootstrap that downloads the Copilot CLI binary for the current host
/// platform from the same @github/copilot-&lt;platform&gt; npm package the
/// GitHub.Copilot.SDK uses at build time, caching it under the user's local app data
/// so the cost is paid once per machine per CLI version.
///
/// <para>
/// Why this exists: the SDK normally bundles the CLI binary into the consuming app's
/// build output at compile time, one RID per build host. For Orchestra's <c>dotnet tool</c>
/// distribution that doesn't work -- tool nupkgs ship a single payload that has to
/// satisfy every consumer OS, and bundling all six supported RIDs blows past NuGet.org's
/// 250 MB package limit (~342 MB observed). Instead the tool ships SMALL (no copilot
/// binary baked in) and this bootstrap fetches just the host's binary the first time
/// any code constructs a <see cref="GitHub.Copilot.SDK.CopilotClient"/>.
/// </para>
/// <para>
/// The download is gated by a <see cref="Lazy{T}"/> over a <see cref="Task{TResult}"/>
/// so that concurrent first-run callers (e.g. a multi-step orchestration that builds
/// several agents in parallel) share a single download instead of racing each other.
/// </para>
/// </summary>
internal static partial class CopilotCliBootstrap
{
	/// <summary>
	/// Mirrors <c>$(CopilotCliVersion)</c> in <c>GitHub.Copilot.SDK.props</c> shipped
	/// with the SDK NuGet (currently 1.0.36-0 in SDK 0.3.0). When bumping
	/// <c>GitHub.Copilot.SDK</c> in <c>Directory.Packages.props</c>, update this constant
	/// to match.
	/// </summary>
	public const string CopilotCliVersion = "1.0.36-0";

	private const string DefaultNpmRegistry = "https://registry.npmjs.org";

	/// <summary>
	/// Optional override for the npm registry URL. Honored when set; falls back to
	/// <see cref="DefaultNpmRegistry"/>. Useful for org-internal mirrors.
	/// </summary>
	public const string NpmRegistryEnvVar = "ORCHESTRA_COPILOT_NPM_REGISTRY";

	/// <summary>
	/// Optional override that, when set, bypasses the bootstrap entirely and returns the
	/// given path verbatim. Lets advanced users point at a pre-installed CLI binary (e.g.
	/// the system-wide <c>copilot</c> installed via <c>npm i -g @github/copilot</c>).
	/// </summary>
	public const string ExplicitCliPathEnvVar = "ORCHESTRA_COPILOT_CLI_PATH";

	// One shared download per process. Reset only by full process restart, which is fine --
	// the on-disk cache is the source of truth across restarts. The `!` on s_bootstrapLogger
	// silences a spurious CS8604: the field is initialised below to NullLogger.Instance
	// (non-null), but C#'s static-init ordering analysis can't prove that here.
	private static readonly Lazy<Task<string>> s_path = new(
		() => EnsureCoreAsync(s_bootstrapLogger!, CancellationToken.None),
		LazyThreadSafetyMode.ExecutionAndPublication);

	private static ILogger s_bootstrapLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

	/// <summary>
	/// Sets the logger used by the lazy bootstrap. Idempotent and best-effort: only the
	/// first non-Null assignment wins so a late-arriving Null doesn't silence ongoing
	/// progress messages. Callers that want a logger should set it BEFORE the first
	/// <see cref="EnsureAsync"/> call (e.g., during host startup).
	/// </summary>
	public static void SetLogger(ILogger logger)
	{
		if (logger is not Microsoft.Extensions.Logging.Abstractions.NullLogger)
		{
			s_bootstrapLogger = logger;
		}
	}

	/// <summary>
	/// Ensures the Copilot CLI binary for the current host platform is available on disk
	/// and returns its absolute path. Safe to call concurrently; only one download happens
	/// per process. The first call may take a minute or two on a cold cache; subsequent
	/// calls (and subsequent runs on the same machine) return immediately.
	/// </summary>
	/// <param name="cancellationToken">
	/// Cancels the wait if the bootstrap is still running. Once a download has completed,
	/// cancellation has no effect -- the cached path is returned.
	/// </param>
	/// <returns>Absolute path to a runnable <c>copilot</c> / <c>copilot.exe</c> binary.</returns>
	public static Task<string> EnsureAsync(CancellationToken cancellationToken = default)
	{
		// Explicit override wins -- no download, no validation. The user knows where their
		// binary lives. (Validated when the SDK actually tries to launch it.)
		var overridePath = Environment.GetEnvironmentVariable(ExplicitCliPathEnvVar);
		if (!string.IsNullOrWhiteSpace(overridePath))
		{
			return Task.FromResult(overridePath.Trim());
		}

		// Wrap to honor the per-call cancellation token even though the Lazy task itself
		// is unforked (so a second caller cancelling can't cancel the first's download).
		return s_path.Value.WaitAsync(cancellationToken);
	}

	/// <summary>
	/// Resolves the (rid, npmPlatform, binaryName) triple describing the build host. We
	/// rely on <see cref="RuntimeInformation"/> rather than the SDK's portable RID so the
	/// bootstrap stays self-contained (no MSBuild-resolved property at runtime).
	/// </summary>
	internal static (string Rid, string NpmPlatform, string BinaryName) ResolveHostPlatform()
	{
		string os;
		string npmOs;
		string binaryName;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			os = "win"; npmOs = "win32"; binaryName = "copilot.exe";
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			os = "osx"; npmOs = "darwin"; binaryName = "copilot";
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			os = "linux"; npmOs = "linux"; binaryName = "copilot";
		}
		else
		{
			throw new PlatformNotSupportedException(
				$"Copilot CLI does not publish binaries for OS '{RuntimeInformation.OSDescription}'. " +
				$"Set the {ExplicitCliPathEnvVar} environment variable to point at a manually-installed binary.");
		}

		var arch = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => throw new PlatformNotSupportedException(
				$"Copilot CLI does not publish binaries for processor architecture '{RuntimeInformation.ProcessArchitecture}'. " +
				$"Supported: x64, arm64. Set {ExplicitCliPathEnvVar} to override."),
		};

		return ($"{os}-{arch}", $"{npmOs}-{arch}", binaryName);
	}

	/// <summary>
	/// Returns the absolute cache directory for a given RID and CLI version. The path is
	/// stable across runs and tool versions so an upgrade that keeps the same Copilot CLI
	/// version re-uses the existing cached binary.
	/// </summary>
	internal static string GetCacheDir(string rid, string cliVersion)
	{
		// SpecialFolder.LocalApplicationData resolves to:
		//   Windows: %LOCALAPPDATA%        (e.g. C:\Users\<u>\AppData\Local)
		//   macOS:   ~/.local/share        (or $XDG_DATA_HOME)
		//   Linux:   $XDG_DATA_HOME or ~/.local/share
		var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrEmpty(root))
		{
			// Last-resort fallback. SpecialFolder returns "" only in very stripped
			// environments; the user's $HOME should still exist.
			root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
		}
		return Path.Combine(root, "Orchestra", "copilot-cli", cliVersion, rid);
	}

	private static async Task<string> EnsureCoreAsync(ILogger logger, CancellationToken cancellationToken)
	{
		var (rid, npmPlatform, binaryName) = ResolveHostPlatform();

		// 1) Prefer a binary already bundled next to our entry assembly at the SDK's
		//    expected path (<AppContext.BaseDirectory>/runtimes/<rid>/native/<binary>).
		//    This is where the SDK's build-time auto-download deposits the host's binary
		//    when projects that DO have CopilotSkipCliDownload=false reference us. Tool
		//    nupkgs packed on a host whose RID matches the user's will hit this fast path.
		var bundledPath = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", binaryName);
		if (File.Exists(bundledPath))
		{
			LogBundledHit(logger, bundledPath);
			return bundledPath;
		}

		// 2) Fall back to the per-user download cache.
		var cacheDir = GetCacheDir(rid, CopilotCliVersion);
		var binaryPath = Path.Combine(cacheDir, binaryName);

		if (File.Exists(binaryPath))
		{
			LogCacheHit(logger, binaryPath);
			return binaryPath;
		}

		Directory.CreateDirectory(cacheDir);

		// Hold a coarse per-directory lock so two concurrent processes (e.g. two
		// orchestra invocations starting in parallel) don't half-write the binary.
		// The file lock is released the moment the using block exits, including on
		// exception, so a crashed download never leaves the lock pinned.
		var lockPath = Path.Combine(cacheDir, ".download.lock");
		using (var lockStream = new FileStream(
			lockPath,
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None,
			bufferSize: 1,
			options: FileOptions.DeleteOnClose))
		{
			// Re-check inside the lock -- another process may have just finished.
			if (File.Exists(binaryPath))
			{
				LogCacheHit(logger, binaryPath);
				return binaryPath;
			}

			var registry = (Environment.GetEnvironmentVariable(NpmRegistryEnvVar) ?? DefaultNpmRegistry).TrimEnd('/');
			var url = $"{registry}/@github/copilot-{npmPlatform}/-/copilot-{npmPlatform}-{CopilotCliVersion}.tgz";
			var archivePath = Path.Combine(cacheDir, "copilot.tgz");

			LogDownloadStarting(logger, CopilotCliVersion, npmPlatform, url);
			// Stderr write so the user-visible progress shows up even when the host hasn't
			// wired CopilotCliBootstrap.SetLogger (which is the default for the Portal /
			// Server hosts today). The download blocks the calling thread for ~30-90 s on
			// a fresh install; without this line the tool appears to hang silently and
			// users open issues thinking it's stuck. Stderr (not stdout) so machine-
			// readable consumers piping `orchestra` JSON output aren't polluted.
			WriteProgressToStderr($"Copilot CLI: downloading {CopilotCliVersion} for {npmPlatform} from {url} (one-time setup, ~100 MB)...");

			using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
			{
				using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				await using var file = File.Create(archivePath);
				await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
			}

			LogExtractStarting(logger, archivePath, cacheDir);
			await ExtractTarGzAsync(archivePath, cacheDir, cancellationToken).ConfigureAwait(false);

			try { File.Delete(archivePath); } catch { /* best-effort: archive is in our cache */ }

			if (!File.Exists(binaryPath))
			{
				throw new InvalidOperationException(
					$"Copilot CLI bootstrap downloaded and extracted '{url}' but the expected binary '{binaryName}' was not found at '{binaryPath}'. " +
					"The npm package layout may have changed; please file an issue against Orchestra.");
			}

			if (!OperatingSystem.IsWindows())
			{
				// chmod 755 -- the SDK Process.Start will refuse to launch a non-executable file on Unix.
				File.SetUnixFileMode(
					binaryPath,
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
					UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
					UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
			}

			LogDownloadCompleted(logger, binaryPath, new FileInfo(binaryPath).Length);
			WriteProgressToStderr($"Copilot CLI: ready at {binaryPath}");
		}

		return binaryPath;
	}

	/// <summary>
	/// Writes a single line to stderr without any formatter dependency, so first-run
	/// progress is visible to the user regardless of how the host wires ILogger.
	/// Best-effort: any I/O failure (e.g., stderr redirected to a broken pipe) is
	/// swallowed -- the visible message is a courtesy, not a contract.
	/// </summary>
	private static void WriteProgressToStderr(string message)
	{
		try
		{
			Console.Error.WriteLine(message);
		}
		catch
		{
			// Ignore. The structured log via ILogger is the authoritative record.
		}
	}

	/// <summary>
	/// Extracts a gzipped tar archive into <paramref name="targetDir"/>, flattening the
	/// npm-conventional top-level <c>package/</c> prefix so the binary lands at
	/// <c>targetDir/copilot[.exe]</c> rather than <c>targetDir/package/copilot[.exe]</c>.
	/// Matches what the SDK's MSBuild target does with <c>tar --strip-components=1</c>.
	/// </summary>
	private static async Task ExtractTarGzAsync(string archivePath, string targetDir, CancellationToken cancellationToken)
	{
		await using var fileStream = File.OpenRead(archivePath);
		await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);

		using var tarReader = new TarReader(gzipStream, leaveOpen: false);
		while (await tarReader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
		{
			// Strip the leading "package/" segment (npm tarball convention).
			var name = entry.Name;
			var firstSlash = name.IndexOf('/');
			if (firstSlash < 0)
			{
				// Top-level files outside the package dir -- skip; nothing we need lives here.
				continue;
			}
			var relative = name[(firstSlash + 1)..];
			if (string.IsNullOrEmpty(relative)) continue;

			var destinationPath = Path.GetFullPath(Path.Combine(targetDir, relative));

			// Defensive: refuse paths that escape the target dir (tar slip / zip slip).
			if (!destinationPath.StartsWith(Path.GetFullPath(targetDir), StringComparison.Ordinal))
			{
				throw new InvalidOperationException($"Tar entry '{name}' escapes the extraction directory.");
			}

			switch (entry.EntryType)
			{
				case TarEntryType.Directory:
					Directory.CreateDirectory(destinationPath);
					break;
				case TarEntryType.RegularFile:
				case TarEntryType.V7RegularFile:
				case TarEntryType.ContiguousFile:
					var parent = Path.GetDirectoryName(destinationPath);
					if (parent is not null) Directory.CreateDirectory(parent);
					await entry.ExtractToFileAsync(destinationPath, overwrite: true, cancellationToken).ConfigureAwait(false);
					break;
				default:
					// Skip symlinks, char devices, block devices etc. The Copilot CLI tarball
					// contains plain files only; anything exotic is suspicious and ignored.
					break;
			}
		}
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Copilot CLI bootstrap: cache hit at {Path}")]
	private static partial void LogCacheHit(ILogger logger, string path);

	[LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Copilot CLI bootstrap: bundled binary found at {Path} (no download needed)")]
	private static partial void LogBundledHit(ILogger logger, string path);

	[LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Copilot CLI bootstrap: downloading {Version} for {Platform} from {Url} (one-time setup, ~100 MB)...")]
	private static partial void LogDownloadStarting(ILogger logger, string version, string platform, string url);

	[LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Copilot CLI bootstrap: extracting {Archive} to {Target}")]
	private static partial void LogExtractStarting(ILogger logger, string archive, string target);

	[LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Copilot CLI bootstrap: ready at {Path} ({SizeBytes} bytes)")]
	private static partial void LogDownloadCompleted(ILogger logger, string path, long sizeBytes);
}
