using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Orchestra.Copilot;

/// <summary>
/// First-run bootstrap that downloads the Copilot CLI binary for the current host
/// platform from the same @github/copilot-&lt;platform&gt; npm package the
/// GitHub.Copilot SDK uses at build time, caching it under the user's local app data
/// so the cost is paid once per machine per CLI version.
///
/// <para>
/// Why this exists: the SDK normally bundles the CLI binary into the consuming app's
/// build output at compile time, one RID per build host. For Orchestra's <c>dotnet tool</c>
/// distribution that doesn't work -- tool nupkgs ship a single payload that has to
/// satisfy every consumer OS, and bundling all six supported RIDs blows past NuGet.org's
/// 250 MB package limit (~342 MB observed). Instead the tool ships SMALL (no copilot
/// binary baked in) and this bootstrap fetches just the host's binary the first time
/// any code constructs a <see cref="GitHub.Copilot.CopilotClient"/>.
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
	/// with the SDK NuGet (currently 1.0.67 in SDK 1.0.5). When bumping
	/// <c>GitHub.Copilot.SDK</c> in <c>Directory.Packages.props</c>, update this constant
	/// to match.
	/// </summary>
	public const string CopilotCliVersion = "1.0.67";

	private const string DefaultNpmRegistry = "https://registry.npmjs.org";

	/// <summary>npm scope the CLI packages are published under; drives the <c>@scope:registry</c> lookup.</summary>
	private const string NpmScope = "@github";

	/// <summary>How long to wait for a peer process that already holds the download lock.</summary>
	private static readonly TimeSpan LockAcquisitionTimeout = TimeSpan.FromMinutes(10);

	/// <summary>Poll interval while waiting for the peer's download to finish.</summary>
	private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(500);

	/// <summary>Overall budget for one download attempt (headers + ~100 MB body).</summary>
	private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

	/// <summary>
	/// TCP/TLS connect budget per attempt. A registry that is firewalled by dropping packets
	/// (rather than by a TLS alert) must fail fast so the next candidate gets its turn well
	/// inside <see cref="DownloadTimeout"/>.
	/// </summary>
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

	/// <summary>
	/// Optional override for the npm registry URL. When set it is the <em>only</em> registry
	/// tried. When unset, the registry npm itself is configured with on this machine
	/// (<c>@github:registry</c> / <c>registry</c> from <c>~/.npmrc</c>, or
	/// <c>npm_config_registry</c>) is tried before <see cref="DefaultNpmRegistry"/>, so a
	/// corporate mirror that already works for <c>npm install</c> works here too.
	/// </summary>
	public const string NpmRegistryEnvVar = "ORCHESTRA_COPILOT_NPM_REGISTRY";

	/// <summary>npm's own environment override for the registry (read case-insensitively, as npm does).</summary>
	internal const string NpmConfigRegistryEnvVar = "npm_config_registry";

	/// <summary>npm's environment override for the user config file path (defaults to <c>~/.npmrc</c>).</summary>
	internal const string NpmConfigUserConfigEnvVar = "npm_config_userconfig";

	/// <summary>
	/// Optional override that, when set, bypasses the bootstrap entirely and returns the
	/// given path verbatim. Lets advanced users point at a pre-installed CLI binary (e.g.
	/// the system-wide <c>copilot</c> installed via <c>npm i -g @github/copilot</c>).
	/// </summary>
	public const string ExplicitCliPathEnvVar = "ORCHESTRA_COPILOT_CLI_PATH";

	// One shared download per process. The `!` on s_bootstrapLogger silences a spurious
	// CS8604: the field is initialised below to NullLogger.Instance (non-null), but C#'s
	// static-init ordering analysis can't prove that here.
	//
	// Held behind a lock rather than a plain static Lazy because a Lazy caches its FAULT:
	// one transient network blip during the download would poison every later run in this
	// process, and the only recovery would be a process restart. ResetIfFaulted below drops
	// a failed attempt so the next caller retries.
	private static readonly Lock s_pathLock = new();
	private static Lazy<Task<string>> s_path = CreatePathLazy();

	private static Lazy<Task<string>> CreatePathLazy() => new(
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
		return AwaitWithRetryAsync(cancellationToken);
	}

	private static async Task<string> AwaitWithRetryAsync(CancellationToken cancellationToken)
	{
		var lazy = GetOrCreatePathLazy();

		try
		{
			return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Drop the faulted attempt so the *next* caller starts a fresh download instead
			// of replaying this exception forever. The current caller still sees the failure.
			ResetIfFaulted(lazy);
			throw;
		}
	}

	private static Lazy<Task<string>> GetOrCreatePathLazy()
	{
		lock (s_pathLock)
		{
			return s_path;
		}
	}

	private static void ResetIfFaulted(Lazy<Task<string>> observed)
	{
		lock (s_pathLock)
		{
			// Only reset the instance we actually observed failing; another thread may have
			// already swapped in a fresh one that is mid-flight or has since succeeded.
			if (!ReferenceEquals(s_path, observed))
				return;

			if (observed.IsValueCreated && observed.Value.IsFaulted)
				s_path = CreatePathLazy();
		}
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

	/// <summary>
	/// Acquires the cross-process download lock, waiting for a peer that already holds it.
	/// </summary>
	/// <returns>
	/// The held lock stream, or <c>null</c> when the peer finished the download while we were
	/// waiting (so the caller can just use the cached binary).
	/// </returns>
	/// <remarks>
	/// Opening with <see cref="FileShare.None"/> throws immediately when another process holds
	/// the lock. Without a retry, two <c>orchestra run</c> invocations starting together on a
	/// cold cache meant the second died with a raw "file is being used by another process"
	/// <see cref="IOException"/> — a confusing first-run failure for something the user did
	/// nothing wrong to cause. Poll instead, and give up only if the peer is still going after
	/// the download could plausibly have finished.
	/// </remarks>
	private static async Task<FileStream?> AcquireDownloadLockAsync(
		string lockPath,
		string binaryPath,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var deadline = DateTimeOffset.UtcNow + LockAcquisitionTimeout;
		var logged = false;

		while (true)
		{
			try
			{
				return new FileStream(
					lockPath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None,
					bufferSize: 1,
					options: FileOptions.DeleteOnClose);
			}
			catch (IOException) when (DateTimeOffset.UtcNow < deadline)
			{
				// Peer holds the lock. If it has finished, take the cached binary and go.
				if (File.Exists(binaryPath))
					return null;

				if (!logged)
				{
					LogWaitingForPeerDownload(logger, lockPath);
					WriteProgressToStderr($"Copilot CLI: another process is downloading {CopilotCliVersion}; waiting...");
					logged = true;
				}

				await Task.Delay(LockPollInterval, cancellationToken).ConfigureAwait(false);
			}
		}
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
		using (var lockStream = await AcquireDownloadLockAsync(lockPath, binaryPath, logger, cancellationToken).ConfigureAwait(false))
		{
			// A null stream means another process finished the download while we waited.
			if (lockStream is null)
			{
				LogCacheHit(logger, binaryPath);
				return binaryPath;
			}

			// Re-check inside the lock -- another process may have just finished.
			if (File.Exists(binaryPath))
			{
				LogCacheHit(logger, binaryPath);
				return binaryPath;
			}

			var registries = ResolveRegistryCandidates();
			LogRegistryCandidates(logger, string.Join(", ", registries.Select(r => $"{r.Url} ({r.Source})")));

			var archivePath = Path.Combine(cacheDir, "copilot.tgz");
			var url = await DownloadArchiveAsync(registries, npmPlatform, archivePath, logger, cancellationToken).ConfigureAwait(false);

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

	/// <summary>An npm registry to try, plus where it was configured (for logs and the failure message).</summary>
	internal sealed record NpmRegistryCandidate(string Url, string Source);

	/// <summary>
	/// Resolves the ordered list of npm registries to try for the CLI tarball, from the live
	/// environment and the user's <c>~/.npmrc</c>.
	/// </summary>
	internal static IReadOnlyList<NpmRegistryCandidate> ResolveRegistryCandidates()
		=> ResolveRegistryCandidates(GetEnvironmentVariableAnyCase, TryReadUserNpmrc(GetEnvironmentVariableAnyCase));

	/// <summary>
	/// Pure core of <see cref="ResolveRegistryCandidates()"/>.
	/// </summary>
	/// <remarks>
	/// Order mirrors what <c>npm install @github/copilot</c> would do on this machine, then
	/// falls back to the public registry:
	/// <list type="number">
	/// <item><see cref="NpmRegistryEnvVar"/> when set -- exclusively, no fallback. An explicit
	/// override that is not an absolute http(s) URL is an error rather than silently ignored.</item>
	/// <item><c>@github:registry</c> from <c>~/.npmrc</c> (npm resolves a scoped package's
	/// registry before the unscoped one).</item>
	/// <item><c>npm_config_registry</c> from the environment (npm's env beats its user config).</item>
	/// <item><c>registry</c> from <c>~/.npmrc</c>.</item>
	/// <item><see cref="DefaultNpmRegistry"/>.</item>
	/// </list>
	/// Duplicates and values that are not absolute http(s) URLs are dropped.
	/// </remarks>
	internal static IReadOnlyList<NpmRegistryCandidate> ResolveRegistryCandidates(Func<string, string?> getEnv, string? npmrcContent)
	{
		var explicitOverride = getEnv(NpmRegistryEnvVar);
		if (!string.IsNullOrWhiteSpace(explicitOverride))
		{
			if (!TryNormalizeRegistryUrl(explicitOverride, out var overrideUrl))
			{
				throw new CopilotCliBootstrapException(
					$"{NpmRegistryEnvVar} is set to '{explicitOverride.Trim()}', which is not an absolute http(s) URL. " +
					"Point it at an npm registry root such as https://registry.npmjs.org or your organisation's mirror.");
			}

			return [new NpmRegistryCandidate(overrideUrl, NpmRegistryEnvVar)];
		}

		var npmrc = npmrcContent is null
			? new Dictionary<string, string>(StringComparer.Ordinal)
			: ParseNpmrc(npmrcContent, getEnv);

		var candidates = new List<NpmRegistryCandidate>(4);

		if (npmrc.TryGetValue($"{NpmScope}:registry", out var scoped))
			Add(scoped, $"~/.npmrc {NpmScope}:registry");

		Add(getEnv(NpmConfigRegistryEnvVar), NpmConfigRegistryEnvVar);

		if (npmrc.TryGetValue("registry", out var unscoped))
			Add(unscoped, "~/.npmrc registry");

		Add(DefaultNpmRegistry, "default");

		return candidates;

		void Add(string? value, string source)
		{
			if (!TryNormalizeRegistryUrl(value, out var url))
				return;

			if (candidates.Any(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase)))
				return;

			candidates.Add(new NpmRegistryCandidate(url, source));
		}
	}

	/// <summary>
	/// Accepts absolute http(s) URLs only and strips the trailing slash so URL composition
	/// never yields <c>//@github</c>.
	/// </summary>
	private static bool TryNormalizeRegistryUrl(string? value, out string url)
	{
		url = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
			return false;

		var trimmed = value.Trim();
		if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
			return false;

		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			return false;

		url = trimmed.TrimEnd('/');
		return url.Length > 0;
	}

	/// <summary>
	/// Minimal parser for npm's ini-style <c>.npmrc</c>: <c>key=value</c> lines, <c>#</c>/<c>;</c>
	/// comments, optional surrounding quotes, <c>${VAR}</c> environment expansion. Later keys win.
	/// A value whose <c>${VAR}</c> cannot be resolved is dropped (npm would refuse to load the
	/// file; we just don't want to try a half-expanded URL).
	/// </summary>
	internal static IReadOnlyDictionary<string, string> ParseNpmrc(string content, Func<string, string?> getEnv)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var rawLine in content.Split('\n'))
		{
			var line = rawLine.Trim();
			if (line.Length == 0 || line[0] is '#' or ';' or '[')
				continue;

			var separator = line.IndexOf('=');
			if (separator <= 0)
				continue;

			var key = line[..separator].Trim();
			var value = line[(separator + 1)..].Trim();
			if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
				value = value[1..^1];

			var expanded = TryExpandEnvPlaceholders(value, getEnv);
			if (expanded is null)
				continue;

			result[key] = expanded;
		}

		return result;
	}

	private static string? TryExpandEnvPlaceholders(string value, Func<string, string?> getEnv)
	{
		if (!value.Contains("${", StringComparison.Ordinal))
			return value;

		var unresolved = false;
		var expanded = EnvPlaceholderRegex().Replace(value, match =>
		{
			var resolved = getEnv(match.Groups[1].Value);
			if (resolved is null)
				unresolved = true;
			return resolved ?? string.Empty;
		});

		return unresolved ? null : expanded;
	}

	[GeneratedRegex(@"\$\{([^}]+)\}")]
	private static partial Regex EnvPlaceholderRegex();

	/// <summary>
	/// Reads the user-level <c>.npmrc</c> (honouring <c>npm_config_userconfig</c>), or null when
	/// there is none or it cannot be read. Never throws: npm config is a hint, not a dependency.
	/// </summary>
	internal static string? TryReadUserNpmrc(Func<string, string?> getEnv)
	{
		try
		{
			var path = getEnv(NpmConfigUserConfigEnvVar);
			if (string.IsNullOrWhiteSpace(path))
			{
				var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				if (string.IsNullOrEmpty(home))
					return null;

				path = Path.Combine(home, ".npmrc");
			}

			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return null;
		}
	}

	/// <summary>
	/// npm reads <c>npm_config_*</c> variables case-insensitively on every OS; Windows already
	/// does, Unix needs the upper-case spelling tried explicitly.
	/// </summary>
	private static string? GetEnvironmentVariableAnyCase(string name)
		=> Environment.GetEnvironmentVariable(name)
		   ?? Environment.GetEnvironmentVariable(name.ToUpperInvariant())
		   ?? Environment.GetEnvironmentVariable(name.ToLowerInvariant());

	/// <summary>Composes the npm tarball URL the SDK's own MSBuild target uses (<c>_CopilotDownloadUrl</c>).</summary>
	internal static string BuildDownloadUrl(string registry, string npmPlatform, string version)
		=> $"{registry.TrimEnd('/')}/{NpmScope}/copilot-{npmPlatform}/-/copilot-{npmPlatform}-{version}.tgz";

	/// <summary>
	/// Downloads the CLI tarball to <paramref name="archivePath"/>, trying each registry in
	/// order until one succeeds. Returns the URL that worked.
	/// </summary>
	/// <exception cref="CopilotCliBootstrapException">
	/// Every registry failed. The message lists each URL with its reason and the environment
	/// overrides that fix the common causes.
	/// </exception>
	/// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
	internal static async Task<string> DownloadArchiveAsync(
		IReadOnlyList<NpmRegistryCandidate> registries,
		string npmPlatform,
		string archivePath,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		if (registries.Count == 0)
			throw new ArgumentException("At least one npm registry is required.", nameof(registries));

		var failures = new List<(string Url, string Source, Exception Error)>();

		using var handler = new SocketsHttpHandler { ConnectTimeout = ConnectTimeout };
		using var http = new HttpClient(handler) { Timeout = DownloadTimeout };

		for (var i = 0; i < registries.Count; i++)
		{
			var candidate = registries[i];
			var url = BuildDownloadUrl(candidate.Url, npmPlatform, CopilotCliVersion);

			LogDownloadStarting(logger, CopilotCliVersion, npmPlatform, url);
			// Stderr write so the user-visible progress shows up even when the host hasn't
			// wired CopilotCliBootstrap.SetLogger (which is the default for the Portal /
			// Server hosts today). The download blocks the calling thread for ~30-90 s on
			// a fresh install; without this line the tool appears to hang silently and
			// users open issues thinking it's stuck. Stderr (not stdout) so machine-
			// readable consumers piping `orchestra` JSON output aren't polluted.
			WriteProgressToStderr($"Copilot CLI: downloading {CopilotCliVersion} for {npmPlatform} from {url} (one-time setup, ~100 MB)...");

			try
			{
				using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				await using (var file = File.Create(archivePath))
				{
					await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
				}

				return url;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				TryDeleteFile(archivePath);
				throw;
			}
			catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
			{
				// HttpRequestException: DNS, TCP, TLS, or a non-2xx status (EnsureSuccessStatusCode).
				// IOException: the connection dropped mid-body. OperationCanceledException here is
				// HttpClient's own timeout -- the caller's token was handled by the filter above.
				TryDeleteFile(archivePath);

				var reason = DescribeFailure(ex);
				failures.Add((url, candidate.Source, ex));
				LogDownloadAttemptFailed(logger, ex, url, candidate.Source, reason);

				var next = i + 1 < registries.Count ? $"; trying {registries[i + 1].Url} next" : string.Empty;
				WriteProgressToStderr($"Copilot CLI: download from {url} failed: {reason}{next}");
			}
		}

		var message = BuildDownloadFailureMessage(npmPlatform, failures.Select(f => (f.Url, f.Source, DescribeFailure(f.Error))));
		Exception inner = failures.Count == 1 ? failures[0].Error : new AggregateException(failures.Select(f => f.Error));
		throw new CopilotCliBootstrapException(message, inner);
	}

	/// <summary>
	/// Flattens an exception chain into one line: the outer message plus up to two inner
	/// messages, so a TLS failure reads as the alert that caused it rather than "see inner
	/// exception".
	/// </summary>
	internal static string DescribeFailure(Exception exception)
	{
		var parts = new List<string>(3);
		for (var current = exception; current is not null && parts.Count < 3; current = current.InnerException)
		{
			var text = current.Message.Replace(", see inner exception", string.Empty, StringComparison.Ordinal).Trim();
			if (text.Length > 0 && !parts.Contains(text, StringComparer.Ordinal))
				parts.Add(text);
		}

		return parts.Count == 0 ? exception.GetType().Name : string.Join(" -> ", parts);
	}

	/// <summary>Composes the message for <see cref="CopilotCliBootstrapException"/> after every registry failed.</summary>
	internal static string BuildDownloadFailureMessage(string npmPlatform, IEnumerable<(string Url, string Source, string Reason)> attempts)
	{
		var builder = new StringBuilder();
		builder.Append("Copilot CLI ").Append(CopilotCliVersion).Append(" for ").Append(npmPlatform)
			.AppendLine(" could not be downloaded from any npm registry:");

		foreach (var (url, source, reason) in attempts)
			builder.Append("  - ").Append(url).Append(" [").Append(source).Append("]: ").AppendLine(reason);

		builder.Append("If this machine reaches npm through a mirror or proxy, set ").Append(NpmRegistryEnvVar)
			.Append(" to its URL (Orchestra also honours '@github:registry' and 'registry' from ~/.npmrc). ")
			.Append("To skip the download entirely, set ").Append(ExplicitCliPathEnvVar)
			.Append(" to a pre-installed Copilot CLI binary, e.g. from 'npm i -g @github/copilot'.");

		return builder.ToString();
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
			// Best-effort: a stale partial archive in our own cache dir is overwritten next time.
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

	[LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Copilot CLI bootstrap: waiting for another process holding {LockPath}")]
	private static partial void LogWaitingForPeerDownload(ILogger logger, string lockPath);

	[LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Copilot CLI bootstrap: download from {Url} ({Source}) failed: {Reason}")]
	private static partial void LogDownloadAttemptFailed(ILogger logger, Exception exception, string url, string source, string reason);

	[LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "Copilot CLI bootstrap: npm registry candidates in order: {Registries}")]
	private static partial void LogRegistryCandidates(ILogger logger, string registries);
}
