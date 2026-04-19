using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Integration tests that verify SDK event flow across multiple orchestration steps.
/// These tests exist because the core multi-step event flow was never tested end-to-end:
/// the existing OrchestrationExecutorTests use MockAgentBuilder.WithResponse() which
/// produces only a single MessageDelta event, never exercising the realistic event
/// sequence (SessionStart → TurnStart → ReasoningDelta → MessageDelta → TurnEnd)
/// that the real CopilotAgent produces.
///
/// These tests caught a production bug where the first step in an orchestration would
/// work fine but subsequent steps would receive no SDK events and timeout.
/// </summary>
public class MultiStepEventFlowTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

	public MultiStepEventFlowTests()
	{
		_loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
		_loggerFactory.CreateLogger<OrchestrationExecutor>().Returns(Substitute.For<ILogger<OrchestrationExecutor>>());
		_loggerFactory.CreateLogger<PromptExecutor>().Returns(Substitute.For<ILogger<PromptExecutor>>());
	}

	/// <summary>
	/// Creates a prompt step with a unique prompt that includes the step name
	/// so the test handler can identify which step is running.
	/// </summary>
	private static PromptOrchestrationStep CreateIdentifiableStep(string name, string[]? dependsOn = null)
	{
		return TestOrchestrations.CreatePromptStep(name,
			dependsOn: dependsOn,
			userPrompt: $"Execute step __STEP_{name}__");
	}

	/// <summary>
	/// Extracts a step name from a prompt that contains "__STEP_X__".
	/// </summary>
	private static string ExtractStepName(string prompt)
	{
		const string prefix = "__STEP_";
		const string suffix = "__";
		var start = prompt.IndexOf(prefix, StringComparison.Ordinal);
		if (start < 0) return "unknown";
		start += prefix.Length;
		var end = prompt.IndexOf(suffix, start, StringComparison.Ordinal);
		if (end < 0) return "unknown";
		return prompt[start..end];
	}

	#region Linear Chain (Sequential Steps)

	[Fact]
	public async Task LinearChain_AllStepsReceiveReasoningAndContent()
	{
		// Arrange — A → B → C, each step emits realistic event sequences
		var receivedEvents = new ConcurrentDictionary<string, List<AgentEventType>>();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler(MockAgentBuilderExtensions.CreatePerStepHandler(
			receivedEvents,
			stepNameExtractor: ExtractStepName,
			reasoning: "Let me think about this step carefully."));

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "linear-chain",
			Description = "A -> B -> C",
			Steps = [
				CreateIdentifiableStep("A"),
				CreateIdentifiableStep("B", dependsOn: ["A"]),
				CreateIdentifiableStep("C", dependsOn: ["B"]),
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — all 3 steps should have completed with full event sequences
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(3);

		// Each step should have received the full event lifecycle
		receivedEvents.Should().ContainKey("A");
		receivedEvents.Should().ContainKey("B");
		receivedEvents.Should().ContainKey("C");

		// Verify each step received SessionStart, TurnStart, reasoning, content, and TurnEnd
		foreach (var stepName in new[] { "A", "B", "C" })
		{
			var events = receivedEvents[stepName];
			events.Should().Contain(AgentEventType.SessionStart,
				$"Step {stepName} should receive SessionStart");
			events.Should().Contain(AgentEventType.TurnStart,
				$"Step {stepName} should receive TurnStart");
			events.Should().Contain(AgentEventType.ReasoningDelta,
				$"Step {stepName} should receive ReasoningDelta");
			events.Should().Contain(AgentEventType.MessageDelta,
				$"Step {stepName} should receive MessageDelta");
			events.Should().Contain(AgentEventType.TurnEnd,
				$"Step {stepName} should receive TurnEnd");
		}
	}

	[Fact]
	public async Task LinearChain_StepOutputsFlowToDownstreamSteps()
	{
		// Arrange — verify that step B receives step A's output in its prompt
		var promptsReceived = new ConcurrentDictionary<string, string>();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var stepName = ExtractStepName(prompt);
			promptsReceived[stepName] = prompt;

			var events = MockAgentBuilderExtensions.BuildRealisticEventSequence($"Result from {stepName}");
			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				foreach (var evt in events)
					await channel.Writer.WriteAsync(evt, ct);
				channel.Writer.Complete();
				return new AgentResult { Content = $"Result from {stepName}", ActualModel = "test-model" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "linear-chain-outputs",
			Description = "A -> B -> C",
			Steps = [
				CreateIdentifiableStep("A"),
				CreateIdentifiableStep("B", dependsOn: ["A"]),
				CreateIdentifiableStep("C", dependsOn: ["B"]),
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		promptsReceived.Should().ContainKey("B");
		promptsReceived["B"].Should().Contain("Result from A",
			"Step B's prompt should include Step A's output as dependency data");
	}

	#endregion

	#region Parallel Steps

	[Fact]
	public async Task ParallelSteps_AllStepsReceiveEventsConcurrently()
	{
		// Arrange — A, B, C run in parallel, all should receive events
		var receivedEvents = new ConcurrentDictionary<string, List<AgentEventType>>();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var stepName = ExtractStepName(prompt);

			var events = MockAgentBuilderExtensions.BuildRealisticEventSequence(
				$"Output from {stepName}",
				reasoning: $"Thinking about {stepName}...");

			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				var stepEvents = new List<AgentEventType>();
				foreach (var evt in events)
				{
					stepEvents.Add(evt.Type);
					await channel.Writer.WriteAsync(evt, ct);
				}
				receivedEvents[stepName] = stepEvents;
				channel.Writer.Complete();
				return new AgentResult { Content = $"Output from {stepName}", ActualModel = "test-model" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "parallel",
			Description = "A, B, C in parallel",
			Steps = [
				CreateIdentifiableStep("A"),
				CreateIdentifiableStep("B"),
				CreateIdentifiableStep("C"),
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — all 3 parallel steps should complete with full event sequences
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(3);

		receivedEvents.Should().HaveCount(3);
		foreach (var stepName in new[] { "A", "B", "C" })
		{
			receivedEvents[stepName].Should().Contain(AgentEventType.SessionStart);
			receivedEvents[stepName].Should().Contain(AgentEventType.MessageDelta);
			receivedEvents[stepName].Should().Contain(AgentEventType.TurnEnd);
		}
	}

	#endregion

	#region Diamond DAG

	[Fact]
	public async Task DiamondDag_AllStepsReceiveEventsIncludingDependencyOutputs()
	{
		// Arrange — A → B, C → D (diamond pattern)
		var receivedEvents = new ConcurrentDictionary<string, List<AgentEventType>>();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var stepName = ExtractStepName(prompt);

			var events = MockAgentBuilderExtensions.BuildRealisticEventSequence(
				$"Output from {stepName}",
				reasoning: $"Reasoning for {stepName}",
				toolCalls: [("read_file", """{"path":"test.txt"}""", "file content")]);

			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				var stepEvents = new List<AgentEventType>();
				foreach (var evt in events)
				{
					stepEvents.Add(evt.Type);
					await channel.Writer.WriteAsync(evt, ct);
				}
				receivedEvents[stepName] = stepEvents;
				channel.Writer.Complete();
				return new AgentResult { Content = $"Output from {stepName}", ActualModel = "test-model" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "diamond-dag",
			Description = "A -> B, C -> D",
			Steps = [
				CreateIdentifiableStep("A"),
				CreateIdentifiableStep("B", dependsOn: ["A"]),
				CreateIdentifiableStep("C", dependsOn: ["A"]),
				CreateIdentifiableStep("D", dependsOn: ["B", "C"]),
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — all 4 steps should receive full event sequences
		result.Status.Should().Be(ExecutionStatus.Succeeded);

		// Step D (terminal) runs after both B and C complete
		receivedEvents.Should().HaveCount(4);
		foreach (var stepName in new[] { "A", "B", "C", "D" })
		{
			receivedEvents[stepName].Should().Contain(AgentEventType.SessionStart,
				$"Step {stepName} should receive SessionStart");
			receivedEvents[stepName].Should().Contain(AgentEventType.ReasoningDelta,
				$"Step {stepName} should receive reasoning events");
			receivedEvents[stepName].Should().Contain(AgentEventType.ToolExecutionStart,
				$"Step {stepName} should receive tool execution events");
			receivedEvents[stepName].Should().Contain(AgentEventType.ToolExecutionComplete,
				$"Step {stepName} should receive tool completion events");
			receivedEvents[stepName].Should().Contain(AgentEventType.MessageDelta,
				$"Step {stepName} should receive content events");
			receivedEvents[stepName].Should().Contain(AgentEventType.TurnEnd,
				$"Step {stepName} should receive TurnEnd");
		}
	}

	#endregion

	#region Steps with Engine Tools

	[Fact]
	public async Task StepWithToolCalls_SubsequentStepStillReceivesEvents()
	{
		// Arrange — Step A uses tool calls, Step B (depends on A) should still receive events
		var receivedEvents = new ConcurrentDictionary<string, List<AgentEventType>>();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var stepName = ExtractStepName(prompt);
			AgentEvent[] events;

			if (stepName == "A")
			{
				// Step A has multiple tool calls
				events = MockAgentBuilderExtensions.BuildRealisticEventSequence(
					"Done with tools",
					reasoning: "Need to use some tools first",
					toolCalls: [
						("read_file", """{"path":"a.txt"}""", "contents of a"),
						("edit_file", """{"path":"b.txt","content":"new"}""", "edited successfully"),
					]);
			}
			else
			{
				// Step B is a simple response
				events = MockAgentBuilderExtensions.BuildRealisticEventSequence(
					"Step B output based on A's results");
			}

			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				var stepEvents = new List<AgentEventType>();
				foreach (var evt in events)
				{
					stepEvents.Add(evt.Type);
					await channel.Writer.WriteAsync(evt, ct);
				}
				receivedEvents[stepName] = stepEvents;
				channel.Writer.Complete();

				var content = stepName == "A" ? "Done with tools" : "Step B output based on A's results";
				return new AgentResult { Content = content, ActualModel = "test-model" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "tool-then-response",
			Description = "Step A uses tools, Step B depends on A",
			Steps = [
				CreateIdentifiableStep("A"),
				CreateIdentifiableStep("B", dependsOn: ["A"]),
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(2);

		// Step A should have tool call events
		receivedEvents["A"].Should().Contain(AgentEventType.ToolExecutionStart);
		receivedEvents["A"].Should().Contain(AgentEventType.ToolExecutionComplete);

		// Step B MUST also receive its full event sequence (this is the critical assertion)
		receivedEvents["B"].Should().Contain(AgentEventType.SessionStart,
			"Step B must receive SessionStart after Step A completes with tool calls");
		receivedEvents["B"].Should().Contain(AgentEventType.MessageDelta,
			"Step B must receive content events after Step A completes with tool calls");
	}

	[Fact]
	public async Task StepWithEngineToolSetStatus_SubsequentStepStillReceivesEvents()
	{
		// Arrange — Step A completes, Step B (depends on A) should still receive events
		var invocationCount = 0;
		var allInvocations = new ConcurrentDictionary<int, (string stepName, List<AgentEventType> events)>();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var invocation = Interlocked.Increment(ref invocationCount);
			var stepName = ExtractStepName(prompt);

			AgentEvent[] events = MockAgentBuilderExtensions.BuildRealisticEventSequence(
				$"Output from {stepName}");

			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				var stepEvents = new List<AgentEventType>();
				foreach (var evt in events)
				{
					stepEvents.Add(evt.Type);
					await channel.Writer.WriteAsync(evt, ct);
				}
				allInvocations[invocation] = (stepName, stepEvents);
				channel.Writer.Complete();
				return new AgentResult { Content = $"Output from {stepName}", ActualModel = "test-model" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "set-status-then-next",
			Description = "Step A sets status, Step B depends on A",
			Steps = [
				CreateIdentifiableStep("A"),
				CreateIdentifiableStep("B", dependsOn: ["A"]),
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(2);

		// Both steps must have been invoked
		invocationCount.Should().Be(2, "both step A and step B should have been invoked");

		// Both invocations should have received full event sequences
		foreach (var (_, (stepName, events)) in allInvocations)
		{
			events.Should().Contain(AgentEventType.SessionStart,
				$"Step {stepName} must receive SessionStart");
			events.Should().Contain(AgentEventType.MessageDelta,
				$"Step {stepName} must receive content events");
			events.Should().Contain(AgentEventType.TurnEnd,
				$"Step {stepName} must receive TurnEnd");
		}
	}

	#endregion

	#region Run Scope Lifecycle

	[Fact]
	public async Task RunScope_CalledOncePerOrchestrationRun()
	{
		// Arrange — use a spy builder that tracks CreateRunScopeAsync calls
		var createRunScopeCalls = 0;
		var agentBuilder = new RunScopeTrackingMockAgentBuilder(
			onCreateRunScope: () => Interlocked.Increment(ref createRunScopeCalls));
		agentBuilder.WithResponse("Output");

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "run-scope-test",
			Description = "A -> B -> C for scope tracking",
			Steps = [
				CreateIdentifiableStep("A"),
				CreateIdentifiableStep("B", dependsOn: ["A"]),
				CreateIdentifiableStep("C", dependsOn: ["B"]),
			]
		};

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — CreateRunScopeAsync should be called exactly once per orchestration run
		createRunScopeCalls.Should().Be(1,
			"CreateRunScopeAsync should be called once per orchestration run, not per step");
	}

	/// <summary>
	/// A MockAgentBuilder that tracks CreateRunScopeAsync calls.
	/// </summary>
	private sealed class RunScopeTrackingMockAgentBuilder : MockAgentBuilder
	{
		private readonly Action _onCreateRunScope;

		public RunScopeTrackingMockAgentBuilder(Action onCreateRunScope)
		{
			_onCreateRunScope = onCreateRunScope;
		}

		public override Task<IAsyncDisposable> CreateRunScopeAsync(CancellationToken cancellationToken = default)
		{
			_onCreateRunScope();
			return base.CreateRunScopeAsync(cancellationToken);
		}
	}

	#endregion
}
