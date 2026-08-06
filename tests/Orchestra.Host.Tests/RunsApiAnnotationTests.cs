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
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// End-to-end tests for the run annotation endpoints under <c>/api/history</c>.
/// </summary>
public class RunsApiAnnotationTests : IDisposable
{
	private const string Orchestration = "test-orchestration";

	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;
	private readonly RunAnnotationStore _annotations;

	public RunsApiAnnotationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-annotation-api-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_annotations = new RunAnnotationStore(_tempDir, NullLogger<RunAnnotationStore>.Instance);
		_store = new FileSystemRunStore(_tempDir, NullLogger<FileSystemRunStore>.Instance, _annotations);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private async Task SaveRunAsync(string runId, string orchestration = Orchestration)
	{
		await _store.SaveRunAsync(new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestration,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			CompletedAt = DateTimeOffset.UtcNow,
			Status = ExecutionStatus.Succeeded,
			FinalContent = "result",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>(),
		}, cancellationToken: default);
	}

	private IHost CreateHost()
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
			runsDir: Path.GetTempPath(),
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

	private static string Url(string runId) => $"/api/history/{Orchestration}/{runId}/annotation";

	// ── Write / read ──

	[Fact]
	public async Task Put_CreatesAnnotation_AndGetReturnsIt()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();

		var put = await client.PutAsJsonAsync(Url("run1"), new
		{
			favorite = true,
			title = "Connect evidence pack",
			tags = new[] { "connect", "keep" },
			note = "Counts unreliable",
		});
		put.StatusCode.Should().Be(HttpStatusCode.OK);

		var json = await client.GetFromJsonAsync<JsonElement>(Url("run1"));
		json.GetProperty("favorite").GetBoolean().Should().BeTrue();
		json.GetProperty("title").GetString().Should().Be("Connect evidence pack");
		json.GetProperty("note").GetString().Should().Be("Counts unreliable");
		json.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
			.Should().BeEquivalentTo(["connect", "keep"]);
	}

	[Fact]
	public async Task Patch_LeavesOmittedFieldsUntouched()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();

		await client.PutAsJsonAsync(Url("run1"), new
		{
			favorite = true,
			title = "Original",
			tags = new[] { "connect" },
		});

		var patch = await client.PatchAsJsonAsync(Url("run1"), new { title = "Renamed" });
		patch.StatusCode.Should().Be(HttpStatusCode.OK);

		var json = await client.GetFromJsonAsync<JsonElement>(Url("run1"));
		json.GetProperty("title").GetString().Should().Be("Renamed");
		json.GetProperty("favorite").GetBoolean().Should().BeTrue();
		json.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
			.Should().BeEquivalentTo(["connect"]);
	}

	[Fact]
	public async Task Get_UnannotatedRun_Returns404()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();

		var response = await host.GetTestClient().GetAsync(Url("run1"));

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Put_UnknownRun_Returns404()
	{
		using var host = CreateHost();

		var response = await host.GetTestClient().PutAsJsonAsync(Url("ghost"), new { favorite = true });

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Delete_RemovesAnnotation()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(Url("run1"), new { favorite = true });

		var response = await client.DeleteAsync(Url("run1"));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		_annotations.Get("run1").Should().BeNull();
	}

	// ── Favorite shortcuts ──

	[Fact]
	public async Task FavoriteShortcuts_ToggleTheFlag()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();

		await client.PostAsync($"/api/history/{Orchestration}/run1/favorite", null);
		_annotations.IsFavorite("run1").Should().BeTrue();

		await client.DeleteAsync($"/api/history/{Orchestration}/run1/favorite");
		_annotations.IsFavorite("run1").Should().BeFalse();
	}

	// ── Projection into history rows ──

	[Fact]
	public async Task HistoryRows_CarryAnnotationFields()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(Url("run1"), new
		{
			favorite = true,
			title = "Kept",
			tags = new[] { "connect" },
		});

		var json = await client.GetFromJsonAsync<JsonElement>("/api/history");
		var row = json.GetProperty("runs").EnumerateArray().Single();

		row.GetProperty("favorite").GetBoolean().Should().BeTrue();
		row.GetProperty("title").GetString().Should().Be("Kept");
		row.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().BeEquivalentTo(["connect"]);
	}

	[Fact]
	public async Task UnannotatedRow_ReportsFalseAndNulls()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();

		var json = await host.GetTestClient().GetFromJsonAsync<JsonElement>("/api/history");
		var row = json.GetProperty("runs").EnumerateArray().Single();

		row.GetProperty("favorite").GetBoolean().Should().BeFalse();
		row.GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
		row.GetProperty("tags").GetArrayLength().Should().Be(0);
	}

	[Fact]
	public async Task RunDetail_IncludesAnnotation()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(Url("run1"), new { favorite = true, title = "Kept" });

		var json = await client.GetFromJsonAsync<JsonElement>($"/api/history/{Orchestration}/run1");

		json.GetProperty("annotation").GetProperty("title").GetString().Should().Be("Kept");
		json.GetProperty("annotation").GetProperty("favorite").GetBoolean().Should().BeTrue();
	}

	// ── Filtering ──

	[Fact]
	public async Task FavoritesFilter_ReturnsOnlyFavoritedRuns()
	{
		await SaveRunAsync("fav");
		await SaveRunAsync("plain");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PostAsync($"/api/history/{Orchestration}/fav/favorite", null);

		var json = await client.GetFromJsonAsync<JsonElement>("/api/history?favorites=true");

		json.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["fav"]);
	}

	[Fact]
	public async Task TagsFilter_UsesOrSemantics()
	{
		await SaveRunAsync("a");
		await SaveRunAsync("b");
		await SaveRunAsync("c");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(Url("a"), new { tags = new[] { "connect" } });
		await client.PutAsJsonAsync(Url("b"), new { tags = new[] { "other" } });

		var json = await client.GetFromJsonAsync<JsonElement>("/api/history?tags=connect,other");

		json.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["a", "b"]);
	}

	[Fact]
	public async Task FavoritesFilter_False_ReturnsUnfavoritedRunsIncludingUnannotatedOnes()
	{
		// The complement of a set, not a set: "not favorited" has to include every run that was
		// never annotated at all, which is most of them. Expressing this as an allow-list of
		// annotated-and-not-favorited runs would return almost nothing.
		await SaveRunAsync("fav");
		await SaveRunAsync("titled-only");
		await SaveRunAsync("untouched");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PostAsync($"/api/history/{Orchestration}/fav/favorite", null);
		await client.PutAsJsonAsync(Url("titled-only"), new { title = "Not a favorite" });

		var json = await client.GetFromJsonAsync<JsonElement>("/api/history?favorites=false");

		json.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["titled-only", "untouched"]);
	}

	[Fact]
	public async Task FavoritesFilter_FalseCombinedWithTags_AppliesBoth()
	{
		await SaveRunAsync("fav-tagged");
		await SaveRunAsync("plain-tagged");
		await SaveRunAsync("untagged");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(Url("fav-tagged"), new { favorite = true, tags = new[] { "connect" } });
		await client.PutAsJsonAsync(Url("plain-tagged"), new { tags = new[] { "connect" } });

		var json = await client.GetFromJsonAsync<JsonElement>("/api/history?favorites=false&tags=connect");

		json.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["plain-tagged"]);
	}

	// ── Search ──

	[Fact]
	public async Task Search_MatchesTitle()
	{
		// The whole point: the run's name is meaningless, the title is what a human searches for.
		await SaveRunAsync("efca835904b6", "ephemeral-efca835904b6-attempt-3");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(
			$"/api/history/ephemeral-efca835904b6-attempt-3/efca835904b6/annotation",
			new { title = "Connect evidence pack" });

		var json = await client.GetFromJsonAsync<JsonElement>("/api/history/search?query=connect");

		json.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["efca835904b6"]);
	}

	[Fact]
	public async Task Search_MatchesTagAndNote()
	{
		await SaveRunAsync("run1");
		await SaveRunAsync("run2");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(Url("run1"), new { tags = new[] { "quarterly" } });
		await client.PutAsJsonAsync(Url("run2"), new { note = "quarterly review evidence" });

		var json = await client.GetFromJsonAsync<JsonElement>("/api/history/search?query=quarterly");

		json.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["run1", "run2"]);
	}

	// ── Delete guard ──

	[Fact]
	public async Task Delete_FavoritedRunWithoutForce_IsRejected()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PostAsync($"/api/history/{Orchestration}/run1/favorite", null);

		var response = await client.DeleteAsync($"/api/history/{Orchestration}/run1");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await _store.GetRunAsync(Orchestration, "run1")).Should().NotBeNull();
	}

	[Fact]
	public async Task Delete_FavoritedRunWithForce_Succeeds()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PostAsync($"/api/history/{Orchestration}/run1/favorite", null);

		var response = await client.DeleteAsync($"/api/history/{Orchestration}/run1?force=true");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await _store.GetRunAsync(Orchestration, "run1")).Should().BeNull();
		_annotations.Get("run1").Should().BeNull("the annotation must not outlive the run");
	}

	[Fact]
	public async Task Delete_UnfavoritedRun_NeedsNoForce()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();

		var response = await host.GetTestClient().DeleteAsync($"/api/history/{Orchestration}/run1");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	// ── Listing and pruning ──

	[Fact]
	public async Task AnnotationsList_ReturnsTagCounts()
	{
		await SaveRunAsync("run1");
		await SaveRunAsync("run2");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(Url("run1"), new { tags = new[] { "connect", "keep" } });
		await client.PutAsJsonAsync(Url("run2"), new { tags = new[] { "connect" } });

		var json = await client.GetFromJsonAsync<JsonElement>("/api/history/annotations");

		json.GetProperty("count").GetInt32().Should().Be(2);
		var tags = json.GetProperty("tags").EnumerateArray()
			.ToDictionary(t => t.GetProperty("tag").GetString()!, t => t.GetProperty("count").GetInt32());
		tags["connect"].Should().Be(2);
		tags["keep"].Should().Be(1);
	}

	[Fact]
	public async Task Orphans_AreReportedButNotAutomaticallyRemoved()
	{
		await SaveRunAsync("run1");
		using var host = CreateHost();
		var client = host.GetTestClient();
		await client.PutAsJsonAsync(Url("run1"), new { favorite = true });

		// Force-delete the run, leaving the annotation behind as if the folder vanished.
		_annotations.Patch("ghost", favorite: true, orchestrationName: Orchestration);

		var listed = await client.GetFromJsonAsync<JsonElement>("/api/history/annotations");
		listed.GetProperty("orphanCount").GetInt32().Should().Be(1);
		_annotations.Get("ghost").Should().NotBeNull("orphans are reported, never silently dropped");

		var pruned = await client.PostAsync("/api/history/annotations/prune", null);
		pruned.StatusCode.Should().Be(HttpStatusCode.OK);
		_annotations.Get("ghost").Should().BeNull();
		_annotations.Get("run1").Should().NotBeNull("live runs keep their annotation");
	}
}
