using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
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
		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "output" });

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
		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "output" });
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
		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "output" });

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

		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "output" });

		calledWith.Should().Be("step-1");
	}

	[Fact]
	public void ReportStatusChange_AddsEvent()
	{
		_reporter.ReportStatusChange("Running");

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
}
