using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Cli.Run;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Cli.Tests.Integration;

/// <summary>
/// End-to-end test of the <see cref="HumanInputResponder"/> against a real Orchestra.Server
/// instance via <see cref="WebApplicationFactory{TEntryPoint}"/>. Verifies that the CLI's
/// response path actually clears a pending input wait registered through the server's
/// <see cref="IHumanInputWaiter"/>.
/// </summary>
public class HumanInputResponderIntegrationTests : IClassFixture<HumanInputResponderIntegrationTests.TestFactory>, IDisposable
{
	private readonly TestFactory _factory;
	private readonly HttpClient _http;

	public HumanInputResponderIntegrationTests(TestFactory factory)
	{
		_factory = factory;
		_http = factory.CreateClient();
	}

	public void Dispose() => _http.Dispose();

	[Fact]
	public async Task RespondAsync_With_Choice_Completes_The_Wait()
	{
		// Seed a pending record + register a wait, then exercise the CLI's
		// HumanInputResponder against the live server. The wait task should resolve.
		var pendingStore = _factory.Services.GetRequiredService<IPendingInputStore>();
		var waiter = _factory.Services.GetRequiredService<IHumanInputWaiter>();

		const string orchestration = "cli-int-orch";
		const string runId = "cli-int-run";
		const string step = "approve";

		await pendingStore.SaveAsync(new PendingInputRecord
		{
			OrchestrationName = orchestration,
			RunId = runId,
			StepName = step,
			Kind = PendingInputKind.Approval,
			Prompt = "Approve?",
			Choices = ["approve", "reject"],
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var waitTask = waiter.WaitAsync(orchestration, runId, step, CancellationToken.None);

		// Wire the CLI's OrchestraClient to the in-memory test server.
		using var client = new OrchestraClient(_http);

		var responder = new HumanInputResponder(client, NullLogger<HumanInputResponder>.Instance);
		var info = new AwaitingInputInfo(orchestration, runId, step, "Approval", "Approve?",
			new[] { "approve", "reject" }, DateTimeOffset.UtcNow, null);

		await responder.RespondAsync(info, new HumanInputResponse("approve", null, "alice"), CancellationToken.None);

		var resolved = await waitTask;
		resolved.Choice.Should().Be("approve");
		resolved.RespondedBy.Should().Be("alice");
	}

	[Fact]
	public async Task RespondAsync_FreeForm_Reply_Completes_The_Wait()
	{
		var pendingStore = _factory.Services.GetRequiredService<IPendingInputStore>();
		var waiter = _factory.Services.GetRequiredService<IHumanInputWaiter>();

		const string orchestration = "cli-int-clarify";
		const string runId = "cli-int-clarify-run";
		const string step = "draft";

		await pendingStore.SaveAsync(new PendingInputRecord
		{
			OrchestrationName = orchestration,
			RunId = runId,
			StepName = step,
			Kind = PendingInputKind.EngineTool,
			Prompt = "What angle?",
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var waitTask = waiter.WaitAsync(orchestration, runId, step, CancellationToken.None);

		using var client = new OrchestraClient(_http);

		var responder = new HumanInputResponder(client, NullLogger<HumanInputResponder>.Instance);
		var info = new AwaitingInputInfo(orchestration, runId, step, "EngineTool", "What angle?",
			Array.Empty<string>(), DateTimeOffset.UtcNow, null);

		await responder.RespondAsync(info, new HumanInputResponse(null, "AI angle, ~200 words", null), CancellationToken.None);

		var resolved = await waitTask;
		resolved.Reply.Should().Be("AI angle, ~200 words");
	}

	/// <summary>
	/// Test factory mirroring <c>ServerWebApplicationFactory</c> but co-located so this test
	/// project doesn't reach into Orchestra.Server.Tests. Each instance gets an isolated
	/// data directory.
	///
	/// Uses <c>global::Program</c> to disambiguate from <c>Orchestra.Cli.Program</c>; the
	/// Server's entry point is in the global namespace.
	/// </summary>
	public class TestFactory : WebApplicationFactory<global::Program>
	{
		private readonly string _testDataPath;

		public TestFactory()
		{
			_testDataPath = Path.Combine(Path.GetTempPath(), "Orchestra.Cli.Tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_testDataPath);
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Testing");
			builder.ConfigureAppConfiguration((_, config) =>
			{
				config.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["data-path"] = _testDataPath,
				});
			});
		}

		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
			if (Directory.Exists(_testDataPath))
			{
				try { Directory.Delete(_testDataPath, recursive: true); }
				catch { /* best effort */ }
			}
		}
	}
}
