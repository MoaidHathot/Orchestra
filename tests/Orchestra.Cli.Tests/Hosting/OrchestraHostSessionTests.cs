using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Orchestra.Cli.Commands;
using Orchestra.Cli.Hosting;
using Orchestra.Exec;
using Orchestra.Host.Hosting;
using Xunit;

namespace Orchestra.Cli.Tests.Hosting;

/// <summary>
/// Integration tests for the shared connect-or-spawn machinery behind the managed Group-A verbs:
/// <see cref="OrchestraHostSessionFactory"/> and the high-level <see cref="ManagedSession"/>. Each
/// test either spawns a real inert host on an ephemeral loopback port or attaches to one, then
/// drives it over the loopback client — mirroring how <c>orchestra list</c>/<c>get</c>/… run when
/// no server is configured. All spawns are hermetic (<c>NoConfig</c> ⇒ ignore orchestra.json and
/// skip the developer's service/MCP config) so the tests don't depend on the host machine.
/// </summary>
[Collection("orchestra-host-session-serial")]
public sealed class OrchestraHostSessionTests : IDisposable
{
	private readonly List<string> _tempDirs = [];

	public void Dispose()
	{
		foreach (var dir in _tempDirs)
		{
			try { Directory.Delete(dir, recursive: true); }
			catch { /* best-effort */ }
		}
	}

	private string NewTempDir()
	{
		var dir = Path.Combine(Path.GetTempPath(), "orchestra-cli-session-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		_tempDirs.Add(dir);
		return dir;
	}

	/// <summary>The inert management profile mirrored from <c>ManagedSession.ConfigureManagementHost</c>.</summary>
	private static void Inert(OrchestrationHostOptions o)
	{
		o.EnableScheduler = false;
		o.AutoResumeCheckpointsOnStartup = false;
		o.LoadPersistedOrchestrations = true;
		o.RegisterJsonTriggers = true;
	}

	private HostSessionRequest SpawnRequest(ExecMode mode, string? serverUrl = null, string? orchestrationsPath = null) => new()
	{
		ServerUrl = serverUrl,
		Mode = mode,
		NoConfig = true,
		DataPath = NewTempDir(),
		OrchestrationsPath = orchestrationsPath,
		ConfigureIsolation = Inert,
	};

	// ── Spawn ────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Auto_NothingConfigured_SpawnsIsolatedHost_AndServesRegistry()
	{
		var result = await OrchestraHostSessionFactory.ConnectOrSpawnAsync(SpawnRequest(ExecMode.Auto));

		result.Ok.Should().BeTrue();
		await using var session = result.Session!;
		session.Spawned.Should().BeTrue("nothing is configured, so auto must spawn a throwaway host");

		var list = await session.Client.ListOrchestrationsAsync();
		list.GetProperty("count").GetInt32().Should().Be(0, "a fresh data path has no orchestrations");
	}

	[Fact]
	public async Task SpawnedHost_StatusEndpoint_AdvertisesAllRegisteredProviders()
	{
		// Black-box check that a CLI-spawned host composes the multi-provider registry: its
		// /api/status must list both built-in providers (copilot + opencode) and a default
		// provider. A single-provider misconfiguration (like the Portal bug that silently ran
		// `provider: opencode` on Copilot) would surface here as a missing 'opencode' entry.
		var result = await OrchestraHostSessionFactory.ConnectOrSpawnAsync(SpawnRequest(ExecMode.Auto));

		result.Ok.Should().BeTrue();
		await using var session = result.Session!;

		var status = await session.Client.GetStatusAsync();

		status.TryGetProperty("defaultProvider", out var defaultProvider).Should().BeTrue(
			"status must report the host's default agent provider");
		defaultProvider.GetString().Should().NotBeNullOrWhiteSpace();

		status.TryGetProperty("providers", out var providers).Should().BeTrue(
			"status must list every registered agent provider");
		providers.ValueKind.Should().Be(JsonValueKind.Array);
		var providerNames = providers.EnumerateArray().Select(p => p.GetString()).ToArray();
		providerNames.Should().Contain("copilot").And.Contain("opencode",
			"a spawned host must register both built-in providers so per-step `provider` is honored");
	}

	[Fact]
	public async Task Isolated_IgnoresServerUrl_AndAlwaysSpawns()
	{
		var result = await OrchestraHostSessionFactory.ConnectOrSpawnAsync(
			SpawnRequest(ExecMode.Isolated, serverUrl: "http://127.0.0.1:5099"));

		result.Ok.Should().BeTrue();
		await using var session = result.Session!;
		session.Spawned.Should().BeTrue();
		session.BaseUrl.Should().NotBe("http://127.0.0.1:5099");
		result.Notes.Should().Contain(n => n.Contains("--server is ignored"));
	}

	// ── Attach ───────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Auto_HealthyServer_Attaches_AndLeavesItUpAfterDispose()
	{
		// Use a spawned host as the "already-running server" to attach to — no external infra.
		await using var running = (await OrchestraHostSessionFactory.ConnectOrSpawnAsync(
			SpawnRequest(ExecMode.Isolated))).Session!;

		var result = await OrchestraHostSessionFactory.ConnectOrSpawnAsync(new HostSessionRequest
		{
			ServerUrl = running.BaseUrl,
			Mode = ExecMode.Auto,
			ConfigureIsolation = Inert,
		});

		result.Ok.Should().BeTrue();
		result.Session!.Spawned.Should().BeFalse("a healthy server is reachable, so auto attaches");
		result.Session.BaseUrl.Should().Be(running.BaseUrl);

		// Disposing the attached session must NOT tear down the server we attached to.
		await result.Session.DisposeAsync();
		(await IsHealthyAsync(running.BaseUrl)).Should().BeTrue("disposing an attached session must leave the server running");
	}

	// ── Existing-mode failures (no spawn) ──────────────────────────────────────────

	[Fact]
	public async Task Existing_NoReachableServer_Fails()
	{
		var result = await OrchestraHostSessionFactory.ConnectOrSpawnAsync(new HostSessionRequest
		{
			ServerUrl = $"http://127.0.0.1:{FreePort()}",
			Mode = ExecMode.Existing,
			ProbeTimeout = TimeSpan.FromSeconds(1),
			ConfigureIsolation = Inert,
		});

		result.Ok.Should().BeFalse();
		result.Session.Should().BeNull();
		result.ErrorMessage.Should().Contain("no healthy");
	}

	[Fact]
	public async Task Existing_NoServerUrl_Fails()
	{
		var result = await OrchestraHostSessionFactory.ConnectOrSpawnAsync(new HostSessionRequest
		{
			ServerUrl = null,
			Mode = ExecMode.Existing,
			ConfigureIsolation = Inert,
		});

		result.Ok.Should().BeFalse();
		result.ErrorMessage.Should().Contain("requires a server URL");
	}

	// ── Inert host serves triggers (JSON-declared) ─────────────────────────────────

	[Fact]
	public async Task ManagementHost_ServesJsonDeclaredTriggers_WithoutFiring()
	{
		var workspace = NewTempDir();
		WriteOrchestrationWithSchedulerTrigger(workspace, "mgmt-trig");

		await using var session = (await OrchestraHostSessionFactory.ConnectOrSpawnAsync(
			SpawnRequest(ExecMode.Auto, orchestrationsPath: workspace))).Session!;

		// The scanned orchestration's scheduler trigger is registered and visible for inspection,
		// even though the inert host's scheduler is disabled (it never fires).
		var triggers = await session.Client.ListTriggersAsync();
		triggers.GetRawText().Should().Contain("mgmt-trig");
	}

	// ── High-level ManagedSession (real management profile + URL resolution) ────────

	[Fact]
	public async Task ManagedSession_AutoNoConfig_SpawnsAndRunsAction_ExitZero()
	{
		JsonElement listed = default;
		var settings = new ListSettings { NoConfig = true, DataPath = NewTempDir() };

		var exit = await ManagedSession.RunAsync(settings, async client =>
		{
			listed = await client.ListOrchestrationsAsync();
		});

		exit.Should().Be(0);
		listed.GetProperty("count").GetInt32().Should().Be(0);
	}

	[Fact]
	public async Task ManagedSession_ExistingMode_NoServer_ReturnsLaunchError()
	{
		var settings = new ListSettings
		{
			Mode = "existing",
			Server = $"http://127.0.0.1:{FreePort()}",
		};

		var exit = await ManagedSession.RunAsync(settings, _ => Task.CompletedTask);

		exit.Should().Be(ManagedSession.LaunchErrorExitCode);
	}

	// ── Group-B live verbs: connection failure → exit 1 (friendly message) ──────────

	[Fact]
	public async Task LiveServerCommand_ConnectionRefused_ReturnsExitOne()
	{
		var settings = new JsonOutputSettings { Server = $"http://127.0.0.1:{FreePort()}" };

		var exit = await LiveServerCommand.RunAsync(settings, "server-status", async client =>
		{
			// Nothing is listening, so the request fails at the connection level (StatusCode null) —
			// LiveServerCommand reinterprets that as a friendly "couldn't reach" error and returns 1.
			await client.GetStatusAsync();
		});

		exit.Should().Be(1);
	}

	// ── helpers ────────────────────────────────────────────────────────────────────

	private static void WriteOrchestrationWithSchedulerTrigger(string workspace, string name)
	{
		var dir = Path.Combine(workspace, "orchestrations");
		Directory.CreateDirectory(dir);
		var orchestration = new
		{
			name,
			description = "management trigger test",
			version = "1.0.0",
			model = "claude-opus-4.6",
			trigger = new { type = "scheduler", enabled = true, intervalSeconds = 3600 },
			steps = new[]
			{
				new { name = "s", type = "prompt", systemPrompt = "x", userPrompt = "y", model = "claude-opus-4.6" },
			},
		};
		File.WriteAllText(Path.Combine(dir, $"{name}.json"), JsonSerializer.Serialize(orchestration));
	}

	private static async Task<bool> IsHealthyAsync(string baseUrl)
	{
		try
		{
			using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(2) };
			return (await http.GetAsync("api/health")).IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}

	private static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
		finally { listener.Stop(); }
	}
}

/// <summary>Serializes host-spawning session tests to keep loopback port/startup usage predictable.</summary>
[CollectionDefinition("orchestra-host-session-serial", DisableParallelization = true)]
public sealed class OrchestraHostSessionSerialCollection;
