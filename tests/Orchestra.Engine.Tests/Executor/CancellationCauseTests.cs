using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Verifies that the executor records a structured <see cref="CancellationDetails"/> on
/// <see cref="OrchestrationResult"/> (and propagates it to step <c>ErrorMessage</c>s) so consumers
/// can distinguish external cancel, the orchestration's own <c>timeoutSeconds</c>, a sync-invoke
/// wrapper timeout, and <c>orchestra_complete</c> without inspecting timestamps.
/// </summary>
public class CancellationCauseTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	private static AgentBuilder BuildHangingAgent()
	{
		// Returns an agent that blocks until its cancellation token fires, simulating
		// a long-running prompt step that we can interrupt by cancelling externally.
		var agent = new MockAgentBuilder();
		agent.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(Timeout.Infinite, ct);
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});
		return agent;
	}

	[Fact]
	public async Task ExecuteAsync_NormalSuccess_LeavesCancellationNull()
	{
		// Arrange — a clean run should never carry cancellation metadata.
		var agentBuilder = new MockAgentBuilder().WithResponse("ok");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Cancellation.Should().BeNull();
	}

	[Fact]
	public async Task ExecuteAsync_ExternalCancellation_RecordsExternalCause()
	{
		// Arrange — caller cancels their own token while a step is running. The engine should
		// surface CancellationCauseKind.External (not Unknown, not OrchestrationTimeout).
		var stepStarted = new TaskCompletionSource();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			stepStarted.TrySetResult();
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(Timeout.Infinite, ct);
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();
		using var cts = new CancellationTokenSource();

		// Act
		var executeTask = executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);
		await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cts.Cancel();
		var result = await executeTask;

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.Cancellation.Should().NotBeNull();
		result.Cancellation!.Kind.Should().Be(CancellationCauseKind.External);
		result.Cancellation.IsTimeout.Should().BeFalse();
		result.Cancellation.TimeoutSeconds.Should().BeNull();

		// The cancelled step's ErrorMessage should be enriched from bare "Cancelled".
		result.StepResults["step1"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["step1"].ErrorMessage.Should().Contain("cancelled by caller");
	}

	[Fact]
	public async Task ExecuteAsync_CancelledMidRun_RecordsAccurateProgressSummary()
	{
		// Build a linear chain A -> B -> C where:
		//   A completes quickly,
		//   B hangs until external cancellation,
		//   C never starts.
		// The recorded CancellationDetails.Progress must report 1 completed (A), 1 cancelled (B),
		// 1 not-started (C), and identify A as the most-recently-completed step.
		var bStarted = new TaskCompletionSource();
		var invocations = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var index = Interlocked.Increment(ref invocations);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				if (index == 2)
				{
					// Second invocation == step B (steps run in declaration order for a linear chain).
					bStarted.TrySetResult();
					await Task.Delay(Timeout.Infinite, ct);
					return new AgentResult { Content = "unreachable" };
				}
				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.MessageDelta,
					Content = "ok",
				}, ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "ok" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain("progress-cancel");
		using var cts = new CancellationTokenSource();

		// Act
		var executeTask = executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);
		await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
		cts.Cancel();
		var result = await executeTask;

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.Cancellation.Should().NotBeNull();
		result.Cancellation!.Progress.Should().NotBeNull("progress summary must be populated on every cancellation");

		var p = result.Cancellation.Progress!;
		p.TotalSteps.Should().Be(3);
		p.StepsCompleted.Should().Be(1, "step A completed before cancellation");
		// B was hanging when the cancel hit; C cascaded as Cancelled because B was its dependency.
		p.StepsCancelled.Should().Be(2, "both B (hanging) and C (cascaded) end in Cancelled");
		p.StepsFailed.Should().Be(0);
		p.LastCompletedStep.Should().Be("A");
		p.LastCompletedAt.Should().NotBeNull();
		p.CancelledSteps.Should().BeEquivalentTo(["B", "C"],
			"CancelledSteps lists every step that ended in Cancelled, in declaration order");
	}

	[Fact]
	public async Task ExecuteAsync_OrchestrationTimeoutFires_RecordsOrchestrationTimeoutCause()
	{
		// Arrange — orchestration's own timeoutSeconds elapses while the step is still running.
		// The engine catches the cancellation gracefully and returns a Cancelled result rather
		// than throwing — so we verify the result, not an exception.
		var agentBuilder = BuildHangingAgent();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "orch-timeout",
			Description = "Hits the orchestration-level timeout.",
			TimeoutSeconds = 1,
			Steps =
			[
				TestOrchestrations.CreatePromptStep("slow"),
			],
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.Cancellation.Should().NotBeNull();
		result.Cancellation!.Kind.Should().Be(CancellationCauseKind.OrchestrationTimeout);
		result.Cancellation.TimeoutSeconds.Should().Be(1);
		result.Cancellation.IsTimeout.Should().BeTrue();
		result.StepResults["slow"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["slow"].ErrorMessage.Should().Contain("orchestration timed out after 1s");
	}

	[Fact]
	public async Task ExecuteAsync_OrchestrationTimeoutFires_RunRecordHasOrchestrationTimeoutCause()
	{
		// Arrange — same scenario as above, but capture the persisted run record so we can
		// inspect Cancellation directly. The engine saves a Cancelled record and returns
		// normally; no exception propagates.
		var capturedRecords = new List<OrchestrationRunRecord>();
		var runStore = new CapturingRunStore(capturedRecords);

		var agentBuilder = BuildHangingAgent();
		var executor = new OrchestrationExecutor(
			_scheduler,
			agentBuilder,
			_reporter,
			_loggerFactory,
			runStore: runStore);

		var orchestration = new Orchestration
		{
			Name = "orch-timeout-record",
			Description = "Hits the orchestration-level timeout and saves a run record.",
			TimeoutSeconds = 1,
			Steps =
			[
				TestOrchestrations.CreatePromptStep("slow"),
			],
		};

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert
		capturedRecords.Should().HaveCount(1);
		var record = capturedRecords[0];
		record.Status.Should().Be(ExecutionStatus.Cancelled);
		record.Cancellation.Should().NotBeNull();
		record.Cancellation!.Kind.Should().Be(CancellationCauseKind.OrchestrationTimeout);
		record.Cancellation.TimeoutSeconds.Should().Be(1);
		record.Cancellation.IsTimeout.Should().BeTrue();
		record.Cancellation.Reason.Should().Contain("1s");

		// FinalContent should mention the cancellation cause directly.
		record.FinalContent.Should().Contain("orchestration timed out after 1s");

		// Step ErrorMessage should be enriched.
		record.StepRecords["slow"].ErrorMessage.Should().Contain("orchestration timed out after 1s");
	}

	[Fact]
	public async Task ExecuteAsync_SyncInvokeTimeoutProbeReturnsCause_RecordsSyncInvokeTimeout()
	{
		// Arrange — simulate the launcher's sync-invoke wrapper: an outside-owned linked
		// CancellationToken fires, and the probe identifies it as SyncInvokeTimeout. The engine
		// should record that precise cause instead of falling back to External.
		var stepStarted = new TaskCompletionSource();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			stepStarted.TrySetResult();
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(Timeout.Infinite, ct);
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		// Outer caller token (would be the launcher's parent CTS) — never cancelled.
		using var parentCts = new CancellationTokenSource();
		// Inner sync-invoke timeout token — fires after the step starts.
		using var syncInvokeCts = CancellationTokenSource.CreateLinkedTokenSource(parentCts.Token);

		// Probe mirrors what ChildOrchestrationLauncher installs.
		const int configuredTimeout = 1800;
		ResolveCancellationCauseDelegate probe = () =>
			syncInvokeCts.IsCancellationRequested && !parentCts.IsCancellationRequested
				? CancellationDetails.SyncInvokeTimeout(configuredTimeout)
				: null;

		// Act
		var executeTask = executor.ExecuteAsync(
			orchestration,
			resolveExternalCancellationCause: probe,
			cancellationToken: syncInvokeCts.Token);
		await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		syncInvokeCts.Cancel();
		var result = await executeTask;

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.Cancellation.Should().NotBeNull();
		result.Cancellation!.Kind.Should().Be(CancellationCauseKind.SyncInvokeTimeout);
		result.Cancellation.TimeoutSeconds.Should().Be(configuredTimeout);
		result.Cancellation.IsTimeout.Should().BeTrue();
		result.Cancellation.Source.Should().Be("sync-invoke");

		result.StepResults["step1"].ErrorMessage.Should().Contain("sync invocation timed out after 1800s");
	}

	[Fact]
	public async Task ExecuteAsync_ExternalCancellationWithProbeReturningNull_FallsBackToExternal()
	{
		// Arrange — the probe is installed but returns null because the cancellation was
		// driven by the outer parent token, not the sync-invoke wrapper. The engine should
		// fall back to External.
		var stepStarted = new TaskCompletionSource();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			stepStarted.TrySetResult();
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(Timeout.Infinite, ct);
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		using var parentCts = new CancellationTokenSource();
		using var syncInvokeCts = CancellationTokenSource.CreateLinkedTokenSource(parentCts.Token);

		ResolveCancellationCauseDelegate probe = () =>
			syncInvokeCts.IsCancellationRequested && !parentCts.IsCancellationRequested
				? CancellationDetails.SyncInvokeTimeout(1800)
				: null; // ← parent cancelled; not our timeout

		// Act
		var executeTask = executor.ExecuteAsync(
			orchestration,
			resolveExternalCancellationCause: probe,
			cancellationToken: syncInvokeCts.Token);
		await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		parentCts.Cancel(); // parent (caller) cancels — not the sync timeout
		var result = await executeTask;

		// Assert — probe returns null, so engine falls back to External.
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.Cancellation.Should().NotBeNull();
		result.Cancellation!.Kind.Should().Be(CancellationCauseKind.External);
	}

	[Fact]
	public async Task ExecuteAsync_OrchestrationTimeoutTakesPriorityOverExternalProbe()
	{
		// Arrange — both signals could fire. The engine owns the orchestration-level timeout
		// CTS; that should win regardless of what an external probe says. (The orchestration's
		// own timeout cancels INSIDE the engine, so externalCancellationToken stays uncancelled.)
		var capturedRecords = new List<OrchestrationRunRecord>();
		var runStore = new CapturingRunStore(capturedRecords);
		var agentBuilder = BuildHangingAgent();
		var executor = new OrchestrationExecutor(
			_scheduler,
			agentBuilder,
			_reporter,
			_loggerFactory,
			runStore: runStore);

		// A probe that *would* report SyncInvokeTimeout if asked. The engine should not ask it
		// because it can identify the cause itself via orchestrationTimeoutCts.
		var probeInvoked = false;
		ResolveCancellationCauseDelegate probe = () =>
		{
			probeInvoked = true;
			return CancellationDetails.SyncInvokeTimeout(9999);
		};

		var orchestration = new Orchestration
		{
			Name = "orch-timeout-priority",
			Description = "Engine-owned timeout takes priority over wrapper probe.",
			TimeoutSeconds = 1,
			Steps = [TestOrchestrations.CreatePromptStep("slow")],
		};

		// Act
		await executor.ExecuteAsync(orchestration, resolveExternalCancellationCause: probe);

		// Assert — engine should report OrchestrationTimeout (its own), not SyncInvokeTimeout.
		capturedRecords.Should().HaveCount(1);
		capturedRecords[0].Cancellation!.Kind.Should().Be(CancellationCauseKind.OrchestrationTimeout);
		capturedRecords[0].Cancellation!.TimeoutSeconds.Should().Be(1);
		probeInvoked.Should().BeFalse("the engine identified the cause itself; the wrapper probe must not be consulted");
	}

	[Fact]
	public async Task CancellationDetails_OrchestrationTimeout_ReasonIncludesSeconds()
	{
		// Arrange / Act
		var details = CancellationDetails.OrchestrationTimeout(600);

		// Assert
		details.Kind.Should().Be(CancellationCauseKind.OrchestrationTimeout);
		details.TimeoutSeconds.Should().Be(600);
		details.IsTimeout.Should().BeTrue();
		details.Reason.Should().Be("orchestration timed out after 600s");
		await Task.CompletedTask;
	}

	[Fact]
	public async Task CancellationDetails_SyncInvokeTimeout_ReasonIncludesSeconds()
	{
		var details = CancellationDetails.SyncInvokeTimeout(1800);
		details.Kind.Should().Be(CancellationCauseKind.SyncInvokeTimeout);
		details.TimeoutSeconds.Should().Be(1800);
		details.IsTimeout.Should().BeTrue();
		details.Reason.Should().Be("sync invocation timed out after 1800s");
		details.Source.Should().Be("sync-invoke");
		await Task.CompletedTask;
	}

	[Fact]
	public async Task CancellationDetails_External_ReasonIsCallerCancel()
	{
		var details = CancellationDetails.External();
		details.Kind.Should().Be(CancellationCauseKind.External);
		details.IsTimeout.Should().BeFalse();
		details.Reason.Should().Be("cancelled by caller");

		var withDetail = CancellationDetails.External("user-pressed-stop");
		withDetail.Reason.Should().Contain("user-pressed-stop");
		await Task.CompletedTask;
	}

	[Fact]
	public void CancellationDetails_HostShutdown_ReasonIdentifiesInterruption()
	{
		var details = CancellationDetails.HostShutdown("process stopping");

		details.Kind.Should().Be(CancellationCauseKind.HostShutdown);
		details.Source.Should().Be("host-shutdown");
		details.IsTimeout.Should().BeFalse();
		details.Reason.Should().Be("interrupted by host shutdown: process stopping");
	}

	[Fact]
	public async Task CancellationDetails_OrchestrationComplete_CarriesCallerReason()
	{
		var details = CancellationDetails.OrchestrationComplete("nothing-to-do", "validate-step");
		details.Kind.Should().Be(CancellationCauseKind.OrchestrationComplete);
		details.IsTimeout.Should().BeFalse();
		details.Reason.Should().Contain("nothing-to-do");
		details.Source.Should().Contain("validate-step");
		await Task.CompletedTask;
	}

	[Fact]
	public void CancellationDetails_McpRequestAborted_ReportsTransportTimeoutAndIsTimeout()
	{
		// The transport layer aborting the MCP request that owns this run is effectively a
		// transport timeout — IsTimeout must be true so dashboards and self-healing logic can
		// classify it as "ran out of time" rather than "user cancelled".
		var details = CancellationDetails.McpRequestAborted(
			transportTimeoutSeconds: 1800,
			source: "mcp-transport",
			detail: "upstream MCP client closed the request (parent: abc123, step: my-step)");

		details.Kind.Should().Be(CancellationCauseKind.McpRequestAborted);
		details.Source.Should().Be("mcp-transport");
		details.IsTimeout.Should().BeTrue();
		details.TimeoutSeconds.Should().Be(1800);
		details.Reason.Should().Contain("1800s");
		details.Reason.Should().Contain("parent: abc123");
	}

	[Fact]
	public void CancellationDetails_McpRequestAborted_WithoutTimeoutSeconds_StillReportsAbort()
	{
		// Common case: the launcher does not know the transport timeout value (it's caller-side).
		// The reason text must still be informative.
		var details = CancellationDetails.McpRequestAborted(
			transportTimeoutSeconds: null,
			source: null,
			detail: "upstream MCP client closed the request");

		details.Kind.Should().Be(CancellationCauseKind.McpRequestAborted);
		details.Source.Should().Be("mcp-transport"); // default when caller passes null
		details.IsTimeout.Should().BeTrue();
		details.TimeoutSeconds.Should().BeNull();
		details.Reason.Should().Be("MCP transport request aborted: upstream MCP client closed the request");
	}

	[Fact]
	public void CancellationDetails_ConfigReload_ReportsDefinitionReload()
	{
		var details = CancellationDetails.ConfigReload(detail: "P:/orch/my.yaml");

		details.Kind.Should().Be(CancellationCauseKind.ConfigReload);
		details.Source.Should().Be("config-reload");
		details.IsTimeout.Should().BeFalse();
		details.Reason.Should().Be("orchestration definition reloaded: P:/orch/my.yaml");
	}

	[Fact]
	public void CancellationDetails_RequestedAt_RoundTripsOnCallerInit()
	{
		// RequestedAt is set by API/MCP handlers before .Cancel(). Verify it survives.
		var stamp = DateTimeOffset.UtcNow;
		var details = new CancellationDetails
		{
			Kind = CancellationCauseKind.External,
			Source = "caller",
			Detail = "REST",
			RequestedAt = stamp,
		};

		details.RequestedAt.Should().Be(stamp);
		// And it must round-trip through the Reason getter without throwing.
		_ = details.Reason;
	}

	[Fact]
	public void CancellationProgressSummary_DefaultsAndRequiredFields_AreEnforced()
	{
		// The summary is `required` for the counters but list/optional fields have defaults
		// so dashboards don't need to defend against null.
		var summary = new CancellationProgressSummary
		{
			TotalSteps = 5,
			StepsCompleted = 3,
			StepsCancelled = 1,
			StepsFailed = 0,
			StepsSkippedOrNoAction = 0,
			StepsNotStarted = 1,
		};

		summary.TotalSteps.Should().Be(5);
		summary.StepsCompleted.Should().Be(3);
		summary.StepsCancelled.Should().Be(1);
		summary.StepsFailed.Should().Be(0);
		summary.StepsSkippedOrNoAction.Should().Be(0);
		summary.StepsNotStarted.Should().Be(1);
		summary.LastCompletedStep.Should().BeNull();
		summary.LastCompletedAt.Should().BeNull();
		summary.CancelledSteps.Should().BeEmpty();
	}

	private sealed class CapturingRunStore(List<OrchestrationRunRecord> sink) : IRunStore
	{
		public Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default)
		{
			sink.Add(record);
			return Task.CompletedTask;
		}

		public Task<OrchestrationRunRecord?> GetRunAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
			=> Task.FromResult<OrchestrationRunRecord?>(sink.FirstOrDefault(r => r.OrchestrationName == orchestrationName && r.RunId == runId));

		public Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(string orchestrationName, int? limit = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>(sink.Where(r => r.OrchestrationName == orchestrationName).ToList());

		public Task<IReadOnlyList<OrchestrationRunRecord>> ListAllRunsAsync(int? limit = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>(sink.ToList());

		public Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsByTriggerAsync(string triggerId, int? limit = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>(sink.Where(r => r.TriggerId == triggerId).ToList());

		public Task<bool> DeleteRunAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
		{
			var removed = sink.RemoveAll(r => r.OrchestrationName == orchestrationName && r.RunId == runId);
			return Task.FromResult(removed > 0);
		}
	}
}
