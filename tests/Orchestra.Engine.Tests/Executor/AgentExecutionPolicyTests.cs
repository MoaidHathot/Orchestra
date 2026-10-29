using FluentAssertions;

namespace Orchestra.Engine.Tests.Executor;

public class AgentExecutionPolicyTests
{
	[Fact]
	public void Autopilot_UsesExistingBudgetToCorrectInvalidJsonWithoutRepeatingWork()
	{
		var schema = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""{"type":"object","required":["count"],"properties":{"count":{"type":"integer"}}}""");
		var continuation = new AgentContinuation(new() { Mode = AgentExecutionMode.Autopilot, MaxContinues = 1 }, schema);
		continuation.TryComplete("Verified.\n{\"count\":42}\n" + AgentContinuation.CompletionMarker, out _).Should().BeFalse();
		continuation.ContinuesUsed.Should().Be(1);
		continuation.NextPrompt.Should().Contain("Correct only the response format").And.Contain("do not repeat file changes");
		continuation.TryComplete("{\"count\":42}\n" + AgentContinuation.CompletionMarker, out var output).Should().BeTrue();
		output.Should().Be("{\"count\":42}");
		Action exceed = () => continuation.TryComplete("Still invalid\n" + AgentContinuation.CompletionMarker, out _);
		exceed.Should().Throw<InvalidOperationException>().WithMessage("*maxContinues (1)*");
	}

	[Fact]
	public void Continuation_OnlyStripsTerminalRootMarker()
	{
		var continuation = new AgentContinuation(new() { Mode = AgentExecutionMode.Autopilot, MaxContinues = 1 });
		continuation.TryComplete("Need more work " + AgentContinuation.CompletionMarker + " later", out _).Should().BeFalse();
		continuation.TryComplete("{\"count\":42}\n" + AgentContinuation.CompletionMarker, out var content).Should().BeTrue();
		content.Should().Be("{\"count\":42}");
	}

	[Fact]
	public void Continuation_ExhaustionIsFailure_StandardNeedsNoMarker()
	{
		var continuation = new AgentContinuation(new() { Mode = AgentExecutionMode.Autopilot, MaxContinues = 0 });
		Action run = () => continuation.TryComplete("I can do that later", out _);
		run.Should().Throw<InvalidOperationException>().WithMessage("*maxContinues (0)*");
		new AgentContinuation(null).TryComplete("ordinary response", out var content).Should().BeTrue();
		content.Should().Be("ordinary response");
	}

	[Fact]
	public void Parser_PreservesPolicy_AndRejectsNegativeLimits()
	{
		const string json = """{"name":"policy","description":"test","steps":[{"name":"task","type":"Prompt","systemPrompt":"","userPrompt":"go","executionPolicy":{"mode":"autopilot","maxContinues":2}}]}""";
		var step = (PromptOrchestrationStep)OrchestrationParser.ParseOrchestration(json, []).Steps.Single();
		step.ExecutionPolicy.Should().Be(new AgentExecutionPolicy { Mode = AgentExecutionMode.Autopilot, MaxContinues = 2 });
		Action invalid = () => OrchestrationParser.ParseOrchestration(json.Replace("\"maxContinues\":2", "\"maxContinues\":-1"), []);
		invalid.Should().Throw<Exception>();
	}
}
