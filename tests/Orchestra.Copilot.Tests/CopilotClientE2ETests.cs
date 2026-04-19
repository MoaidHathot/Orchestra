using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// End-to-end integration tests that exercise the real Copilot SDK client.
/// These tests create real CopilotClient instances, start real CLI processes,
/// create real sessions, and send real prompts to verify that SDK events
/// flow correctly across multiple sequential and concurrent sessions.
///
/// These tests require a working Copilot CLI binary on the machine.
/// They are categorized with Trait("Category", "E2E") so they can be
/// excluded from fast CI runs that don't have SDK access.
/// </summary>
[Trait("Category", "E2E")]
public class CopilotClientE2ETests : IAsyncLifetime
{
	private CopilotClient _client = null!;
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	public async Task InitializeAsync()
	{
		_client = new CopilotClient();
		await _client.StartAsync();
	}

	public async Task DisposeAsync()
	{
		try { await _client.StopAsync(); } catch { }
		try { await _client.DisposeAsync(); } catch { }
	}

	[Fact]
	public async Task SingleSession_ReceivesEventsAndCompletes()
	{
		// Arrange
		var receivedEventTypes = new ConcurrentBag<string>();
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		string? assistantContent = null;

		var config = new SessionConfig
		{
			Model = "claude-opus-4.6",
			OnPermissionRequest = PermissionHandler.ApproveAll,
		};

		// Act
		await using var session = await _client.CreateSessionAsync(config);

		session.On(evt =>
		{
			receivedEventTypes.Add(evt.GetType().Name);

			if (evt is AssistantMessageEvent msg)
				assistantContent = msg.Data.Content;
			if (evt is SessionIdleEvent)
				done.TrySetResult();
			if (evt is SessionErrorEvent err)
				done.TrySetException(new Exception($"Session error: {err.Data.Message}"));
		});

		await session.SendAsync(new MessageOptions { Prompt = "Reply with exactly: hello" });

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		cts.Token.Register(() => done.TrySetCanceled());
		await done.Task;

		// Assert
		receivedEventTypes.Should().NotBeEmpty("at least some events should have been received");
		assistantContent.Should().NotBeNullOrEmpty("the assistant should have responded");
	}

	[Fact]
	public async Task SequentialSessions_SecondSessionReceivesEvents()
	{
		// This is the critical test. The user reports that after the first session
		// completes, subsequent sessions on the same client receive no events.

		var config = new SessionConfig
		{
			Model = "claude-opus-4.6",
			OnPermissionRequest = PermissionHandler.ApproveAll,
		};

		// --- Session 1 ---
		string? content1 = null;
		{
			var done1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			await using var session1 = await _client.CreateSessionAsync(config);

			session1.On(evt =>
			{
				if (evt is AssistantMessageEvent msg)
					content1 = msg.Data.Content;
				if (evt is SessionIdleEvent)
					done1.TrySetResult();
				if (evt is SessionErrorEvent err)
					done1.TrySetException(new Exception($"Session 1 error: {err.Data.Message}"));
			});

			await session1.SendAsync(new MessageOptions { Prompt = "Reply with exactly: session1" });

			using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			cts1.Token.Register(() => done1.TrySetCanceled());
			await done1.Task;

			content1.Should().NotBeNullOrEmpty("Session 1 should have received a response");
		}
		// session1 is disposed here

		// --- Session 2 (on the SAME client) ---
		string? content2 = null;
		var eventTypes2 = new ConcurrentBag<string>();
		{
			var done2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			await using var session2 = await _client.CreateSessionAsync(config);

			session2.On(evt =>
			{
				eventTypes2.Add(evt.GetType().Name);

				if (evt is AssistantMessageEvent msg)
					content2 = msg.Data.Content;
				if (evt is SessionIdleEvent)
					done2.TrySetResult();
				if (evt is SessionErrorEvent err)
					done2.TrySetException(new Exception($"Session 2 error: {err.Data.Message}"));
			});

			await session2.SendAsync(new MessageOptions { Prompt = "Reply with exactly: session2" });

			using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			cts2.Token.Register(() => done2.TrySetCanceled());
			await done2.Task;

			eventTypes2.Should().NotBeEmpty(
				"Session 2 MUST receive events after Session 1 completes - " +
				"this is the core bug: subsequent sessions on the same client receive no events");
			content2.Should().NotBeNullOrEmpty(
				"Session 2 MUST produce a response - if this fails, the SDK client " +
				"is in a bad state after the first session");
		}
	}

	[Fact]
	public async Task ThreeSequentialSessions_AllReceiveEvents()
	{
		// Extended version: verify 3 sequential sessions all work
		var config = new SessionConfig
		{
			Model = "claude-opus-4.6",
			OnPermissionRequest = PermissionHandler.ApproveAll,
		};

		for (var i = 1; i <= 3; i++)
		{
			var sessionNumber = i;
			string? content = null;
			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			await using var session = await _client.CreateSessionAsync(config);

			session.On(evt =>
			{
				if (evt is AssistantMessageEvent msg)
					content = msg.Data.Content;
				if (evt is SessionIdleEvent)
					done.TrySetResult();
				if (evt is SessionErrorEvent err)
					done.TrySetException(new Exception($"Session {sessionNumber} error: {err.Data.Message}"));
			});

			await session.SendAsync(new MessageOptions { Prompt = $"Reply with exactly: session{sessionNumber}" });

			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			cts.Token.Register(() => done.TrySetCanceled());
			await done.Task;

			content.Should().NotBeNullOrEmpty(
				$"Session {sessionNumber} should have received a response");
		}
	}

	[Fact]
	public async Task ConcurrentSessions_AllReceiveEvents()
	{
		// Verify multiple sessions running in parallel on the same client
		var config = new SessionConfig
		{
			Model = "claude-opus-4.6",
			OnPermissionRequest = PermissionHandler.ApproveAll,
		};

		var results = new ConcurrentDictionary<int, string>();
		var tasks = new List<Task>();

		for (var i = 1; i <= 3; i++)
		{
			var sessionNumber = i;
			tasks.Add(Task.Run(async () =>
			{
				var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				string? content = null;

				await using var session = await _client.CreateSessionAsync(config);

				session.On(evt =>
				{
					if (evt is AssistantMessageEvent msg)
						content = msg.Data.Content;
					if (evt is SessionIdleEvent)
						done.TrySetResult();
					if (evt is SessionErrorEvent err)
						done.TrySetException(new Exception($"Session {sessionNumber} error: {err.Data.Message}"));
				});

				await session.SendAsync(new MessageOptions
				{
					Prompt = $"Reply with exactly: parallel{sessionNumber}"
				});

				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
				cts.Token.Register(() => done.TrySetCanceled());
				await done.Task;

				if (content is not null)
					results[sessionNumber] = content;
			}));
		}

		await Task.WhenAll(tasks);

		results.Should().HaveCount(3,
			"all 3 concurrent sessions should have produced responses");
	}

	[Fact]
	public async Task SequentialSessions_ViaOrchestrationAgent_EventsFlowCorrectly()
	{
		// This test exercises the full Orchestra agent path:
		// CopilotAgentBuilder → CopilotAgent → CopilotSessionHandler → AgentEventProcessor
		// It simulates what happens when two orchestration steps run sequentially.

		var builder = new CopilotAgentBuilder(_loggerFactory);

		// Step 1
		var agent1 = await builder
			.WithModel("claude-opus-4.6")
			.WithReporter(NullOrchestrationReporter.Instance)
			.BuildAgentAsync();

		var task1 = agent1.SendAsync("Reply with exactly: step1");
		var events1 = new List<AgentEventType>();
		await foreach (var evt in task1)
		{
			events1.Add(evt.Type);
		}
		var result1 = await task1.GetResultAsync();

		result1.Content.Should().NotBeNullOrEmpty("Step 1 should produce content");
		events1.Should().Contain(AgentEventType.MessageDelta, "Step 1 should receive message events");

		// Step 2 — on the same builder (same client)
		var agent2 = await builder
			.WithModel("claude-opus-4.6")
			.WithReporter(NullOrchestrationReporter.Instance)
			.BuildAgentAsync();

		var task2 = agent2.SendAsync("Reply with exactly: step2");
		var events2 = new List<AgentEventType>();
		await foreach (var evt in task2)
		{
			events2.Add(evt.Type);
		}
		var result2 = await task2.GetResultAsync();

		result2.Content.Should().NotBeNullOrEmpty(
			"Step 2 MUST produce content - this verifies sequential sessions " +
			"through the Orchestra agent layer work correctly");
		events2.Should().Contain(AgentEventType.MessageDelta,
			"Step 2 MUST receive message events - if this fails, the CopilotAgent/Handler " +
			"is not properly handling sequential sessions");
	}

	[Fact]
	public async Task TwoRunsBackToBack_HandlerAndStepShareSameClient_BothSucceed()
	{
		// Architectural invariant: per-orchestration run, there is exactly ONE Copilot CLI
		// process. Both the trigger's InputHandlerPrompt agent and the orchestration step
		// agents share that single client (each gets its own SDK Session, not its own
		// CLI subprocess).
		//
		// Regression history: TriggerManager used to BuildAgentAsync for the InputHandlerPrompt
		// either OUTSIDE any scope (silently lazy-creating a long-lived shared "fallback"
		// CopilotClient — first run worked, second run hit broken pipes) or INSIDE a separate
		// per-handler scope (two CLI processes per logical run, defeating the isolation model).
		// The current design: the handler runs as a delegate invoked by OrchestrationExecutor
		// AFTER the run scope is open, so handler + steps share the same client.
		//
		// This test reproduces the production sequence: for each "run", open ONE scope, build
		// the input-handler agent inside it, then build the step agent inside it. Verify both
		// agents resolved to the SAME underlying client (one CLI process for the whole run)
		// AND that running the same pattern twice in sequence both succeed (no stale state
		// leaking between runs).
		var builder = new CopilotAgentBuilder(_loggerFactory);

		string? clientHashRun1 = null;
		string? clientHashRun2 = null;

		for (var run = 1; run <= 2; run++)
		{
			await using var runScope = await builder.CreateRunScopeAsync();

			// --- Input handler agent inside the run scope ---
			var inputAgent = await builder
				.WithModel("claude-opus-4.6")
				.WithReporter(NullOrchestrationReporter.Instance)
				.BuildAgentAsync();

			var clientHashAfterHandlerBuild = builder.GetRunScopedClientDiagnostic();

			var inputTask = inputAgent.SendAsync($"Reply with exactly: input{run}");
			await foreach (var _ in inputTask) { }
			var inputContent = (await inputTask.GetResultAsync()).Content;

			inputContent.Should().NotBeNullOrEmpty(
				$"Run {run} input-handler agent must produce content");

			// --- Step agent inside the SAME run scope ---
			var stepAgent = await builder
				.WithModel("claude-opus-4.6")
				.WithReporter(NullOrchestrationReporter.Instance)
				.BuildAgentAsync();

			var clientHashAfterStepBuild = builder.GetRunScopedClientDiagnostic();

			clientHashAfterStepBuild.Should().Be(clientHashAfterHandlerBuild,
				$"Run {run}: handler and step MUST resolve to the same per-run CLI client " +
				"(one CLI process per orchestration, multiple sessions). If this fails, the " +
				"per-run-scope invariant has regressed.");

			var stepTask = stepAgent.SendAsync($"Reply with exactly: step{run}");
			await foreach (var _ in stepTask) { }
			var stepContent = (await stepTask.GetResultAsync()).Content;

			stepContent.Should().NotBeNullOrEmpty(
				$"Run {run} step agent must produce content within the same run scope");

			if (run == 1) clientHashRun1 = clientHashAfterStepBuild;
			else clientHashRun2 = clientHashAfterStepBuild;
		}

		clientHashRun1.Should().NotBeNullOrEmpty();
		clientHashRun2.Should().NotBeNullOrEmpty();
		clientHashRun1.Should().NotBe(clientHashRun2,
			"each run MUST get its own dedicated CLI process — runs must NOT share clients");
	}

	[Fact]
	public async Task PerRunScope_SequentialSessions_EventsFlowCorrectly()
	{
		// This test exercises the per-run scope lifecycle:
		// CreateRunScopeAsync → multiple BuildAgentAsync calls → scope disposal
		// This is what OrchestrationExecutor does.

		var builder = new CopilotAgentBuilder(_loggerFactory);

		await using var runScope = await builder.CreateRunScopeAsync();

		// Step 1 within the run scope
		var agent1 = await builder
			.WithModel("claude-opus-4.6")
			.WithReporter(NullOrchestrationReporter.Instance)
			.BuildAgentAsync();

		var task1 = agent1.SendAsync("Reply with exactly: run-step1");
		await foreach (var _ in task1) { }
		var result1 = await task1.GetResultAsync();

		result1.Content.Should().NotBeNullOrEmpty("Run step 1 should produce content");

		// Step 2 within the same run scope (same client)
		var agent2 = await builder
			.WithModel("claude-opus-4.6")
			.WithReporter(NullOrchestrationReporter.Instance)
			.BuildAgentAsync();

		var task2 = agent2.SendAsync("Reply with exactly: run-step2");
		await foreach (var _ in task2) { }
		var result2 = await task2.GetResultAsync();

		result2.Content.Should().NotBeNullOrEmpty(
			"Run step 2 MUST produce content within the same run scope");
	}
}
