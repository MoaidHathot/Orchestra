using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public sealed class DecisionStepTests
{
	private const string Workflow = """
		{"name":"routing","description":"Route JSON evidence","defaultProvider":"opencode","defaultModel":"chat-only","defaultStepTimeoutSeconds":7,
		 "steps":[
		  {"name":"source","type":"Transform","template":"{{param.data}}","parameters":["data"]},
		  {"name":"route","type":"Decision","context":"{{source.output}}","contextFormat":"json","dependsOn":["source"],
		   "questions":[{"name":"approved","kind":"boolean","instructions":"Is it approved?","threshold":0.9}]},
		  {"name":"notify","type":"Transform","dependsOn":["route"],"when":"{{route.output.answers.approved.value}}","template":"approved"}]}
		""";

	private static OrchestrationExecutor ExecutorFor(DecisionBuilder builder) => new(new OrchestrationScheduler(),
		new AgentProviderRegistry(new Dictionary<string, AgentBuilder> { ["copilot"] = builder, ["opencode"] = new MockAgentBuilder() }, "opencode"),
		NullOrchestrationReporter.Instance, NullLoggerFactory.Instance);

	[Theory]
	[InlineData("{\"approved\":false}", JsonValueKind.Object)]
	[InlineData("[{\"approved\":false}]", JsonValueKind.Array)]
	public async Task JsonEvidenceAndChatDefaults_ReachOnlyTheDecisionProvider(string data, JsonValueKind kind)
	{
		var builder = new DecisionBuilder();
		var result = await ExecutorFor(builder).ExecuteAsync(OrchestrationParser.ParseOrchestration(Workflow, []), new() { ["data"] = data });
		result.Status.Should().Be(ExecutionStatus.Succeeded, result.StepResults["route"].ErrorMessage);
		builder.Request!.Model.Should().Be("microsoft-decision-1");
		builder.Request.TimeoutSeconds.Should().Be(7);
		builder.Request.Context.ValueKind.Should().Be(kind);
		builder.Request.Context.GetRawText().Should().Be(data);
		builder.CapturedConfig.Should().BeNull("decisions must not start chat sessions");
		result.StepResults["route"].ActualModel.Should().Be("microsoft-decision-1.1");
		result.StepResults["notify"].SkipKind.Should().Be(SkipKind.Condition, "0.8 is below the authored threshold");
	}

	[Theory]
	[InlineData("[1,2]")]
	[InlineData("not JSON")]
	[InlineData("true")]
	public async Task InvalidJsonEvidence_FailsBeforeInference(string data)
	{
		var builder = new DecisionBuilder();
		var result = await ExecutorFor(builder).ExecuteAsync(OrchestrationParser.ParseOrchestration(Workflow, []), new() { ["data"] = data });
		builder.Request.Should().BeNull();
		result.StepResults["route"].ErrorCategory.Should().Be(StepErrorCategory.ValidationError);
		result.StepResults["notify"].Status.Should().Be(ExecutionStatus.Skipped);
	}

	[Fact]
	public async Task StepDeadline_CancelsPendingInferenceAndIsRecordedAsTimeout()
	{
		var builder = new DecisionBuilder(async (_, token) =>
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
			throw new InvalidOperationException("The call must be cancelled.");
		});
		var definition = new Orchestration
		{
			Name = "timeout", Description = "Deadline while deciding", DefaultStepTimeoutSeconds = 20,
			Steps = [new DecisionOrchestrationStep { Name = "check", Type = OrchestrationStepType.Decision, TimeoutSeconds = 1,
				Context = "Evidence", Questions = [new BooleanQuestion("gate", "Gate?")] }],
		};
		var result = await ExecutorFor(builder).ExecuteAsync(definition).WaitAsync(TimeSpan.FromSeconds(10));
		builder.Request!.TimeoutSeconds.Should().Be(1);
		result.StepResults["check"].ErrorCategory.Should().Be(StepErrorCategory.Timeout);
	}

	[Fact]
	public async Task RestoredDecision_RoutesFromItsCheckpointWithoutNeedingCopilot()
	{
		var definition = OrchestrationParser.ParseOrchestration(Workflow, []);
		var checkpoint = new CheckpointData
		{
			RunId = "restore-decision", OrchestrationName = definition.Name,
			StartedAt = DateTimeOffset.UtcNow, CheckpointedAt = DateTimeOffset.UtcNow,
			Parameters = new() { ["data"] = "{}" },
			CompletedSteps = new()
			{
				["source"] = CheckpointStepResult.FromExecutionResult(ExecutionResult.Succeeded("{}")),
				["route"] = CheckpointStepResult.FromExecutionResult(ExecutionResult.Succeeded("""{"answers":{"approved":{"kind":"boolean","value":true}}}""")),
			},
		};
		var providers = new AgentProviderRegistry(new Dictionary<string, AgentBuilder> { ["opencode"] = new MockAgentBuilder() }, "opencode");
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), providers, NullOrchestrationReporter.Instance, NullLoggerFactory.Instance);
		var result = await executor.ResumeAsync(definition, checkpoint);
		result.StepResults["notify"].Content.Should().Be("approved");
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("""[{"name":"a","kind":"boolean","instructions":"x","threshold":1.1}]""")]
	[InlineData("""[{"name":"a","kind":"boolean","instructions":"x"},{"name":"A","kind":"boolean","instructions":"y"}]""")]
	[InlineData("""[{"name":"a","kind":"choice","instructions":"x","options":[{"value":"x","description":"1"},{"value":"x","description":"2"}]}]""")]
	[InlineData("""[{"name":"a","kind":"score","instructions":"x","levels":[{"label":"low","description":"l"},{"label":"high","description":"h"}],"range":{"min":2,"max":1}}]""")]
	[InlineData("""[{"name":"a","kind":"boolean","instructions":"x","levels":[]}]""")]
	public void InvalidRubric_IsRejectedWhenParsing(string questions)
	{
		var workflow = $$"""{"name":"test","description":"test","steps":[{"type":"Decision","name":"check","context":"x","questions":{{questions}}}]}""";
		var act = () => OrchestrationParser.ParseOrchestration(workflow, []);
		act.Should().Throw<JsonException>().WithMessage("*check*");
	}

	private sealed class DecisionBuilder(Func<DecisionRequest, CancellationToken, Task<DecisionResult>>? handler = null) : MockAgentBuilder, IDecisionProvider
	{
		public DecisionRequest? Request { get; private set; }
		public Task<DecisionResult> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
		{
			Request = request;
			if (handler is not null) return handler(request, cancellationToken);
			var q = (BooleanQuestion)request.Questions.Single();
			return Task.FromResult(new DecisionResult(request.Model, "microsoft-decision-1.1", 10,
				new Dictionary<string, DecisionAnswer> { [q.Name] = new BooleanAnswer(0.8, q.Threshold) }, null));
		}
	}
}
