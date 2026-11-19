using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

[Trait("Category", "E2E")]
public sealed class DecisionStepE2ETests
{
	[Fact]
	public async Task TicketExample_BatchesThreeQuestions_AndRoutesUsingTheReturnedValues()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "examples", "copilot-decision-triage.yaml");
		await using var builder = new CopilotAgentBuilder();
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder,
			NullOrchestrationReporter.Instance, NullLoggerFactory.Instance);
		using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		var result = await executor.ExecuteAsync(OrchestrationParser.ParseOrchestrationFile(path, []), cancellationToken: deadline.Token);
		var triage = result.StepResults["triage"];
		triage.Status.Should().Be(ExecutionStatus.Succeeded, triage.ErrorMessage);
		var payload = JsonSerializer.Deserialize<JsonElement>(triage.Content);
		triage.SelectedModel.Should().Be("microsoft-decision-1");
		triage.ActualModel.Should().Be(payload.TryGetProperty("upstreamModel", out var upstream) ? upstream.GetString() : triage.SelectedModel);
		triage.Trace!.ActualProvider.Should().Be("copilot");
		var answers = payload.GetProperty("answers");
		answers.GetProperty("team").GetProperty("value").GetString().Should().Be("billing");
		answers.GetProperty("refund").GetProperty("value").GetBoolean().Should().BeTrue();
		var severity = answers.GetProperty("severity");
		severity.GetProperty("value").GetDouble().Should().BeApproximately(50 * severity.GetProperty("score").GetDouble(), 0.000001);
		result.StepResults["billing-refund"].Content.Should().Contain(severity.GetProperty("level").GetString()!);
		result.StepResults["general-review"].SkipKind.Should().Be(SkipKind.Condition);
		result.IsIncomplete.Should().BeFalse();
		result.Results["summary"].Content.Should().Be(result.StepResults["billing-refund"].Content);
	}

	[Fact]
	public async Task ExplicitUnavailableModel_SurfacesTypedError_AndTheWorkerAcceptsTheNextJsonRequest()
	{
		await using var builder = new CopilotAgentBuilder(poolOptions: new CopilotAgentPoolOptions { DefaultMaxInstancesPerRun = 1 });
		using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		await using var scope = await builder.CreateRunScopeAsync(cancellationToken: deadline.Token);
		var request = new DecisionRequest("unavailable-decision-test-model", JsonSerializer.SerializeToElement(new { fruit = "apple" }),
			[new BooleanQuestion("apple", "Is the fruit an apple?")], TimeoutSeconds: 15);
		var unavailable = () => builder.AskAsync(request, deadline.Token);
		await unavailable.Should().ThrowAsync<InvalidOperationException>().WithMessage("*noModel*");
		var answer = await builder.AskAsync(request with { Model = DecisionOrchestrationStep.DefaultModel }, deadline.Token);
		answer.Answers["apple"].Should().BeOfType<BooleanAnswer>().Which.Value.Should().BeTrue();
	}
}
