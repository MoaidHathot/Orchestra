using System.Collections.Concurrent;
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
/// Paging and total-count contract for <c>/api/history/all</c> and <c>/api/history/search</c>.
/// </summary>
/// <remarks>
/// These endpoints back Portal's infinite-scroll history list, which pages by offset and uses
/// <c>total</c> to decide whether to keep fetching. Two properties must therefore hold, and
/// neither was covered before:
/// <list type="bullet">
/// <item><b>Partitioning.</b> Walking every page must yield each run exactly once — no gaps and
/// no repeats — including when runs share a start timestamp, which happens routinely because
/// runs launched together are stamped at the same instant.</item>
/// <item><b>Honest totals.</b> <c>total</c> must be the size of the whole match set, not the size
/// of the page that was returned; otherwise a client can never tell that more results exist.</item>
/// </list>
/// </remarks>
public class RunsApiHistoryPagingTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _active = new();

	public RunsApiHistoryPagingTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-paging-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemRunStore(_tempDir);
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

	private static readonly DateTimeOffset s_epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private async Task SaveRunsAsync(
		int count, string namePrefix = "alpha", TimeSpan? spacing = null, string runIdPrefix = "run")
	{
		var gap = spacing ?? TimeSpan.FromMinutes(1);
		for (var i = 0; i < count; i++)
		{
			var startedAt = s_epoch + gap * i;
			await _store.SaveRunAsync(new OrchestrationRunRecord
			{
				RunId = $"{runIdPrefix}-{i:D3}",
				OrchestrationName = $"{namePrefix}-orch",
				OrchestrationVersion = "1.0.0",
				TriggeredBy = "manual",
				StartedAt = startedAt,
				CompletedAt = startedAt.AddSeconds(5),
				Status = ExecutionStatus.Succeeded,
				IsIncomplete = false,
				FinalContent = string.Empty,
				SavedFiles = [],
				HookExecutions = [],
				StepRecords = new Dictionary<string, StepRunRecord>(),
				AllStepRecords = new Dictionary<string, StepRunRecord>(),
			}, cancellationToken: default);
		}
	}

	/// <summary>Walks every page and returns the run ids in the order they were served.</summary>
	private static async Task<List<string>> PageThroughAsync(HttpClient client, string path, int pageSize)
	{
		var seen = new List<string>();
		var offset = 0;
		while (true)
		{
			var separator = path.Contains('?') ? "&" : "?";
			var response = await client.GetAsync($"{path}{separator}offset={offset}&limit={pageSize}");
			response.EnsureSuccessStatusCode();
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

			var runs = doc.RootElement.GetProperty("runs").EnumerateArray().ToList();
			if (runs.Count == 0)
				break;

			seen.AddRange(runs.Select(r => r.GetProperty("runId").GetString()!));
			offset += pageSize;

			// Guard against a non-terminating endpoint rather than hanging the suite.
			if (offset > 10_000)
				throw new InvalidOperationException("paging did not terminate");
		}
		return seen;
	}

	[Fact]
	public async Task HistoryAll_PagingVisitsEveryRunExactlyOnce()
	{
		await SaveRunsAsync(10);

		using var host = CreateHost();
		var seen = await PageThroughAsync(host.GetTestClient(), "/api/history/all", pageSize: 3);

		seen.Should().HaveCount(10);
		seen.Should().OnlyHaveUniqueItems("a paged walk must not repeat rows");
		seen.Should().BeEquivalentTo(Enumerable.Range(0, 10).Select(i => $"run-{i:D3}"),
			"a paged walk must not skip rows either");
	}

	[Fact]
	public async Task HistoryAll_PagingIsStableWhenRunsShareAStartTimestamp()
	{
		// Runs launched in the same batch land on the same timestamp. Ordering by start time
		// alone leaves ties unordered, so SQLite is free to return them in a different order
		// per query — which silently duplicates some rows and drops others while paging.
		await SaveRunsAsync(10, spacing: TimeSpan.Zero);

		using var host = CreateHost();
		var seen = await PageThroughAsync(host.GetTestClient(), "/api/history/all", pageSize: 3);

		seen.Should().HaveCount(10);
		seen.Should().OnlyHaveUniqueItems("tied timestamps must still produce a total order");
		seen.Should().BeEquivalentTo(Enumerable.Range(0, 10).Select(i => $"run-{i:D3}"));
	}

	[Fact]
	public async Task HistoryAll_PagingVisitsEveryRunExactlyOnceWithRunningExecutions()
	{
		await SaveRunsAsync(8);
		for (var i = 0; i < 3; i++)
		{
			var id = $"live-{i}";
			_active[id] = new ActiveExecutionInfo
			{
				ExecutionId = id,
				OrchestrationId = $"orch-{id}",
				OrchestrationName = "alpha-orch",
				TriggeredBy = "manual",
				StartedAt = s_epoch.AddHours(1).AddMinutes(i),
				CancellationTokenSource = new CancellationTokenSource(),
				Reporter = new SseReporter(),
				Status = HostExecutionStatus.Running,
			};
		}

		using var host = CreateHost();
		var seen = await PageThroughAsync(host.GetTestClient(), "/api/history/all", pageSize: 3);

		seen.Should().HaveCount(11);
		seen.Should().OnlyHaveUniqueItems(
			"running runs are prepended to the completed page, so the completed offset must be "
			+ "shifted by the number of running rows already served");
	}

	[Fact]
	public async Task HistoryAll_TotalCountsEveryMatchNotJustThePage()
	{
		await SaveRunsAsync(10);

		using var host = CreateHost();
		var response = await host.GetTestClient().GetAsync("/api/history/all?offset=0&limit=3");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		doc.RootElement.GetProperty("count").GetInt32().Should().Be(3, "the page holds 3 rows");
		doc.RootElement.GetProperty("total").GetInt32().Should().Be(10, "10 runs match overall");
	}

	[Fact]
	public async Task Search_TotalCountsEveryMatchNotJustThePage()
	{
		await SaveRunsAsync(10);

		using var host = CreateHost();
		var response = await host.GetTestClient().GetAsync("/api/history/search?query=alpha&limit=3");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		doc.RootElement.GetProperty("count").GetInt32().Should().Be(3, "the page holds 3 rows");
		doc.RootElement.GetProperty("total").GetInt32().Should().Be(10,
			"total must describe the match set so a client can tell more results exist");
	}

	[Fact]
	public async Task HistoryAll_TotalRespectsFilters()
	{
		await SaveRunsAsync(6, namePrefix: "alpha");
		await SaveRunsAsync(4, namePrefix: "beta", runIdPrefix: "beta-run");

		using var host = CreateHost();
		var response = await host.GetTestClient().GetAsync("/api/history/all?statuses=Succeeded&limit=2");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		doc.RootElement.GetProperty("total").GetInt32().Should().Be(10,
			"total must count the filtered match set, not the unfiltered history");
	}

	[Fact]
	public async Task HistoryAll_FilteredPagingVisitsEveryMatchExactlyOnce()
	{
		await SaveRunsAsync(7, namePrefix: "alpha");
		await SaveRunsAsync(5, namePrefix: "beta", runIdPrefix: "beta-run");

		using var host = CreateHost();
		var seen = await PageThroughAsync(
			host.GetTestClient(), "/api/history/all?statuses=Succeeded", pageSize: 4);

		seen.Should().HaveCount(12);
		seen.Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public async Task Search_PagingVisitsEveryMatchExactlyOnce()
	{
		await SaveRunsAsync(10);

		using var host = CreateHost();
		var seen = await PageThroughAsync(
			host.GetTestClient(), "/api/history/search?query=alpha", pageSize: 3);

		seen.Should().HaveCount(10);
		seen.Should().OnlyHaveUniqueItems();
	}

	[Theory]
	[InlineData("%")]
	[InlineData("_")]
	[InlineData("50%")]
	public async Task Search_TreatsSqlWildcardsAsLiteralText(string wildcardQuery)
	{
		// Pushing the search into SQL means the query string reaches a LIKE pattern. Unescaped,
		// "%" matches every run and "_" matches every single character, so a user searching for
		// a literal percent sign would get the entire history back.
		await SaveRunsAsync(5);

		using var host = CreateHost();
		var response = await host.GetTestClient().GetAsync(
			$"/api/history/search?query={Uri.EscapeDataString(wildcardQuery)}");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		doc.RootElement.GetProperty("total").GetInt32().Should().Be(0,
			"no run name or id contains that literal text");
	}

	[Fact]
	public async Task Search_MatchesRunIdSubstring()
	{
		await SaveRunsAsync(12);

		using var host = CreateHost();
		var response = await host.GetTestClient().GetAsync("/api/history/search?query=run-01");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		// run-010 and run-011 only.
		doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["run-010", "run-011"]);
	}

	private IHost CreateHost()
	{
		var jsonOptions = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
			Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
		};

		var triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			_active,
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
					services.AddSingleton(new RunAnnotationStore(
						Path.Combine(_tempDir, "annotations"), NullLogger<RunAnnotationStore>.Instance));
					services.AddSingleton(_active);
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
}
