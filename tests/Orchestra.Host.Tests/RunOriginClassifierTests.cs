using FluentAssertions;
using Orchestra.Host.Api;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for <see cref="RunOriginClassifier"/> and <see cref="HistoryFilterParser"/>.
/// These are the building blocks for the sidebar's filter UI; they need to behave
/// identically server-side and client-side, so the rules must be precise.
/// </summary>
public class RunOriginClassifierTests
{
	[Theory]
	[InlineData("manual", RunOriginKind.Manual)]
	[InlineData("MANUAL", RunOriginKind.Manual)]
	[InlineData("Manual", RunOriginKind.Manual)]
	[InlineData("scheduler", RunOriginKind.Scheduler)]
	[InlineData("loop", RunOriginKind.Loop)]
	[InlineData("webhook", RunOriginKind.Webhook)]
	[InlineData("mcp", RunOriginKind.Mcp)]
	[InlineData("retry", RunOriginKind.Retry)]
	[InlineData("resume", RunOriginKind.Resume)]
	public void Classify_ExactMatchTokens_ReturnsKind(string triggeredBy, RunOriginKind expected)
	{
		RunOriginClassifier.Classify(triggeredBy).Should().Be(expected);
	}

	[Theory]
	[InlineData("orchestration:my-orch:abc123")]
	[InlineData("orchestration:abc123")]
	[InlineData("ORCHESTRATION:upper")]
	[InlineData("Orchestration:Mixed")]
	public void Classify_OrchestrationPrefix_ReturnsOrchestration(string triggeredBy)
	{
		RunOriginClassifier.Classify(triggeredBy).Should().Be(RunOriginKind.Orchestration);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("garbage")]
	[InlineData("orchestrate")] // close-but-not "orchestration:" prefix
	public void Classify_UnknownOrEmpty_ReturnsUnknown(string? triggeredBy)
	{
		RunOriginClassifier.Classify(triggeredBy).Should().Be(RunOriginKind.Unknown);
	}

	[Theory]
	[InlineData(RunOriginKind.Manual, "manual")]
	[InlineData(RunOriginKind.Scheduler, "scheduler")]
	[InlineData(RunOriginKind.Loop, "loop")]
	[InlineData(RunOriginKind.Webhook, "webhook")]
	[InlineData(RunOriginKind.Mcp, "mcp")]
	[InlineData(RunOriginKind.Orchestration, "orchestration")]
	[InlineData(RunOriginKind.Retry, "retry")]
	[InlineData(RunOriginKind.Resume, "resume")]
	[InlineData(RunOriginKind.Unknown, "unknown")]
	public void ToWireValue_AllKinds_ProducesStableTokens(RunOriginKind kind, string expected)
	{
		RunOriginClassifier.ToWireValue(kind).Should().Be(expected);
	}

	[Fact]
	public void ParseWireValues_ValidTokens_ReturnsKindSet()
	{
		var result = RunOriginClassifier.ParseWireValues(["manual", "scheduler", "orchestration"]);

		result.Should().BeEquivalentTo(new[]
		{
			RunOriginKind.Manual,
			RunOriginKind.Scheduler,
			RunOriginKind.Orchestration,
		});
	}

	[Fact]
	public void ParseWireValues_DropsUnknownTokensSilently()
	{
		var result = RunOriginClassifier.ParseWireValues(["manual", "garbage", "scheduler", "", "  "]);

		result.Should().BeEquivalentTo(new[]
		{
			RunOriginKind.Manual,
			RunOriginKind.Scheduler,
		});
	}

	[Fact]
	public void ParseWireValues_AllUnknownTokens_ReturnsEmptySet()
	{
		var result = RunOriginClassifier.ParseWireValues(["garbage", "more-garbage"]);

		result.Should().BeEmpty();
	}

	[Fact]
	public void ParseWireValues_IsCaseInsensitive()
	{
		var result = RunOriginClassifier.ParseWireValues(["MANUAL", "Scheduler", "RETRY"]);

		result.Should().BeEquivalentTo(new[]
		{
			RunOriginKind.Manual,
			RunOriginKind.Scheduler,
			RunOriginKind.Retry,
		});
	}

	[Fact]
	public void ClassifyAndToWire_RoundTrip_ProducesSameToken()
	{
		// For every known token, Classify -> ToWireValue must round-trip to the same token.
		string[] tokens = ["manual", "scheduler", "loop", "webhook", "mcp", "retry", "resume"];
		foreach (var token in tokens)
		{
			var kind = RunOriginClassifier.Classify(token);
			RunOriginClassifier.ToWireValue(kind).Should().Be(token, $"because '{token}' should round-trip");
		}
	}
}
