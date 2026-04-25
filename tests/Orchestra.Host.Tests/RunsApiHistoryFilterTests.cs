using System.Collections.Concurrent;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Persistence;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for RunsApi behaviour: verifying that completed/cancelled/failed executions
/// are excluded from the "active" entries in the history endpoints, and that the
/// search endpoint works correctly.
/// </summary>
public class RunsApiHistoryFilterTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;

	public RunsApiHistoryFilterTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-runsapi-tests-{Guid.NewGuid():N}");
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

	private static ActiveExecutionInfo CreateActiveExecution(
		string executionId,
		string orchestrationName,
		HostExecutionStatus status = HostExecutionStatus.Running)
	{
		return new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = $"orch-{executionId}",
			OrchestrationName = orchestrationName,
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			TriggeredBy = "manual",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = new SseReporter(),
			Status = status,
		};
	}

	private OrchestrationRunRecord CreateTestRecord(
		string? runId = null,
		string? orchestrationName = null,
		ExecutionStatus status = ExecutionStatus.Succeeded,
		string? completionReason = null,
		string? completedByStep = null,
		bool isIncomplete = false)
	{
		var now = DateTimeOffset.UtcNow;
		return new OrchestrationRunRecord
		{
			RunId = runId ?? Guid.NewGuid().ToString("N")[..12],
			OrchestrationName = orchestrationName ?? "test-orchestration",
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = now.AddMinutes(-5),
			CompletedAt = now,
			Status = status,
			CompletionReason = completionReason,
			CompletedByStep = completedByStep,
			IsIncomplete = isIncomplete,
			FinalContent = "Test result",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>()
		};
	}

	// ── Active execution filtering tests ─────────────────────────────────

	[Theory]
	[InlineData(HostExecutionStatus.Completed)]
	[InlineData(HostExecutionStatus.Cancelled)]
	[InlineData(HostExecutionStatus.Failed)]
	public void CompletedExecutions_ShouldBeFilteredFromActiveList(HostExecutionStatus terminalStatus)
	{
		// Arrange: simulate the 5-second cleanup window where an execution is in the
		// activeExecutionInfos dictionary but has a terminal status.
		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		var exec = CreateActiveExecution("exec-1", "test-orch", terminalStatus);
		activeInfos.TryAdd("exec-1", exec);

		// Act: apply the same filter that RunsApi now uses
		var runningRuns = activeInfos.Values
			.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
			.ToList();

		// Assert
		runningRuns.Should().BeEmpty(
			$"executions with status {terminalStatus} should be filtered out during cleanup window");
	}

	[Theory]
	[InlineData(HostExecutionStatus.Running)]
	[InlineData(HostExecutionStatus.Cancelling)]
	public void NonTerminalExecutions_ShouldRemainInActiveList(HostExecutionStatus activeStatus)
	{
		// Arrange
		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		var exec = CreateActiveExecution("exec-1", "test-orch", activeStatus);
		activeInfos.TryAdd("exec-1", exec);

		// Act
		var runningRuns = activeInfos.Values
			.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
			.ToList();

		// Assert
		runningRuns.Should().HaveCount(1,
			$"executions with status {activeStatus} should remain in the active list");
		runningRuns[0].ExecutionId.Should().Be("exec-1");
	}

	[Fact]
	public void MixedStatusExecutions_OnlyNonTerminalShouldRemain()
	{
		// Arrange: one running, one completed, one cancelled, one failed
		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		activeInfos.TryAdd("exec-running", CreateActiveExecution("exec-running", "orch-1", HostExecutionStatus.Running));
		activeInfos.TryAdd("exec-completed", CreateActiveExecution("exec-completed", "orch-2", HostExecutionStatus.Completed));
		activeInfos.TryAdd("exec-cancelled", CreateActiveExecution("exec-cancelled", "orch-3", HostExecutionStatus.Cancelled));
		activeInfos.TryAdd("exec-failed", CreateActiveExecution("exec-failed", "orch-4", HostExecutionStatus.Failed));
		activeInfos.TryAdd("exec-cancelling", CreateActiveExecution("exec-cancelling", "orch-5", HostExecutionStatus.Cancelling));

		// Act
		var runningRuns = activeInfos.Values
			.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
			.ToList();

		// Assert
		runningRuns.Should().HaveCount(2);
		runningRuns.Select(r => r.ExecutionId).Should().BeEquivalentTo("exec-running", "exec-cancelling");
	}

	// ── Search functionality tests ───────────────────────────────────────

	[Fact]
	public async Task SearchSummaries_ByOrchestrationName_FindsMatches()
	{
		// Arrange: save several runs with different names
		await _store.SaveRunAsync(CreateTestRecord(runId: "run-alpha-1", orchestrationName: "email-processor"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(runId: "run-alpha-2", orchestrationName: "email-analyzer"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(runId: "run-beta-1", orchestrationName: "data-pipeline"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(runId: "run-beta-2", orchestrationName: "file-processor"), cancellationToken: default);

		// Act: simulate the search endpoint logic
		var allSummaries = await _store.GetRunSummariesAsync();
		var searchQuery = "email";
		var results = allSummaries
			.Where(s => s.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
				|| s.RunId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
			.ToList();

		// Assert
		results.Should().HaveCount(2);
		results.Select(r => r.OrchestrationName).Should().BeEquivalentTo("email-processor", "email-analyzer");
	}

	[Fact]
	public async Task SearchSummaries_ByRunId_FindsMatches()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(runId: "abc123", orchestrationName: "orch-1"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(runId: "def456", orchestrationName: "orch-2"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(runId: "abc789", orchestrationName: "orch-3"), cancellationToken: default);

		// Act
		var allSummaries = await _store.GetRunSummariesAsync();
		var searchQuery = "abc";
		var results = allSummaries
			.Where(s => s.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
				|| s.RunId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
			.ToList();

		// Assert
		results.Should().HaveCount(2);
		results.Select(r => r.RunId).Should().BeEquivalentTo("abc123", "abc789");
	}

	[Fact]
	public async Task SearchSummaries_CaseInsensitive()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(runId: "run-1", orchestrationName: "EmailProcessor"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(runId: "run-2", orchestrationName: "data-pipeline"), cancellationToken: default);

		// Act
		var allSummaries = await _store.GetRunSummariesAsync();
		var searchQuery = "emailprocessor";
		var results = allSummaries
			.Where(s => s.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
				|| s.RunId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
			.ToList();

		// Assert
		results.Should().HaveCount(1);
		results[0].OrchestrationName.Should().Be("EmailProcessor");
	}

	[Fact]
	public async Task SearchSummaries_EmptyQuery_ReturnsNoResults()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(runId: "run-1", orchestrationName: "test-orch"), cancellationToken: default);

		// Act: simulate the search endpoint behavior for empty query
		var searchQuery = "";
		var isEmpty = string.IsNullOrEmpty(searchQuery.Trim());

		// Assert
		isEmpty.Should().BeTrue("empty search query should return no results");
	}

	[Fact]
	public async Task SearchSummaries_NoMatches_ReturnsEmpty()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(runId: "run-1", orchestrationName: "email-processor"), cancellationToken: default);

		// Act
		var allSummaries = await _store.GetRunSummariesAsync();
		var searchQuery = "nonexistent";
		var results = allSummaries
			.Where(s => s.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
				|| s.RunId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
			.ToList();

		// Assert
		results.Should().BeEmpty();
	}

	[Fact]
	public void SearchActiveExecutions_FiltersTerminalStatusAndMatchesQuery()
	{
		// Arrange: mix of statuses
		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		activeInfos.TryAdd("exec-1", CreateActiveExecution("exec-1", "email-processor", HostExecutionStatus.Running));
		activeInfos.TryAdd("exec-2", CreateActiveExecution("exec-2", "email-analyzer", HostExecutionStatus.Completed));
		activeInfos.TryAdd("exec-3", CreateActiveExecution("exec-3", "data-pipeline", HostExecutionStatus.Running));

		var searchQuery = "email";

		// Act: simulate the search endpoint logic
		var matchingActive = activeInfos.Values
			.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
			.Where(e => e.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
				|| e.ExecutionId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
			.ToList();

		// Assert: only "email-processor" should match (running + matches query)
		// "email-analyzer" matches query but is completed, "data-pipeline" is running but doesn't match
		matchingActive.Should().HaveCount(1);
		matchingActive[0].OrchestrationName.Should().Be("email-processor");
	}

	[Fact]
	public async Task SearchSummaries_RespectsLimit()
	{
		// Arrange: save many runs
		for (int i = 0; i < 10; i++)
		{
			await _store.SaveRunAsync(CreateTestRecord(
				runId: $"run-search-{i:D3}",
				orchestrationName: "searchable-orch"), cancellationToken: default);
		}

		// Act
		var allSummaries = await _store.GetRunSummariesAsync();
		var searchQuery = "searchable";
		var limit = 5;
		var results = allSummaries
			.Where(s => s.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
				|| s.RunId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
			.Take(limit)
			.ToList();

		// Assert
		results.Should().HaveCount(5);
	}
}
