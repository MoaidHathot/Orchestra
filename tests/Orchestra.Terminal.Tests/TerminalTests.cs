using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Playground.Copilot.Terminal;
using Xunit;

namespace Orchestra.Terminal.Tests;

public class WordWrapTests
{
	[Fact]
	public void ShortText_Returns_SingleLine()
	{
		var result = TerminalUI.WordWrap("hello world", 80);
		result.Should().HaveCount(1);
		result[0].Should().Be("hello world");
	}

	[Fact]
	public void EmptyText_Returns_SingleEmptyLine()
	{
		var result = TerminalUI.WordWrap("", 80);
		result.Should().HaveCount(1);
		result[0].Should().Be("");
	}

	[Fact]
	public void NullText_Returns_SingleEmptyLine()
	{
		var result = TerminalUI.WordWrap(null!, 80);
		result.Should().HaveCount(1);
		result[0].Should().Be("");
	}

	[Fact]
	public void LongText_Wraps_AtWordBoundary()
	{
		var result = TerminalUI.WordWrap("the quick brown fox jumps over the lazy dog", 20);
		result.Should().HaveCountGreaterThan(1);
		result.Should().AllSatisfy(line => line.Length.Should().BeLessThanOrEqualTo(20));
	}

	[Fact]
	public void LongWord_HardBreaks_WhenNoSpaces()
	{
		var result = TerminalUI.WordWrap("aaaaabbbbbcccccdddddeeeee", 10);
		result.Should().HaveCount(3); // 10 + 10 + 5
		result[0].Should().Be("aaaaabbbbb");
	}

	[Fact]
	public void ExactWidth_NoWrap()
	{
		var result = TerminalUI.WordWrap("12345", 5);
		result.Should().HaveCount(1);
		result[0].Should().Be("12345");
	}

	[Fact]
	public void ZeroWidth_DefaultsToSafe()
	{
		// Should not throw, maxWidth <= 0 defaults to 80
		var result = TerminalUI.WordWrap("hello", 0);
		result.Should().NotBeEmpty();
	}
}

public class TerminalOrchestrationReporterTests
{
	[Fact]
	public void GetEvents_Returns_EmptyInitially()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.GetEvents().Should().BeEmpty();
	}

	[Fact]
	public void ReportStepStarted_Adds_Event()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.ReportStepStarted("step1");
		var events = reporter.GetEvents();
		events.Should().HaveCount(1);
		events[0].Type.Should().Be("step-started");
		events[0].Message.Should().Contain("step1");
	}

	[Fact]
	public void ReportStepCompleted_Adds_Event_And_Invokes_Callback()
	{
		var reporter = new TerminalOrchestrationReporter();
		string? completedStep = null;
		reporter.OnStepCompleted = s => completedStep = s;

		var result = new AgentResult { Content = "test content", ActualModel = "gpt-4o", Usage = new AgentUsage { InputTokens = 100, OutputTokens = 50 } };
		reporter.ReportStepCompleted("step1", result);

		reporter.GetEvents().Should().HaveCount(1);
		completedStep.Should().Be("step1");
	}

	[Fact]
	public void ReportStepStarted_Invokes_OnStepStarted_Callback()
	{
		var reporter = new TerminalOrchestrationReporter();
		string? startedStep = null;
		reporter.OnStepStarted = s => startedStep = s;

		reporter.ReportStepStarted("myStep");
		startedStep.Should().Be("myStep");
	}

	[Fact]
	public void Clear_Removes_All_Events()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.ReportStepStarted("step1");
		reporter.ReportStepStarted("step2");
		reporter.GetEvents().Should().HaveCount(2);

		reporter.Clear();
		reporter.GetEvents().Should().BeEmpty();
	}

	[Fact]
	public void Events_Limited_To_100()
	{
		var reporter = new TerminalOrchestrationReporter();
		for (int i = 0; i < 110; i++)
		{
			reporter.ReportStepStarted($"step{i}");
		}
		reporter.GetEvents().Should().HaveCount(100);
	}

	[Fact]
	public void OnUpdate_Fires_On_Event()
	{
		var reporter = new TerminalOrchestrationReporter();
		int updateCount = 0;
		reporter.OnUpdate += () => updateCount++;

		reporter.ReportStepStarted("step1");
		reporter.ReportStepError("step1", "error");

		updateCount.Should().Be(2);
	}

	[Fact]
	public void ReportUsage_Captures_Token_Info()
	{
		var reporter = new TerminalOrchestrationReporter();
		var usage = new AgentUsage { InputTokens = 500, OutputTokens = 200 };
		reporter.ReportUsage("step1", "gpt-4", usage);

		var events = reporter.GetEvents();
		events.Should().HaveCount(1);
		events[0].Type.Should().Be("usage");
		events[0].Message.Should().Contain("500");
		events[0].Message.Should().Contain("200");
	}

	[Fact]
	public void ReportToolExecutionStarted_Includes_McpServer()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.ReportToolExecutionStarted("step1", "myTool", "{}", "myServer");

		var events = reporter.GetEvents();
		events.Should().HaveCount(1);
		events[0].Type.Should().Be("tool-started");
		events[0].Message.Should().Contain("myTool");
		events[0].Message.Should().Contain("myServer");
	}

	[Fact]
	public void ReportToolExecutionCompleted_Success_And_Failure()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.ReportToolExecutionCompleted("step1", "tool1", true, "result", null);
		reporter.ReportToolExecutionCompleted("step1", "tool2", false, null, "error msg");

		var events = reporter.GetEvents();
		events.Should().HaveCount(2);
		events[0].Message.Should().Contain("completed");
		events[1].Message.Should().Contain("failed");
	}

	[Fact]
	public void ReportLoopIteration_Captures_Details()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.ReportLoopIteration("checker", "target", 2, 5);

		var events = reporter.GetEvents();
		events.Should().HaveCount(1);
		events[0].Type.Should().Be("loop-iteration");
		events[0].Message.Should().Contain("2/5");
	}

	[Fact]
	public void ReportStepRetry_Captures_Details()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.ReportStepRetry("step1", 1, 3, "timeout", TimeSpan.FromSeconds(2));

		var events = reporter.GetEvents();
		events.Should().HaveCount(1);
		events[0].Type.Should().Be("step-retry");
		events[0].Message.Should().Contain("1/3");
	}

	[Fact]
	public void ReportSubagent_Events_Captured()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.ReportSubagentStarted("step1", "tc1", "agent1", "Agent One", "desc");
		reporter.ReportSubagentCompleted("step1", "tc1", "agent1", "Agent One");
		reporter.ReportSubagentFailed("step1", "tc2", "agent2", "Agent Two", "err");
		reporter.ReportSubagentDeselected("step1");

		var events = reporter.GetEvents();
		events.Should().HaveCount(4);
		events[0].Type.Should().Be("subagent-started");
		events[1].Type.Should().Be("subagent-completed");
		events[2].Type.Should().Be("subagent-failed");
		events[3].Type.Should().Be("subagent-deselected");
	}
}

public class ReporterEventTests
{
	[Fact]
	public void DefaultTimestamp_IsSet()
	{
		var evt = new ReporterEvent("test", "message");
		evt.Timestamp.Should().BeCloseTo(DateTimeOffset.Now, TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void ExplicitTimestamp_IsPreserved()
	{
		var ts = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var evt = new ReporterEvent("test", "message", ts);
		evt.Timestamp.Should().Be(ts);
	}
}

public class TuiViewEnumTests
{
	[Fact]
	public void EventLog_Exists()
	{
		// Verify the new EventLog view exists
		Enum.IsDefined(typeof(TuiView), TuiView.EventLog).Should().BeTrue();
	}

	[Fact]
	public void All_Views_Are_Defined()
	{
		var values = Enum.GetValues<TuiView>();
		values.Should().HaveCount(8); // Dashboard, Orchestrations, Triggers, History, Active, OrchestrationDetail, ExecutionDetail, EventLog
	}
}
