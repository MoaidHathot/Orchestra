using System.Collections.Concurrent;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.McpServer;
using Orchestra.Host.Persistence;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for <see cref="HistoryFilterParser"/>: parsing query strings into <see cref="HistoryFilters"/>
/// and applying those filters to <see cref="ActiveExecutionInfo"/> and <see cref="RunIndex"/>.
/// </summary>
public class HistoryFilterParserTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;

	public HistoryFilterParserTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-filter-tests-{Guid.NewGuid():N}");
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

	// ── Parse() ─────────────────────────────────────────────────────────

	[Fact]
	public void Parse_AllNull_ReturnsAllNullFilters()
	{
		var filters = HistoryFilterParser.Parse(null, null, null);

		filters.Origins.Should().BeNull();
		filters.Roots.Should().BeNull();
		filters.Statuses.Should().BeNull();
		filters.HasAnyFilter.Should().BeFalse();
	}

	[Fact]
	public void Parse_EmptyOriginsString_TreatedAsNoFilter()
	{
		var filters = HistoryFilterParser.Parse("", null, "");

		filters.Origins.Should().BeNull("empty allow-list is equivalent to no filter");
		filters.Statuses.Should().BeNull();
		filters.HasAnyFilter.Should().BeFalse();
	}

	[Fact]
	public void Parse_OriginsWithOnlyUnknownTokens_TreatedAsNoFilter()
	{
		// All tokens drop out, so the resulting allow-list is empty -> treated as "no filter"
		// rather than "match nothing", which is more user-forgiving for typos.
		var filters = HistoryFilterParser.Parse("garbage,more-garbage", null, null);

		filters.Origins.Should().BeNull();
		filters.HasAnyFilter.Should().BeFalse();
	}

	[Fact]
	public void Parse_OriginsCsv_ParsesAndTrims()
	{
		var filters = HistoryFilterParser.Parse(" manual , Scheduler ,orchestration", null, null);

		filters.Origins.Should().NotBeNull();
		filters.Origins!.Should().BeEquivalentTo(new[]
		{
			RunOriginKind.Manual,
			RunOriginKind.Scheduler,
			RunOriginKind.Orchestration,
		});
		filters.HasAnyFilter.Should().BeTrue();
	}

	[Fact]
	public void Parse_StatusesCsv_ParsesAndPreservesCasingForCaseInsensitiveMatch()
	{
		var filters = HistoryFilterParser.Parse(null, null, "Running, Succeeded");

		filters.Statuses.Should().NotBeNull();
		// HashSet was constructed with OrdinalIgnoreCase comparer, so checks pass case-insensitively.
		filters.Statuses!.Contains("running").Should().BeTrue();
		filters.Statuses.Contains("SUCCEEDED").Should().BeTrue();
		filters.Statuses.Contains("Failed").Should().BeFalse();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Parse_RootsTriState_PreservedExactly(bool roots)
	{
		var filters = HistoryFilterParser.Parse(null, roots, null);

		filters.Roots.Should().Be(roots);
		filters.HasAnyFilter.Should().BeTrue();
	}

	// ── Matches(ActiveExecutionInfo) ────────────────────────────────────

	private static ActiveExecutionInfo CreateActive(
		string triggeredBy = "manual",
		HostExecutionStatus status = HostExecutionStatus.Running,
		string? parentExecutionId = null)
	{
		var nesting = parentExecutionId is null
			? null
			: new ExecutionMetadata
			{
				ParentExecutionId = parentExecutionId,
				RootExecutionId = parentExecutionId,
				Depth = 1,
			};

		return new ActiveExecutionInfo
		{
			ExecutionId = "exec-" + Guid.NewGuid().ToString("N")[..8],
			OrchestrationId = "orch-1",
			OrchestrationName = "test",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = triggeredBy,
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = new SseReporter(),
			Status = status,
			NestingMetadata = nesting,
		};
	}

	[Fact]
	public void MatchesActive_NoFilters_AlwaysTrue()
	{
		var filters = HistoryFilterParser.Parse(null, null, null);
		var info = CreateActive();

		HistoryFilterParser.Matches(info, filters).Should().BeTrue();
	}

	[Fact]
	public void MatchesActive_OriginAllowList_FiltersByTriggeredBy()
	{
		var filters = HistoryFilterParser.Parse("manual,scheduler", null, null);

		HistoryFilterParser.Matches(CreateActive(triggeredBy: "manual"), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateActive(triggeredBy: "scheduler"), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateActive(triggeredBy: "webhook"), filters).Should().BeFalse();
		HistoryFilterParser.Matches(CreateActive(triggeredBy: "orchestration:p:abc"), filters).Should().BeFalse();
	}

	[Fact]
	public void MatchesActive_RootsTrue_ExcludesChildren()
	{
		var filters = HistoryFilterParser.Parse(null, true, null);

		HistoryFilterParser.Matches(CreateActive(parentExecutionId: null), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateActive(parentExecutionId: "parent-1"), filters).Should().BeFalse();
	}

	[Fact]
	public void MatchesActive_RootsFalse_OnlyChildren()
	{
		var filters = HistoryFilterParser.Parse(null, false, null);

		HistoryFilterParser.Matches(CreateActive(parentExecutionId: null), filters).Should().BeFalse();
		HistoryFilterParser.Matches(CreateActive(parentExecutionId: "parent-1"), filters).Should().BeTrue();
	}

	[Fact]
	public void MatchesActive_StatusFilter_AppliedCaseInsensitively()
	{
		var filters = HistoryFilterParser.Parse(null, null, "running");

		HistoryFilterParser.Matches(CreateActive(status: HostExecutionStatus.Running), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateActive(status: HostExecutionStatus.Cancelling), filters).Should().BeFalse();
	}

	// ── Matches(RunIndex) ───────────────────────────────────────────────

	private static RunIndex CreateIndex(
		string triggeredBy = "manual",
		ExecutionStatus status = ExecutionStatus.Succeeded,
		string? parentExecutionId = null)
	{
		var now = DateTimeOffset.UtcNow;
		return new RunIndex
		{
			RunId = "run-" + Guid.NewGuid().ToString("N")[..8],
			OrchestrationName = "test",
			TriggeredBy = triggeredBy,
			StartedAt = now.AddMinutes(-1),
			CompletedAt = now,
			Status = status,
			FolderPath = "/tmp",
			ParentExecutionId = parentExecutionId,
			RootExecutionId = parentExecutionId is null ? null : "root-1",
			NestingDepth = parentExecutionId is null ? 0 : 1,
		};
	}

	[Fact]
	public void MatchesIndex_OriginAllowList_FiltersByTriggeredBy()
	{
		var filters = HistoryFilterParser.Parse("retry,resume", null, null);

		HistoryFilterParser.Matches(CreateIndex(triggeredBy: "retry"), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateIndex(triggeredBy: "resume"), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateIndex(triggeredBy: "manual"), filters).Should().BeFalse();
	}

	[Fact]
	public void MatchesIndex_RootsTrue_ExcludesChildren()
	{
		var filters = HistoryFilterParser.Parse(null, true, null);

		HistoryFilterParser.Matches(CreateIndex(parentExecutionId: null), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateIndex(parentExecutionId: "p1"), filters).Should().BeFalse();
	}

	[Fact]
	public void MatchesIndex_StatusFilter_AcceptsExactNameCaseInsensitive()
	{
		var filters = HistoryFilterParser.Parse(null, null, "Failed,Cancelled");

		HistoryFilterParser.Matches(CreateIndex(status: ExecutionStatus.Failed), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateIndex(status: ExecutionStatus.Cancelled), filters).Should().BeTrue();
		HistoryFilterParser.Matches(CreateIndex(status: ExecutionStatus.Succeeded), filters).Should().BeFalse();
	}

	[Fact]
	public void MatchesIndex_AllFiltersStacked_AllMustMatch()
	{
		var filters = HistoryFilterParser.Parse("manual", true, "Succeeded");

		// Manual + root + succeeded -> match
		HistoryFilterParser.Matches(
			CreateIndex(triggeredBy: "manual", status: ExecutionStatus.Succeeded, parentExecutionId: null),
			filters).Should().BeTrue();

		// Manual + child + succeeded -> miss (child)
		HistoryFilterParser.Matches(
			CreateIndex(triggeredBy: "manual", status: ExecutionStatus.Succeeded, parentExecutionId: "p1"),
			filters).Should().BeFalse();

		// Scheduler + root + succeeded -> miss (origin)
		HistoryFilterParser.Matches(
			CreateIndex(triggeredBy: "scheduler", status: ExecutionStatus.Succeeded, parentExecutionId: null),
			filters).Should().BeFalse();

		// Manual + root + failed -> miss (status)
		HistoryFilterParser.Matches(
			CreateIndex(triggeredBy: "manual", status: ExecutionStatus.Failed, parentExecutionId: null),
			filters).Should().BeFalse();
	}

	// ── End-to-end: persisted lineage round-trips through RunIndex ──────

	[Fact]
	public async Task RunIndex_RoundTripsLineageFields()
	{
		// Save a run with all the new lineage fields populated, then read it back via the index
		// and verify nothing was dropped on the way through.
		var record = CreateRecord(
			runId: "child-run",
			orchestrationName: "child-orch",
			parentExecutionId: "parent-run-id",
			parentStepName: "parent-step",
			rootExecutionId: "root-run-id",
			nestingDepth: 2,
			retriedFromRunId: "previous-run",
			retryMode: "from-step:judge");

		await _store.SaveRunAsync(record, cancellationToken: default);

		var summaries = await _store.GetRunSummariesAsync();
		var loaded = summaries.FirstOrDefault(s => s.RunId == "child-run");

		loaded.Should().NotBeNull();
		loaded!.ParentExecutionId.Should().Be("parent-run-id");
		loaded.ParentStepName.Should().Be("parent-step");
		loaded.RootExecutionId.Should().Be("root-run-id");
		loaded.NestingDepth.Should().Be(2);
		loaded.RetriedFromRunId.Should().Be("previous-run");
		loaded.RetryMode.Should().Be("from-step:judge");
	}

	[Fact]
	public async Task RunIndex_DefaultsForRootRunsAreNullsAndZero()
	{
		var record = CreateRecord(
			runId: "root-run",
			orchestrationName: "root-orch",
			parentExecutionId: null,
			parentStepName: null,
			rootExecutionId: null,
			nestingDepth: 0,
			retriedFromRunId: null,
			retryMode: null);

		await _store.SaveRunAsync(record, cancellationToken: default);

		var summaries = await _store.GetRunSummariesAsync();
		var loaded = summaries.FirstOrDefault(s => s.RunId == "root-run");

		loaded.Should().NotBeNull();
		loaded!.ParentExecutionId.Should().BeNull();
		loaded.ParentStepName.Should().BeNull();
		loaded.RootExecutionId.Should().BeNull();
		loaded.NestingDepth.Should().Be(0);
		loaded.RetriedFromRunId.Should().BeNull();
		loaded.RetryMode.Should().BeNull();
	}

	private static OrchestrationRunRecord CreateRecord(
		string runId,
		string orchestrationName,
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
			TriggeredBy = retriedFromRunId is not null ? "retry" : "manual",
			StartedAt = now.AddMinutes(-1),
			CompletedAt = now,
			Status = ExecutionStatus.Succeeded,
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
}
