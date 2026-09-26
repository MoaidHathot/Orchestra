using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// <c>POST /api/history/prune</c>: on-demand run retention behind <c>orchestra runs prune</c>.
/// </summary>
public class RunsApiPruneTests : IDisposable
{
	private const string Orchestration = "nightly";

	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;
	private readonly RunAnnotationStore _annotations;

	public RunsApiPruneTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-prune-api-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_annotations = new RunAnnotationStore(_tempDir, NullLogger<RunAnnotationStore>.Instance);
		_store = new FileSystemRunStore(_tempDir, NullLogger<FileSystemRunStore>.Instance, _annotations);
	}

	public void Dispose()
	{
		_store.Dispose();
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private async Task SaveRunAsync(string runId, DateTimeOffset startedAt, string orchestration = Orchestration)
	{
		await _store.SaveRunAsync(new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestration,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = startedAt,
			CompletedAt = startedAt.AddMinutes(1),
			Status = ExecutionStatus.Succeeded,
			FinalContent = "result",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>(),
		}, cancellationToken: default);
	}

	private IHost CreateHost(RetentionPolicy configured)
	{
		var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		var activeExecutionInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		var triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			activeExecutionInfos,
			agentBuilder: null!,
			scheduler: new OrchestrationScheduler(),
			loggerFactory: NullLoggerFactory.Instance,
			logger: NullLogger<TriggerManager>.Instance,
			dataPath: _tempDir,
			runStore: _store,
			checkpointStore: Substitute.For<ICheckpointStore>(),
			launcher: Substitute.For<IChildOrchestrationLauncher>());

		var host = new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddRouting();
					services.AddSingleton(_store);
					services.AddSingleton(_annotations);
					services.AddSingleton(configured);
					services.AddSingleton(activeExecutionInfos);
					services.AddSingleton(triggerManager);
					services.AddSingleton(new OrchestrationRegistry());
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapRunsApi(jsonOptions));
				});
			})
			.Build();

		host.Start();
		return host;
	}

	private async Task<string[]> LiveRunIdsAsync()
		=> (await _store.GetRunSummariesAsync()).Select(s => s.RunId).OrderBy(id => id).ToArray();

	[Fact]
	public async Task Prune_UsesTheConfiguredPolicy_AndReportsWhatItDeleted()
	{
		// Three runs, keep the newest 2.
		await SaveRunAsync("old", DateTimeOffset.UtcNow.AddHours(-3));
		await SaveRunAsync("mid", DateTimeOffset.UtcNow.AddHours(-2));
		await SaveRunAsync("new", DateTimeOffset.UtcNow.AddHours(-1));
		using var host = CreateHost(new RetentionPolicy { MaxRunsPerOrchestration = 2 });
		var client = host.GetTestClient();

		var response = await client.PostAsync("/api/history/prune", null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		body.GetProperty("dryRun").GetBoolean().Should().BeFalse();
		body.GetProperty("deleted").GetInt32().Should().Be(1);
		body.GetProperty("failed").GetInt32().Should().Be(0);
		body.GetProperty("policy").GetProperty("source").GetString().Should().Be("orchestra.json");
		body.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("runId").GetString()).Should().Equal("old");
		(await LiveRunIdsAsync()).Should().Equal("mid", "new");
	}

	[Fact]
	public async Task Prune_DryRun_ListsCandidatesWithoutDeleting()
	{
		await SaveRunAsync("old", DateTimeOffset.UtcNow.AddDays(-40));
		await SaveRunAsync("new", DateTimeOffset.UtcNow.AddDays(-1));
		using var host = CreateHost(new RetentionPolicy { MaxRunAgeDays = 30 });
		var client = host.GetTestClient();

		var response = await client.PostAsync("/api/history/prune?dryRun=true", null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
		body.GetProperty("wouldDelete").GetInt32().Should().Be(1);
		body.GetProperty("runs").EnumerateArray().Single().GetProperty("runId").GetString().Should().Be("old");
		(await LiveRunIdsAsync()).Should().Equal("new", "old");
	}

	[Fact]
	public async Task Prune_QueryOverrides_ReplaceTheConfiguredPolicyForThisCall()
	{
		await SaveRunAsync("r1", DateTimeOffset.UtcNow.AddHours(-3));
		await SaveRunAsync("r2", DateTimeOffset.UtcNow.AddHours(-2));
		await SaveRunAsync("r3", DateTimeOffset.UtcNow.AddHours(-1));
		// Configured policy keeps everything; the request keeps only 1.
		using var host = CreateHost(new RetentionPolicy());
		var client = host.GetTestClient();

		var response = await client.PostAsync("/api/history/prune?maxRuns=1", null);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		body.GetProperty("policy").GetProperty("source").GetString().Should().Be("request");
		body.GetProperty("policy").GetProperty("maxRunsPerOrchestration").GetInt32().Should().Be(1);
		body.GetProperty("deleted").GetInt32().Should().Be(2);
		(await LiveRunIdsAsync()).Should().Equal("r3");
	}

	[Fact]
	public async Task Prune_KeepsFavorites()
	{
		await SaveRunAsync("keep-me", DateTimeOffset.UtcNow.AddHours(-3));
		await SaveRunAsync("r2", DateTimeOffset.UtcNow.AddHours(-2));
		await SaveRunAsync("r3", DateTimeOffset.UtcNow.AddHours(-1));
		_annotations.Patch("keep-me", favorite: true, orchestrationName: Orchestration);
		using var host = CreateHost(new RetentionPolicy { MaxRunsPerOrchestration = 1 });
		var client = host.GetTestClient();

		var response = await client.PostAsync("/api/history/prune", null);

		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		body.GetProperty("deleted").GetInt32().Should().Be(1);
		(await LiveRunIdsAsync()).Should().Equal("keep-me", "r3");
	}

	[Fact]
	public async Task Prune_WithoutAnyPolicy_Is400_AndTellsTheOperatorWhatToDo()
	{
		await SaveRunAsync("r1", DateTimeOffset.UtcNow.AddHours(-1));
		using var host = CreateHost(new RetentionPolicy());
		var client = host.GetTestClient();

		var response = await client.PostAsync("/api/history/prune", null);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		problem.GetProperty("detail").GetString().Should().Contain("retention.maxRunsPerOrchestration").And.Contain("maxRuns");
		(await LiveRunIdsAsync()).Should().ContainSingle("nothing is deleted when no policy applies").Which.Should().Be("r1");
	}

	[Theory]
	[InlineData("maxRuns=-1")]
	[InlineData("maxAgeDays=-5")]
	[InlineData("maxRuns=0&maxAgeDays=0")]
	public async Task Prune_RejectsUnusableOverrides(string query)
	{
		using var host = CreateHost(new RetentionPolicy { MaxRunsPerOrchestration = 10 });
		var client = host.GetTestClient();

		var response = await client.PostAsync($"/api/history/prune?{query}", null);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}
}
