#pragma warning disable GHCP001 // UIElicitationResponseAction is an evaluation-only SDK API.
using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Xunit;
using UserInputResponse = Orchestra.Engine.UserInputResponse;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests the opt-in human-in-the-loop wiring: when <c>humanInput</c> is enabled the agent
/// registers SDK elicitation / exit-plan-mode handlers that route to Orchestra's pending-input
/// waiter; when disabled the handlers stay null (autonomous default unchanged).
/// </summary>
public class CopilotAgentHumanInputTests
{
	private static CopilotAgent CreateAgent(bool humanInput, EngineToolContext? engineToolContext)
	{
		return new CopilotAgent(
			clientPool: new FixedCopilotClientPool(new CopilotSdkClientAdapter(new CopilotClient(), ownsClient: false)),
			model: "test-model",
			systemPrompt: null,
			mcps: [],
			subagents: [],
			reasoningLevel: null,
			systemPromptMode: null,
			systemPromptSections: null,
			reporter: NullOrchestrationReporter.Instance,
			engineTools: [],
			engineToolContext: engineToolContext,
			skillDirectories: [],
			infiniteSessionConfig: null,
			attachments: [],
			swapOptions: null,
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>(),
			loggerFactory: null,
			excludedTools: null,
			reasoningSummary: null,
			contextTier: null,
			workingDirectory: null,
			gitHubToken: null,
			humanInput: humanInput);
	}

	private static EngineToolContext Context(IHumanInputWaiter waiter) => new()
	{
		OrchestrationName = "orch",
		RunId = "run-1",
		StepName = "step-1",
		HumanInputWaiter = waiter,
	};

	[Fact]
	public void BuildSessionConfig_HumanInputDisabled_LeavesHandlersNull()
	{
		var config = CreateAgent(humanInput: false, engineToolContext: Context(new StubWaiter())).BuildSessionConfig();

		config.OnElicitationRequest.Should().BeNull();
		config.OnExitPlanModeRequest.Should().BeNull();
	}

	[Fact]
	public void BuildSessionConfig_HumanInputEnabledButNoContext_LeavesHandlersNull()
	{
		var config = CreateAgent(humanInput: true, engineToolContext: null).BuildSessionConfig();

		config.OnElicitationRequest.Should().BeNull();
		config.OnExitPlanModeRequest.Should().BeNull();
	}

	[Fact]
	public async Task Elicitation_RoutesOperatorReply_ToAccept()
	{
		var waiter = new StubWaiter();
		var config = CreateAgent(humanInput: true, engineToolContext: Context(waiter)).BuildSessionConfig();
		config.OnElicitationRequest.Should().NotBeNull();

		var resultTask = config.OnElicitationRequest!(new ElicitationContext { Message = "What tone?" });
		await waiter.WaitForRegistration();
		waiter.Complete(new UserInputResponse { Reply = "friendly", RespondedAt = DateTimeOffset.UtcNow });

		var result = await resultTask;
		result.Action.Value.Should().Be(GitHub.Copilot.Rpc.UIElicitationResponseAction.Accept.Value);
		result.Content.Should().ContainKey("response");
		result.Content["response"].ToString().Should().Be("friendly");
	}

	[Fact]
	public async Task ExitPlanMode_Approve_SetsApprovedTrue()
	{
		var waiter = new StubWaiter();
		var config = CreateAgent(humanInput: true, engineToolContext: Context(waiter)).BuildSessionConfig();
		config.OnExitPlanModeRequest.Should().NotBeNull();

		var resultTask = config.OnExitPlanModeRequest!(
			new ExitPlanModeRequest { Summary = "Plan", PlanContent = "Steps" },
			new ExitPlanModeInvocation { SessionId = "s" });
		await waiter.WaitForRegistration();
		waiter.Complete(new UserInputResponse { Choice = "approve", RespondedAt = DateTimeOffset.UtcNow });

		var result = await resultTask;
		result.Approved.Should().BeTrue();
	}

	[Fact]
	public async Task ExitPlanMode_Feedback_KeepsPlanningWithFeedback()
	{
		var waiter = new StubWaiter();
		var config = CreateAgent(humanInput: true, engineToolContext: Context(waiter)).BuildSessionConfig();

		var resultTask = config.OnExitPlanModeRequest!(
			new ExitPlanModeRequest { Summary = "Plan" },
			new ExitPlanModeInvocation { SessionId = "s" });
		await waiter.WaitForRegistration();
		waiter.Complete(new UserInputResponse { Reply = "please add error handling", RespondedAt = DateTimeOffset.UtcNow });

		var result = await resultTask;
		result.Approved.Should().BeFalse();
		result.Feedback.Should().Be("please add error handling");
	}

	private sealed class StubWaiter : IHumanInputWaiter
	{
		private TaskCompletionSource<UserInputResponse>? _pending;
		private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task WaitForRegistration() => _registered.Task;
		public void Complete(UserInputResponse response) => _pending?.TrySetResult(response);

		public Task<UserInputResponse> WaitAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken)
		{
			_pending = new TaskCompletionSource<UserInputResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
			cancellationToken.Register(() => _pending.TrySetCanceled(cancellationToken));
			_registered.TrySetResult();
			return _pending.Task;
		}

		public bool TryComplete(string orchestrationName, string runId, string stepName, UserInputResponse response)
		{
			Complete(response);
			return true;
		}

		public bool TryCancel(string orchestrationName, string runId, string stepName) => false;
		public void BeginWait(string runId, string stepName) { }
		public void EndWait(string runId, string stepName) { }
	}
}
