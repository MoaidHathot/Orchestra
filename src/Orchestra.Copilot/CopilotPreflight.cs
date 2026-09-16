using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Orchestra.Copilot;

/// <summary>
/// Where a resolved Copilot CLI binary came from.
/// </summary>
public enum CopilotCliSource
{
	/// <summary>No binary is available yet; the first run will download one.</summary>
	NotInstalled,

	/// <summary>An explicit path supplied via <c>ORCHESTRA_COPILOT_CLI_PATH</c>.</summary>
	ExplicitPath,

	/// <summary>A binary shipped inside the Orchestra installation.</summary>
	Bundled,

	/// <summary>The per-user download cache under local app data.</summary>
	Cache,
}

/// <summary>
/// Result of inspecting the Copilot CLI without downloading anything.
/// </summary>
/// <param name="Available">True when a binary is already usable on this machine.</param>
/// <param name="Path">Resolved binary path, or the cache path it *would* occupy.</param>
/// <param name="Source">How <paramref name="Path"/> was resolved.</param>
/// <param name="Version">
/// CLI version this build of Orchestra <em>pins</em> (what the bootstrap would download). Not
/// necessarily what is on disk: an explicit path can be any version, and the CLI self-updates
/// in place, so a cached binary drifts too. Use <see cref="CopilotPreflight.TryGetInstalledVersionAsync"/>
/// for the version the binary actually reports.
/// </param>
/// <param name="Rid">Runtime identifier the binary is selected for.</param>
/// <param name="Error">Set when the host platform has no published Copilot CLI build.</param>
public sealed record CopilotCliProbe(
	bool Available,
	string? Path,
	CopilotCliSource Source,
	string Version,
	string? Rid,
	string? Error);

/// <summary>
/// Result of an authenticated round-trip to the Copilot backend.
/// </summary>
/// <param name="Ok">True when the CLI started and returned at least one model.</param>
/// <param name="ModelCount">Number of models the account can reach.</param>
/// <param name="Error">Failure detail when <paramref name="Ok"/> is false.</param>
public sealed record CopilotAuthProbe(bool Ok, int ModelCount, string? Error);

/// <summary>
/// Read-only prerequisite checks for the Copilot provider, surfaced by <c>orchestra doctor</c>.
/// </summary>
/// <remarks>
/// Exists because the whole Copilot adapter is internal, and because the normal execution path
/// only discovers a missing CLI or bad credentials <em>mid-run</em> — after the ~100 MB download
/// and after a run scope has been opened. These checks answer both questions up front.
/// </remarks>
public static partial class CopilotPreflight
{
	/// <summary>Environment variable that points at a pre-installed Copilot CLI, bypassing the download.</summary>
	public const string ExplicitCliPathEnvVar = CopilotCliBootstrap.ExplicitCliPathEnvVar;

	/// <summary>Environment variable that redirects the Copilot CLI download to an npm mirror.</summary>
	public const string NpmRegistryEnvVar = CopilotCliBootstrap.NpmRegistryEnvVar;
	/// <summary>
	/// Reports whether a Copilot CLI binary is already present, without ever downloading.
	/// </summary>
	/// <remarks>
	/// Safe to call on any machine: platform-resolution failures are returned as
	/// <see cref="CopilotCliProbe.Error"/> rather than thrown, so doctor can report an
	/// unsupported OS/architecture as one failed check instead of crashing.
	/// </remarks>
	public static CopilotCliProbe Inspect()
	{
		var explicitPath = Environment.GetEnvironmentVariable(CopilotCliBootstrap.ExplicitCliPathEnvVar);
		if (!string.IsNullOrWhiteSpace(explicitPath))
		{
			// The bootstrap returns this verbatim without validating, so mirror that here and
			// only report existence — an override pointing at a missing file is worth flagging.
			return new CopilotCliProbe(
				File.Exists(explicitPath),
				explicitPath,
				CopilotCliSource.ExplicitPath,
				CopilotCliBootstrap.CopilotCliVersion,
				Rid: null,
				Error: File.Exists(explicitPath)
					? null
					: $"{CopilotCliBootstrap.ExplicitCliPathEnvVar} points at '{explicitPath}', which does not exist.");
		}

		string rid, binaryName;
		try
		{
			(rid, _, binaryName) = CopilotCliBootstrap.ResolveHostPlatform();
		}
		catch (PlatformNotSupportedException ex)
		{
			return new CopilotCliProbe(
				false, null, CopilotCliSource.NotInstalled,
				CopilotCliBootstrap.CopilotCliVersion, Rid: null, Error: ex.Message);
		}

		var bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", binaryName);
		if (File.Exists(bundled))
			return new CopilotCliProbe(true, bundled, CopilotCliSource.Bundled, CopilotCliBootstrap.CopilotCliVersion, rid, null);

		var cached = Path.Combine(
			CopilotCliBootstrap.GetCacheDir(rid, CopilotCliBootstrap.CopilotCliVersion),
			binaryName);

		return File.Exists(cached)
			? new CopilotCliProbe(true, cached, CopilotCliSource.Cache, CopilotCliBootstrap.CopilotCliVersion, rid, null)
			: new CopilotCliProbe(false, cached, CopilotCliSource.NotInstalled, CopilotCliBootstrap.CopilotCliVersion, rid, null);
	}

	/// <summary>
	/// Downloads and caches the Copilot CLI if it is not already present, returning its path.
	/// </summary>
	/// <param name="logger">
	/// Receives the bootstrap's structured progress events. Without this the bootstrap logs to
	/// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/> and only writes two
	/// bare lines to stderr.
	/// </param>
	/// <exception cref="CopilotCliBootstrapException">
	/// Every configured npm registry failed; the message names each URL tried and the
	/// environment overrides that fix it.
	/// </exception>
	public static Task<string> EnsureAsync(ILogger? logger = null, CancellationToken cancellationToken = default)
	{
		if (logger is not null)
			CopilotCliBootstrap.SetLogger(logger);

		return CopilotCliBootstrap.EnsureAsync(cancellationToken);
	}

	/// <summary>
	/// Asks the binary at <paramref name="cliPath"/> which version it is (<c>copilot --version</c>)
	/// and returns the bare version string, e.g. <c>1.0.85</c>; null when the binary cannot be
	/// started, times out, or prints nothing recognisable.
	/// </summary>
	/// <remarks>
	/// The pinned <see cref="CopilotCliProbe.Version"/> is what Orchestra <em>expects</em>; this
	/// is what is <em>installed</em>. They legitimately differ when <c>ORCHESTRA_COPILOT_CLI_PATH</c>
	/// points at a user-managed install or after the CLI self-updated in place, and doctor
	/// should say so rather than label a 1.0.85 binary "1.0.67". Costs one short process
	/// launch (~1 s warm), so callers on a hot path should not use it.
	/// </remarks>
	public static async Task<string?> TryGetInstalledVersionAsync(
		string cliPath,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
			return null;

		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));

		using var process = new System.Diagnostics.Process
		{
			StartInfo = new System.Diagnostics.ProcessStartInfo(cliPath, "--version")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			},
		};

		try
		{
			process.Start();

			var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
			var stderr = process.StandardError.ReadToEndAsync(cts.Token);
			await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

			return ParseCliVersion(await stdout.ConfigureAwait(false))
				?? ParseCliVersion(await stderr.ConfigureAwait(false));
		}
		catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
		{
			// Diagnostic only: an unlaunchable or hung binary is reported as "unknown", and the
			// auth probe that follows will surface the real failure with the SDK's own error.
			// Kill a hung process so the probe never leaves a stray copilot behind.
			try
			{
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
			catch
			{
				// Never started, or already gone.
			}

			return null;
		}
	}

	/// <summary>
	/// Extracts the semantic version from the CLI's <c>--version</c> banner
	/// ("GitHub Copilot CLI 1.0.85." / "1.0.84-5"). Null when no version-shaped token exists.
	/// </summary>
	internal static string? ParseCliVersion(string? output)
	{
		if (string.IsNullOrWhiteSpace(output))
			return null;

		var match = CliVersionRegex().Match(output);
		return match.Success ? match.Groups[1].Value.TrimEnd('.') : null;
	}

	// Word-ish boundary on the left (an optional "v" prefix is allowed, as in "v1.0.85") so
	// "CLI 1.0.85" works but ".85" in the middle of a longer dotted token never does; \b on the
	// right stops before the banner's trailing period. Prerelease tags may contain dots and
	// hyphens ("1.0.84-5", "1.0.0-beta-2").
	[GeneratedRegex(@"(?<![\w.])v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\b")]
	private static partial Regex CliVersionRegex();

	/// <summary>
	/// Starts the Copilot CLI and asks it to list models — the cheapest call that actually
	/// exercises credentials and subscription entitlement.
	/// </summary>
	/// <remarks>
	/// Only call this when <see cref="Inspect"/> reports the CLI as available: constructing a
	/// client triggers the one-time ~100 MB download, which a diagnostic command should never
	/// do implicitly.
	/// </remarks>
	public static async Task<CopilotAuthProbe> CheckAuthAsync(
		string? gitHubToken = null,
		bool? useLoggedInUser = null,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));

		ICopilotClient? client = null;
		try
		{
			client = new CopilotSdkClientFactory(
				baseDirectory: null,
				gitHubToken: gitHubToken,
				useLoggedInUser: useLoggedInUser).CreateClient();

			await client.StartAsync(cts.Token).ConfigureAwait(false);

			var models = await client.ListModelsAsync(cts.Token).ConfigureAwait(false);
			return models.Count > 0
				? new CopilotAuthProbe(true, models.Count, null)
				: new CopilotAuthProbe(false, 0, "The Copilot CLI started but reported no available models. This usually means the account has no active GitHub Copilot subscription.");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new CopilotAuthProbe(false, 0, "Timed out waiting for the Copilot CLI to report available models.");
		}
		catch (Exception ex)
		{
			return new CopilotAuthProbe(false, 0, ex.Message);
		}
		finally
		{
			if (client is not null)
			{
				try { await client.DisposeAsync().ConfigureAwait(false); }
				catch { /* best-effort teardown of a diagnostic client */ }
			}
		}
	}
}
