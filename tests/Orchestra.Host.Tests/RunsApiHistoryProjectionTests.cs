using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// End-to-end tests for the <c>/api/history</c> endpoints exercising the new lineage / origin
/// projection and the <c>?origins=</c>, <c>?roots=</c>, <c>?statuses=</c> query parameters.
/// </summary>
public class RunsApiHistoryProjectionTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;

	public RunsApiHistoryProjectionTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-projection-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemRunStore(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private static OrchestrationRunRecord CreateRecord(
		string runId,
		string orchestrationName = "test-orch",
		string triggeredBy = "manual",
		ExecutionStatus status = ExecutionStatus.Succeeded,
		string? parentExecutionId = null,
		string? parentStepName = null,
		string? rootExecutionId = null,
		int nestingDepth = 0,
		string? retriedFromRunId = null,
		string? retryMode = null)
	{
		var now = DateTimeOffset.UtcNow;
		return new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestrationName,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = triggeredBy,
			StartedAt = now.AddMinutes(-1),
			CompletedAt = now,
			Status = status,
			IsIncomplete = false,
			FinalContent = string.Empty,
			SavedFiles = [],
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>(),
			ParentExecutionId = parentExecutionId,
			ParentStepName = parentStepName,
			RootExecutionId = rootExecutionId,
			NestingDepth = nestingDepth,
			RetriedFromRunId = retriedFromRunId,
			RetryMode = retryMode,
		};
	}

	[Fact]
	public async Task History_ChildRow_ProjectsLineageAndParentOrchestrationName()
	{
		// Arrange: parent run + child run referencing it
		await _store.SaveRunAsync(CreateRecord(
			runId: "parent-1",
			orchestrationName: "parent-orch"), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(
			runId: "child-1",
			orchestrationName: "child-orch",
			triggeredBy: "orchestration:parent-orch:parent-1",
			parentExecutionId: "parent-1",
			parentStepName: "step-A",
			rootExecutionId: "parent-1",
			nestingDepth: 1), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		// Act
		var response = await client.GetAsync("/api/history");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		// Assert
		var runs = doc.RootElement.GetProperty("runs").EnumerateArray().ToList();
		runs.Should().HaveCount(2);

		var child = runs.First(r => r.GetProperty("runId").GetString() == "child-1");
		child.GetProperty("parentExecutionId").GetString().Should().Be("parent-1");
		child.GetProperty("parentStepName").GetString().Should().Be("step-A");
		child.GetProperty("parentOrchestrationName").GetString().Should().Be("parent-orch");
		child.GetProperty("rootExecutionId").GetString().Should().Be("parent-1");
		child.GetProperty("nestingDepth").GetInt32().Should().Be(1);
		child.GetProperty("origin").GetString().Should().Be("orchestration");

		var parent = runs.First(r => r.GetProperty("runId").GetString() == "parent-1");
		// JSON serializer is configured to omit null fields (DefaultIgnoreCondition.WhenWritingNull),
		// so the parent row should NOT have parentExecutionId at all - reflecting "no parent".
		parent.TryGetProperty("parentExecutionId", out _).Should().BeFalse(
			"root rows omit lineage fields entirely rather than emitting nulls");
		parent.GetProperty("nestingDepth").GetInt32().Should().Be(0);
		parent.GetProperty("origin").GetString().Should().Be("manual");
	}

	[Fact]
	public async Task History_RetryRow_ProjectsRetriedFromAndOrigin()
	{
		await _store.SaveRunAsync(CreateRecord(
			runId: "original-1",
			orchestrationName: "test-orch",
			status: ExecutionStatus.Failed), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(
			runId: "retry-1",
			orchestrationName: "test-orch",
			triggeredBy: "retry",
			retriedFromRunId: "original-1",
			retryMode: "from-step:judge"), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var runs = doc.RootElement.GetProperty("runs").EnumerateArray().ToList();
		var retry = runs.First(r => r.GetProperty("runId").GetString() == "retry-1");

		retry.GetProperty("retriedFromRunId").GetString().Should().Be("original-1");
		retry.GetProperty("retryMode").GetString().Should().Be("from-step:judge");
		retry.GetProperty("origin").GetString().Should().Be("retry");
	}

	[Fact]
	public async Task History_OriginsFilter_KeepsOnlyMatchingRuns()
	{
		await _store.SaveRunAsync(CreateRecord(runId: "manual-1", triggeredBy: "manual"), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "scheduler-1", triggeredBy: "scheduler"), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "child-1", triggeredBy: "orchestration:p:1", parentExecutionId: "p1"), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history?origins=manual,scheduler");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var runIds = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.ToList();

		runIds.Should().BeEquivalentTo("manual-1", "scheduler-1");
	}

	[Fact]
	public async Task History_RootsTrue_ExcludesChildren()
	{
		await _store.SaveRunAsync(CreateRecord(runId: "root-1"), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "child-1", parentExecutionId: "root-1", nestingDepth: 1), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history?roots=true");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var runIds = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.ToList();

		runIds.Should().ContainSingle().Which.Should().Be("root-1");
	}

	[Fact]
	public async Task History_RootsFalse_ExcludesRoots()
	{
		await _store.SaveRunAsync(CreateRecord(runId: "root-1"), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "child-1", parentExecutionId: "root-1", nestingDepth: 1), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history?roots=false");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var runIds = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.ToList();

		runIds.Should().ContainSingle().Which.Should().Be("child-1");
	}

	[Fact]
	public async Task History_StatusesFilter_KeepsOnlyMatchingStatuses()
	{
		await _store.SaveRunAsync(CreateRecord(runId: "ok-1", status: ExecutionStatus.Succeeded), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "fail-1", status: ExecutionStatus.Failed), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "cancel-1", status: ExecutionStatus.Cancelled), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history?statuses=Failed,Cancelled");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var runIds = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.ToList();

		runIds.Should().BeEquivalentTo("fail-1", "cancel-1");
	}

	[Fact]
	public async Task History_StackedFilters_AllMustMatch()
	{
		// Mix of origins, statuses, and parent presence
		await _store.SaveRunAsync(CreateRecord(runId: "match", triggeredBy: "manual", status: ExecutionStatus.Failed), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "wrong-origin", triggeredBy: "scheduler", status: ExecutionStatus.Failed), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "wrong-status", triggeredBy: "manual", status: ExecutionStatus.Succeeded), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "wrong-scope", triggeredBy: "manual", status: ExecutionStatus.Failed, parentExecutionId: "p1"), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history?origins=manual&statuses=Failed&roots=true");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var runIds = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.ToList();

		runIds.Should().ContainSingle().Which.Should().Be("match");
	}

	[Fact]
	public async Task HistoryAll_AppliesSameFilters()
	{
		await _store.SaveRunAsync(CreateRecord(runId: "manual-1", triggeredBy: "manual"), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "retry-1", triggeredBy: "retry", retriedFromRunId: "manual-1"), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history/all?origins=retry");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var runIds = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.ToList();

		runIds.Should().ContainSingle().Which.Should().Be("retry-1");
	}

	[Fact]
	public async Task HistorySearch_AppliesSameFilters()
	{
		await _store.SaveRunAsync(CreateRecord(runId: "name-A", orchestrationName: "alpha", triggeredBy: "manual"), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "name-B", orchestrationName: "alpha", triggeredBy: "scheduler"), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history/search?query=alpha&origins=manual");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var runIds = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.ToList();

		runIds.Should().ContainSingle().Which.Should().Be("name-A");
	}

	[Fact]
	public async Task History_NoFilters_ReturnsAllRows()
	{
		// Sanity: ensure default (no filter) behaviour is preserved when none of the new
		// query parameters are provided.
		await _store.SaveRunAsync(CreateRecord(runId: "a"), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "b", parentExecutionId: "a", nestingDepth: 1), cancellationToken: default);
		await _store.SaveRunAsync(CreateRecord(runId: "c", triggeredBy: "scheduler"), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		doc.RootElement.GetProperty("count").GetInt32().Should().Be(3);
	}

	[Fact]
	public async Task History_ParentNameMissing_ParentOrchestrationNameIsNull()
	{
		// Child references a parent that is no longer in the index (e.g. retention pruned it).
		await _store.SaveRunAsync(CreateRecord(
			runId: "orphan-child",
			triggeredBy: "orchestration:gone:abc",
			parentExecutionId: "missing-parent",
			rootExecutionId: "missing-parent",
			nestingDepth: 1), cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/history");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var child = doc.RootElement.GetProperty("runs").EnumerateArray()
			.First(r => r.GetProperty("runId").GetString() == "orphan-child");

		child.GetProperty("parentExecutionId").GetString().Should().Be("missing-parent");
		// JSON serializer is configured to omit null fields, so an unresolved parent
		// surfaces as a missing parentOrchestrationName property rather than a JSON null.
		child.TryGetProperty("parentOrchestrationName", out _).Should().BeFalse(
			"unresolved parents leave the name absent instead of fabricating one");
	}

	[Fact]
	public async Task History_OrchestrationStep_SurfacesChildExecutionIdAndName()
	{
		// A run whose step records include both an Orchestration step (with child lineage
		// fields populated) and a non-orchestration step (with the fields null) must surface
		// childExecutionId/Name/Status on the former and omit them on the latter — letting
		// Portal render parent → child navigation without inferring lineage from triggeredBy.
		var now = DateTimeOffset.UtcNow;
		var record = new OrchestrationRunRecord
		{
			RunId = "parent-run",
			OrchestrationName = "parent-orch",
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = now.AddMinutes(-1),
			CompletedAt = now,
			Status = ExecutionStatus.Succeeded,
			IsIncomplete = false,
			FinalContent = string.Empty,
			SavedFiles = [],
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(StringComparer.OrdinalIgnoreCase)
			{
				["invoke-child"] = new StepRunRecord
				{
					StepName = "invoke-child",
					Status = ExecutionStatus.Succeeded,
					StartedAt = now.AddSeconds(-30),
					CompletedAt = now.AddSeconds(-25),
					Content = "child final content",
					ChildExecutionId = "child-exec-id-99",
					ChildOrchestrationName = "child-orch",
					ChildStatus = ExecutionStatus.Failed,
				},
				["plain-prompt"] = new StepRunRecord
				{
					StepName = "plain-prompt",
					Status = ExecutionStatus.Succeeded,
					StartedAt = now.AddSeconds(-20),
					CompletedAt = now.AddSeconds(-15),
					Content = "agent output",
				},
			},
			AllStepRecords = new Dictionary<string, StepRunRecord>(),
		};
		await _store.SaveRunAsync(record, cancellationToken: default);

		using var host = CreateHost();
		var client = host.GetTestClient();
		var response = await client.GetAsync("/api/history/parent-orch/parent-run");
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		var steps = doc.RootElement.GetProperty("steps").EnumerateArray()
			.ToDictionary(s => s.GetProperty("name").GetString()!, s => s);

		// Orchestration step: child fields surfaced
		var invokeChild = steps["invoke-child"];
		invokeChild.GetProperty("childExecutionId").GetString().Should().Be("child-exec-id-99");
		invokeChild.GetProperty("childOrchestrationName").GetString().Should().Be("child-orch");
		invokeChild.GetProperty("childStatus").GetString().Should().Be("failed",
			"child status must be lowercased for symmetry with the rest of the projection");

		// Non-orchestration step: null fields elided by the WhenWritingNull serializer
		var plainPrompt = steps["plain-prompt"];
		plainPrompt.TryGetProperty("childExecutionId", out _).Should().BeFalse(
			"plain steps must not carry a childExecutionId property when ChildExecutionId is null");
		plainPrompt.TryGetProperty("childOrchestrationName", out _).Should().BeFalse();
		plainPrompt.TryGetProperty("childStatus", out _).Should().BeFalse();
	}

	private IHost CreateHost()
	{
		var jsonOptions = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
			Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
		};

		var activeExecutionInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		var triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			activeExecutionInfos,
			agentBuilder: null!,
			scheduler: new OrchestrationScheduler(),
			loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
			logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<TriggerManager>.Instance,
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
					services.AddSingleton(new RunAnnotationStore(Path.Combine(Path.GetTempPath(), $"orchestra-api-annotations-{Guid.NewGuid():N}"), NullLogger<RunAnnotationStore>.Instance));
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
}
