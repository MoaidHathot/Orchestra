using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Verifies the Prompt-step resolution chain for agent settings:
/// step -> orchestration default -> host default (<see cref="HostAgentDefaults"/>) -> none.
/// Covers both <c>reasoningLevel</c> and <c>model</c>, since the host fallbacks for the two
/// travel together.
/// </summary>
public class HostAgentDefaultsResolutionTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = NullOrchestrationReporter.Instance;
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	private static Orchestration Build(
		string? defaultModel,
		ReasoningLevel? defaultReasoningLevel,
		string? stepModel,
		ReasoningLevel? stepReasoningLevel) => new()
	{
		Name = "resolution",
		Description = "reasoning/model resolution chain",
		DefaultModel = defaultModel,
		DefaultReasoningLevel = defaultReasoningLevel,
		Steps =
		[
			new PromptOrchestrationStep
			{
				Name = "step1",
				Type = OrchestrationStepType.Prompt,
				SystemPrompt = "You are a test assistant.",
				UserPrompt = "Hello",
				// Model is declared non-nullable but the parser leaves it null when the YAML omits
				// it; mirror that here so the executor's fallback chain is what gets exercised.
				Model = stepModel!,
				ReasoningLevel = stepReasoningLevel,
			},
		],
	};

	private async Task<AgentBuildConfig> RunAndCaptureAsync(Orchestration orchestration, HostAgentDefaults? hostDefaults)
	{
		var agentBuilder = new MockAgentBuilder().WithResponse("ok");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, hostDefaults: hostDefaults);

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded, result.StepResults.TryGetValue("step1", out var r) ? r.ErrorMessage : null);
		agentBuilder.CapturedConfig.Should().NotBeNull();
		return agentBuilder.CapturedConfig!;
	}

	[Fact]
	public async Task ReasoningLevel_StepValueWins_OverOrchestrationAndHostDefaults()
	{
		var orchestration = Build("gpt-6-astra", ReasoningLevel.High, stepModel: null, stepReasoningLevel: ReasoningLevel.Low);
		var host = new HostAgentDefaults { ReasoningLevel = ReasoningLevel.Max };

		var config = await RunAndCaptureAsync(orchestration, host);

		config.ReasoningLevel.Should().Be(ReasoningLevel.Low);
	}

	[Fact]
	public async Task ReasoningLevel_OrchestrationDefaultWins_OverHostDefault()
	{
		var orchestration = Build("gpt-6-astra", ReasoningLevel.XHigh, stepModel: null, stepReasoningLevel: null);
		var host = new HostAgentDefaults { ReasoningLevel = ReasoningLevel.Max };

		var config = await RunAndCaptureAsync(orchestration, host);

		config.ReasoningLevel.Should().Be(ReasoningLevel.XHigh);
	}

	[Fact]
	public async Task ReasoningLevel_HostDefaultApplies_WhenStepAndOrchestrationAreSilent()
	{
		var orchestration = Build("gpt-6-astra", defaultReasoningLevel: null, stepModel: null, stepReasoningLevel: null);
		var host = new HostAgentDefaults { ReasoningLevel = ReasoningLevel.Max };

		var config = await RunAndCaptureAsync(orchestration, host);

		config.ReasoningLevel.Should().Be(ReasoningLevel.Max);
	}

	[Fact]
	public async Task ReasoningLevel_NoDefaultsAnywhere_LeavesProviderDefault()
	{
		var orchestration = Build("gpt-6-astra", defaultReasoningLevel: null, stepModel: null, stepReasoningLevel: null);

		var config = await RunAndCaptureAsync(orchestration, hostDefaults: null);

		config.ReasoningLevel.Should().BeNull();
	}

	[Fact]
	public async Task Model_StepValueWins_OverOrchestrationAndHostDefaults()
	{
		var orchestration = Build("orchestration-model", null, stepModel: "step-model", stepReasoningLevel: null);
		var host = new HostAgentDefaults { Model = "host-model" };

		var config = await RunAndCaptureAsync(orchestration, host);

		config.Model.Should().Be("step-model");
	}

	[Fact]
	public async Task Model_OrchestrationDefaultWins_OverHostDefault()
	{
		var orchestration = Build("orchestration-model", null, stepModel: null, stepReasoningLevel: null);
		var host = new HostAgentDefaults { Model = "host-model" };

		var config = await RunAndCaptureAsync(orchestration, host);

		config.Model.Should().Be("orchestration-model");
	}

	[Fact]
	public async Task Model_HostDefaultApplies_WhenStepAndOrchestrationAreSilent()
	{
		// Before host defaults were plumbed into the executor this configuration failed with
		// "no 'model' and no 'defaultModel'" even though orchestra.json documented the fallback.
		var orchestration = Build(defaultModel: null, defaultReasoningLevel: null, stepModel: null, stepReasoningLevel: null);
		var host = new HostAgentDefaults { Model = "host-model" };

		var config = await RunAndCaptureAsync(orchestration, host);

		config.Model.Should().Be("host-model");
	}

	[Fact]
	public async Task Model_NoModelAnywhere_FailsTheStepWithActionableMessage()
	{
		var orchestration = Build(defaultModel: null, defaultReasoningLevel: null, stepModel: null, stepReasoningLevel: null);
		var agentBuilder = new MockAgentBuilder().WithResponse("ok");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, hostDefaults: HostAgentDefaults.None);

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["step1"].ErrorMessage.Should().Contain("defaultModel").And.Contain("host");
		agentBuilder.CapturedConfig.Should().BeNull("no agent should be built when no model can be resolved");
	}
}
