using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Client;
using Orchestra.Client.Run;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Exec.Tests;

/// <summary>
/// End-to-end tests for <c>orchestra-exec</c>: each test boots the real isolated host on an
/// ephemeral loopback port, runs one orchestration via the loopback client (with a fake agent
/// so no Copilot CLI is needed), then asserts the process exit code and isolation guarantees.
/// </summary>
[Collection("exec-serial")]
public sealed class ExecRunTests : IDisposable
{
	private readonly List<string> _tempDirs = [];
	private readonly Dictionary<string, string?> _savedEnvVars = new();

	public ExecRunTests()
	{
		// Hermetic config discovery. These tests assert the behavior of exec's *isolated* spawned
		// host (and auto-mode's spawn-when-nothing-is-configured fallback). Without this, a
		// developer who happens to have a real Orchestra configured in orchestra.json / via
		// $ORCHESTRA_URL — and running — would have auto-mode resolve and connect to THAT server
		// instead of spawning the test host, bypassing the fake agent and onStarted hook and
		// failing the assertions. Plant an empty config and clear the discovery env vars so
		// "nothing is configured" is guaranteed regardless of the host machine. Mutating process
		// env here is safe: this collection is serialized (DisableParallelization) and runs in the
		// Exec.Tests assembly's own test process.
		SaveAndSet("ORCHESTRA_URL", null);
		SaveAndSet("XDG_CONFIG_HOME", null);
		var emptyConfig = Path.Combine(NewTempDir(), "orchestra.json");
		File.WriteAllText(emptyConfig, "{}");
		SaveAndSet("ORCHESTRA_CONFIG_PATH", emptyConfig);
	}

	public void Dispose()
	{
		foreach (var kv in _savedEnvVars)
			Environment.SetEnvironmentVariable(kv.Key, kv.Value);

		foreach (var dir in _tempDirs)
		{
			try { Directory.Delete(dir, recursive: true); }
			catch { /* best-effort */ }
		}
	}

	private void SaveAndSet(string name, string? value)
	{
		_savedEnvVars[name] = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, value);
	}

	private string NewTempDir()
	{
		var dir = Path.Combine(Path.GetTempPath(), "orchestra-exec-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		_tempDirs.Add(dir);
		return dir;
	}

	private static ExecHooks Hooks(
		AgentBuilder agent,
		IHumanInputPrompter? prompter = null,
		Action<IServiceProvider>? onStarted = null) => new()
	{
		// Don't load the developer's orchestra.services.json / orchestra.mcp.json (which could
		// spawn real processes) during tests.
		ConfigureBuilder = b => b.Configuration.AddInMemoryCollection(
			new Dictionary<string, string?> { ["skip-services"] = "true" }),
		ConfigureServices = s => s.AddSingleton(agent),
		Prompter = prompter,
		OnHostStarted = onStarted,
	};

	[Fact]
	public async Task RunFile_PromptStep_Succeeds_ReturnsZero()
	{
		var work = NewTempDir();
		var data = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "hello");

		var exit = await ExecProgram.RunAsync(
			["--run-file", file, "--data-path", data, "--quiet"],
			Hooks(FakeAgentBuilder.Returning("hi there")));

		exit.Should().Be(0);
	}

	[Fact]
	public async Task RunFile_AgentFails_ReturnsOne()
	{
		var work = NewTempDir();
		var data = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "boom");

		var exit = await ExecProgram.RunAsync(
			["--run-file", file, "--data-path", data, "--quiet"],
			Hooks(FakeAgentBuilder.Throwing()));

		exit.Should().Be(1);
	}

	[Fact]
	public async Task RunByName_FromScanDirectory_Succeeds()
	{
		var workspace = NewTempDir();
		var data = NewTempDir();
		TestOrchestrations.WritePromptOrchestration(Path.Combine(workspace, "orchestrations"), "byname");

		var exit = await ExecProgram.RunAsync(
			["--run", "byname", "--orchestrations-path", workspace, "--data-path", data, "--quiet"],
			Hooks(FakeAgentBuilder.Returning("ok")));

		exit.Should().Be(0);
	}

	[Fact]
	public async Task Run_UnknownOrchestration_ReturnsLaunchError()
	{
		var data = NewTempDir();

		var exit = await ExecProgram.RunAsync(
			["--run", "does-not-exist", "--data-path", data, "--quiet"],
			Hooks(FakeAgentBuilder.Returning("ok")));

		exit.Should().Be(ExecProgram.LaunchErrorExitCode);
	}

	[Fact]
	public async Task Run_IsolatedHost_DisablesSchedulerAndDoesNotRegisterTriggers()
	{
		var workspace = NewTempDir();
		var data = NewTempDir();
		var orchDir = Path.Combine(workspace, "orchestrations");
		TestOrchestrations.WritePromptOrchestration(orchDir, "manual-one");
		// A scheduler-triggered orchestration that MUST NOT fire under exec's isolated host.
		TestOrchestrations.WriteScheduledOrchestration(orchDir, "scheduled-two");

		OrchestrationHostOptions? capturedOptions = null;
		TriggerManager? capturedTriggers = null;

		var exit = await ExecProgram.RunAsync(
			["--run", "manual-one", "--orchestrations-path", workspace, "--data-path", data, "--quiet"],
			Hooks(
				FakeAgentBuilder.Returning("ok"),
				onStarted: sp =>
				{
					capturedOptions = sp.GetRequiredService<OrchestrationHostOptions>();
					capturedTriggers = sp.GetRequiredService<TriggerManager>();
				}));

		exit.Should().Be(0);
		capturedOptions!.EnableScheduler.Should().BeFalse();
		capturedTriggers!.SchedulingEnabled.Should().BeFalse();
		capturedTriggers!.GetAllTriggers().Should()
			.BeEmpty("RegisterJsonTriggers is off and scheduling is disabled, so the scheduled orchestration never registers a trigger");
	}

	[Fact]
	public async Task RunFile_ApprovalStep_NonInteractive_ReturnsTwo()
	{
		var work = NewTempDir();
		var data = NewTempDir();
		var file = TestOrchestrations.WriteApprovalOrchestration(work, "gate-noninteractive");

		var exit = await ExecProgram.RunAsync(
			["--run-file", file, "--data-path", data, "--no-interactive", "--quiet"],
			Hooks(FakeAgentBuilder.Returning("unused")));

		exit.Should().Be(2);
	}

	[Fact]
	public async Task RunFile_ApprovalStep_ScriptedApproval_Succeeds()
	{
		var work = NewTempDir();
		var data = NewTempDir();
		var file = TestOrchestrations.WriteApprovalOrchestration(work, "gate-approved");

		var exit = await ExecProgram.RunAsync(
			["--run-file", file, "--data-path", data, "--quiet"],
			Hooks(FakeAgentBuilder.Returning("unused"), prompter: new ScriptedPrompter(choice: "approve")));

		exit.Should().Be(0);
	}

	[Fact]
	public async Task RunFile_WithOutput_WritesFinalContentToFile()
	{
		var work = NewTempDir();
		var data = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "withresult");
		var outFile = Path.Combine(NewTempDir(), "out.txt");

		var exit = await ExecProgram.RunAsync(
			["--run-file", file, "--data-path", data, "--output", outFile, "--quiet"],
			Hooks(FakeAgentBuilder.Returning("hello-result")));

		exit.Should().Be(0);
		File.Exists(outFile).Should().BeTrue();
		(await File.ReadAllTextAsync(outFile)).Should().Contain("hello-result");
	}

	[Fact]
	public async Task RunFile_ReportJson_WritesRunRecord()
	{
		var work = NewTempDir();
		var data = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "report-json");
		var reportFile = Path.Combine(NewTempDir(), "report.json");

		var exit = await ExecProgram.RunAsync(
			["--run-file", file, "--data-path", data, "--report", "json", "--report-output", reportFile, "--quiet"],
			Hooks(FakeAgentBuilder.Returning("report-content")));

		exit.Should().Be(0);
		var json = await File.ReadAllTextAsync(reportFile);
		json.Should().Contain("orchestrationName").And.Contain("say-hello").And.Contain("report-content");
	}

	[Fact]
	public async Task RunFile_ReportMarkdown_WritesDigest()
	{
		var work = NewTempDir();
		var data = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "report-md");
		var reportFile = Path.Combine(NewTempDir(), "report.md");

		var exit = await ExecProgram.RunAsync(
			["--run-file", file, "--data-path", data, "--report", "markdown", "--report-output", reportFile, "--quiet"],
			Hooks(FakeAgentBuilder.Returning("md-content")));

		exit.Should().Be(0);
		var md = await File.ReadAllTextAsync(reportFile);
		md.Should().Contain("# Run report").And.Contain("## Steps").And.Contain("say-hello");
	}

	[Fact]
	public async Task RunFile_Detailed_Succeeds()
	{
		var work = NewTempDir();
		var data = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "detailed");

		var exit = await ExecProgram.RunAsync(
			["--run-file", file, "--data-path", data, "--detailed"],
			Hooks(FakeAgentBuilder.Returning("ok")));

		exit.Should().Be(0);
	}

	// ── Connect-or-spawn (mode auto/existing/isolated) ──────────────────────────────

	[Fact]
	public async Task Existing_UsesRunningServer_RemovesRegistrationAfterRun()
	{
		var work = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "remote-hello");
		await using var server = await TestServerHost.StartAsync(FakeAgentBuilder.Returning("hi"), NewTempDir());

		var exit = await ExecProgram.RunAsync(
			["--mode", "existing", "--server", server.Url, "--run-file", file, "--quiet"]);

		exit.Should().Be(0);

		// The running instance must NOT be terminated by exec.
		using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
		(await http.GetAsync("api/health")).IsSuccessStatusCode.Should().BeTrue("exec must leave a running instance up");

		// By default the transient registration we created is removed, leaving the instance clean.
		server.Services.GetRequiredService<OrchestrationRegistry>().GetByIdOrName("remote-hello")
			.Should().BeNull("exec should remove the orchestration it registered once the run is done");
	}

	[Fact]
	public async Task Existing_KeepRegistered_LeavesTaggedOrchestration()
	{
		var work = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "remote-tagged");
		await using var server = await TestServerHost.StartAsync(FakeAgentBuilder.Returning("hi"), NewTempDir());

		var exit = await ExecProgram.RunAsync(
			["--mode", "existing", "--server", server.Url, "--run-file", file, "--tag", "smoke", "--keep-registered", "--quiet"]);

		exit.Should().Be(0);
		var entry = server.Services.GetRequiredService<OrchestrationRegistry>().GetByIdOrName("remote-tagged");
		entry.Should().NotBeNull("--keep-registered should leave the orchestration registered");
		var tags = server.Services.GetRequiredService<OrchestrationTagStore>().GetTags(entry!.Id);
		tags.Should().Contain("ephemeral").And.Contain("run-once").And.Contain("smoke");
	}

	[Fact]
	public async Task Existing_PreExistingOrchestration_IsNotRemoved()
	{
		var work = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "pre-existing");
		await using var server = await TestServerHost.StartAsync(FakeAgentBuilder.Returning("hi"), NewTempDir());

		// Pre-register the same orchestration on the server (as if the user already had it).
		using (var seedClient = new OrchestraClient(server.Url))
		{
			await seedClient.RegisterOrchestrationAsync(file);
		}

		var exit = await ExecProgram.RunAsync(
			["--mode", "existing", "--server", server.Url, "--run-file", file, "--quiet"]);

		exit.Should().Be(0);
		// We only remove what WE add; a pre-existing registration must survive.
		server.Services.GetRequiredService<OrchestrationRegistry>().GetByIdOrName("pre-existing")
			.Should().NotBeNull("exec must not remove an orchestration that already existed before the run");
	}

	[Fact]
	public async Task Auto_WithHealthyServer_UsesItAndLeavesItUp()
	{
		var work = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "auto-remote");
		await using var server = await TestServerHost.StartAsync(FakeAgentBuilder.Returning("hi"), NewTempDir());

		// mode defaults to auto; --server is configured and healthy, so it should be used.
		var exit = await ExecProgram.RunAsync(["--server", server.Url, "--run-file", file, "--quiet"]);

		exit.Should().Be(0);
		using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
		(await http.GetAsync("api/health")).IsSuccessStatusCode.Should().BeTrue();
	}

	[Fact]
	public async Task Existing_NoReachableServer_ReturnsLaunchError()
	{
		var work = NewTempDir();
		var file = TestOrchestrations.WritePromptOrchestration(work, "noserver");
		var deadUrl = $"http://127.0.0.1:{FreeLoopbackPort()}"; // nothing is listening here

		var exit = await ExecProgram.RunAsync(
			["--mode", "existing", "--server", deadUrl, "--run-file", file, "--quiet"]);

		exit.Should().Be(ExecProgram.LaunchErrorExitCode);
	}

	[Fact]
	public async Task NoConfig_SpawnedHost_IgnoresOrchestraJson()
	{
		await WithConfigEnvAsync("Critical", async () =>
		{
			var work = NewTempDir();
			var file = TestOrchestrations.WritePromptOrchestration(work, "noconfig");
			string? capturedLogLevel = null;

			var exit = await ExecProgram.RunAsync(
				["--mode", "isolated", "--run-file", file, "--data-path", NewTempDir(), "--no-config", "--quiet"],
				Hooks(
					FakeAgentBuilder.Returning("ok"),
					onStarted: sp => capturedLogLevel = sp.GetRequiredService<OrchestrationHostOptions>().LogLevel));

			exit.Should().Be(0);
			capturedLogLevel.Should().Be("Information", "--no-config must ignore orchestra.json (default LogLevel)");
		});
	}

	[Fact]
	public async Task Default_SpawnedHost_AppliesOrchestraJson()
	{
		await WithConfigEnvAsync("Critical", async () =>
		{
			var work = NewTempDir();
			var file = TestOrchestrations.WritePromptOrchestration(work, "withconfig");
			string? capturedLogLevel = null;

			var exit = await ExecProgram.RunAsync(
				["--mode", "isolated", "--run-file", file, "--data-path", NewTempDir(), "--quiet"],
				Hooks(
					FakeAgentBuilder.Returning("ok"),
					onStarted: sp => capturedLogLevel = sp.GetRequiredService<OrchestrationHostOptions>().LogLevel));

			exit.Should().Be(0);
			capturedLogLevel.Should().Be("Critical", "without --no-config the spawned host honors orchestra.json");
		});
	}

	/// <summary>Plants an orchestra.json with the given logLevel and points ORCHESTRA_CONFIG_PATH at
	/// it for the duration of <paramref name="body"/>, restoring the prior value afterward. Safe
	/// because exec host-booting tests run serially in their own process.</summary>
	private async Task WithConfigEnvAsync(string logLevel, Func<Task> body)
	{
		var configDir = NewTempDir();
		var configPath = Path.Combine(configDir, "orchestra.json");
		File.WriteAllText(configPath, $"{{ \"logLevel\": \"{logLevel}\" }}");
		var prior = Environment.GetEnvironmentVariable("ORCHESTRA_CONFIG_PATH");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);
		try
		{
			await body();
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", prior);
		}
	}

	private static int FreeLoopbackPort()
	{
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
		finally { listener.Stop(); }
	}
}

/// <summary>Serializes exec host-booting tests to keep port/console usage predictable.</summary>
[CollectionDefinition("exec-serial", DisableParallelization = true)]
public sealed class ExecSerialCollection;
