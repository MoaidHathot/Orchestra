using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for SseReporter's bounded memory, subscriber limits, and lifecycle.
/// </summary>
public class SseReporterTests : IDisposable
{
	private readonly SseReporter _reporter;

	public SseReporterTests()
	{
		_reporter = new SseReporter();
	}

	public void Dispose()
	{
		_reporter.Dispose();
	}

	[Fact]
	public void NewReporter_HasNoEvents()
	{
		_reporter.AccumulatedEventCount.Should().Be(0);
		_reporter.AccumulatedEvents.Should().BeEmpty();
	}

	[Fact]
	public void NewReporter_IsNotCompleted()
	{
		_reporter.IsCompleted.Should().BeFalse();
	}

	[Fact]
	public void NewReporter_HasNoSubscribers()
	{
		_reporter.SubscriberCount.Should().Be(0);
	}

	[Fact]
	public void ReportStepStarted_AddsEvent()
	{
		_reporter.ReportStepStarted("step-1");

		_reporter.AccumulatedEventCount.Should().Be(1);
		var events = _reporter.AccumulatedEvents;
		events.Should().HaveCount(1);
		events[0].Type.Should().Be("step-started");
		events[0].Data.Should().Contain("step-1");
	}

	[Fact]
	public void ReportStepCompleted_AddsEvent()
	{
		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "output" }, OrchestrationStepType.Prompt);

		_reporter.AccumulatedEventCount.Should().Be(1);
		_reporter.AccumulatedEvents[0].Type.Should().Be("step-completed");
	}

	[Fact]
	public void ReportStepOutput_AddsEvent()
	{
		_reporter.ReportStepOutput("step-1", "some output");

		_reporter.AccumulatedEventCount.Should().Be(1);
		_reporter.AccumulatedEvents[0].Type.Should().Be("step-output");
		_reporter.AccumulatedEvents[0].Data.Should().Contain("some output");
	}

	[Fact]
	public void ReportStepError_AddsEvent()
	{
		_reporter.ReportStepError("step-1", "something went wrong");

		_reporter.AccumulatedEventCount.Should().Be(1);
		_reporter.AccumulatedEvents[0].Type.Should().Be("step-error");
		_reporter.AccumulatedEvents[0].Data.Should().Contain("something went wrong");
	}

	[Fact]
	public void Complete_MarksReporterAsCompleted()
	{
		_reporter.Complete();

		_reporter.IsCompleted.Should().BeTrue();
	}

	[Fact]
	public void Complete_ClosesAllSubscriberChannels()
	{
		var (_, future1) = _reporter.Subscribe();
		var (_, future2) = _reporter.Subscribe();

		future1.Should().NotBeNull();
		future2.Should().NotBeNull();

		_reporter.Complete();

		// Channels should be completed — reading should return false when drained
		future1!.Completion.IsCompleted.Should().BeTrue();
		future2!.Completion.IsCompleted.Should().BeTrue();
	}

	[Fact]
	public void Subscribe_ReturnsReplayOfAccumulatedEvents()
	{
		_reporter.ReportStepStarted("step-1");
		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "output" }, OrchestrationStepType.Prompt);
		_reporter.ReportStepStarted("step-2");

		var (replay, _) = _reporter.Subscribe();

		replay.Should().HaveCount(3);
		replay[0].Type.Should().Be("step-started");
		replay[1].Type.Should().Be("step-completed");
		replay[2].Type.Should().Be("step-started");
	}

	[Fact]
	public void Subscribe_AfterComplete_ReturnsCompletedChannel()
	{
		_reporter.ReportStepStarted("step-1");
		_reporter.Complete();

		var (replay, future) = _reporter.Subscribe();

		replay.Should().HaveCount(1);
		future.Should().NotBeNull();
		future!.Completion.IsCompleted.Should().BeTrue();
	}

	[Fact]
	public async Task Subscribe_ReceivesFutureEvents()
	{
		var (_, future) = _reporter.Subscribe();
		future.Should().NotBeNull();

		_reporter.ReportStepStarted("step-1");

		// Should be able to read the event from the channel
		var result = await future!.ReadAsync();
		result.Type.Should().Be("step-started");
		result.Data.Should().Contain("step-1");
	}

	[Fact]
	public void Unsubscribe_RemovesSubscriber()
	{
		var (_, future) = _reporter.Subscribe();
		_reporter.SubscriberCount.Should().Be(1);

		_reporter.Unsubscribe(future);
		_reporter.SubscriberCount.Should().Be(0);
	}

	[Fact]
	public void Unsubscribe_NullReader_DoesNotThrow()
	{
		var act = () => _reporter.Unsubscribe(null);
		act.Should().NotThrow();
	}

	[Fact]
	public void MaxSubscribers_EnforcesLimit()
	{
		// Subscribe up to the max
		for (var i = 0; i < SseReporter.MaxSubscribers; i++)
		{
			var (_, future) = _reporter.Subscribe();
			future.Should().NotBeNull($"subscriber {i + 1} should be accepted");
		}

		_reporter.SubscriberCount.Should().Be(SseReporter.MaxSubscribers);

		// Next subscription should return null future
		var (replay, overflowFuture) = _reporter.Subscribe();
		overflowFuture.Should().BeNull("subscriber limit has been reached");

		// Replay should still work
		_reporter.ReportStepStarted("step-1");

		// Subscribe again after limit — should get replay but no future
		var (replay2, _) = _reporter.Subscribe();
		replay2.Should().HaveCount(1);
	}

	[Fact]
	public void CircularBuffer_WrapsAtMaxCapacity()
	{
		// Write more events than the buffer can hold
		var totalEvents = SseReporter.MaxAccumulatedEvents + 500;
		for (var i = 0; i < totalEvents; i++)
		{
			_reporter.ReportStepOutput($"step-{i}", $"output-{i}");
		}

		// Should only keep the most recent MaxAccumulatedEvents
		_reporter.AccumulatedEventCount.Should().Be(SseReporter.MaxAccumulatedEvents);

		var events = _reporter.AccumulatedEvents;
		events.Should().HaveCount(SseReporter.MaxAccumulatedEvents);

		// The oldest events should have been discarded.
		// The most recent event should be the last one we wrote.
		events[^1].Data.Should().Contain($"output-{totalEvents - 1}");

		// The first event in the buffer should be the one that survived the wrap.
		// That would be event number (totalEvents - MaxAccumulatedEvents) = 500
		events[0].Data.Should().Contain("output-500");
	}

	[Fact]
	public void Heartbeat_DoesNotAccumulate()
	{
		_reporter.ReportStepStarted("step-1");
		_reporter.SendHeartbeat();
		_reporter.SendHeartbeat();
		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "output" }, OrchestrationStepType.Prompt);

		// Heartbeats should NOT be in accumulated events
		_reporter.AccumulatedEventCount.Should().Be(2);
		_reporter.AccumulatedEvents.All(e => e.Type != "heartbeat").Should().BeTrue();
	}

	[Fact]
	public async Task Heartbeat_IsSentToSubscribers()
	{
		var (_, future) = _reporter.Subscribe();
		future.Should().NotBeNull();

		_reporter.SendHeartbeat();

		var evt = await future!.ReadAsync();
		evt.Type.Should().Be("heartbeat");
	}

	[Fact]
	public void Heartbeat_AfterComplete_DoesNothing()
	{
		var (_, future) = _reporter.Subscribe();
		_reporter.Complete();

		// Should not throw
		_reporter.SendHeartbeat();

		// Channel should still be completed (heartbeat didn't re-open it)
		future!.Completion.IsCompleted.Should().BeTrue();
	}

	[Fact]
	public void Dispose_MarksAsDisposed()
	{
		_reporter.ReportStepStarted("step-1");
		var (_, future) = _reporter.Subscribe();

		_reporter.Dispose();

		// After dispose, the reporter should be completed
		_reporter.IsCompleted.Should().BeTrue();
		future!.Completion.IsCompleted.Should().BeTrue();
	}

	[Fact]
	public void Dispose_CanBeCalledMultipleTimes()
	{
		_reporter.Dispose();
		var act = () => _reporter.Dispose();
		act.Should().NotThrow();
	}

	[Fact]
	public void OnStepStarted_CallbackInvoked()
	{
		string? calledWith = null;
		_reporter.OnStepStarted = name => calledWith = name;

		_reporter.ReportStepStarted("step-1");

		calledWith.Should().Be("step-1");
	}

	[Fact]
	public void OnStepCompleted_CallbackInvoked()
	{
		string? calledWith = null;
		_reporter.OnStepCompleted = name => calledWith = name;

		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "output" }, OrchestrationStepType.Prompt);

		calledWith.Should().Be("step-1");
	}

	[Fact]
	public void ReportStatusChange_AddsEvent()
	{
		_reporter.ReportStatusChange(HostExecutionStatus.Running);

		_reporter.AccumulatedEventCount.Should().Be(1);
		_reporter.AccumulatedEvents[0].Type.Should().Be("status-changed");
		_reporter.AccumulatedEvents[0].Data.Should().Contain("Running");
	}

	[Fact]
	public async Task MultipleSubscribers_AllReceiveEvents()
	{
		var (_, future1) = _reporter.Subscribe();
		var (_, future2) = _reporter.Subscribe();
		var (_, future3) = _reporter.Subscribe();

		_reporter.ReportStepStarted("step-1");

		var evt1 = await future1!.ReadAsync();
		var evt2 = await future2!.ReadAsync();
		var evt3 = await future3!.ReadAsync();

		evt1.Type.Should().Be("step-started");
		evt2.Type.Should().Be("step-started");
		evt3.Type.Should().Be("step-started");
	}

	[Fact]
	public void ReportOrchestrationDone_AddsCompletedEvent()
	{
		var result = new OrchestrationResult
		{
			Status = ExecutionStatus.Succeeded,
			Results = new Dictionary<string, ExecutionResult>
			{
				["step-1"] = ExecutionResult.Succeeded("output")
			},
			StepResults = new Dictionary<string, ExecutionResult>
			{
				["step-1"] = ExecutionResult.Succeeded("output")
			}
		};

		_reporter.ReportOrchestrationDone(result);

		_reporter.AccumulatedEvents.Should().Contain(e => e.Type == "orchestration-done");
	}

	[Fact]
	public void ReportOrchestrationCancelled_AddsEvent()
	{
		_reporter.ReportOrchestrationCancelled();

		_reporter.AccumulatedEvents.Should().Contain(e => e.Type == "orchestration-cancelled");
	}

	[Fact]
	public void ReportOrchestrationError_AddsEvent()
	{
		_reporter.ReportOrchestrationError("something failed");

		_reporter.AccumulatedEvents.Should().Contain(e => e.Type == "orchestration-error");
		_reporter.AccumulatedEvents[0].Data.Should().Contain("something failed");
	}

	[Fact]
	public async Task ReportStatusChange_Cancelling_SentToSubscribers()
	{
		// Arrange - subscribe before reporting
		var (_, future) = _reporter.Subscribe();
		future.Should().NotBeNull();

		// Act
		_reporter.ReportStatusChange(HostExecutionStatus.Cancelling);

		// Assert
		var evt = await future!.ReadAsync();
		evt.Type.Should().Be("status-changed");
		evt.Data.Should().Contain("Cancelling");
	}

	[Fact]
	public async Task CancellationFlow_StatusChangeThenCancelled_BothEventsDelivered()
	{
		// Arrange — Simulates the full cancellation SSE flow:
		// 1. status-changed (Cancelling)
		// 2. orchestration-cancelled
		var (_, future) = _reporter.Subscribe();
		future.Should().NotBeNull();

		// Act
		_reporter.ReportStatusChange(HostExecutionStatus.Cancelling);
		_reporter.ReportOrchestrationCancelled();

		// Assert — both events arrive in order
		var evt1 = await future!.ReadAsync();
		evt1.Type.Should().Be("status-changed");
		evt1.Data.Should().Contain("Cancelling");

		var evt2 = await future!.ReadAsync();
		evt2.Type.Should().Be("orchestration-cancelled");
	}

	[Fact]
	public async Task CancellationFlow_CompleteThenCancelled_ChannelIsClosed()
	{
		// Arrange
		var (_, future) = _reporter.Subscribe();
		future.Should().NotBeNull();

		// Act — emit cancellation events then complete
		_reporter.ReportStatusChange(HostExecutionStatus.Cancelling);
		_reporter.ReportOrchestrationCancelled();
		_reporter.Complete();

		// Drain the channel — ChannelReader.Completion only resolves once
		// all buffered items are consumed and the writer is completed.
		var events = new List<SseEvent>();
		while (await future!.WaitToReadAsync())
		{
			while (future.TryRead(out var evt))
			{
				events.Add(evt);
			}
		}

		// Assert — channel is now closed after draining
		future.Completion.IsCompleted.Should().BeTrue();
		events.Should().HaveCount(2);
		events[0].Type.Should().Be("status-changed");
		events[1].Type.Should().Be("orchestration-cancelled");

		// Accumulated events should contain both events
		_reporter.AccumulatedEvents.Should().HaveCount(2);
		_reporter.AccumulatedEvents[0].Type.Should().Be("status-changed");
		_reporter.AccumulatedEvents[1].Type.Should().Be("orchestration-cancelled");
	}

	[Fact]
	public void ReportStatusChange_AfterComplete_DoesNothing()
	{
		// Arrange
		_reporter.Complete();

		// Act — writing after completion should not throw or add events
		_reporter.ReportStatusChange(HostExecutionStatus.Cancelling);

		// Assert
		_reporter.AccumulatedEventCount.Should().Be(0);
	}

	[Fact]
	public async Task ReportStatusChange_LateSubscriber_ReceivesReplayOfCancellingEvent()
	{
		// Act — emit status change before subscriber connects
		_reporter.ReportStatusChange(HostExecutionStatus.Cancelling);
		_reporter.ReportOrchestrationCancelled();

		// Subscribe late
		var (replay, _) = _reporter.Subscribe();

		// Assert — late subscriber should see both events in replay
		replay.Should().HaveCount(2);
		replay[0].Type.Should().Be("status-changed");
		replay[0].Data.Should().Contain("Cancelling");
		replay[1].Type.Should().Be("orchestration-cancelled");
	}

	[Fact]
	public async Task FullLifecycle_StepEventsAndOrchestrationDone_AllReceivedBySubscriber()
	{
		// Arrange — subscribe before execution starts
		var (_, future) = _reporter.Subscribe();
		future.Should().NotBeNull();

		// Act — simulate a full execution lifecycle (what TriggerManager now does)
		_reporter.ReportStepStarted("analyze");
		_reporter.ReportStepCompleted("analyze", new AgentResult { Content = "result" }, OrchestrationStepType.Prompt);
		_reporter.ReportStepStarted("summarize");
		_reporter.ReportStepCompleted("summarize", new AgentResult { Content = "summary" }, OrchestrationStepType.Prompt);
		_reporter.ReportStepOutput("analyze", "result content");
		_reporter.ReportStepOutput("summarize", "summary content");

		var orchestrationResult = new OrchestrationResult
		{
			Status = ExecutionStatus.Succeeded,
			Results = new Dictionary<string, ExecutionResult>
			{
				["summarize"] = ExecutionResult.Succeeded("summary"),
			},
			StepResults = new Dictionary<string, ExecutionResult>
			{
				["analyze"] = ExecutionResult.Succeeded("result"),
				["summarize"] = ExecutionResult.Succeeded("summary"),
			}
		};
		_reporter.ReportOrchestrationDone(orchestrationResult);
		_reporter.Complete();

		// Assert — subscriber should receive all events in order
		var receivedEvents = new List<SseEvent>();
		await foreach (var evt in future!.ReadAllAsync())
		{
			receivedEvents.Add(evt);
		}

		receivedEvents.Should().HaveCount(7);
		receivedEvents[0].Type.Should().Be("step-started");
		receivedEvents[1].Type.Should().Be("step-completed");
		receivedEvents[2].Type.Should().Be("step-started");
		receivedEvents[3].Type.Should().Be("step-completed");
		receivedEvents[4].Type.Should().Be("step-output");
		receivedEvents[5].Type.Should().Be("step-output");
		receivedEvents[6].Type.Should().Be("orchestration-done");
		receivedEvents[6].Data.Should().Contain("Succeeded");
	}

	[Fact]
	public async Task FullLifecycle_LateAttach_ReceivesReplayIncludingTerminalEvents()
	{
		// Act — simulate a completed execution (no subscriber at start)
		_reporter.ReportStepStarted("step1");
		_reporter.ReportStepCompleted("step1", new AgentResult { Content = "output" }, OrchestrationStepType.Prompt);
		_reporter.ReportStepOutput("step1", "output");

		var orchestrationResult = new OrchestrationResult
		{
			Status = ExecutionStatus.Succeeded,
			Results = new Dictionary<string, ExecutionResult>
			{
				["step1"] = ExecutionResult.Succeeded("output"),
			},
			StepResults = new Dictionary<string, ExecutionResult>
			{
				["step1"] = ExecutionResult.Succeeded("output"),
			}
		};
		_reporter.ReportOrchestrationDone(orchestrationResult);
		_reporter.Complete();

		// Arrange — subscribe AFTER completion (late attach)
		var (replay, future) = _reporter.Subscribe();

		// Assert — replay should contain all events including terminal ones
		replay.Should().HaveCount(4);
		replay[0].Type.Should().Be("step-started");
		replay[1].Type.Should().Be("step-completed");
		replay[2].Type.Should().Be("step-output");
		replay[3].Type.Should().Be("orchestration-done");
		replay[3].Data.Should().Contain("Succeeded");

		// Future channel should be completed immediately
		future.Should().NotBeNull();
		future!.Completion.IsCompleted.Should().BeTrue();
	}
}

/// <summary>
/// Tests for DefaultExecutionCallback wiring SseReporter callbacks for progress tracking.
/// </summary>
public class DefaultExecutionCallbackTests
{
	[Fact]
	public void OnExecutionStarted_WiresSseReporterCallbacks_ForStepProgress()
	{
		// Arrange
		using var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "exec-1",
			OrchestrationId = "orch-1",
			OrchestrationName = "Test",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "scheduler",
			CancellationTokenSource = cts,
			Reporter = reporter,
			TotalSteps = 3
		};
		var callback = new DefaultExecutionCallback(new SseReporterFactory());

		// Act
		callback.OnExecutionStarted(info);

		// Simulate step events via the reporter
		reporter.ReportStepStarted("Step1");
		info.CurrentStep.Should().Be("Step1");
		info.CompletedSteps.Should().Be(0);

		reporter.ReportStepCompleted("Step1", new AgentResult { Content = "done" }, OrchestrationStepType.Prompt);
		info.CurrentStep.Should().BeNull();
		info.CompletedSteps.Should().Be(1);

		reporter.ReportStepStarted("Step2");
		info.CurrentStep.Should().Be("Step2");

		reporter.ReportStepCompleted("Step2", new AgentResult { Content = "done" }, OrchestrationStepType.Prompt);
		info.CompletedSteps.Should().Be(2);
		info.CurrentStep.Should().BeNull();

		reporter.Dispose();
	}

	[Fact]
	public void OnStepStarted_UpdatesCurrentStep()
	{
		// Arrange
		using var cts = new CancellationTokenSource();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "exec-2",
			OrchestrationId = "orch-2",
			OrchestrationName = "Test",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = new SseReporter()
		};
		var callback = new DefaultExecutionCallback(new SseReporterFactory());

		// Act
		callback.OnStepStarted(info, "MyStep");

		// Assert
		info.CurrentStep.Should().Be("MyStep");

		((SseReporter)info.Reporter).Dispose();
	}

	[Fact]
	public void OnStepCompleted_IncrementsCompletedStepsAndClearsCurrentStep()
	{
		// Arrange
		using var cts = new CancellationTokenSource();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "exec-3",
			OrchestrationId = "orch-3",
			OrchestrationName = "Test",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = new SseReporter()
		};
		var callback = new DefaultExecutionCallback(new SseReporterFactory());

		info.CurrentStep = "StepA";

		// Act
		callback.OnStepCompleted(info, "StepA");

		// Assert
		info.CompletedSteps.Should().Be(1);
		info.CurrentStep.Should().BeNull();

		((SseReporter)info.Reporter).Dispose();
	}

	[Fact]
	public void OnExecutionStarted_WithNonSseReporter_DoesNotThrow()
	{
		// Arrange - use a reporter that is NOT SseReporter
		using var cts = new CancellationTokenSource();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "exec-non-sse",
			OrchestrationId = "orch-non-sse",
			OrchestrationName = "Test",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = new NullReporter()
		};
		var callback = new DefaultExecutionCallback(new SseReporterFactory());

		// Act - should not throw, but callbacks won't be wired
		callback.OnExecutionStarted(info);

		// Assert - progress stays at defaults since callbacks were not wired
		info.CurrentStep.Should().BeNull();
		info.CompletedSteps.Should().Be(0);
	}

	[Fact]
	public void OnExecutionStarted_FullLifecycle_TracksAllStepsCorrectly()
	{
		// Arrange - simulate a 4-step orchestration
		using var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "exec-lifecycle",
			OrchestrationId = "orch-lifecycle",
			OrchestrationName = "Lifecycle Test",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "scheduler",
			CancellationTokenSource = cts,
			Reporter = reporter,
			TotalSteps = 4
		};
		var callback = new DefaultExecutionCallback(new SseReporterFactory());

		// Act
		callback.OnExecutionStarted(info);

		// Verify initial state
		info.CompletedSteps.Should().Be(0);
		info.CurrentStep.Should().BeNull();

		// Step 1
		reporter.ReportStepStarted("Gather");
		info.CurrentStep.Should().Be("Gather");
		info.CompletedSteps.Should().Be(0);

		reporter.ReportStepCompleted("Gather", new AgentResult { Content = "gathered" }, OrchestrationStepType.Prompt);
		info.CurrentStep.Should().BeNull();
		info.CompletedSteps.Should().Be(1);

		// Step 2
		reporter.ReportStepStarted("Analyze");
		info.CurrentStep.Should().Be("Analyze");
		info.CompletedSteps.Should().Be(1);

		reporter.ReportStepCompleted("Analyze", new AgentResult { Content = "analyzed" }, OrchestrationStepType.Prompt);
		info.CurrentStep.Should().BeNull();
		info.CompletedSteps.Should().Be(2);

		// Step 3
		reporter.ReportStepStarted("Transform");
		info.CurrentStep.Should().Be("Transform");

		reporter.ReportStepCompleted("Transform", new AgentResult { Content = "transformed" }, OrchestrationStepType.Transform);
		info.CompletedSteps.Should().Be(3);

		// Step 4
		reporter.ReportStepStarted("Report");
		info.CurrentStep.Should().Be("Report");

		reporter.ReportStepCompleted("Report", new AgentResult { Content = "reported" }, OrchestrationStepType.Prompt);
		info.CompletedSteps.Should().Be(4);
		info.CurrentStep.Should().BeNull();

		// Final state
		info.CompletedSteps.Should().Be(info.TotalSteps);

		reporter.Dispose();
	}

	[Fact]
	public void OnExecutionStarted_CallbackWiringIsIdempotent_LastWinsForSameReporter()
	{
		// Arrange - calling OnExecutionStarted twice should overwrite callbacks
		using var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var info1 = new ActiveExecutionInfo
		{
			ExecutionId = "exec-first",
			OrchestrationId = "orch-first",
			OrchestrationName = "First",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "scheduler",
			CancellationTokenSource = cts,
			Reporter = reporter,
			TotalSteps = 2
		};
		var info2 = new ActiveExecutionInfo
		{
			ExecutionId = "exec-second",
			OrchestrationId = "orch-second",
			OrchestrationName = "Second",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "scheduler",
			CancellationTokenSource = cts,
			Reporter = reporter,
			TotalSteps = 2
		};
		var callback = new DefaultExecutionCallback(new SseReporterFactory());

		// Act - wire to info1, then rewire to info2
		callback.OnExecutionStarted(info1);
		callback.OnExecutionStarted(info2);

		// Now step events should only update info2
		reporter.ReportStepStarted("StepA");
		info1.CurrentStep.Should().BeNull("info1 callbacks were overwritten");
		info2.CurrentStep.Should().Be("StepA");

		reporter.ReportStepCompleted("StepA", new AgentResult { Content = "done" }, OrchestrationStepType.Prompt);
		info1.CompletedSteps.Should().Be(0, "info1 callbacks were overwritten");
		info2.CompletedSteps.Should().Be(1);

		reporter.Dispose();
	}

	/// <summary>Minimal reporter that does nothing -- used to test non-SseReporter path.</summary>
	private class NullReporter : IOrchestrationReporter
	{
		public void ReportSessionStarted(string requestedModel, string? selectedModel) { }
		public void ReportModelChange(string? previousModel, string newModel) { }
		public void ReportUsage(string stepName, string model, AgentUsage usage) { }
		public void ReportContentDelta(string stepName, string chunk) { }
		public void ReportReasoningDelta(string stepName, string chunk) { }
		public void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer) { }
		public void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error) { }
		public void ReportStepError(string stepName, string errorMessage) { }
		public void ReportStepCancelled(string stepName) { }
		public void ReportStepCompleted(string stepName, AgentResult result, OrchestrationStepType stepType) { }
		public void ReportStepTrace(string stepName, StepExecutionTrace trace) { }
		public void ReportModelMismatch(ModelMismatchInfo mismatch) { }
		public void ReportStepOutput(string stepName, string content) { }
		public void ReportStepStarted(string stepName) { }
		public void ReportStepSkipped(string stepName, string reason) { }
		public void ReportStepRetry(string stepName, int attempt, int maxRetries, string error, TimeSpan delay) { }
		public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations) { }
		public void ReportCheckpointSaved(string runId, string stepName, int completedSteps, int totalSteps) { }
		public void ReportSessionWarning(string warningType, string message) { }
		public void ReportSessionInfo(string infoType, string message) { }
		public void ReportMcpServersLoaded(IReadOnlyList<McpServerStatusInfo> servers) { }
		public void ReportMcpServerStatusChanged(string serverName, string status) { }
		public void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools) { }
		public void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description) { }
		public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName) { }
		public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error) { }
		public void ReportSubagentDeselected(string stepName) { }
		public void ReportRunContext(RunContext context) { }
	}
}
