using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for the authoritative snapshot, sequence numbers, and Last-Event-Id resume
/// cursor introduced to fix the "DAG nodes not colored / not reactive on large running
/// orchestrations" bug. The core invariant: snapshots survive circular-buffer eviction,
/// so attaches mid-run always see correct per-step state regardless of how many delta
/// events have rolled off.
/// </summary>
public class SseReporterSnapshotTests : IDisposable
{
	private readonly SseReporter _reporter;

	public SseReporterSnapshotTests()
	{
		_reporter = new SseReporter();
	}

	public void Dispose() => _reporter.Dispose();

	[Fact]
	public void GetCurrentSnapshot_OnFreshReporter_ReturnsEmptyState()
	{
		var snap = _reporter.GetCurrentSnapshot();

		snap.Should().NotBeNull();
		snap.Steps.Should().BeEmpty();
		snap.LastEventSequence.Should().Be(0);
		snap.IsCompleted.Should().BeFalse();
	}

	[Fact]
	public void Snapshot_ReflectsStepStartedAsRunning()
	{
		_reporter.ReportStepStarted("fetch");

		var step = _reporter.GetCurrentSnapshot().Steps["fetch"];
		step.Status.Should().Be("running");
		step.StartedAt.Should().NotBeNull();
		step.CompletedAt.Should().BeNull();
	}

	[Fact]
	public void Snapshot_ReflectsStepCompletedWithModelAndPreview()
	{
		_reporter.ReportStepStarted("fetch");
		_reporter.ReportStepCompleted(
			"fetch",
			new AgentResult { Content = "hello world", ActualModel = "claude-opus-4.6", SelectedModel = "claude-opus-4.6" },
			OrchestrationStepType.Prompt);

		var step = _reporter.GetCurrentSnapshot().Steps["fetch"];
		step.Status.Should().Be("completed");
		step.CompletedAt.Should().NotBeNull();
		step.ContentPreview.Should().Be("hello world");
		step.ActualModel.Should().Be("claude-opus-4.6");
		step.SelectedModel.Should().Be("claude-opus-4.6");
	}

	[Fact]
	public void Snapshot_ReflectsStepError()
	{
		_reporter.ReportStepError("fetch", "boom");

		var step = _reporter.GetCurrentSnapshot().Steps["fetch"];
		step.Status.Should().Be("failed");
		step.Error.Should().Be("boom");
		step.CompletedAt.Should().NotBeNull();
	}

	[Fact]
	public void Snapshot_ReflectsStepCancelled()
	{
		_reporter.ReportStepCancelled("fetch");

		_reporter.GetCurrentSnapshot().Steps["fetch"].Status.Should().Be("cancelled");
	}

	[Fact]
	public void Snapshot_ReflectsStepSkipped()
	{
		_reporter.ReportStepSkipped("fetch", "no data");

		_reporter.GetCurrentSnapshot().Steps["fetch"].Status.Should().Be("skipped");
	}

	[Fact]
	public void Snapshot_ReflectsStepStatusSet_LowerCases()
	{
		_reporter.ReportStepStatusSet("fetch", "Completed", "set by tool");

		_reporter.GetCurrentSnapshot().Steps["fetch"].Status.Should().Be("completed");
	}

	[Fact]
	public void Snapshot_TracksOutputUpToCap()
	{
		var long_ = new string('x', SseReporter.MaxSnapshotStepOutputLength + 100);
		_reporter.ReportStepOutput("fetch", long_);

		var step = _reporter.GetCurrentSnapshot().Steps["fetch"];
		step.Output.Should().NotBeNull();
		step.Output!.Length.Should().Be(SseReporter.MaxSnapshotStepOutputLength);
	}

	[Fact]
	public void Snapshot_TracksTracePayload()
	{
		_reporter.ReportStepTrace("fetch", new StepExecutionTrace
		{
			SystemPrompt = "sys",
			UserPromptRaw = "user-raw",
			UserPromptProcessed = "user-processed",
		});

		var step = _reporter.GetCurrentSnapshot().Steps["fetch"];
		step.Trace.Should().NotBeNull();
		step.Trace!.Value.GetProperty("systemPrompt").GetString().Should().Be("sys");
		step.Trace!.Value.GetProperty("userPromptRaw").GetString().Should().Be("user-raw");
	}

	[Fact]
	public void Snapshot_AccumulatesSavedFiles()
	{
		_reporter.ReportSavedFile("fetch", "C:/tmp/a.txt");
		_reporter.ReportSavedFile("fetch", "C:/tmp/b.txt");
		_reporter.ReportSavedFile("fetch", "C:/tmp/a.txt"); // duplicate

		var step = _reporter.GetCurrentSnapshot().Steps["fetch"];
		step.SavedFiles.Should().BeEquivalentTo(new[] { "C:/tmp/a.txt", "C:/tmp/b.txt" });
	}

	[Fact]
	public void Snapshot_TracksActiveSubagents()
	{
		_reporter.ReportSubagentStarted("fetch", "tc-1", "researcher", "Researcher", "do work");
		_reporter.ReportSubagentStarted("fetch", "tc-2", "writer", "Writer", "write");

		_reporter.GetCurrentSnapshot().Steps["fetch"].ActiveSubagents.Should().Be(2);

		_reporter.ReportSubagentCompleted("fetch", "tc-1", "researcher", "Researcher");

		_reporter.GetCurrentSnapshot().Steps["fetch"].ActiveSubagents.Should().Be(1);

		_reporter.ReportSubagentFailed("fetch", "tc-2", "writer", "Writer", "boom");

		_reporter.GetCurrentSnapshot().Steps["fetch"].ActiveSubagents.Should().Be(0);
	}

	[Fact]
	public void Snapshot_TracksRetryCount()
	{
		_reporter.ReportStepRetry("fetch", 1, 3, "transient", TimeSpan.FromSeconds(1));
		_reporter.ReportStepRetry("fetch", 2, 3, "transient", TimeSpan.FromSeconds(1));

		_reporter.GetCurrentSnapshot().Steps["fetch"].RetryCount.Should().Be(2);
	}

	[Fact]
	public void Snapshot_TracksAuditEntriesUpToCap()
	{
		for (var i = 0; i < SseReporter.MaxSnapshotAuditEntriesPerStep + 50; i++)
		{
			_reporter.ReportAuditLogEntry("fetch", new AuditLogEntry
			{
				Sequence = i,
				Timestamp = DateTimeOffset.UtcNow,
				EventType = AuditEventType.PreToolUse,
				ToolName = $"tool-{i}",
			});
		}

		var entries = _reporter.GetCurrentSnapshot().Steps["fetch"].AuditEntries;
		entries.Should().HaveCount(SseReporter.MaxSnapshotAuditEntriesPerStep);
		// The oldest entries should have been dropped; the newest tool name survives.
		var last = entries[^1].GetProperty("toolName").GetString();
		last.Should().Be($"tool-{SseReporter.MaxSnapshotAuditEntriesPerStep + 49}");
	}

	[Fact]
	public void Snapshot_PreservesStepStateAcrossCircularBufferOverflow()
	{
		// Pin one step as completed up front.
		_reporter.ReportStepStarted("anchor");
		_reporter.ReportStepCompleted("anchor", new AgentResult { Content = "done" }, OrchestrationStepType.Prompt);

		// Now flood the circular buffer with reasoning deltas — these will evict the
		// anchor's step-completed event from the replay buffer, but they MUST NOT
		// disturb the per-step snapshot state.
		for (var i = 0; i < SseReporter.MaxAccumulatedEvents + 500; i++)
		{
			_reporter.ReportReasoningDelta($"chatty-{i % 5}", new string('a', 32));
		}

		var snap = _reporter.GetCurrentSnapshot();
		snap.Steps["anchor"].Status.Should().Be("completed",
			"the per-step snapshot must survive replay-buffer eviction so DAG colors stay correct on attach");
		snap.Steps["anchor"].ContentPreview.Should().Be("done");
		snap.Steps["anchor"].CompletedAt.Should().NotBeNull();

		// Sanity: the replay buffer DID overflow.
		_reporter.AccumulatedEventCount.Should().Be(SseReporter.MaxAccumulatedEvents);
		// And the oldest "anchor" events are gone from the replay.
		_reporter.AccumulatedEvents.Should().NotContain(e => e.Type == "step-started" && e.Data.Contains("anchor"));
	}

	[Fact]
	public void Sequence_MonotonicallyIncreases()
	{
		_reporter.ReportStepStarted("a");
		_reporter.ReportStepStarted("b");
		_reporter.ReportStepStarted("c");

		var events = _reporter.AccumulatedEvents;
		events.Should().HaveCount(3);
		events[0].Sequence.Should().Be(1);
		events[1].Sequence.Should().Be(2);
		events[2].Sequence.Should().Be(3);
		_reporter.LastEventSequence.Should().Be(3);
	}

	[Fact]
	public void Heartbeat_DoesNotConsumeSequenceNumber()
	{
		_reporter.ReportStepStarted("a");
		_reporter.SendHeartbeat();
		_reporter.SendHeartbeat();
		_reporter.ReportStepCompleted("a", new AgentResult { Content = "ok" }, OrchestrationStepType.Prompt);

		var events = _reporter.AccumulatedEvents;
		events.Should().HaveCount(2);
		events[0].Sequence.Should().Be(1);
		events[1].Sequence.Should().Be(2);
		_reporter.LastEventSequence.Should().Be(2);
	}

	[Fact]
	public void Subscribe_NoLastEventId_ReplaysEverything()
	{
		_reporter.ReportStepStarted("a");
		_reporter.ReportStepStarted("b");

		var result = _reporter.SubscribeWithSnapshot();

		result.Replay.Should().HaveCount(2);
		result.ReplayTruncated.Should().BeFalse();
		result.Future.Should().NotBeNull();
	}

	[Fact]
	public void Subscribe_WithLastEventId_OnlyReturnsNewerEvents()
	{
		_reporter.ReportStepStarted("a"); // seq 1
		_reporter.ReportStepStarted("b"); // seq 2
		_reporter.ReportStepStarted("c"); // seq 3

		// Client claims to have seen up through seq 1.
		var result = _reporter.SubscribeWithSnapshot(lastEventId: 1);

		result.Replay.Should().HaveCount(2);
		result.Replay[0].Sequence.Should().Be(2);
		result.Replay[1].Sequence.Should().Be(3);
		result.ReplayTruncated.Should().BeFalse();
	}

	[Fact]
	public void Subscribe_WithStaleLastEventId_FlagsReplayTruncated()
	{
		// Force the buffer to wrap so the earliest events are evicted.
		var total = SseReporter.MaxAccumulatedEvents + 100;
		for (var i = 0; i < total; i++)
		{
			_reporter.ReportStepOutput("step", $"chunk-{i}");
		}

		// Client claims to have last seen event 5, which is long gone.
		var result = _reporter.SubscribeWithSnapshot(lastEventId: 5);

		result.ReplayTruncated.Should().BeTrue(
			"the requested resume cursor is older than the oldest event still in the buffer");
		// The snapshot still gives us authoritative state.
		result.Snapshot.Steps["step"].Output.Should().NotBeNull();
	}

	[Fact]
	public void Subscribe_AtMaxSubscribers_StillReturnsSnapshotButNoFuture()
	{
		for (var i = 0; i < SseReporter.MaxSubscribers; i++)
		{
			_reporter.SubscribeWithSnapshot();
		}

		_reporter.ReportStepStarted("a");

		var result = _reporter.SubscribeWithSnapshot();
		result.Future.Should().BeNull("subscriber limit reached");
		result.Replay.Should().HaveCount(1, "replay still served");
		result.Snapshot.Steps["a"].Status.Should().Be("running",
			"snapshot still served even when future stream is rejected");
	}

	[Fact]
	public void SetExecutionContext_FoldsIntoSnapshot()
	{
		_reporter.SetExecutionContext(
			"exec-123",
			"orch-abc",
			"My Orchestration",
			DateTimeOffset.UtcNow,
			"manual",
			new Dictionary<string, string> { ["key"] = "value" });

		var snap = _reporter.GetCurrentSnapshot();
		snap.ExecutionId.Should().Be("exec-123");
		snap.OrchestrationId.Should().Be("orch-abc");
		snap.OrchestrationName.Should().Be("My Orchestration");
		snap.TriggeredBy.Should().Be("manual");
		snap.Parameters.Should().ContainKey("key").WhoseValue.Should().Be("value");
		snap.Status.Should().Be("Running");
	}

	[Fact]
	public void OrchestrationDone_ReconcilesPerStepStatusInSnapshot()
	{
		// Some steps that don't have individual events at all — the run jumped straight
		// to a terminal frame (e.g. import path that emits orchestration-done with results).
		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["a"] = ExecutionResult.Succeeded("ok-a"),
			["b"] = ExecutionResult.Failed("boom"),
			["c"] = ExecutionResult.Skipped("nothing"),
		};
		_reporter.ReportOrchestrationDone(new OrchestrationResult
		{
			Status = ExecutionStatus.Succeeded,
			Results = stepResults,
			StepResults = stepResults,
		});

		var snap = _reporter.GetCurrentSnapshot();
		snap.Steps["a"].Status.Should().Be("completed");
		snap.Steps["b"].Status.Should().Be("failed");
		snap.Steps["b"].Error.Should().Be("boom");
		snap.Steps["c"].Status.Should().Be("skipped");
	}

	[Fact]
	public void SseOptions_CustomCaps_AreHonored()
	{
		using var reporter = new SseReporter(
			dashboardBroadcaster: null,
			options: new SseOptions
			{
				MaxAccumulatedEvents = 100,
				MaxChannelCapacity = 50,
				MaxSubscribers = 3,
			},
			logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<SseReporter>.Instance);

		// Subscriber cap of 3
		reporter.SubscribeWithSnapshot();
		reporter.SubscribeWithSnapshot();
		reporter.SubscribeWithSnapshot();
		var overflow = reporter.SubscribeWithSnapshot();
		overflow.Future.Should().BeNull();

		// Buffer cap of 100
		for (var i = 0; i < 250; i++)
		{
			reporter.ReportStepOutput("s", $"chunk-{i}");
		}
		reporter.AccumulatedEventCount.Should().Be(100);
	}

	[Fact]
	public void RunContext_FoldsIntoSnapshot()
	{
		_reporter.ReportRunContext(new RunContext
		{
			RunId = "run-1",
			OrchestrationName = "Demo",
			OrchestrationVersion = "1.0.0",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			Parameters = new Dictionary<string, string> { ["foo"] = "bar" },
			Variables = new Dictionary<string, string>(),
			ResolvedVariables = new Dictionary<string, string>(),
			AccessedEnvironmentVariables = new Dictionary<string, string?>(),
		});

		var snap = _reporter.GetCurrentSnapshot();
		snap.RunContext.Should().NotBeNull();
		snap.RunContext!.Value.GetProperty("runId").GetString().Should().Be("run-1");
		snap.RunContext!.Value.GetProperty("parameters").GetProperty("foo").GetString().Should().Be("bar");
	}

	[Fact]
	public void Snapshot_SerializesToJsonCleanly()
	{
		_reporter.SetExecutionContext("e", "o", "name", DateTimeOffset.UtcNow, "manual", null);
		_reporter.ReportStepStarted("step-1");
		_reporter.ReportStepCompleted("step-1", new AgentResult { Content = "out" }, OrchestrationStepType.Prompt);

		var snap = _reporter.GetCurrentSnapshot();
		var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

		json.Should().Contain("\"executionId\":\"e\"");
		json.Should().Contain("\"stepName\":\"step-1\"");
		json.Should().Contain("\"status\":\"completed\"");
		json.Should().Contain("\"lastEventSequence\":2");
	}
}
