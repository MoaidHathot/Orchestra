using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Orchestra.Client;
using Orchestra.Composition;
using Orchestra.Exec;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.McpServer;

namespace Orchestra.Cli.Hosting;

/// <summary>
/// A connected Orchestra instance for the lifetime of a single CLI operation, obtained by either
/// attaching to an already-running server or spawning a throwaway isolated host. This is the
/// shared "connect-or-spawn" machinery behind both <c>orchestra run</c>/<c>exec</c> and the
/// managed Group-A verbs (<c>list</c>/<c>get</c>/<c>register</c>/…): every caller resolves a
/// target instance the same way and tears down only what it spawned.
///
/// Dispose stops and disposes the spawned host (if any) and the client. Use
/// <c>await using</c> so the throwaway instance is always cleaned up — even on exception.
/// </summary>
internal sealed class OrchestraHostSession : IAsyncDisposable
{
	/// <summary>A client bound to <see cref="BaseUrl"/>. Owned by the session — do not dispose it directly.</summary>
	public required OrchestraClient Client { get; init; }

	/// <summary>The base URL the client talks to (a running server, or the spawned host's loopback address).</summary>
	public required string BaseUrl { get; init; }

	/// <summary>True when this session spawned a throwaway host (so the caller knows it owns cleanup,
	/// must not tag/persist transient state, etc.); false when attached to a pre-existing server.</summary>
	public bool Spawned { get; init; }

	/// <summary>The spawned host, when <see cref="Spawned"/> is true; null when attached to a running server.</summary>
	internal WebApplication? App { get; init; }

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();

		if (App is not null)
		{
			// Best-effort graceful stop: a throwaway instance is discarded, so a teardown hiccup
			// must not surface as a command failure.
			try { await App.StopAsync(); }
			catch { /* ignore */ }
			await App.DisposeAsync();
		}
	}
}

/// <summary>
/// Inputs describing how to obtain an <see cref="OrchestraHostSession"/>: the resolved target URL,
/// the connect/spawn mode, and the configuration for a spawned isolated host. The
/// <see cref="ConfigureIsolation"/> delegate is the per-use-case "profile" — <c>run</c>/<c>exec</c>
/// pass a one-shot execution profile; the managed verbs pass an inert management profile.
/// </summary>
internal sealed record HostSessionRequest
{
	/// <summary>Resolved server URL (explicit flag / env / orchestra.json) or null when nothing is configured.</summary>
	public string? ServerUrl { get; init; }

	/// <summary>Connect-or-spawn mode. <c>Auto</c> uses a healthy configured server else spawns;
	/// <c>Existing</c> requires one; <c>Isolated</c> always spawns.</summary>
	public ExecMode Mode { get; init; } = ExecMode.Auto;

	/// <summary>Health-probe timeout for an existing server before falling back to spawn.</summary>
	public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

	// ── Spawn configuration ──────────────────────────────────────────────────────

	/// <summary>When true, the spawned host ignores orchestra.json and co-located service/MCP config
	/// (a fully reproducible instance). Mirrors <c>--no-config</c>.</summary>
	public bool NoConfig { get; init; }

	/// <summary>
	/// Whether the spawned host sets the <c>skip-services</c> flag (don't load/start
	/// orchestra.services.json + orchestra.mcp.json). Null (default) mirrors <see cref="NoConfig"/>.
	/// A management host sets this <c>false</c> so global MCP <em>definitions</em> still load for
	/// correct parsing, while <c>StartExternalServices=false</c> (applied via
	/// <see cref="ConfigureIsolation"/>) keeps the proxies/processes from actually starting.
	/// </summary>
	public bool? SkipExternalServices { get; init; }

	/// <summary>Optional data-path override for the spawned host (else config/default).</summary>
	public string? DataPath { get; init; }

	/// <summary>Optional scan directory for the spawned host (orchestrations/ + profiles/).</summary>
	public string? OrchestrationsPath { get; init; }

	/// <summary>Applies the isolation "profile" to the spawned host options (scheduler/auto-resume/
	/// what-to-load). Required so each caller declares exactly how inert the spawned host should be.</summary>
	public required Action<OrchestrationHostOptions> ConfigureIsolation { get; init; }

	/// <summary>Keep spawned-host console logs at Warning+ so the command's own stdout stays clean.</summary>
	public bool QuietHostLogs { get; init; } = true;

	// ── Messaging ────────────────────────────────────────────────────────────────

	/// <summary>Noun used in the "starting a …" note when auto-mode spawns after finding no server.</summary>
	public string SpawnedInstanceNoun { get; init; } = "temporary instance";

	/// <summary>Labels of spawn-only options the caller passed; when we attach to a running server
	/// instead of spawning, a note explains they were ignored.</summary>
	public IReadOnlyList<string> SpawnOnlyOptionLabels { get; init; } = [];

	// ── Test seams (mirror ExecHooks) ────────────────────────────────────────────

	public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }
	public Action<IServiceCollection>? ConfigureServices { get; init; }
	public Action<IServiceProvider>? OnHostStarted { get; init; }
}

/// <summary>
/// Outcome of <see cref="OrchestraHostSessionFactory.ConnectOrSpawnAsync"/>: either a live
/// <see cref="OrchestraHostSession"/> plus any informational notes to render, or an error message
/// for the unrecoverable cases (existing-mode with nothing reachable / no URL). Returned as data —
/// not thrown and not printed — so callers control exit codes and where notes go (stdout vs stderr).
/// </summary>
internal sealed class HostSessionResult
{
	public OrchestraHostSession? Session { get; private init; }
	public string? ErrorMessage { get; private init; }
	public IReadOnlyList<string> Notes { get; private init; } = [];

	/// <summary>True when a usable session was produced.</summary>
	public bool Ok => Session is not null;

	public static HostSessionResult Connected(OrchestraHostSession session, IReadOnlyList<string> notes)
		=> new() { Session = session, Notes = notes };

	public static HostSessionResult Failed(string error)
		=> new() { ErrorMessage = error };
}

/// <summary>
/// Builds an <see cref="OrchestraHostSession"/> by attaching to a running Orchestra server or
/// spawning a throwaway isolated host, applying the same precedence <c>run</c>/<c>exec</c> use.
/// Extracted from the exec runner so the management verbs reuse the exact host-boot/probe/teardown
/// code path rather than duplicating it.
/// </summary>
internal static class OrchestraHostSessionFactory
{
	public static async Task<HostSessionResult> ConnectOrSpawnAsync(HostSessionRequest request)
	{
		var notes = new List<string>();

		// ── Attach to a running instance? ───────────────────────────────────────────
		// Conservative: only probe when we know a URL (flag / env / orchestra.json).
		// 'isolated' skips detection and always spawns.
		if (request.Mode != ExecMode.Isolated && request.ServerUrl is not null)
		{
			if (await ProbeServerHealthyAsync(request.ServerUrl, request.ProbeTimeout))
			{
				if (request.SpawnOnlyOptionLabels.Count > 0)
				{
					notes.Add(
						$"{string.Join(", ", request.SpawnOnlyOptionLabels)} only apply to a spawned instance and are ignored when using a running one.");
				}

				var attached = new OrchestraHostSession
				{
					Client = new OrchestraClient(request.ServerUrl),
					BaseUrl = request.ServerUrl,
					Spawned = false,
				};
				return HostSessionResult.Connected(attached, notes);
			}

			if (request.Mode == ExecMode.Existing)
			{
				return HostSessionResult.Failed($"no healthy Orchestra instance reachable at {request.ServerUrl}.");
			}

			// auto: configured server is down — fall back to spawning a throwaway instance.
			notes.Add($"No running Orchestra at {request.ServerUrl}; starting a {request.SpawnedInstanceNoun}.");
		}
		else if (request.Mode == ExecMode.Existing)
		{
			return HostSessionResult.Failed(
				"--mode existing requires a server URL (set --server, ORCHESTRA_URL, or hostBaseUrl in orchestra.json).");
		}

		// ── Spawn an isolated instance ───────────────────────────────────────────────
		if (request.Mode == ExecMode.Isolated && request.ServerUrl is not null)
		{
			notes.Add("--server is ignored in isolated mode.");
		}

		var spawned = await SpawnAsync(request);
		return HostSessionResult.Connected(spawned, notes);
	}

	private static async Task<OrchestraHostSession> SpawnAsync(HostSessionRequest request)
	{
		// Pick a free loopback port BEFORE building so the host's HostBaseUrl (used for
		// self-referential /mcp/data callbacks) resolves to the real listening address.
		var url = $"http://127.0.0.1:{GetFreeLoopbackPort()}";

		var builder = WebApplication.CreateBuilder();
		builder.Logging.AddSimpleConsole(o =>
		{
			o.SingleLine = true;
			o.IncludeScopes = false;
			o.TimestampFormat = "HH:mm:ss ";
			o.ColorBehavior = LoggerColorBehavior.Enabled;
		});
		// Spawned-host diagnostics go to stderr so the command's own stdout stays a clean JSON
		// document (or run stream) for piping.
		builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
		if (request.QuietHostLogs)
		{
			// Keep the console focused on the command's own output; host chatter stays at Warning+.
			builder.Logging.SetMinimumLevel(LogLevel.Warning);
		}
		builder.WebHost.UseUrls(url);

		// skip-services controls whether the host loads/starts orchestra.services.json + orchestra.mcp.json.
		// Defaults to mirroring NoConfig (a fully reproducible instance); a management host opts out
		// (SkipExternalServices = false) so global MCP definitions still load for correct parsing, while
		// StartExternalServices (via ConfigureIsolation) keeps the proxies/processes from starting.
		if (request.SkipExternalServices ?? request.NoConfig)
		{
			builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["skip-services"] = "true" });
		}

		builder.Services.AddOrchestraHost(o =>
		{
			if (request.DataPath is not null)
			{
				o.DataPath = Path.GetFullPath(request.DataPath);
			}

			if (request.OrchestrationsPath is not null)
			{
				o.Scan = new ScanConfig
				{
					Directory = Path.GetFullPath(request.OrchestrationsPath),
					Watch = false,
					Recursive = true,
				};
			}

			request.ConfigureIsolation(o);
		}, loadConfigurationFile: !request.NoConfig);

		// Register agent providers (copilot + opencode) + the provider registry: required by the
		// host DI graph (TriggerManager / ChildOrchestrationLauncher) even for read-only verbs.
		builder.Services.AddOrchestraAgentProviders();
		builder.Services.AddOrchestraMcpServer();

		request.ConfigureBuilder?.Invoke(builder);
		request.ConfigureServices?.Invoke(builder.Services);

		var app = builder.Build();

		await app.Services.InitializeOrchestraHostAsync();

		app.UseOrchestraHostProblemDetails();
		app.MapOrchestraHostEndpoints();
		app.MapOrchestraMcpEndpoints();

		await app.StartAsync();
		request.OnHostStarted?.Invoke(app.Services);

		return new OrchestraHostSession
		{
			Client = new OrchestraClient(url),
			BaseUrl = url,
			Spawned = true,
			App = app,
		};
	}

	/// <summary>
	/// Probes <c>GET {baseUrl}/api/health</c> with a short timeout. Returns true only on a 2xx
	/// response; any error (connection refused, timeout, non-Orchestra service) is "not running".
	/// </summary>
	private static async Task<bool> ProbeServerHealthyAsync(string baseUrl, TimeSpan timeout)
	{
		try
		{
			using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = timeout };
			using var response = await http.GetAsync("api/health");
			return response.IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Reserves an ephemeral loopback TCP port by binding a listener to port 0 and reading the
	/// assigned port. There is a small TOCTOU window before Kestrel binds, but on loopback it is
	/// effectively immediate and collisions are vanishingly rare.
	/// </summary>
	private static int GetFreeLoopbackPort()
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
}
