using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;
using System.Threading.Channels;

namespace Orchestra.Engine.Tests.Executor;

public class PromptExecutorTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);
	private readonly ILogger<PromptExecutor> _logger = Substitute.For<ILogger<PromptExecutor>>();
	private readonly IPromptFormatter _formatter = DefaultPromptFormatter.Instance;

	#region Basic Execution

	[Fact]
	public async Task ExecuteAsync_SimpleStep_ReturnsSucceededResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Hello, world!");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Hello, world!");
	}

	[Fact]
	public async Task ExecuteAsync_ReportsContentDelta()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response content");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportContentDelta("test-step", Arg.Any<string>(), Arg.Any<ActorContext>());
	}

	[Fact]
	public async Task ExecuteAsync_WithError_ReturnsFailedResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Agent error"));
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("Agent error");
	}

	[Fact]
	public async Task ExecuteAsync_WithError_ReportsError()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Agent error"));
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert — PromptExecutor now uses the structured ReportStepError overload that
		// also accepts AgentSessionErrorDetails (null here because the underlying error
		// is a plain Exception with no IAgentSessionFailedException marker).
		reporter.Received().ReportStepError(
			"test-step",
			Arg.Is<string>(s => s.Contains("Agent error")),
			Arg.Is<AgentSessionErrorDetails?>(d => d == null));
	}

	[Fact]
	public async Task ExecuteAsync_WithAgentSessionFailedException_PopulatesErrorDetailsOnResult()
	{
		// Arrange — When the underlying agent (Copilot SDK) raises an exception that
		// implements IAgentSessionFailedException with structured Details, the executor
		// must:
		//   1. Carry those details into ExecutionResult.ErrorDetails so they land in run.json.
		//   2. Pass them through to the reporter's structured ReportStepError overload so
		//      live SSE consumers (Portal) can surface them.
		// This guarantees the upstream ErrorType/StatusCode/ProviderCallId/Url/Stack survive
		// the trip from the SDK boundary into the engine's result/persistence layer.
		var details = new AgentSessionErrorDetails
		{
			ErrorType = "query",
			StatusCode = 502,
			ProviderCallId = "abcd-efgh-1234",
			Url = "https://example.invalid/troubleshoot",
			Stack = "at Provider.send (cli.js:42)",
		};
		var sessionException = new TestSessionFailedException("Copilot session failed: boom", details);
		var agentBuilder = new MockAgentBuilder().WithException(sessionException);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — Result surface
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.ModelError);
		result.ErrorDetails.Should().NotBeNull("session-error exceptions must propagate Details into ExecutionResult so run.json carries them");
		result.ErrorDetails!.ErrorType.Should().Be("query");
		result.ErrorDetails.StatusCode.Should().Be(502);
		result.ErrorDetails.ProviderCallId.Should().Be("abcd-efgh-1234");
		result.ErrorDetails.Url.Should().Be("https://example.invalid/troubleshoot");
		result.ErrorDetails.Stack.Should().Contain("at Provider.send");

		// Assert — Structured reporter overload was called with the details
		reporter.Received().ReportStepError(
			"test-step",
			Arg.Is<string>(s => s.Contains("boom")),
			Arg.Is<AgentSessionErrorDetails>(d =>
				d.ErrorType == "query"
				&& d.StatusCode == 502
				&& d.ProviderCallId == "abcd-efgh-1234"));
	}

	[Fact]
	public async Task ExecuteAsync_WithPlainException_ErrorDetailsAreNull()
	{
		// Arrange — A non-session exception (no IAgentSessionFailedException marker)
		// must not synthesize fake details; ErrorDetails stays null and the structured
		// reporter overload is still invoked (with null details) so consumers see a
		// consistent shape.
		var agentBuilder = new MockAgentBuilder().WithException(new InvalidOperationException("not a session error"));
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorDetails.Should().BeNull();

		reporter.Received().ReportStepError(
			"test-step",
			Arg.Is<string>(s => s.Contains("not a session error")),
			Arg.Is<AgentSessionErrorDetails?>(d => d == null));
	}

	/// <summary>
	/// Test-only exception that implements <see cref="IAgentSessionFailedException"/>
	/// so PromptExecutor catch tests can drive the marker-interface code path without
	/// taking a dependency on Orchestra.Copilot (where the production implementation
	/// <c>CopilotSessionFailedException</c> lives).
	/// </summary>
	private sealed class TestSessionFailedException : Exception, IAgentSessionFailedException
	{
		public TestSessionFailedException(string message, AgentSessionErrorDetails? details)
			: base(message)
		{
			Details = details;
		}

		public AgentSessionErrorDetails? Details { get; }
	}

	#endregion

	#region Executor-Level CLI-Exhaustion Swap Retry

	private static AgentSessionErrorDetails ExhaustedCliRetriesDetails() => new()
	{
		ErrorType = "model",
		ExhaustedCliRetries = true,
	};

	[Fact]
	public async Task ExecuteAsync_ExhaustedCliRetries_RetriesOnFreshAgent_AndSucceeds()
	{
		// Arrange — The bundled Copilot CLI surfaces "Failed to get response from the
		// AI model; retried 5 times" as an IAgentSessionFailedException whose Details
		// carry ExhaustedCliRetries=true. The executor-level swap loop must catch this
		// shape, re-build the agent, and re-run the step. A successful response on the
		// second attempt is what the user-visible outcome should be.
		var sessionException = new TestSessionFailedException(
			"Copilot session failed: Execution failed: Error: Failed to get response from the AI model; retried 5 times (total retry wait time: 5.92 seconds) Last error: Unknown error",
			ExhaustedCliRetriesDetails());
		var agentBuilder = new MockAgentBuilder()
			.WithFailuresThenResponse(sessionException, failureCount: 1, finalResponseContent: "Recovered output");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("get-latest-teams-chat");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded,
			"the executor-level swap loop must recover from a single CLI-exhaustion failure by re-running on a fresh agent");
		result.Content.Should().Be("Recovered output");
	}

	[Fact]
	public async Task ExecuteAsync_ExhaustedCliRetries_PlainExceptionMessage_AlsoTriggersSwap()
	{
		// Arrange — Defence-in-depth: even if the failure surfaces as a plain Exception
		// (no IAgentSessionFailedException marker, no structured Details), the executor
		// must recognise the well-known CLI message pattern and still trigger a swap.
		// This guards against future SDK changes that route the same error class through
		// a different exception type.
		var plainException = new Exception(
			"Execution failed: Error: Failed to get response from the AI model; retried 5 times (total retry wait time: 5.92 seconds) Last error: Unknown error");
		var agentBuilder = new MockAgentBuilder()
			.WithFailuresThenResponse(plainException, failureCount: 1, finalResponseContent: "Recovered output");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Recovered output");
	}

	[Fact]
	public async Task ExecuteAsync_ExhaustedCliRetries_BudgetExhausted_ReturnsFailed()
	{
		// Arrange — With MaxAgentSwapAttempts=1 (default) the loop allows one extra
		// attempt. If BOTH attempts hit the CLI-exhaustion error the step must fail
		// with the original error category preserved (ModelError, not silently demoted)
		// and ErrorDetails.ExhaustedCliRetries must still be true so the run record
		// shows operators exactly why the recovery didn't take.
		var sessionException = new TestSessionFailedException(
			"Copilot session failed: Failed to get response from the AI model; retried 5 times",
			ExhaustedCliRetriesDetails());
		var agentBuilder = new MockAgentBuilder().WithException(sessionException);

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.ModelError);
		result.ErrorDetails.Should().NotBeNull();
		result.ErrorDetails!.ExhaustedCliRetries.Should().BeTrue(
			"the persisted error details must still reflect the CLI-exhaustion classification so post-mortem readers know why the swap budget was consumed");
	}

	[Fact]
	public async Task ExecuteAsync_PlainModelError_IsNotRetried()
	{
		// Arrange — A plain failure with no CLI-exhaustion signal must NOT trigger the
		// executor-level swap loop. Re-running the step on a fresh agent for an ordinary
		// model error would waste tokens and time — the existing orchestration-level
		// retry policy is the right knob for those.
		var unrelatedException = new InvalidOperationException("validation error: bad parameter");
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder().WithHandler((_, _) =>
		{
			Interlocked.Increment(ref callCount);
			var ch = Channel.CreateUnbounded<AgentEvent>();
			ch.Writer.Complete();
			return new AgentTask(ch.Reader, Task.FromException<AgentResult>(unrelatedException));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(1, "a non-exhaustion failure must not trigger the executor-level swap loop");
		result.ErrorDetails.Should().BeNull("plain exceptions should not synthesise structured details");
	}

	[Fact]
	public async Task ExecuteAsync_ExhaustedCliRetries_WithZeroBudget_FailsImmediately()
	{
		// Arrange — Operators can opt out of executor-level recovery by passing
		// maxAgentSwapAttempts: 0. In that case even a CLI-exhaustion failure must
		// fail-fast on the first attempt (in-agent swap remains the only recovery path).
		var sessionException = new TestSessionFailedException(
			"Failed to get response from the AI model; retried 5 times",
			ExhaustedCliRetriesDetails());
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder().WithHandler((_, _) =>
		{
			Interlocked.Increment(ref callCount);
			var ch = Channel.CreateUnbounded<AgentEvent>();
			ch.Writer.Complete();
			return new AgentTask(ch.Reader, Task.FromException<AgentResult>(sessionException));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, maxAgentSwapAttempts: 0);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(1, "with budget=0 the executor must not attempt a second run");
	}

	[Fact]
	public void LooksLikeCliExhaustedRetriesMessage_RecognisesUserVisibleErrorString()
	{
		// Defends against drift between the engine's executor-level detector and
		// CopilotSessionHandler.LooksLikeCliExhaustedRetries (the CLI/SDK-side detector
		// that flags ExhaustedCliRetries on the structured details record). Both sites
		// must recognise the exact production error string captured in the run.json of
		// the failing run that motivated this safety net.
		var userVisibleMessage =
			"Copilot session failed: Execution failed: Error: Failed to get response from the AI model; retried 5 times (total retry wait time: 5.92 seconds) Last error: Unknown error";

		PromptExecutor.LooksLikeCliExhaustedRetriesMessage(userVisibleMessage).Should().BeTrue();
		PromptExecutor.LooksLikeCliExhaustedRetriesMessage("Failed to get response from the AI model").Should().BeTrue();
		PromptExecutor.LooksLikeCliExhaustedRetriesMessage("the operation was retried 7 times before timing out").Should().BeTrue();
		PromptExecutor.LooksLikeCliExhaustedRetriesMessage("validation error: bad parameter").Should().BeFalse();
		PromptExecutor.LooksLikeCliExhaustedRetriesMessage(null).Should().BeFalse();
		PromptExecutor.LooksLikeCliExhaustedRetriesMessage("").Should().BeFalse();
	}

	[Fact]
	public void LooksLikeTransientUpstreamMessage_RecognisesUserVisibleErrorString()
	{
		// Defends against drift between the engine's executor-level detector and
		// CopilotSessionHandler.LooksLikeTransientUpstreamFailure. Both sites must
		// recognise the exact production error string captured in the run.json of
		// the failing zts-official-pipeline-auto-discoverer run that motivated this
		// safety net.
		var brokerError =
			"Copilot session failed: Execution failed: Error: 500 \"can't get copilot user by id: error getting copilot user details: twirp error permission_denied: Error from intermediary with HTTP status code 403 \\\"Forbidden\\\"\\n\" (Request ID: F490:865D5:3A32591:3FE6380:6A0C16FC)";

		PromptExecutor.LooksLikeTransientUpstreamMessage(brokerError).Should().BeTrue();
		PromptExecutor.LooksLikeTransientUpstreamMessage("Error: 502 Bad Gateway").Should().BeTrue();
		PromptExecutor.LooksLikeTransientUpstreamMessage("HTTP status code 503").Should().BeTrue();
		PromptExecutor.LooksLikeTransientUpstreamMessage("HTTP status code 403").Should().BeTrue();
		PromptExecutor.LooksLikeTransientUpstreamMessage("twirp error permission_denied: ...").Should().BeTrue();
		PromptExecutor.LooksLikeTransientUpstreamMessage("can't get copilot user by id").Should().BeTrue();
		PromptExecutor.LooksLikeTransientUpstreamMessage("rate limit exceeded").Should().BeTrue();
		// SDK session-create failures where the bundled CLI lost its auth handle.
		// Production string captured from zts-official-pipeline-tracker on 2026-05-19.
		PromptExecutor.LooksLikeTransientUpstreamMessage(
			"Copilot session failed: Execution failed: Error: Session was not created with authentication info or custom provider").Should().BeTrue();
		PromptExecutor.LooksLikeTransientUpstreamMessage("Session was not created with authentication info").Should().BeTrue();

		PromptExecutor.LooksLikeTransientUpstreamMessage("HTTP status code 400").Should().BeFalse();
		PromptExecutor.LooksLikeTransientUpstreamMessage("validation error: bad parameter").Should().BeFalse();
		PromptExecutor.LooksLikeTransientUpstreamMessage(null).Should().BeFalse();
		PromptExecutor.LooksLikeTransientUpstreamMessage("").Should().BeFalse();
	}

	private static AgentSessionErrorDetails TransientUpstreamDetails() => new()
	{
		ErrorType = "authorization",
		StatusCode = 500,
		TransientUpstreamFailure = true,
	};

	[Fact]
	public async Task ExecuteAsync_TransientUpstreamFailure_RetriesOnFreshAgent_AndSucceeds()
	{
		// Arrange — the 500/403 broker handshake failure that took down
		// zts-official-pipeline-auto-discoverer surfaces as IAgentSessionFailedException
		// with Details.TransientUpstreamFailure=true. The executor-level swap loop must
		// catch this shape, re-build the agent, and re-run the step.
		var sessionException = new TestSessionFailedException(
			"Copilot session failed: Execution failed: Error: 500 \"can't get copilot user by id: ... twirp error permission_denied: Error from intermediary with HTTP status code 403 \\\"Forbidden\\\"\\n\"",
			TransientUpstreamDetails());
		var agentBuilder = new MockAgentBuilder()
			.WithFailuresThenResponse(sessionException, failureCount: 1, finalResponseContent: "Recovered output");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("gate-discovery");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded,
			"the executor-level swap loop must recover from a single transient upstream failure by re-running on a fresh agent");
		result.Content.Should().Be("Recovered output");
	}

	[Fact]
	public async Task ExecuteAsync_TransientUpstreamFailure_PlainExceptionMessage_AlsoTriggersSwap()
	{
		// Defence-in-depth: even if the broker error surfaces as a plain Exception
		// (no IAgentSessionFailedException marker, no structured Details), the executor
		// must recognise the well-known message pattern and still trigger a swap.
		var plainException = new Exception(
			"Execution failed: Error: 500 \"can't get copilot user by id: error getting copilot user details: twirp error permission_denied: Error from intermediary with HTTP status code 403 \\\"Forbidden\\\"\\n\"");
		var agentBuilder = new MockAgentBuilder()
			.WithFailuresThenResponse(plainException, failureCount: 1, finalResponseContent: "Recovered output");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("gate-discovery");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Recovered output");
	}

	[Fact]
	public async Task ExecuteAsync_TransientUpstreamFailure_BudgetExhausted_ReturnsFailed()
	{
		// Both attempts hit the broker error; the step must fail with the original
		// error preserved AND ErrorDetails.TransientUpstreamFailure still set so the
		// run record shows operators exactly why the recovery didn't take.
		var sessionException = new TestSessionFailedException(
			"Copilot session failed: Execution failed: Error: 500 broker permission_denied",
			TransientUpstreamDetails());
		var agentBuilder = new MockAgentBuilder().WithException(sessionException);

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorDetails.Should().NotBeNull();
		result.ErrorDetails!.TransientUpstreamFailure.Should().BeTrue(
			"the structured flag must propagate even when the swap budget is exhausted so operators can triage");
	}

	#endregion

	#region Fix C — Captured set_status guards the swap-retry loop

	[Fact]
	public async Task ExecuteAsync_LlmDeclaredSuccess_ThenExhaustedCliRetries_DoesNotRetry_AndReturnsSucceeded()
	{
		// Regression for run 505940e23cc1: the LLM successfully completed the work
		// and called orchestra_set_status('success'), then a transport-class failure
		// surfaced. Previously the executor's swap-retry loop would re-run the prompt
		// on a fresh agent, which could (and did) flip the result to Failed when the
		// fresh model reached a different conclusion. With Fix C the executor must
		// honour the LLM's declared terminal status and return Succeeded without
		// re-running anything.
		var callCount = 0;
		var setStatusTool = new SetStatusTool();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var ch = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run<AgentResult>(async () =>
			{
				// Step 1: drive set_status(success) against the real EngineToolContext
				// captured by the mock builder, so PromptExecutor sees the override.
				var ctx = agentBuilder.CapturedEngineToolContext!;
				setStatusTool.Execute("""{"status":"success","reason":"work done"}""", ctx);

				// Step 2: simulate the trailing CLI-exhaustion failure that motivated
				// the executor-level swap-retry path. With Fix C, the captured override
				// MUST short-circuit the retry — even though ExhaustedCliRetries is set.
				await Task.Yield();
				ch.Writer.Complete();
				throw new TestSessionFailedException(
					"Copilot session failed: Failed to get response from the AI model; retried 5 times",
					new AgentSessionErrorDetails { ExhaustedCliRetries = true });
			}, ct);
			return new AgentTask(ch.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("get-latest-teams-chat");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded,
			"the LLM declared success before the transport failure; the executor must not retry and must honour that decision");
		result.CapturedStatusOverride.Should().Be(ExecutionStatus.Succeeded);
		callCount.Should().Be(1, "the swap-retry loop must NOT re-run a step the LLM already declared terminal");
	}

	[Fact]
	public async Task ExecuteAsync_LlmDeclaredNoAction_ThenExhaustedCliRetries_DoesNotRetry_AndReturnsNoAction()
	{
		// no_action is symmetric to success: a terminal LLM decision the executor
		// must respect rather than blow away with a swap retry.
		var callCount = 0;
		var setStatusTool = new SetStatusTool();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var ch = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run<AgentResult>(async () =>
			{
				var ctx = agentBuilder.CapturedEngineToolContext!;
				setStatusTool.Execute("""{"status":"no_action","reason":"nothing to process"}""", ctx);
				await Task.Yield();
				ch.Writer.Complete();
				throw new TestSessionFailedException(
					"Failed to get response from the AI model; retried 5 times",
					new AgentSessionErrorDetails { ExhaustedCliRetries = true });
			}, ct);
			return new AgentTask(ch.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.NoAction);
		result.CapturedStatusOverride.Should().Be(ExecutionStatus.NoAction);
		callCount.Should().Be(1);
	}

	[Fact]
	public async Task ExecuteAsync_LlmDeclaredFailure_ThenExhaustedCliRetries_DoesNotRetry_AndReturnsFailed()
	{
		// An LLM-declared failure (set_status('failed')) is ALSO terminal — the
		// executor must not retry it. The result stays Failed (no upgrade to success).
		var callCount = 0;
		var setStatusTool = new SetStatusTool();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var ch = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run<AgentResult>(async () =>
			{
				var ctx = agentBuilder.CapturedEngineToolContext!;
				setStatusTool.Execute("""{"status":"failed","reason":"genuine failure"}""", ctx);
				await Task.Yield();
				ch.Writer.Complete();
				throw new TestSessionFailedException(
					"Failed to get response from the AI model; retried 5 times",
					new AgentSessionErrorDetails { ExhaustedCliRetries = true });
			}, ct);
			return new AgentTask(ch.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed, "an LLM-declared failure remains Failed; the swap-retry guard does NOT upgrade Failed→Succeeded");
		result.CapturedStatusOverride.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(1, "the swap-retry loop must not re-run a step the LLM explicitly declared Failed");
	}

	[Fact]
	public async Task ExecuteAsync_NoCapturedOverride_ExhaustedCliRetries_StillRetries()
	{
		// Sanity guard: the Fix C short-circuit must ONLY fire when the LLM declared
		// a terminal status. Pure transport failures with no engine-tool signal must
		// still benefit from the executor-level swap-retry loop introduced previously.
		var sessionException = new TestSessionFailedException(
			"Failed to get response from the AI model; retried 5 times",
			new AgentSessionErrorDetails { ExhaustedCliRetries = true });
		var agentBuilder = new MockAgentBuilder()
			.WithFailuresThenResponse(sessionException, failureCount: 1, finalResponseContent: "Recovered output");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded, "without a captured override the swap-retry loop must still recover from CLI exhaustion");
		result.Content.Should().Be("Recovered output");
	}

	#endregion

	#region Parameter Injection

	[Fact]
	public async Task ExecuteAsync_InjectsParameters()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response").WithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreateStepWithParameterizedPrompt(
			"param-step",
			"Hello {{name}}, your id is {{id}}",
			["name", "id"]);

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>
			{
				["name"] = "Alice",
				["id"] = "123"
			},
			OrchestrationInfo = s_defaultInfo
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Hello Alice, your id is 123");
	}

	[Fact]
	public async Task ExecuteAsync_MissingParameter_LeavesPlaceholder()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreateStepWithParameterizedPrompt(
			"param-step",
			"Hello {{name}}",
			["name"]);

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(), // name not provided
			OrchestrationInfo = s_defaultInfo
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Placeholder remains if parameter not provided
		capturedPrompt.Should().Contain("{{name}}");
	}

	#endregion

	#region Dependency Outputs

	[Fact]
	public async Task ExecuteAsync_IncludesDependencyOutputs()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["producer"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("producer", ExecutionResult.Succeeded("Producer output content"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Producer output content");
	}

	[Fact]
	public async Task ExecuteAsync_MultipleDependencies_FormatsWithHeaders()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["dep1", "dep2"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("dep1", ExecutionResult.Succeeded("Output from dep1"));
		context.AddResult("dep2", ExecutionResult.Succeeded("Output from dep2"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Output from dep1");
		capturedPrompt.Should().Contain("Output from dep2");
		capturedPrompt.Should().Contain("dep1");
		capturedPrompt.Should().Contain("dep2");
	}

	#endregion

	#region Loop Feedback

	[Fact]
	public async Task ExecuteAsync_WithLoopFeedback_IncludesFeedbackInPrompt()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("looping-step");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.SetLoopFeedback("looping-step", "Please improve the output by adding more details.");

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Please improve the output by adding more details.");
		capturedPrompt.Should().Contain("Feedback from previous attempt");
	}

	[Fact]
	public async Task ExecuteAsync_ConsumesLoopFeedback()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("looping-step");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.SetLoopFeedback("looping-step", "Feedback");

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Feedback should be consumed
		context.ConsumeLoopFeedback("looping-step").Should().BeNull();
	}

	#endregion

	#region Execution Trace

	[Fact]
	public async Task ExecuteAsync_BuildsExecutionTrace()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response content");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("traced-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Trace.Should().NotBeNull();
		result.Trace!.SystemPrompt.Should().Be(step.SystemPrompt);
	}

	[Fact]
	public async Task ExecuteAsync_ReportsStepTrace()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response content");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("traced-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportStepTrace("traced-step", Arg.Any<StepExecutionTrace>());
	}

	[Fact]
	public async Task ExecuteAsync_CapturesRawDependencyOutputs()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["producer"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("producer", ExecutionResult.Succeeded("processed", rawContent: "raw content"));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.RawDependencyOutputs.Should().ContainKey("producer");
		result.RawDependencyOutputs["producer"].Should().Be("raw content");
	}

	#endregion

	#region Tool Execution Events

	[Fact]
	public async Task ExecuteAsync_WithToolCalls_ReportsToolExecution()
	{
		// Arrange
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "call1",
				ToolName = "read_file",
				ToolArguments = "{\"path\": \"/test.txt\"}"
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "call1",
				ToolName = "read_file",
				ToolSuccess = true,
				ToolResult = "file content"
			},
			new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Final response"
			}
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Final response", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("tool-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportToolExecutionStarted("tool-step", "read_file", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ActorContext>());
		reporter.Received().ReportToolExecutionCompleted("tool-step", "read_file", true, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ActorContext>());
	}

	[Fact]
	public async Task ExecuteAsync_WithToolCalls_IncludesInTrace()
	{
		// Arrange
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "call1",
				ToolName = "search"
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "call1",
				ToolName = "search",
				ToolSuccess = true,
				ToolResult = "results"
			},
			new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Done"
			}
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Done", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("tool-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Trace!.ToolCalls.Should().HaveCount(1);
		result.Trace.ToolCalls[0].ToolName.Should().Be("search");
		result.Trace.ToolCalls[0].Success.Should().BeTrue();
	}

	#endregion

	#region failOnToolError (Step-Level Tool Failure Gate)

	private static AgentEvent[] BuildFailedToolCallEvents(
		string toolName = "ask_work_iq",
		string callId = "call-failed-1",
		string errorMessage = "MCP server 'workiq': An unexpected error occurred while processing your request.",
		string? finalContent = "I attempted the tool call but it failed. Returning a summary.")
	{
		var events = new List<AgentEvent>
		{
			new()
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = callId,
				ToolName = toolName,
				McpServerName = "workiq",
				ToolArguments = "{\"q\":\"x\"}",
			},
			new()
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = callId,
				ToolName = toolName,
				ToolSuccess = false,
				ToolError = errorMessage,
			},
		};
		if (finalContent is not null)
		{
			events.Add(new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = finalContent,
			});
		}
		return events.ToArray();
	}

	[Fact]
	public async Task ExecuteAsync_FailOnToolErrorTrue_AndFailedToolCall_ReturnsFailedWithToolErrorCategory()
	{
		// Arrange — this is the new opt-in behavior. Without failOnToolError, the
		// historical path keeps the step Succeeded (the LLM summarized the failure
		// and ended its turn). With failOnToolError=true, the step must short-circuit
		// to Failed/ToolError so downstream gating works (e.g. dependency cascade).
		var events = BuildFailedToolCallEvents();
		var agentBuilder = new MockAgentBuilder().WithResponse("summarized", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = new PromptOrchestrationStep
		{
			Name = "search-workiq",
			Type = OrchestrationStepType.Prompt,
			SystemPrompt = "test",
			UserPrompt = "test",
			Model = "claude-opus-4.6",
			FailOnToolError = true,
		};
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.ToolError);
		result.ErrorMessage.Should().Contain("failOnToolError=true");
		result.ErrorMessage.Should().Contain("ask_work_iq");
		result.ErrorMessage.Should().Contain("mcp: workiq");
		reporter.Received().ReportStepError(
			"search-workiq",
			Arg.Is<string>(s => s.Contains("failOnToolError=true") && s.Contains("ask_work_iq")));
	}

	[Fact]
	public async Task ExecuteAsync_FailOnToolErrorTrue_AndNoFailedToolCalls_ReturnsSucceeded()
	{
		// Arrange — failOnToolError must not over-fire. A successful run (no failed
		// tool calls in the trace) continues to succeed even when the toggle is on.
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "call1",
				ToolName = "ask_work_iq",
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "call1",
				ToolName = "ask_work_iq",
				ToolSuccess = true,
				ToolResult = "results",
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Done" },
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Done", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = new PromptOrchestrationStep
		{
			Name = "happy-path",
			Type = OrchestrationStepType.Prompt,
			SystemPrompt = "s",
			UserPrompt = "u",
			Model = "claude-opus-4.6",
			FailOnToolError = true,
		};
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.ErrorCategory.Should().BeNull();
	}

	[Fact]
	public async Task ExecuteAsync_FailOnToolErrorFalse_AndFailedToolCall_ReturnsSucceeded()
	{
		// Arrange — explicitly setting failOnToolError=false at the step level must
		// preserve the historical behavior even if the orchestration default flips on
		// later. This is the "I know this tool can fail and I want the LLM to handle
		// it" escape hatch.
		var events = BuildFailedToolCallEvents();
		var agentBuilder = new MockAgentBuilder().WithResponse("summarized", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = new PromptOrchestrationStep
		{
			Name = "search-workiq",
			Type = OrchestrationStepType.Prompt,
			SystemPrompt = "s",
			UserPrompt = "u",
			Model = "claude-opus-4.6",
			FailOnToolError = false,
		};
		// Even though the orchestration-level default is on, the explicit step-level
		// false must win.
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			DefaultFailOnToolError = true,
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.ErrorCategory.Should().BeNull();
	}

	[Fact]
	public async Task ExecuteAsync_NoFailOnToolErrorSetting_AndFailedToolCall_ReturnsSucceeded()
	{
		// Arrange — regression guard: orchestrations authored before this feature
		// existed (no FailOnToolError on the step, no DefaultFailOnToolError on the
		// orchestration) must continue to behave exactly as before. This is the
		// scenario that the user's debug-m365-search run hit on 2026-06-08:
		// MCP tool failed, LLM wrote a summary, step ended Succeeded.
		var events = BuildFailedToolCallEvents();
		var agentBuilder = new MockAgentBuilder().WithResponse("summarized", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("search-workiq");
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			// Default value of DefaultFailOnToolError is false.
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — historical behavior preserved.
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_FailOnToolErrorNull_AndContextDefaultTrue_AndFailedToolCall_ReturnsFailed()
	{
		// Arrange — step inherits from the orchestration default. With the default
		// flipped on and no step-level override, a failed tool call must fail the step.
		var events = BuildFailedToolCallEvents();
		var agentBuilder = new MockAgentBuilder().WithResponse("summarized", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("search-workiq");
		// step.FailOnToolError is null (default), so context default applies.
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			DefaultFailOnToolError = true,
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.ToolError);
	}

	[Fact]
	public async Task ExecuteAsync_FailOnToolErrorTrue_LlmExplicitlySetSucceeded_HonorsLlmOverride()
	{
		// Arrange — precedence rule: the LLM's explicit orchestra_set_status('success')
		// override beats failOnToolError. The LLM has acknowledged the failure and
		// chosen to succeed; trust that decision (the same precedence applies in the
		// CLI swap-retry path — see ExecuteAsync_LlmDeclaredSuccess_ThenExhaustedCliRetries_*).
		var setStatusTool = new SetStatusTool();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			// Push the failed tool-call events into the channel before the result task
			// completes. The PromptExecutor will see them in eventProcessor.ToolCalls.
			foreach (var evt in BuildFailedToolCallEvents(finalContent: null))
			{
				channel.Writer.TryWrite(evt);
			}
			channel.Writer.TryWrite(new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Tool failed but I'm marking this succeeded.",
			});
			var resultTask = Task.Run<AgentResult>(async () =>
			{
				// Drive set_status(success) on the executor's real EngineToolContext.
				var ctx = agentBuilder.CapturedEngineToolContext!;
				setStatusTool.Execute(
					"""{"status":"success","reason":"Acknowledged the tool failure; using cached data instead."}""",
					ctx);
				await Task.Yield();
				channel.Writer.Complete();
				return new AgentResult
				{
					Content = "Tool failed but I'm marking this succeeded.",
					ActualModel = "claude-opus-4.6",
				};
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = new PromptOrchestrationStep
		{
			Name = "llm-waiver",
			Type = OrchestrationStepType.Prompt,
			SystemPrompt = "s",
			UserPrompt = "u",
			Model = "claude-opus-4.6",
			FailOnToolError = true,
		};
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded,
			"the LLM's explicit set_status('success') is an informed waiver of failOnToolError");
		result.CapturedStatusOverride.Should().Be(ExecutionStatus.Succeeded);
		result.ErrorCategory.Should().BeNull();
	}

	[Fact]
	public async Task ExecuteAsync_FailOnToolErrorTrue_MultipleFailedToolCalls_SummaryListsAll()
	{
		// Arrange — the error summary should aggregate every failed tool so an
		// operator can triage from the top-level error message without opening the
		// full trace.
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "call-1",
				ToolName = "ask_work_iq",
				McpServerName = "workiq",
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "call-1",
				ToolName = "ask_work_iq",
				ToolSuccess = false,
				ToolError = "first error",
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "call-2",
				ToolName = "copilot_chat",
				McpServerName = "m365-copilot",
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "call-2",
				ToolName = "copilot_chat",
				ToolSuccess = false,
				ToolError = "Copilot chat failed (408)",
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Done" },
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Done", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = new PromptOrchestrationStep
		{
			Name = "many-failures",
			Type = OrchestrationStepType.Prompt,
			SystemPrompt = "s",
			UserPrompt = "u",
			Model = "claude-opus-4.6",
			FailOnToolError = true,
		};
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.ToolError);
		result.ErrorMessage.Should().Contain("2 tool calls failed");
		result.ErrorMessage.Should().Contain("ask_work_iq");
		result.ErrorMessage.Should().Contain("copilot_chat");
		result.ErrorMessage.Should().Contain("first error");
		result.ErrorMessage.Should().Contain("Copilot chat failed (408)");
	}

	#endregion

	#region Reasoning Events

	[Fact]
	public async Task ExecuteAsync_WithReasoning_ReportsReasoningDelta()
	{
		// Arrange
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ReasoningDelta,
				Content = "Let me think..."
			},
			new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Response"
			}
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Response", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("reasoning-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportReasoningDelta("reasoning-step", "Let me think...", Arg.Any<ActorContext>());
	}

	[Fact]
	public async Task ExecuteAsync_WithReasoning_IncludesInTrace()
	{
		// Arrange
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ReasoningDelta,
				Content = "First thought. "
			},
			new AgentEvent
			{
				Type = AgentEventType.ReasoningDelta,
				Content = "Second thought."
			},
			new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Response"
			}
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Response", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("reasoning-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Trace!.Reasoning.Should().Be("First thought. Second thought.");
	}

	#endregion

	#region Usage Reporting

	[Fact]
	public async Task ExecuteAsync_WithUsage_ReportsUsage()
	{
		// Arrange
		var usage = new AgentUsage { InputTokens = 100, OutputTokens = 50 };
		var agentBuilder = new MockAgentBuilder().WithResponse("Response", usage: usage, actualModel: "claude-opus-4.5");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("usage-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportUsage("usage-step", "claude-opus-4.5", Arg.Any<AgentUsage>());
	}

	[Fact]
	public async Task ExecuteAsync_CapturesTokenUsage()
	{
		// Arrange
		var usage = new AgentUsage { InputTokens = 100, OutputTokens = 50 };
		var agentBuilder = new MockAgentBuilder().WithResponse("Response", usage: usage, actualModel: "model");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("usage-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Usage.Should().NotBeNull();
		result.Usage!.InputTokens.Should().Be(100);
		result.Usage.OutputTokens.Should().Be(50);
	}

	#endregion

	#region Model Information

	[Fact]
	public async Task ExecuteAsync_CapturesActualModel()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response", actualModel: "gpt-4-turbo");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("model-step", model: "gpt-4");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.ActualModel.Should().Be("gpt-4-turbo");
	}

	#endregion

	#region SystemPromptMode

	[Fact]
	public async Task ExecuteAsync_StepSystemPromptMode_OverridesContextDefault()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStepWithSystemPromptMode(
			"test-step",
			SystemPromptMode.Append); // Step explicitly sets Append

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			DefaultSystemPromptMode = SystemPromptMode.Replace, // Context default is Replace
			OrchestrationInfo = s_defaultInfo
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Step's mode should override context default
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Append);
	}

	[Fact]
	public async Task ExecuteAsync_NoStepSystemPromptMode_UsesContextDefault()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step"); // No SystemPromptMode set

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			DefaultSystemPromptMode = SystemPromptMode.Replace, // Context default is Replace
			OrchestrationInfo = s_defaultInfo
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Should use context's default
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	[Fact]
	public async Task ExecuteAsync_NoStepModeNoContextDefault_UsesNull()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step"); // No SystemPromptMode set

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo
			// No DefaultSystemPromptMode set
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Should be null (SDK default behavior)
		agentBuilder.CapturedSystemPromptMode.Should().BeNull();
	}

	[Fact]
	public async Task ExecuteAsync_StepModeReplace_PassesReplace()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStepWithSystemPromptMode(
			"test-step",
			SystemPromptMode.Replace);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	[Fact]
	public async Task ExecuteAsync_ContextDefaultAppend_StepNullMode_UsesAppend()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step"); // No mode

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			DefaultSystemPromptMode = SystemPromptMode.Append,
			OrchestrationInfo = s_defaultInfo
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Append);
	}

	#endregion

	#region Template Resolution in UserPrompt

	[Fact]
	public async Task ExecuteAsync_ResolvesInlineStepOutputTemplates()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		// Step references {{step-a.output}} inline in its userPrompt but depends on step-b
		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: ["step-b"],
			userPrompt: "Data from A: {{step-a.output}}\n\nPlease process.");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("step-a", ExecutionResult.Succeeded("incident data from step A"));
		context.AddResult("step-b", ExecutionResult.Succeeded("check passed"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - The {{step-a.output}} should be resolved inline, not left as literal text
		capturedPrompt.Should().Contain("Data from A: incident data from step A");
		capturedPrompt.Should().NotContain("{{step-a.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesInlineTemplateFromDirectDependency()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		// Step references {{dep.output}} inline and dep IS in dependsOn
		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: ["dep"],
			userPrompt: "Result: {{dep.output}}");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("dep", ExecutionResult.Succeeded("dependency output value"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Template resolved inline
		capturedPrompt.Should().Contain("Result: dependency output value");
		capturedPrompt.Should().NotContain("{{dep.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesMultipleInlineTemplates()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		// Step references multiple outputs inline, some from direct deps, some transitive
		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: ["dep-b"],
			userPrompt: "A output: {{dep-a.output}}\n\nB output: {{dep-b.output}}");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("dep-a", ExecutionResult.Succeeded("output from A"));
		context.AddResult("dep-b", ExecutionResult.Succeeded("output from B"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("A output: output from A");
		capturedPrompt.Should().Contain("B output: output from B");
		capturedPrompt.Should().NotContain("{{dep-a.output}}");
		capturedPrompt.Should().NotContain("{{dep-b.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_UnresolvedTemplate_RemainsLiteral()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		// Step references a step that doesn't exist in the context
		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: [],
			userPrompt: "Data: {{nonexistent-step.output}}");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Unresolvable template remains as-is
		capturedPrompt.Should().Contain("{{nonexistent-step.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesRawOutputTemplateInline()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: ["dep"],
			userPrompt: "Raw: {{dep.rawOutput}}");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("dep", ExecutionResult.Succeeded("processed", rawContent: "raw content here"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Raw: raw content here");
		capturedPrompt.Should().NotContain("{{dep.rawOutput}}");
	}

	[Fact]
	public async Task ExecuteAsync_IcmAcknowledgeScenario_ResolvesTransitiveDependencyOutput()
	{
		// Arrange - This test simulates the exact icm-acknowledge.json bug scenario:
		// acknowledge-incidents depends on check-incidents, but references {{fetch-active-incidents.output}}
		// which is a transitive dependency (check-incidents depends on fetch-active-incidents)
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("[12345, 67890]")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep(
			"acknowledge-incidents",
			dependsOn: ["check-incidents"],
			userPrompt: "The following is the list of currently active IcM incidents:\n\n{{fetch-active-incidents.output}}\n\nFor each incident, acknowledge it.");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("fetch-active-incidents", ExecutionResult.Succeeded("[{\"id\": 12345, \"title\": \"Server Down\"}, {\"id\": 67890, \"title\": \"High CPU\"}]"));
		context.AddResult("check-incidents", ExecutionResult.Succeeded("Proceeding with acknowledgment"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - The {{fetch-active-incidents.output}} must be resolved, not sent as literal
		capturedPrompt.Should().Contain("Server Down");
		capturedPrompt.Should().Contain("High CPU");
		capturedPrompt.Should().NotContain("{{fetch-active-incidents.output}}");
	}

	#endregion

	#region MCP Server Failure Detection

	[Fact]
	public async Task ExecuteAsync_RequiredMcpServerFailed_ReturnsFailedResult()
	{
		// Arrange - Mock agent that emits an MCP server failure event
		var mcpFailedEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Failed", Error: "Connection timeout")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "No IcM MCP tools are available." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("No IcM MCP tools are available.", mcpFailedEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("acknowledge-incidents");
		step.Mcps = [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("icm");
		result.ErrorMessage.Should().Contain("failed to start");
	}

	[Fact]
	public async Task ExecuteAsync_RequiredMcpServerFailed_ReportsError()
	{
		// Arrange
		var mcpFailedEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Failed", Error: "Process exited")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "No tools available." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("No tools available.", mcpFailedEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportStepError("test-step", Arg.Is<string>(s => s.Contains("icm") && s.Contains("failed to start")));
	}

	[Fact]
	public async Task ExecuteAsync_NonRequiredMcpServerFailed_SucceedsNormally()
	{
		// Arrange - "graph" MCP failed but step only requires "icm" which connected
		var mcpEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Connected"),
					new("graph", "Failed", Error: "Connection refused")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Acknowledged incidents." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Acknowledged incidents.", mcpEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - Step should succeed because its required MCP ("icm") connected
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Acknowledged incidents.");
	}

	[Fact]
	public async Task ExecuteAsync_NoMcpServersConfigured_IgnoresFailedServers()
	{
		// Arrange - MCP servers failed but step doesn't require any
		var mcpEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("graph", "Failed", Error: "Connection refused")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Success." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Success.", mcpEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		// No MCPs configured on this step

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - Step should succeed because it doesn't require any MCPs
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_RequiredMcpServerFailed_IncludesTraceInResult()
	{
		// Arrange
		var mcpFailedEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Failed", Error: "Server crashed")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "No tools." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("No tools.", mcpFailedEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - Trace should be included even on MCP failure
		result.Trace.Should().NotBeNull();
		result.Trace!.SystemPrompt.Should().NotBeNull();
	}

	[Fact]
	public async Task ExecuteAsync_MultipleMcpServersAllFailed_ErrorListsAllServers()
	{
		// Arrange
		var mcpFailedEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Failed", Error: "Timeout"),
					new("graph", "Failed", Error: "Connection refused")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "No tools." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("No tools.", mcpFailedEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps =
		[
			new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] },
			new LocalMcp { Name = "graph", Type = McpType.Local, Command = "dnx", Arguments = ["Graph.Mcp"] }
		];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("icm");
		result.ErrorMessage.Should().Contain("graph");
	}

	#endregion

	#region Skill Directories

	[Fact]
	public async Task ExecuteAsync_WithSkillDirectories_PassesSkillDirectoriesToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("skill-step");
		step.SkillDirectories = ["./skills/coding", "/absolute/skills/devops"];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SkillDirectories.Should().HaveCount(2);
		agentBuilder.CapturedConfig.SkillDirectories.Should().Contain("./skills/coding");
		agentBuilder.CapturedConfig.SkillDirectories.Should().Contain("/absolute/skills/devops");
	}

	[Fact]
	public async Task ExecuteAsync_WithSkillDirectories_ResolvesTemplateVariables()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("skill-step");
		step.SkillDirectories = ["{{vars.skillsDir}}", "./relative/skills"];

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			Variables = new Dictionary<string, string>
			{
				["skillsDir"] = @"P:\Github\OrcStra-Uruk\Skills"
			}
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Template variable should be resolved, relative path should remain as-is
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SkillDirectories.Should().HaveCount(2);
		agentBuilder.CapturedConfig.SkillDirectories[0].Should().Be(@"P:\Github\OrcStra-Uruk\Skills");
		agentBuilder.CapturedConfig.SkillDirectories[1].Should().Be("./relative/skills");
	}

	[Fact]
	public async Task ExecuteAsync_WithoutSkillDirectories_PassesEmptyArray()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("no-skills-step");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SkillDirectories.Should().BeEmpty();
	}

	#endregion

	#region Template Resolution in Model

	[Fact]
	public async Task ExecuteAsync_WithModelTemplateVariable_ResolvesBeforePassingToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("model-var-step", model: "{{vars.defaultModel}}");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			Variables = new Dictionary<string, string>
			{
				["defaultModel"] = "claude-opus-4.5"
			}
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Model should be resolved from the variable, not the literal template string
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.Model.Should().Be("claude-opus-4.5");
	}

	[Fact]
	public async Task ExecuteAsync_WithModelParameterTemplate_ResolvesBeforePassingToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("model-param-step", model: "{{param.model}}");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>
			{
				["model"] = "gpt-4o"
			},
			OrchestrationInfo = s_defaultInfo,
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.Model.Should().Be("gpt-4o");
	}

	[Fact]
	public async Task ExecuteAsync_WithLiteralModel_PassesModelUnchanged()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("literal-model-step", model: "claude-opus-4.5");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.Model.Should().Be("claude-opus-4.5");
	}

	#endregion

	#region Template Resolution in SystemPrompt

	[Fact]
	public async Task ExecuteAsync_WithSystemPromptTemplateVariable_ResolvesBeforePassingToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("sysprompt-step",
			systemPrompt: "You are reviewing code for {{vars.project}}.");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			Variables = new Dictionary<string, string>
			{
				["project"] = "Orchestra"
			}
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SystemPrompt.Should().Be("You are reviewing code for Orchestra.");
	}

	[Fact]
	public async Task ExecuteAsync_WithSystemPromptStepOutputTemplate_ResolvesBeforePassingToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("sysprompt-step",
			dependsOn: ["context-step"],
			systemPrompt: "You are a reviewer. Context: {{context-step.output}}");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
		};
		context.AddResult("context-step", ExecutionResult.Succeeded("important context data"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SystemPrompt.Should().Be("You are a reviewer. Context: important context data");
	}

	#endregion

	#region IMcpResolver Integration

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_CallsResolveOnStepMcps()
	{
		// Arrange
		var globalMcp = new LocalMcp { Name = "global-server", Type = McpType.Local, Command = "test", Arguments = [] };
		var proxyMcp = new RemoteMcp { Name = "orchestra-mcp-proxy", Type = McpType.Remote, Endpoint = "http://localhost:9999/mcp", Headers = [] };

		var resolver = Substitute.For<IMcpResolver>();
		resolver.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>()).Returns(new Mcp[] { proxyMcp });

		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [globalMcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert — resolver was called
		resolver.Received(1).Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>());

		// Assert — the resolved proxy MCP was passed to the agent builder, not the original
		agentBuilder.CapturedMcps.Should().HaveCount(1);
		agentBuilder.CapturedMcps[0].Should().BeOfType<RemoteMcp>();
		agentBuilder.CapturedMcps[0].Name.Should().Be("orchestra-mcp-proxy");
		((RemoteMcp)agentBuilder.CapturedMcps[0]).Endpoint.Should().Be("http://localhost:9999/mcp");
	}

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_GlobalMcpsReplacedInlineMcpsPreserved()
	{
		// Arrange
		var globalMcp = new LocalMcp { Name = "global", Type = McpType.Local, Command = "cmd", Arguments = [] };
		var inlineMcp = new LocalMcp { Name = "inline", Type = McpType.Local, Command = "inline-cmd", Arguments = [] };
		var proxyMcp = new RemoteMcp { Name = "orchestra-mcp-proxy", Type = McpType.Remote, Endpoint = "http://localhost:8888/mcp", Headers = [] };

		var resolver = Substitute.For<IMcpResolver>();
		// Resolver should return inline + proxy (globals collapsed, inline preserved)
		resolver.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>()).Returns(callInfo =>
		{
			// Simulate McpManager behavior: remove global, add proxy, keep inline
			return new Mcp[] { inlineMcp, proxyMcp };
		});

		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [globalMcp, inlineMcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert — two MCPs: inline preserved + proxy replacing global
		agentBuilder.CapturedMcps.Should().HaveCount(2);
		agentBuilder.CapturedMcps[0].Name.Should().Be("inline");
		agentBuilder.CapturedMcps[0].Should().BeOfType<LocalMcp>();
		agentBuilder.CapturedMcps[1].Name.Should().Be("orchestra-mcp-proxy");
		agentBuilder.CapturedMcps[1].Should().BeOfType<RemoteMcp>();
	}

	[Fact]
	public async Task ExecuteAsync_WithoutMcpResolver_McpsPassedUnchanged()
	{
		// Arrange — no resolver injected (null)
		var mcp = new LocalMcp { Name = "my-server", Type = McpType.Local, Command = "cmd", Arguments = [] };

		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger); // no mcpResolver

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [mcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert — MCP passed through unchanged (no resolver to replace it)
		agentBuilder.CapturedMcps.Should().HaveCount(1);
		agentBuilder.CapturedMcps[0].Name.Should().Be("my-server");
		agentBuilder.CapturedMcps[0].Should().BeOfType<LocalMcp>();
	}

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_NoMcpsOnStep_ResolverNotCalled()
	{
		// Arrange — step has no MCPs, so resolver should still be called but with empty array
		var resolver = Substitute.For<IMcpResolver>();
		resolver.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>()).Returns(callInfo => callInfo.ArgAt<Mcp[]>(0));

		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		// step.Mcps is default empty
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert — resolver still called (with empty array), MCPs are empty
		resolver.Received(1).Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>());
		agentBuilder.CapturedMcps.Should().BeEmpty();
	}

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_MultipleGlobalMcps_CollapsedIntoSingleProxy()
	{
		// Arrange — simulates what McpManager.Resolve does: multiple globals → one proxy
		var global1 = new LocalMcp { Name = "azdo", Type = McpType.Local, Command = "azdo-mcp", Arguments = [] };
		var global2 = new LocalMcp { Name = "icm", Type = McpType.Local, Command = "icm-mcp", Arguments = [] };
		var proxyMcp = new RemoteMcp { Name = "orchestra-mcp-proxy", Type = McpType.Remote, Endpoint = "http://localhost:7777/mcp", Headers = [] };

		var resolver = Substitute.For<IMcpResolver>();
		resolver.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>()).Returns(new Mcp[] { proxyMcp });

		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [global1, global2];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert — two globals collapsed into one proxy
		agentBuilder.CapturedMcps.Should().HaveCount(1);
		agentBuilder.CapturedMcps[0].Name.Should().Be("orchestra-mcp-proxy");
		((RemoteMcp)agentBuilder.CapturedMcps[0]).Endpoint.Should().Be("http://localhost:7777/mcp");
	}

	[Fact]
	public async Task ExecuteAsync_WithMcpResolver_ForwardsParentAnnotationFromContext()
	{
		// Arrange — captures the ParentExecutionAnnotation that PromptExecutor passes to the
		// resolver. This is the handoff that lets DataPlaneTools.InvokeOrchestration auto-populate
		// parentExecutionId on nested invocations.
		ParentExecutionAnnotation? captured = null;
		var resolver = Substitute.For<IMcpResolver>();
		resolver
			.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>())
			.Returns(callInfo =>
			{
				captured = callInfo.ArgAt<ParentExecutionAnnotation?>(1);
				return callInfo.ArgAt<Mcp[]>(0);
			});

		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("calling-step");
		step.Mcps = [new RemoteMcp { Name = "orchestra", Type = McpType.Remote, Endpoint = "http://localhost/mcp/data", Headers = [] }];
		var info = new OrchestrationInfo("parent-orch", "1.0.0", "parent-run-id-xyz", DateTimeOffset.UtcNow);
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = info };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		captured.Should().NotBeNull("PromptExecutor must hand the calling step's identity to the resolver");
		captured!.ExecutionId.Should().Be("parent-run-id-xyz");
		captured.OrchestrationName.Should().Be("parent-orch");
		captured.StepName.Should().Be("calling-step");
	}

	[Fact]
	public async Task ExecuteAsync_McpResolverReportsZeroTools_FailsFastBeforeLlm()
	{
		// Arrange — simulates the proxy-deferred-auth race: the MCP server is reachable
		// (Resolve succeeds) but the probe finds tools/list returns 0. The executor must
		// fail-fast BEFORE invoking the LLM so we don't waste tokens on a step that
		// can't possibly succeed.
		var calendarMcp = new LocalMcp { Name = "calendar", Type = McpType.Local, Command = "fake", Arguments = [] };
		var resolver = Substitute.For<IMcpResolver>();
		resolver
			.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>())
			.Returns(callInfo => callInfo.ArgAt<Mcp[]>(0));
		resolver
			.GetGlobalMcpToolCountsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
			.Returns(Task.FromResult<IReadOnlyDictionary<string, int?>>(
				new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["calendar"] = 0 }));

		var agentBuilder = new MockAgentBuilder().WithResponse("should not be reached");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("list-events");
		step.Mcps = [calendarMcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — step failed, agent was never invoked, error category is McpFailure
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.McpFailure);
		result.ErrorMessage.Should().Contain("calendar");
		result.ErrorMessage.Should().Contain("0 tools");
		agentBuilder.CapturedMcps.Should().BeEmpty("the LLM must NOT be invoked when a required MCP has 0 tools");
	}

	[Fact]
	public async Task ExecuteAsync_McpResolverReportsZeroToolsForOneOfMany_FailsFast()
	{
		// Arrange — multiple required MCPs, one of them has 0 tools.
		var calendarMcp = new LocalMcp { Name = "calendar", Type = McpType.Local, Command = "fake", Arguments = [] };
		var mailMcp = new LocalMcp { Name = "mail", Type = McpType.Local, Command = "fake", Arguments = [] };
		var resolver = Substitute.For<IMcpResolver>();
		resolver
			.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>())
			.Returns(callInfo => callInfo.ArgAt<Mcp[]>(0));
		resolver
			.GetGlobalMcpToolCountsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
			.Returns(Task.FromResult<IReadOnlyDictionary<string, int?>>(
				new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase)
				{
					["calendar"] = 0,
					["mail"] = 12,
				}));

		var agentBuilder = new MockAgentBuilder().WithResponse("should not be reached");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("multi-mcp-step");
		step.Mcps = [calendarMcp, mailMcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — calendar is named; mail is not because it had non-zero tools
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("calendar");
		result.ErrorMessage.Should().NotContain("mail");
		agentBuilder.CapturedMcps.Should().BeEmpty();
	}

	[Fact]
	public async Task ExecuteAsync_McpResolverReportsNullToolCount_ProceedsToLlm()
	{
		// Arrange — probe couldn't determine a count (e.g. proxy unreachable, race, etc.).
		// The pre-LLM fast-fail must NOT trigger; the post-LLM SDK-status check remains
		// the safety net. The LLM should be invoked normally.
		var inlineMcp = new LocalMcp { Name = "ad-hoc-inline", Type = McpType.Local, Command = "fake", Arguments = [] };
		var resolver = Substitute.For<IMcpResolver>();
		resolver
			.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>())
			.Returns(callInfo => callInfo.ArgAt<Mcp[]>(0));
		resolver
			.GetGlobalMcpToolCountsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
			.Returns(Task.FromResult<IReadOnlyDictionary<string, int?>>(
				new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["ad-hoc-inline"] = null }));

		var agentBuilder = new MockAgentBuilder().WithResponse("Hello from the LLM");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("test");
		step.Mcps = [inlineMcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — unknown count must NOT fail-fast; LLM ran and step succeeded
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		agentBuilder.CapturedMcps.Should().HaveCount(1);
	}

	[Fact]
	public async Task ExecuteAsync_McpResolverReportsPositiveToolCount_StepSucceedsNormally()
	{
		// Arrange — happy path: probe finds tools, step proceeds and succeeds.
		var calendarMcp = new LocalMcp { Name = "calendar", Type = McpType.Local, Command = "fake", Arguments = [] };
		var resolver = Substitute.For<IMcpResolver>();
		resolver
			.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>())
			.Returns(callInfo => callInfo.ArgAt<Mcp[]>(0));
		resolver
			.GetGlobalMcpToolCountsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
			.Returns(Task.FromResult<IReadOnlyDictionary<string, int?>>(
				new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["calendar"] = 5 }));

		var agentBuilder = new MockAgentBuilder().WithResponse("All five tools available");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("test");
		step.Mcps = [calendarMcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("All five tools available");
		agentBuilder.CapturedMcps.Should().HaveCount(1);
	}

	[Fact]
	public async Task ExecuteAsync_McpResolverProbeThrows_DoesNotBlockStep()
	{
		// Arrange — a probe-side exception must NOT abort the step; it should log and
		// fall through to the post-LLM SDK-status check as the safety net.
		var calendarMcp = new LocalMcp { Name = "calendar", Type = McpType.Local, Command = "fake", Arguments = [] };
		var resolver = Substitute.For<IMcpResolver>();
		resolver
			.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>())
			.Returns(callInfo => callInfo.ArgAt<Mcp[]>(0));
		resolver
			.GetGlobalMcpToolCountsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
			.Returns<Task<IReadOnlyDictionary<string, int?>>>(_ => throw new InvalidOperationException("probe boom"));

		var agentBuilder = new MockAgentBuilder().WithResponse("LLM ran despite probe failure");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("test");
		step.Mcps = [calendarMcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — step proceeded; the probe failure was swallowed
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		agentBuilder.CapturedMcps.Should().HaveCount(1);
	}

	[Fact]
	public async Task ExecuteAsync_SdkReportsConnectedAndProbeReportsZeroTools_FailsPostLlm()
	{
		// Arrange — the post-LLM safety net. The pre-LLM probe wasn't able to determine
		// (returns null for this test path) but the SDK fires McpServersLoaded with
		// status=Connected while an `ApplyMcpToolCounts({calendar:0})` (simulated via
		// the AgentEvent path that the SDK would have triggered) shows 0 tools. Here we
		// simulate the SDK side via the mid-session McpServersLoaded event AND have the
		// resolver supply a probe count of 0 — same end-state, exercising the post-LLM
		// `GetMcpServersWithoutTools` check that's intentionally redundant with the
		// pre-LLM fast-fail.
		var calendarMcp = new LocalMcp { Name = "calendar", Type = McpType.Local, Command = "fake", Arguments = [] };
		var resolver = Substitute.For<IMcpResolver>();
		resolver
			.Resolve(Arg.Any<Mcp[]>(), Arg.Any<ParentExecutionAnnotation?>())
			.Returns(callInfo => callInfo.ArgAt<Mcp[]>(0));
		// Probe says 0 → pre-LLM fast-fail engages. (Post-LLM is the safety net for
		// MCPs the probe didn't / couldn't report on, exercised by other tests.)
		resolver
			.GetGlobalMcpToolCountsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
			.Returns(Task.FromResult<IReadOnlyDictionary<string, int?>>(
				new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase) { ["calendar"] = 0 }));

		var agentBuilder = new MockAgentBuilder().WithResponse("hallucinated content");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, mcpResolver: resolver);

		var step = TestOrchestrations.CreatePromptStep("test");
		step.Mcps = [calendarMcp];
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.McpFailure);
		result.ErrorMessage.Should().Contain("0 tools");
		// Trace should also surface the "Unknown" status entry from the probe-only
		// recompute path (no SDK status was actually fired in this short-circuit case).
		result.Trace.Should().NotBeNull();
		result.Trace!.McpServers.Should().Contain(s => s.Contains("calendar") && s.Contains("tools: 0"));
	}

	#endregion
}
