using System.Collections.Concurrent;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for TriggerManager.CancelExecution() — specifically verifying
/// that it emits SSE status-changed events and correctly manages the
/// ActiveExecutionInfo state.
/// </summary>
public class TriggerManagerCancelTests
{
	/// <summary>
	/// Creates a TriggerManager instance with minimal wiring for cancel tests.
	/// We inject pre-populated activeExecutions and activeExecutionInfos dictionaries
	/// so we can test CancelExecution() without running actual orchestrations.
	/// </summary>
	private static TriggerManager CreateTriggerManager(
		ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos)
	{
		var tempDir = Path.Combine(Path.GetTempPath(), "Orchestra.TriggerManagerTests", Guid.NewGuid().ToString("N"));
		var runsDir = Path.Combine(tempDir, "runs");
		Directory.CreateDirectory(runsDir);

		var loggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
		var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<TriggerManager>();

		return new TriggerManager(
			activeExecutions,
			activeExecutionInfos,
			agentBuilder: null!, // Not needed for cancel tests
			scheduler: null!, // Not needed for cancel tests
			loggerFactory: loggerFactory,
			logger: logger,
			runsDir: runsDir,
			runStore: null!, // Not needed for cancel tests
			checkpointStore: null! // Not needed for cancel tests
		);
	}

	[Fact]
	public void CancelExecution_WithSseReporter_EmitsStatusChangedEvent()
	{
		// Arrange
		var executionId = "test-cancel-sse";
		using var cts = new CancellationTokenSource();
		using var reporter = new SseReporter();

		var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		activeExecutions[executionId] = cts;

		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		activeInfos[executionId] = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-1",
			OrchestrationName = "Test Orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "scheduler",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Running
		};

		var triggerManager = CreateTriggerManager(activeExecutions, activeInfos);

		// Act
		var result = triggerManager.CancelExecution(executionId);

		// Assert
		result.Should().BeTrue();

		// Verify status was changed to Cancelling
		activeInfos[executionId].Status.Should().Be(HostExecutionStatus.Cancelling);

		// Verify SSE event was emitted
		reporter.AccumulatedEvents.Should().ContainSingle(e => e.Type == "status-changed");
		reporter.AccumulatedEvents[0].Data.Should().Contain("Cancelling");

		// Verify CTS was actually cancelled
		cts.Token.IsCancellationRequested.Should().BeTrue();
	}

	[Fact]
	public async Task CancelExecution_WithSseReporter_SubscriberReceivesStatusChangedEvent()
	{
		// Arrange
		var executionId = "test-cancel-sub";
		using var cts = new CancellationTokenSource();
		using var reporter = new SseReporter();

		var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		activeExecutions[executionId] = cts;

		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		activeInfos[executionId] = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-1",
			OrchestrationName = "Test Orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "webhook",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Running
		};

		// Subscribe BEFORE cancellation
		var (replay, future) = reporter.Subscribe();
		future.Should().NotBeNull();
		replay.Should().BeEmpty();

		var triggerManager = CreateTriggerManager(activeExecutions, activeInfos);

		// Act
		triggerManager.CancelExecution(executionId);

		// Assert — subscriber should receive the status-changed event
		var evt = await future!.ReadAsync();
		evt.Type.Should().Be("status-changed");
		evt.Data.Should().Contain("Cancelling");
	}

	[Fact]
	public void CancelExecution_WithNonSseReporter_StillCancelsToken()
	{
		// Arrange - use a non-SseReporter
		var executionId = "test-cancel-non-sse";
		using var cts = new CancellationTokenSource();
		var reporter = NSubstitute.Substitute.For<IOrchestrationReporter>();

		var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		activeExecutions[executionId] = cts;

		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		activeInfos[executionId] = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-1",
			OrchestrationName = "Test Orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Running
		};

		var triggerManager = CreateTriggerManager(activeExecutions, activeInfos);

		// Act
		var result = triggerManager.CancelExecution(executionId);

		// Assert — should still cancel the token and update status
		result.Should().BeTrue();
		activeInfos[executionId].Status.Should().Be(HostExecutionStatus.Cancelling);
		cts.Token.IsCancellationRequested.Should().BeTrue();
	}

	[Fact]
	public void CancelExecution_NonExistentExecution_ReturnsFalse()
	{
		// Arrange
		var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();

		var triggerManager = CreateTriggerManager(activeExecutions, activeInfos);

		// Act
		var result = triggerManager.CancelExecution("nonexistent-id");

		// Assert
		result.Should().BeFalse();
	}

	[Fact]
	public void CancelExecution_DoubleCancelSameExecution_SecondCallStillSucceeds()
	{
		// Arrange — The first Cancel() on CTS will fire the token, the second is a no-op.
		// TriggerManager.CancelExecution should handle this gracefully.
		var executionId = "test-double-cancel";
		using var cts = new CancellationTokenSource();
		using var reporter = new SseReporter();

		var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		activeExecutions[executionId] = cts;

		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		activeInfos[executionId] = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-1",
			OrchestrationName = "Test Orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Running
		};

		var triggerManager = CreateTriggerManager(activeExecutions, activeInfos);

		// Act — Cancel twice
		var result1 = triggerManager.CancelExecution(executionId);
		var result2 = triggerManager.CancelExecution(executionId);

		// Assert — both calls should succeed (CTS.Cancel() is idempotent)
		result1.Should().BeTrue();
		result2.Should().BeTrue();
		activeInfos[executionId].Status.Should().Be(HostExecutionStatus.Cancelling);
		cts.Token.IsCancellationRequested.Should().BeTrue();

		// Two status-changed events should have been emitted
		reporter.AccumulatedEvents.Count(e => e.Type == "status-changed").Should().Be(2);
	}

	[Fact]
	public void CancelExecution_AfterExecutionRemoved_ReturnsFalse()
	{
		// Arrange — Simulates race condition where execution completes right as cancel is called.
		// The execution has been removed from activeExecutions but may still be in activeInfos.
		var executionId = "test-cancel-race";

		var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		// Execution already removed from activeExecutions (it completed)

		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		// But the info might still be there briefly
		activeInfos[executionId] = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-1",
			OrchestrationName = "Test Orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = NSubstitute.Substitute.For<IOrchestrationReporter>(),
			Status = HostExecutionStatus.Completed
		};

		var triggerManager = CreateTriggerManager(activeExecutions, activeInfos);

		// Act — CancelExecution looks up activeExecutions first, so this returns false
		var result = triggerManager.CancelExecution(executionId);

		// Assert
		result.Should().BeFalse();
		// The info status should remain unchanged
		activeInfos[executionId].Status.Should().Be(HostExecutionStatus.Completed);
	}

	[Fact]
	public void CancelExecution_WithCtsInDictButNoInfo_StillCancelsToken()
	{
		// Arrange — edge case where the CTS is in activeExecutions but the info hasn't
		// been added to activeExecutionInfos yet (race during startup)
		var executionId = "test-cancel-no-info";
		using var cts = new CancellationTokenSource();

		var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		activeExecutions[executionId] = cts;

		var activeInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		// intentionally empty — no matching info

		var triggerManager = CreateTriggerManager(activeExecutions, activeInfos);

		// Act
		var result = triggerManager.CancelExecution(executionId);

		// Assert — should still return true and cancel the token
		result.Should().BeTrue();
		cts.Token.IsCancellationRequested.Should().BeTrue();
	}
}
