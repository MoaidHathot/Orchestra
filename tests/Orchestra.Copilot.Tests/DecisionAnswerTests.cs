using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;
using Rpc = GitHub.Copilot.Rpc;

namespace Orchestra.Copilot.Tests;

#pragma warning disable GHCP001
public sealed class DecisionAnswerTests
{
	private static DecisionRequest Request(params DecisionQuestion[] questions) =>
		new("catalog-id", JsonSerializer.SerializeToElement("evidence"), questions);

	[Fact]
	public void TiesThresholdBoundariesAndPartialRefusal_PreserveTheModelEvidence()
	{
		var request = Request(new BooleanQuestion("gate", "Gate?", Threshold: 0.8),
			new ChoiceQuestion("owner", "Who?", [new("one", "First"), new("two", "Second")]),
			new ScoreQuestion("priority", "Priority?", [new("Low", "Later"), new("High", "Now")], new(-10, 10)),
			new BooleanQuestion("other", "Another gate?"));
		var result = CopilotSdkClientAdapter.ReadDecisionResult(request, new Rpc.DecisionsAskResult
		{
			Model = "catalog-id", UpstreamModel = "backend-version", Answers = [
				new Rpc.DecisionAnswerBoolean { Name = "gate", Probability = 0.8 },
				new Rpc.DecisionAnswerChoice { Name = "owner", Value = "one", Confidence = 0, Tied = true,
					Probabilities = [new() { Value = "one", Probability = 0.5 }, new() { Value = "two", Probability = 0.5 }] },
				new Rpc.DecisionAnswerScore { Name = "priority", Level = "Low", Score = 0.25, Confidence = 0.5,
					Probabilities = [new() { Label = "Low", Probability = 0.75 }, new() { Label = "High", Probability = 0.25 }] },
				new Rpc.DecisionAnswerRefused { Name = "other" },
			],
		});
		result.Answers["gate"].Should().BeOfType<BooleanAnswer>().Which.Value.Should().BeTrue();
		var choice = result.Answers["owner"].Should().BeOfType<ChoiceAnswer>().Which;
		choice.Tied.Should().BeTrue();
		choice.Probabilities["two"].Should().Be(0.5);
		var score = result.Answers["priority"].Should().BeOfType<ScoreAnswer>().Which;
		score.Score.Should().Be(0.25);
		score.Value.Should().Be(-5);
		result.Answers["other"].Should().BeOfType<RefusedAnswer>();
		result.Usage.Should().BeNull("usage is optional and cannot be inferred");
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("""[{"kind":"boolean","name":"wrong","probability":0.8}]""")]
	[InlineData("""[{"kind":"boolean","name":"owner","probability":0.8}]""")]
	[InlineData("""[{"kind":"choice","name":"owner","value":"unknown","confidence":1,"probabilities":[],"tied":false}]""")]
	public void MalformedAnswers_DoNotBecomeRoutingDecisions(string wireAnswers)
	{
		var request = Request(new ChoiceQuestion("owner", "Who?", [new("one", "First"), new("two", "Second")]));
		var response = JsonSerializer.Deserialize<Rpc.DecisionsAskResult>($$"""{"model":"catalog-id","answers":{{wireAnswers}}}""")!;
		var act = () => CopilotSdkClientAdapter.ReadDecisionResult(request, response);
		act.Should().Throw<InvalidDataException>();
	}
}
#pragma warning restore GHCP001
