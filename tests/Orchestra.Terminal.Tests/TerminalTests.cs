using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
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
	public void McpServers_Exists()
	{
		Enum.IsDefined(typeof(TuiView), TuiView.McpServers).Should().BeTrue();
	}

	[Fact]
	public void McpDetail_Exists()
	{
		Enum.IsDefined(typeof(TuiView), TuiView.McpDetail).Should().BeTrue();
	}

	[Fact]
	public void All_Views_Are_Defined()
	{
		var values = Enum.GetValues<TuiView>();
		values.Should().HaveCount(16); // Dashboard, Orchestrations, Triggers, History, Active, OrchestrationDetail, ExecutionDetail, EventLog, McpServers, McpDetail, VersionHistory, VersionDiff, DagView, RawJsonView, Checkpoints, TriggerCreate
	}

	[Fact]
	public void VersionHistory_Exists()
	{
		Enum.IsDefined(typeof(TuiView), TuiView.VersionHistory).Should().BeTrue();
	}

	[Fact]
	public void VersionDiff_Exists()
	{
		Enum.IsDefined(typeof(TuiView), TuiView.VersionDiff).Should().BeTrue();
	}
}

public class ExecutionDetailTabEnumTests
{
	[Fact]
	public void Stream_Tab_Exists()
	{
		Enum.IsDefined(typeof(ExecutionDetailTab), ExecutionDetailTab.Stream).Should().BeTrue();
	}

	[Fact]
	public void All_Tabs_Are_Defined()
	{
		var values = Enum.GetValues<ExecutionDetailTab>();
		values.Should().HaveCount(4); // Summary, Steps, Output, Stream
	}
}

public class StreamingReporterTests
{
	[Fact]
	public void ReportContentDelta_Accumulates_Content()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportContentDelta("step1", "Hello ");
		reporter.ReportContentDelta("step1", "world");

		reporter.GetStreamingContent("step1").Should().Be("Hello world");
	}

	[Fact]
	public void ReportContentDelta_Tracks_Multiple_Steps()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportContentDelta("step1", "content1");
		reporter.ReportContentDelta("step2", "content2");

		reporter.GetStreamingContent("step1").Should().Be("content1");
		reporter.GetStreamingContent("step2").Should().Be("content2");
	}

	[Fact]
	public void ReportContentDelta_Updates_CurrentStreamingStep()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.CurrentStreamingStep.Should().BeNull();

		reporter.ReportContentDelta("step1", "chunk");
		reporter.CurrentStreamingStep.Should().Be("step1");

		reporter.ReportContentDelta("step2", "chunk");
		reporter.CurrentStreamingStep.Should().Be("step2");
	}

	[Fact]
	public void ReportReasoningDelta_Accumulates_Reasoning()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportReasoningDelta("step1", "I think ");
		reporter.ReportReasoningDelta("step1", "this is ");
		reporter.ReportReasoningDelta("step1", "correct.");

		reporter.GetStreamingReasoning("step1").Should().Be("I think this is correct.");
	}

	[Fact]
	public void ReportContentDelta_DoesNot_Add_Events()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportContentDelta("step1", "chunk1");
		reporter.ReportContentDelta("step1", "chunk2");

		// Content deltas should NOT flood the event list
		reporter.GetEvents().Should().BeEmpty();
	}

	[Fact]
	public void ReportReasoningDelta_DoesNot_Add_Events()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportReasoningDelta("step1", "reasoning chunk");

		reporter.GetEvents().Should().BeEmpty();
	}

	[Fact]
	public void ReportContentDelta_Fires_OnStreamingUpdate()
	{
		var reporter = new TerminalOrchestrationReporter();
		int streamingUpdateCount = 0;
		reporter.OnStreamingUpdate += () => streamingUpdateCount++;

		reporter.ReportContentDelta("step1", "chunk");
		reporter.ReportReasoningDelta("step1", "reasoning");

		streamingUpdateCount.Should().Be(2);
	}

	[Fact]
	public void ReportContentDelta_DoesNot_Fire_OnUpdate()
	{
		var reporter = new TerminalOrchestrationReporter();
		int generalUpdateCount = 0;
		reporter.OnUpdate += () => generalUpdateCount++;

		reporter.ReportContentDelta("step1", "chunk");

		// Content deltas should not trigger general OnUpdate
		generalUpdateCount.Should().Be(0);
	}

	[Fact]
	public void ReportStepCompleted_Clears_CurrentStreamingStep()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportContentDelta("step1", "content");
		reporter.CurrentStreamingStep.Should().Be("step1");

		var result = new AgentResult
		{
			Content = "final content",
			ActualModel = "gpt-4o",
			Usage = new AgentUsage { InputTokens = 100, OutputTokens = 50 }
		};
		reporter.ReportStepCompleted("step1", result);

		reporter.CurrentStreamingStep.Should().BeNull();
	}

	[Fact]
	public void ReportStepCompleted_Preserves_CurrentStreamingStep_For_Other_Steps()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportContentDelta("step2", "content for step2");
		reporter.CurrentStreamingStep.Should().Be("step2");

		// Completing a different step should not clear current streaming step
		var result = new AgentResult
		{
			Content = "done",
			ActualModel = "gpt-4o",
			Usage = new AgentUsage { InputTokens = 10, OutputTokens = 5 }
		};
		reporter.ReportStepCompleted("step1", result);

		reporter.CurrentStreamingStep.Should().Be("step2");
	}

	[Fact]
	public void GetStreamingContent_Returns_Null_For_Unknown_Step()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.GetStreamingContent("nonexistent").Should().BeNull();
	}

	[Fact]
	public void GetStreamingReasoning_Returns_Null_For_Unknown_Step()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.GetStreamingReasoning("nonexistent").Should().BeNull();
	}

	[Fact]
	public void GetStreamingStepNames_Returns_Steps_With_Content()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportContentDelta("alpha", "a");
		reporter.ReportContentDelta("beta", "b");
		reporter.ReportContentDelta("gamma", "c");

		var names = reporter.GetStreamingStepNames();
		names.Should().HaveCount(3);
		names.Should().Contain("alpha");
		names.Should().Contain("beta");
		names.Should().Contain("gamma");
	}

	[Fact]
	public void GetStreamingStepNames_Empty_Initially()
	{
		var reporter = new TerminalOrchestrationReporter();
		reporter.GetStreamingStepNames().Should().BeEmpty();
	}

	[Fact]
	public void LastDeltaTime_Is_Updated_On_ContentDelta()
	{
		var reporter = new TerminalOrchestrationReporter();
		var before = DateTime.Now;

		reporter.ReportContentDelta("step1", "chunk");

		reporter.LastDeltaTime.Should().BeOnOrAfter(before);
		reporter.LastDeltaTime.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(1));
	}

	[Fact]
	public void LastDeltaTime_Is_Updated_On_ReasoningDelta()
	{
		var reporter = new TerminalOrchestrationReporter();
		var before = DateTime.Now;

		reporter.ReportReasoningDelta("step1", "thought");

		reporter.LastDeltaTime.Should().BeOnOrAfter(before);
		reporter.LastDeltaTime.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(1));
	}

	[Fact]
	public void Clear_Removes_Streaming_Content_And_Reasoning()
	{
		var reporter = new TerminalOrchestrationReporter();

		reporter.ReportContentDelta("step1", "content");
		reporter.ReportReasoningDelta("step1", "reasoning");
		reporter.GetStreamingContent("step1").Should().NotBeNull();
		reporter.GetStreamingReasoning("step1").Should().NotBeNull();
		reporter.CurrentStreamingStep.Should().Be("step1");

		reporter.Clear();

		reporter.GetStreamingContent("step1").Should().BeNull();
		reporter.GetStreamingReasoning("step1").Should().BeNull();
		reporter.CurrentStreamingStep.Should().BeNull();
		reporter.GetStreamingStepNames().Should().BeEmpty();
	}

	[Fact]
	public void ContentDelta_ThreadSafe_ConcurrentWrites()
	{
		var reporter = new TerminalOrchestrationReporter();
		var tasks = new List<Task>();

		// Simulate concurrent delta writes from multiple threads
		for (int i = 0; i < 10; i++)
		{
			var index = i;
			tasks.Add(Task.Run(() =>
			{
				for (int j = 0; j < 100; j++)
				{
					reporter.ReportContentDelta($"step{index}", $"chunk{j} ");
				}
			}));
		}

		Task.WaitAll(tasks.ToArray());

		// All 10 steps should have content
		reporter.GetStreamingStepNames().Should().HaveCount(10);

		// Each step should have accumulated all chunks
		for (int i = 0; i < 10; i++)
		{
			var content = reporter.GetStreamingContent($"step{i}");
			content.Should().NotBeNull();
			// Each step had 100 chunks, each starting with "chunk" followed by a number and space
			content!.Split("chunk", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(100);
		}
	}

	[Fact]
	public void ReportContentDelta_Large_Content_Accumulation()
	{
		var reporter = new TerminalOrchestrationReporter();

		// Simulate a large streaming response (e.g., 10000 tokens)
		for (int i = 0; i < 10000; i++)
		{
			reporter.ReportContentDelta("step1", "x");
		}

		var content = reporter.GetStreamingContent("step1");
		content.Should().NotBeNull();
		content!.Length.Should().Be(10000);
	}
}

public class McpUsageCollectionTests
{
	private static OrchestrationEntry CreateEntry(
		string id,
		string name,
		Mcp[]? orchestrationMcps = null,
		PromptOrchestrationStep[]? steps = null,
		string? mcpPath = null)
	{
		var allSteps = steps?.Cast<OrchestrationStep>().ToArray() ?? [];
		return new OrchestrationEntry
		{
			Id = id,
			Path = $"/fake/{id}.json",
			McpPath = mcpPath,
			Orchestration = new Orchestration
			{
				Name = name,
				Description = $"Test orchestration {name}",
				Steps = allSteps,
				Mcps = orchestrationMcps ?? []
			},
			RegisteredAt = DateTimeOffset.UtcNow
		};
	}

	private static LocalMcp CreateLocalMcp(string name, string command = "npx", string[]? args = null)
	{
		return new LocalMcp
		{
			Name = name,
			Type = McpType.Local,
			Command = command,
			Arguments = args ?? ["run"]
		};
	}

	private static RemoteMcp CreateRemoteMcp(string name, string endpoint = "https://example.com/mcp")
	{
		return new RemoteMcp
		{
			Name = name,
			Type = McpType.Remote,
			Endpoint = endpoint,
			Headers = new Dictionary<string, string>()
		};
	}

	[Fact]
	public void CollectMcpUsage_Empty_Registry_Returns_Empty()
	{
		var result = TerminalUI.CollectMcpUsage([]);
		result.Should().BeEmpty();
	}

	[Fact]
	public void CollectMcpUsage_Orchestration_Level_Mcps_Are_Collected()
	{
		var mcp = CreateLocalMcp("filesystem");
		var entry = CreateEntry("orch1", "My Orchestration", orchestrationMcps: [mcp]);

		var result = TerminalUI.CollectMcpUsage([entry]);

		result.Should().HaveCount(1);
		result[0].Mcp.Name.Should().Be("filesystem");
		result[0].Mcp.Type.Should().Be(McpType.Local);
		result[0].UsedByOrchestrationIds.Should().Contain("orch1");
		result[0].UsedByOrchestrationNames.Should().Contain("My Orchestration");
	}

	[Fact]
	public void CollectMcpUsage_Step_Level_Mcps_Are_Collected()
	{
		var mcp = CreateRemoteMcp("context7", "https://mcp.context7.com/mcp");
		var step = new PromptOrchestrationStep
		{
			Name = "step1",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			SystemPrompt = "test",
			UserPrompt = "test",
			Model = "claude-opus-4.5",
		};
		step.Mcps = [mcp];
		var entry = CreateEntry("orch1", "Step MCP Test", steps: [step]);

		var result = TerminalUI.CollectMcpUsage([entry]);

		result.Should().HaveCount(1);
		result[0].Mcp.Name.Should().Be("context7");
		result[0].Mcp.Type.Should().Be(McpType.Remote);
		((RemoteMcp)result[0].Mcp).Endpoint.Should().Be("https://mcp.context7.com/mcp");
	}

	[Fact]
	public void CollectMcpUsage_Deduplicates_Same_Mcp_In_Same_Orchestration()
	{
		var mcp1 = CreateLocalMcp("filesystem");
		var mcp2 = CreateLocalMcp("filesystem"); // Same name
		var step = new PromptOrchestrationStep
		{
			Name = "step1",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			SystemPrompt = "test",
			UserPrompt = "test",
			Model = "claude-opus-4.5",
		};
		step.Mcps = [mcp2];
		var entry = CreateEntry("orch1", "Dedup Test", orchestrationMcps: [mcp1], steps: [step]);

		var result = TerminalUI.CollectMcpUsage([entry]);

		result.Should().HaveCount(1);
		result[0].Mcp.Name.Should().Be("filesystem");
		// Should only list the orchestration once even though MCP appears twice
		result[0].UsedByOrchestrationIds.Should().HaveCount(1);
	}

	[Fact]
	public void CollectMcpUsage_Multiple_Orchestrations_Tracking()
	{
		var mcp = CreateLocalMcp("graph");
		var entry1 = CreateEntry("orch1", "First", orchestrationMcps: [mcp]);
		var entry2 = CreateEntry("orch2", "Second", orchestrationMcps: [mcp]);

		var result = TerminalUI.CollectMcpUsage([entry1, entry2]);

		result.Should().HaveCount(1);
		result[0].UsedByOrchestrationIds.Should().HaveCount(2);
		result[0].UsedByOrchestrationIds.Should().Contain("orch1");
		result[0].UsedByOrchestrationIds.Should().Contain("orch2");
		result[0].UsedByOrchestrationNames.Should().Contain("First");
		result[0].UsedByOrchestrationNames.Should().Contain("Second");
	}

	[Fact]
	public void CollectMcpUsage_Multiple_Different_Mcps_Sorted_By_Name()
	{
		var mcpZ = CreateRemoteMcp("zebra-server");
		var mcpA = CreateLocalMcp("alpha-server");
		var entry = CreateEntry("orch1", "Multi MCP", orchestrationMcps: [mcpZ, mcpA]);

		var result = TerminalUI.CollectMcpUsage([entry]);

		result.Should().HaveCount(2);
		result[0].Mcp.Name.Should().Be("alpha-server"); // Sorted alphabetically
		result[1].Mcp.Name.Should().Be("zebra-server");
	}

	[Fact]
	public void CollectMcpUsage_Case_Insensitive_Name_Matching()
	{
		var mcp1 = CreateLocalMcp("FileSystem");
		var mcp2 = CreateLocalMcp("filesystem"); // Same name, different case
		var entry1 = CreateEntry("orch1", "First", orchestrationMcps: [mcp1]);
		var entry2 = CreateEntry("orch2", "Second", orchestrationMcps: [mcp2]);

		var result = TerminalUI.CollectMcpUsage([entry1, entry2]);

		// Should deduplicate case-insensitively
		result.Should().HaveCount(1);
		result[0].UsedByOrchestrationIds.Should().HaveCount(2);
	}

	[Fact]
	public void CollectMcpUsage_LocalMcp_Properties_Preserved()
	{
		var mcp = new LocalMcp
		{
			Name = "graph",
			Type = McpType.Local,
			Command = "dotnet",
			Arguments = ["run", "--project", "src/Graph.csproj"],
			WorkingDirectory = "/app"
		};
		var entry = CreateEntry("orch1", "Local Test", orchestrationMcps: [mcp]);

		var result = TerminalUI.CollectMcpUsage([entry]);

		result.Should().HaveCount(1);
		var local = result[0].Mcp.Should().BeOfType<LocalMcp>().Subject;
		local.Command.Should().Be("dotnet");
		local.Arguments.Should().BeEquivalentTo(["run", "--project", "src/Graph.csproj"]);
		local.WorkingDirectory.Should().Be("/app");
	}

	[Fact]
	public void CollectMcpUsage_RemoteMcp_Properties_Preserved()
	{
		var mcp = new RemoteMcp
		{
			Name = "context7",
			Type = McpType.Remote,
			Endpoint = "https://mcp.context7.com/mcp",
			Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token123" }
		};
		var entry = CreateEntry("orch1", "Remote Test", orchestrationMcps: [mcp]);

		var result = TerminalUI.CollectMcpUsage([entry]);

		result.Should().HaveCount(1);
		var remote = result[0].Mcp.Should().BeOfType<RemoteMcp>().Subject;
		remote.Endpoint.Should().Be("https://mcp.context7.com/mcp");
		remote.Headers.Should().ContainKey("Authorization");
	}

	[Fact]
	public void CollectMcpUsage_ExternalLoader_Called_For_McpPath()
	{
		var externalMcp = CreateLocalMcp("external-server");
		var entry = CreateEntry("orch1", "With External", mcpPath: "/fake/mcp.json");

		var loaderCalled = false;
		var result = TerminalUI.CollectMcpUsage([entry], path =>
		{
			loaderCalled = true;
			path.Should().Be("/fake/mcp.json");
			return [externalMcp];
		});

		loaderCalled.Should().BeTrue();
		result.Should().HaveCount(1);
		result[0].Mcp.Name.Should().Be("external-server");
		result[0].UsedByOrchestrationIds.Should().Contain("orch1");
	}

	[Fact]
	public void CollectMcpUsage_ExternalLoader_Deduplicates_Same_McpPath()
	{
		var externalMcp = CreateLocalMcp("shared-server");
		var entry1 = CreateEntry("orch1", "First", mcpPath: "/fake/mcp.json");
		var entry2 = CreateEntry("orch2", "Second", mcpPath: "/fake/mcp.json");

		var callCount = 0;
		var result = TerminalUI.CollectMcpUsage([entry1, entry2], path =>
		{
			callCount++;
			return [externalMcp];
		});

		// External loader should only be called once for the same path
		callCount.Should().Be(1);
		result.Should().HaveCount(1);
		// But both orchestrations should be listed as users (since they share the path)
		result[0].UsedByOrchestrationIds.Should().Contain("orch1");
	}

	[Fact]
	public void CollectMcpUsage_ExternalLoader_Error_Is_Ignored()
	{
		var entry = CreateEntry("orch1", "Error Test", mcpPath: "/fake/bad.json");

		var result = TerminalUI.CollectMcpUsage([entry], _ => throw new InvalidOperationException("bad file"));

		result.Should().BeEmpty();
	}

	[Fact]
	public void CollectMcpUsage_No_McpPath_Skips_ExternalLoader()
	{
		var entry = CreateEntry("orch1", "No MCP Path");

		var loaderCalled = false;
		var result = TerminalUI.CollectMcpUsage([entry], _ =>
		{
			loaderCalled = true;
			return [];
		});

		loaderCalled.Should().BeFalse();
		result.Should().BeEmpty();
	}

	[Fact]
	public void CollectMcpUsage_Mixed_Orchestration_And_Step_Mcps()
	{
		var orchMcp = CreateLocalMcp("filesystem");
		var stepMcp = CreateRemoteMcp("context7");
		var step = new PromptOrchestrationStep
		{
			Name = "analyze",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			SystemPrompt = "test",
			UserPrompt = "test",
			Model = "claude-opus-4.5",
		};
		step.Mcps = [stepMcp];
		var entry = CreateEntry("orch1", "Mixed", orchestrationMcps: [orchMcp], steps: [step]);

		var result = TerminalUI.CollectMcpUsage([entry]);

		result.Should().HaveCount(2);
		result.Select(r => r.Mcp.Name).Should().BeEquivalentTo(["context7", "filesystem"]);
	}

	[Fact]
	public void McpUsageInfo_Record_Equality()
	{
		var mcp = CreateLocalMcp("test");
		var info1 = new TerminalUI.McpUsageInfo(mcp, ["id1"], ["Name1"]);
		var info2 = new TerminalUI.McpUsageInfo(mcp, ["id1"], ["Name1"]);

		// Records compare by value for value-type/string members,
		// but arrays are reference types, so two different arrays won't be equal
		info1.Mcp.Should().Be(info2.Mcp);
	}
}

public class VersionHistoryTests
{
	[Fact]
	public void ComputeDiff_Identical_Content_Returns_All_Unchanged()
	{
		var json = "{\n  \"name\": \"test\"\n}";
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(json, json);

		diff.Should().NotBeEmpty();
		diff.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Unchanged));
	}

	[Fact]
	public void ComputeDiff_Added_Lines_Are_Marked()
	{
		var oldJson = "{\n  \"name\": \"test\"\n}";
		var newJson = "{\n  \"name\": \"test\",\n  \"version\": \"2.0\"\n}";
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(oldJson, newJson);

		diff.Should().Contain(d => d.Type == DiffLineType.Added);
	}

	[Fact]
	public void ComputeDiff_Removed_Lines_Are_Marked()
	{
		var oldJson = "{\n  \"name\": \"test\",\n  \"extra\": \"value\"\n}";
		var newJson = "{\n  \"name\": \"test\"\n}";
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(oldJson, newJson);

		diff.Should().Contain(d => d.Type == DiffLineType.Removed);
	}

	[Fact]
	public void ComputeDiff_Empty_Strings_Returns_Empty()
	{
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff("", "");

		diff.Should().NotBeNull();
	}

	[Fact]
	public void ComputeDiff_From_Empty_To_Content_Has_Added_Lines()
	{
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff("", "line1\nline2");

		diff.Should().Contain(d => d.Type == DiffLineType.Added);
		// The added lines should contain our new content
		diff.Where(d => d.Type == DiffLineType.Added).Should().Contain(d => d.Content == "line1");
		diff.Where(d => d.Type == DiffLineType.Added).Should().Contain(d => d.Content == "line2");
	}

	[Fact]
	public void ComputeDiff_From_Content_To_Empty_Has_Removed_Lines()
	{
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff("line1\nline2", "");

		diff.Should().Contain(d => d.Type == DiffLineType.Removed);
		// The removed lines should contain our old content
		diff.Where(d => d.Type == DiffLineType.Removed).Should().Contain(d => d.Content == "line1");
		diff.Where(d => d.Type == DiffLineType.Removed).Should().Contain(d => d.Content == "line2");
	}

	[Fact]
	public void ComputeContentHash_Same_Content_Same_Hash()
	{
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash("{\"name\":\"test\"}");
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash("{\"name\":\"test\"}");

		hash1.Should().Be(hash2);
	}

	[Fact]
	public void ComputeContentHash_Different_Content_Different_Hash()
	{
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash("{\"name\":\"test1\"}");
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash("{\"name\":\"test2\"}");

		hash1.Should().NotBe(hash2);
	}

	[Fact]
	public void ComputeContentHash_Returns_Lowercase_Hex()
	{
		var hash = FileSystemOrchestrationVersionStore.ComputeContentHash("{\"name\":\"test\"}");

		hash.Should().MatchRegex("^[0-9a-f]+$");
		hash.Should().HaveLength(64); // SHA-256 = 64 hex chars
	}

	[Fact]
	public void GenerateChangeDescription_Initial_Version()
	{
		var entry = new OrchestrationVersionEntry
		{
			ContentHash = "abc123",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "Test",
			StepCount = 3
		};

		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(null, entry);

		desc.Should().Be("Initial version");
	}

	[Fact]
	public void GenerateChangeDescription_Version_Changed()
	{
		var previous = new OrchestrationVersionEntry
		{
			ContentHash = "abc123",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
			OrchestrationName = "Test",
			StepCount = 3
		};
		var current = new OrchestrationVersionEntry
		{
			ContentHash = "def456",
			DeclaredVersion = "2.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "Test",
			StepCount = 3
		};

		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		desc.Should().Contain("Version changed: 1.0 -> 2.0");
	}

	[Fact]
	public void GenerateChangeDescription_Steps_Added()
	{
		var previous = new OrchestrationVersionEntry
		{
			ContentHash = "abc123",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
			OrchestrationName = "Test",
			StepCount = 3
		};
		var current = new OrchestrationVersionEntry
		{
			ContentHash = "def456",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "Test",
			StepCount = 5
		};

		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		desc.Should().Contain("Steps: +2");
	}

	[Fact]
	public void GenerateChangeDescription_Steps_Removed()
	{
		var previous = new OrchestrationVersionEntry
		{
			ContentHash = "abc123",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
			OrchestrationName = "Test",
			StepCount = 5
		};
		var current = new OrchestrationVersionEntry
		{
			ContentHash = "def456",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "Test",
			StepCount = 3
		};

		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		desc.Should().Contain("Steps: -2");
	}

	[Fact]
	public void GenerateChangeDescription_Renamed()
	{
		var previous = new OrchestrationVersionEntry
		{
			ContentHash = "abc123",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
			OrchestrationName = "OldName",
			StepCount = 3
		};
		var current = new OrchestrationVersionEntry
		{
			ContentHash = "def456",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "NewName",
			StepCount = 3
		};

		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		desc.Should().Contain("Renamed: OldName -> NewName");
	}

	[Fact]
	public void GenerateChangeDescription_Content_Updated_Only()
	{
		var previous = new OrchestrationVersionEntry
		{
			ContentHash = "abc123",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
			OrchestrationName = "Test",
			StepCount = 3
		};
		var current = new OrchestrationVersionEntry
		{
			ContentHash = "def456",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "Test",
			StepCount = 3
		};

		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		desc.Should().Be("Content updated");
	}

	[Fact]
	public void DiffLine_Properties_Are_Set()
	{
		var line = new DiffLine { Type = DiffLineType.Added, Content = "new line" };

		line.Type.Should().Be(DiffLineType.Added);
		line.Content.Should().Be("new line");
	}

	[Fact]
	public void DiffLineType_Has_Expected_Values()
	{
		Enum.IsDefined(typeof(DiffLineType), DiffLineType.Unchanged).Should().BeTrue();
		Enum.IsDefined(typeof(DiffLineType), DiffLineType.Added).Should().BeTrue();
		Enum.IsDefined(typeof(DiffLineType), DiffLineType.Removed).Should().BeTrue();
	}

	[Fact]
	public void VersionHistoryView_Is_DetailView()
	{
		TuiView.VersionHistory.Should().NotBe(TuiView.Dashboard);
		TuiView.VersionDiff.Should().NotBe(TuiView.Dashboard);
	}

	[Fact]
	public void OrchestrationVersionEntry_Properties()
	{
		var entry = new OrchestrationVersionEntry
		{
			ContentHash = "abcdef1234567890",
			DeclaredVersion = "1.0.0",
			Timestamp = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero),
			OrchestrationName = "TestOrch",
			StepCount = 5,
			ChangeDescription = "Added new step"
		};

		entry.ContentHash.Should().Be("abcdef1234567890");
		entry.DeclaredVersion.Should().Be("1.0.0");
		entry.Timestamp.Year.Should().Be(2025);
		entry.OrchestrationName.Should().Be("TestOrch");
		entry.StepCount.Should().Be(5);
		entry.ChangeDescription.Should().Be("Added new step");
	}

	[Fact]
	public void OrchestrationVersionEntry_ChangeDescription_Is_Optional()
	{
		var entry = new OrchestrationVersionEntry
		{
			ContentHash = "hash",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "Test",
			StepCount = 1
		};

		entry.ChangeDescription.Should().BeNull();
	}

	[Fact]
	public async Task NullOrchestrationVersionStore_Returns_Empty_Lists()
	{
		var store = new NullOrchestrationVersionStore();

		(await store.ListVersionsAsync("test")).Should().BeEmpty();
		(await store.GetSnapshotAsync("test", "hash")).Should().BeNull();
		(await store.GetLatestVersionAsync("test")).Should().BeNull();
	}

	[Fact]
	public async Task NullOrchestrationVersionStore_SaveVersion_DoesNotThrow()
	{
		var store = new NullOrchestrationVersionStore();

		var version = new OrchestrationVersionEntry
		{
			ContentHash = "hash",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "test",
			StepCount = 3
		};
		var act = () => store.SaveVersionAsync("test", version, "{}");
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task NullOrchestrationVersionStore_DeleteAll_DoesNotThrow()
	{
		var store = new NullOrchestrationVersionStore();

		var act = () => store.DeleteAllVersionsAsync("test");
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public void ComputeDiff_Multiple_Changes_Returns_Correct_Types()
	{
		var oldJson = "{\n  \"name\": \"test\",\n  \"steps\": [\n    \"step1\"\n  ]\n}";
		var newJson = "{\n  \"name\": \"updated\",\n  \"steps\": [\n    \"step1\",\n    \"step2\"\n  ]\n}";
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(oldJson, newJson);

		diff.Should().Contain(d => d.Type == DiffLineType.Unchanged);
		diff.Should().Contain(d => d.Type == DiffLineType.Added);
		diff.Should().Contain(d => d.Type == DiffLineType.Removed);
	}

	[Fact]
	public void GenerateChangeDescription_Multiple_Changes()
	{
		var previous = new OrchestrationVersionEntry
		{
			ContentHash = "abc123",
			DeclaredVersion = "1.0",
			Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
			OrchestrationName = "OldName",
			StepCount = 3
		};
		var current = new OrchestrationVersionEntry
		{
			ContentHash = "def456",
			DeclaredVersion = "2.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "NewName",
			StepCount = 5
		};

		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		desc.Should().Contain("Version changed: 1.0 -> 2.0");
		desc.Should().Contain("Steps: +2");
		desc.Should().Contain("Renamed: OldName -> NewName");
	}
}

public class DagVisualizationTests
{
	private static PromptOrchestrationStep CreatePromptStep(string name, params string[] dependsOn) =>
		new()
		{
			Name = name,
			Type = OrchestrationStepType.Prompt,
			DependsOn = dependsOn,
			SystemPrompt = "system",
			UserPrompt = "user",
			Model = "claude-opus-4.5"
		};

	private static TransformOrchestrationStep CreateTransformStep(string name, params string[] dependsOn) =>
		new()
		{
			Name = name,
			Type = OrchestrationStepType.Transform,
			DependsOn = dependsOn,
			Template = "template"
		};

	[Fact]
	public void DagView_Enum_Exists()
	{
		Enum.IsDefined(typeof(TuiView), TuiView.DagView).Should().BeTrue();
	}

	[Fact]
	public void BuildDagAscii_Empty_Steps_Returns_Empty()
	{
		var result = TerminalUI.BuildDagAscii([]);

		result.Should().BeEmpty();
	}

	[Fact]
	public void BuildDagAscii_Single_Step_Returns_One_Layer()
	{
		var steps = new OrchestrationStep[] { CreatePromptStep("analyze") };
		var result = TerminalUI.BuildDagAscii(steps);

		result.Should().NotBeEmpty();
		result.Should().Contain(l => l.Contains("Layer 1"));
		result.Should().Contain(l => l.Contains("analyze"));
		result.Should().Contain(l => l.Contains("1 steps in 1 layers"));
	}

	[Fact]
	public void BuildDagAscii_Sequential_Steps_Multiple_Layers()
	{
		var steps = new OrchestrationStep[]
		{
			CreatePromptStep("step1"),
			CreatePromptStep("step2", "step1"),
			CreatePromptStep("step3", "step2")
		};
		var result = TerminalUI.BuildDagAscii(steps);

		result.Should().Contain(l => l.Contains("Layer 1"));
		result.Should().Contain(l => l.Contains("Layer 2"));
		result.Should().Contain(l => l.Contains("Layer 3"));
		result.Should().Contain(l => l.Contains("3 steps in 3 layers"));
	}

	[Fact]
	public void BuildDagAscii_Parallel_Steps_Same_Layer()
	{
		var steps = new OrchestrationStep[]
		{
			CreatePromptStep("step1"),
			CreatePromptStep("step2"),
			CreatePromptStep("step3")
		};
		var result = TerminalUI.BuildDagAscii(steps);

		result.Should().Contain(l => l.Contains("Layer 1"));
		result.Should().Contain(l => l.Contains("parallel"));
		result.Should().Contain(l => l.Contains("3 steps in 1 layers"));
	}

	[Fact]
	public void BuildDagAscii_Diamond_Pattern()
	{
		// A -> B, C -> D (diamond: B and C are parallel, D depends on both)
		var steps = new OrchestrationStep[]
		{
			CreatePromptStep("A"),
			CreatePromptStep("B", "A"),
			CreatePromptStep("C", "A"),
			CreatePromptStep("D", "B", "C")
		};
		var result = TerminalUI.BuildDagAscii(steps);

		result.Should().Contain(l => l.Contains("Layer 1"));
		result.Should().Contain(l => l.Contains("Layer 2"));
		result.Should().Contain(l => l.Contains("Layer 3"));
		result.Should().Contain(l => l.Contains("4 steps in 3 layers"));
	}

	[Fact]
	public void BuildDagAscii_Shows_Dependencies()
	{
		var steps = new OrchestrationStep[]
		{
			CreatePromptStep("gather"),
			CreatePromptStep("analyze", "gather")
		};
		var result = TerminalUI.BuildDagAscii(steps);

		// The analyze step should show its dependency on gather
		result.Should().Contain(l => l.Contains("gather"));
	}

	[Fact]
	public void BuildDagAscii_Shows_Legend()
	{
		var steps = new OrchestrationStep[] { CreatePromptStep("step1") };
		var result = TerminalUI.BuildDagAscii(steps);

		result.Should().Contain(l => l.Contains("Legend"));
		result.Should().Contain(l => l.Contains("Prompt"));
	}

	[Fact]
	public void BuildDagAscii_Different_Step_Types_Show_Different_Icons()
	{
		var steps = new OrchestrationStep[]
		{
			CreatePromptStep("prompt_step"),
			CreateTransformStep("transform_step", "prompt_step")
		};
		var result = TerminalUI.BuildDagAscii(steps);

		// P for Prompt, T for Transform
		result.Should().Contain(l => l.Contains("[P]"));
		result.Should().Contain(l => l.Contains("[T]"));
	}

	[Fact]
	public void BuildDagAscii_Shows_Arrow_Between_Layers()
	{
		var steps = new OrchestrationStep[]
		{
			CreatePromptStep("step1"),
			CreatePromptStep("step2", "step1")
		};
		var result = TerminalUI.BuildDagAscii(steps);

		// Should contain arrow indicators
		result.Should().Contain(l => l.Contains("v"));
	}

	[Fact]
	public void BuildDagAscii_Complex_Graph()
	{
		// Complex multi-layer graph:
		// research, gather (parallel) -> analyze (depends on both) -> summarize (depends on analyze) and recommend (depends on analyze)
		var steps = new OrchestrationStep[]
		{
			CreatePromptStep("research"),
			CreatePromptStep("gather"),
			CreatePromptStep("analyze", "research", "gather"),
			CreatePromptStep("summarize", "analyze"),
			CreatePromptStep("recommend", "analyze"),
			CreateTransformStep("final_report", "summarize", "recommend")
		};
		var result = TerminalUI.BuildDagAscii(steps);

		result.Should().Contain(l => l.Contains("Layer 1"));
		result.Should().Contain(l => l.Contains("Layer 2"));
		result.Should().Contain(l => l.Contains("Layer 3"));
		result.Should().Contain(l => l.Contains("Layer 4"));
		result.Should().Contain(l => l.Contains("6 steps in 4 layers"));
	}
}

public class RawJsonViewTests
{
	[Fact]
	public void RawJsonView_Enum_Exists()
	{
		Enum.IsDefined(typeof(TuiView), TuiView.RawJsonView).Should().BeTrue();
	}

	[Fact]
	public void ColorizeJsonLine_Empty_Returns_Empty()
	{
		var result = TerminalUI.ColorizeJsonLine("");
		result.Should().Be("");
	}

	[Fact]
	public void ColorizeJsonLine_Null_Returns_Empty()
	{
		var result = TerminalUI.ColorizeJsonLine(null!);
		result.Should().Be("");
	}

	[Fact]
	public void ColorizeJsonLine_Whitespace_Returns_Empty()
	{
		var result = TerminalUI.ColorizeJsonLine("   ");
		result.Should().Be("");
	}

	[Fact]
	public void ColorizeJsonLine_Key_Value_Pair()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"name\": \"hello\"");
		// Key should be cyan, value should be green
		result.Should().Contain("[cyan]");
		result.Should().Contain("[green]");
		result.Should().Contain("name");
		result.Should().Contain("hello");
	}

	[Fact]
	public void ColorizeJsonLine_Numeric_Value()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"count\": 42");
		result.Should().Contain("[yellow]42[/]");
	}

	[Fact]
	public void ColorizeJsonLine_Boolean_True()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"enabled\": true");
		result.Should().Contain("[magenta]true[/]");
	}

	[Fact]
	public void ColorizeJsonLine_Boolean_False()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"enabled\": false");
		result.Should().Contain("[magenta]false[/]");
	}

	[Fact]
	public void ColorizeJsonLine_Null_Value()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"value\": null");
		result.Should().Contain("[magenta]null[/]");
	}

	[Fact]
	public void ColorizeJsonLine_Braces_Are_Bold()
	{
		var result = TerminalUI.ColorizeJsonLine("{");
		result.Should().Contain("[bold]");
	}

	[Fact]
	public void ColorizeJsonLine_Brackets_Are_Bold()
	{
		var result = TerminalUI.ColorizeJsonLine("[");
		result.Should().Contain("[bold]");
	}

	[Fact]
	public void ColorizeJsonLine_Colon_Is_Dim()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"key\": \"value\"");
		result.Should().Contain("[dim]:[/]");
	}

	[Fact]
	public void ColorizeJsonLine_Preserves_Leading_Whitespace()
	{
		var result = TerminalUI.ColorizeJsonLine("    \"name\": \"test\"");
		result.Should().StartWith("    ");
	}

	[Fact]
	public void ColorizeJsonLine_Escaped_Quotes_In_String()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"msg\": \"hello \\\"world\\\"\"");
		result.Should().Contain("[green]");
		result.Should().Contain("hello");
	}

	[Fact]
	public void ColorizeJsonLine_Negative_Number()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"offset\": -10");
		result.Should().Contain("[yellow]-10[/]");
	}

	[Fact]
	public void ColorizeJsonLine_Decimal_Number()
	{
		var result = TerminalUI.ColorizeJsonLine("  \"rate\": 3.14");
		result.Should().Contain("[yellow]3.14[/]");
	}

	[Fact]
	public void ColorizeJsonLine_Closing_Brace_With_Comma()
	{
		var result = TerminalUI.ColorizeJsonLine("  },");
		result.Should().Contain("[bold]}[/]");
	}

	[Fact]
	public void ColorizeJsonLine_Array_Of_Strings()
	{
		var result = TerminalUI.ColorizeJsonLine("    \"step1\", \"step2\"");
		// Both should be green string values (not followed by colon)
		result.Should().Contain("[green]");
	}
}

public class CheckpointViewTests
{
	[Fact]
	public void TuiView_Checkpoints_Exists()
	{
		Enum.IsDefined(typeof(TuiView), TuiView.Checkpoints).Should().BeTrue();
	}

	[Fact]
	public void TuiView_Checkpoints_Is_Second_To_Last_Value()
	{
		var values = Enum.GetValues<TuiView>();
		values[^2].Should().Be(TuiView.Checkpoints);
	}

	[Fact]
	public void CheckpointData_Properties_Are_Accessible()
	{
		var cp = new CheckpointData
		{
			RunId = "run-123",
			OrchestrationName = "TestOrch",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
			CheckpointedAt = DateTimeOffset.UtcNow,
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["step1"] = new CheckpointStepResult
				{
					Status = ExecutionStatus.Succeeded,
					Content = "result1"
				}
			}
		};

		cp.RunId.Should().Be("run-123");
		cp.OrchestrationName.Should().Be("TestOrch");
		cp.CompletedSteps.Should().HaveCount(1);
		cp.CompletedSteps["step1"].Status.Should().Be(ExecutionStatus.Succeeded);
		cp.Parameters.Should().BeEmpty();
	}

	[Fact]
	public void CheckpointData_With_Parameters()
	{
		var cp = new CheckpointData
		{
			RunId = "run-456",
			OrchestrationName = "ParamOrch",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CheckpointedAt = DateTimeOffset.UtcNow,
			Parameters = new Dictionary<string, string>
			{
				["key1"] = "value1",
				["key2"] = "value2"
			},
			CompletedSteps = []
		};

		cp.Parameters.Should().HaveCount(2);
		cp.Parameters["key1"].Should().Be("value1");
		cp.CompletedSteps.Should().BeEmpty();
	}

	[Fact]
	public void CheckpointData_With_TriggerId()
	{
		var cp = new CheckpointData
		{
			RunId = "run-789",
			OrchestrationName = "TriggerOrch",
			StartedAt = DateTimeOffset.UtcNow,
			CheckpointedAt = DateTimeOffset.UtcNow,
			TriggerId = "trigger-abc",
			CompletedSteps = []
		};

		cp.TriggerId.Should().Be("trigger-abc");
	}

	[Fact]
	public void CheckpointStepResult_ToExecutionResult_RoundTrips()
	{
		var stepResult = new CheckpointStepResult
		{
			Status = ExecutionStatus.Succeeded,
			Content = "Hello world",
			RawContent = "raw content",
			ActualModel = "claude-opus-4.5",
			PromptSent = "test prompt"
		};

		var execResult = stepResult.ToExecutionResult();
		execResult.Status.Should().Be(ExecutionStatus.Succeeded);
		execResult.Content.Should().Be("Hello world");
		execResult.RawContent.Should().Be("raw content");
		execResult.ActualModel.Should().Be("claude-opus-4.5");
		execResult.PromptSent.Should().Be("test prompt");
	}

	[Fact]
	public void CheckpointStepResult_FromExecutionResult_RoundTrips()
	{
		var execResult = new ExecutionResult
		{
			Status = ExecutionStatus.Failed,
			Content = "error output",
			ErrorMessage = "Something went wrong",
			ActualModel = "gpt-4"
		};

		var stepResult = CheckpointStepResult.FromExecutionResult(execResult);
		stepResult.Status.Should().Be(ExecutionStatus.Failed);
		stepResult.Content.Should().Be("error output");
		stepResult.ErrorMessage.Should().Be("Something went wrong");
		stepResult.ActualModel.Should().Be("gpt-4");
	}

	[Fact]
	public void NullCheckpointStore_ListReturnsEmpty()
	{
		var store = new NullCheckpointStore();
		var result = store.ListCheckpointsAsync().GetAwaiter().GetResult();
		result.Should().BeEmpty();
	}

	[Fact]
	public void NullCheckpointStore_LoadReturnsNull()
	{
		var store = new NullCheckpointStore();
		var result = store.LoadCheckpointAsync("orch", "run1").GetAwaiter().GetResult();
		result.Should().BeNull();
	}

	[Fact]
	public async Task NullCheckpointStore_SaveDoesNotThrow()
	{
		var store = new NullCheckpointStore();
		var cp = new CheckpointData
		{
			RunId = "run-1",
			OrchestrationName = "test",
			StartedAt = DateTimeOffset.UtcNow,
			CheckpointedAt = DateTimeOffset.UtcNow,
			CompletedSteps = []
		};
		var act = () => store.SaveCheckpointAsync(cp);
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task NullCheckpointStore_DeleteDoesNotThrow()
	{
		var store = new NullCheckpointStore();
		var act = () => store.DeleteCheckpointAsync("orch", "run1");
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public void CheckpointData_Multiple_CompletedSteps()
	{
		var cp = new CheckpointData
		{
			RunId = "run-multi",
			OrchestrationName = "MultiStep",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
			CheckpointedAt = DateTimeOffset.UtcNow,
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["gather_data"] = new CheckpointStepResult
				{
					Status = ExecutionStatus.Succeeded,
					Content = "data gathered"
				},
				["analyze"] = new CheckpointStepResult
				{
					Status = ExecutionStatus.Succeeded,
					Content = "analysis complete",
					ActualModel = "claude-opus-4.5"
				},
				["report"] = new CheckpointStepResult
				{
					Status = ExecutionStatus.Failed,
					Content = "",
					ErrorMessage = "Timeout"
				}
			}
		};

		cp.CompletedSteps.Should().HaveCount(3);
		cp.CompletedSteps["gather_data"].Status.Should().Be(ExecutionStatus.Succeeded);
		cp.CompletedSteps["analyze"].ActualModel.Should().Be("claude-opus-4.5");
		cp.CompletedSteps["report"].ErrorMessage.Should().Be("Timeout");
	}

	[Fact]
	public void CheckpointStepResult_WithDependencyOutputs()
	{
		var stepResult = new CheckpointStepResult
		{
			Status = ExecutionStatus.Succeeded,
			Content = "output",
			RawDependencyOutputs = new Dictionary<string, string>
			{
				["step1"] = "dep output 1",
				["step2"] = "dep output 2"
			}
		};

		stepResult.RawDependencyOutputs.Should().HaveCount(2);
		stepResult.RawDependencyOutputs["step1"].Should().Be("dep output 1");

		var execResult = stepResult.ToExecutionResult();
		execResult.RawDependencyOutputs.Should().HaveCount(2);
	}
}

public class TriggerCreateTests
{
	[Fact]
	public void TuiView_TriggerCreate_Is_Defined()
	{
		Enum.IsDefined(typeof(TuiView), TuiView.TriggerCreate).Should().BeTrue();
	}

	[Fact]
	public void TuiView_TriggerCreate_Is_Last_Value()
	{
		var values = Enum.GetValues<TuiView>();
		values.Last().Should().Be(TuiView.TriggerCreate);
	}

	[Fact]
	public void TriggerCreateStep_Has_Four_Steps()
	{
		var steps = Enum.GetValues<TriggerCreateStep>();
		steps.Should().HaveCount(4);
		steps.Should().ContainInOrder(
			TriggerCreateStep.SelectOrchestration,
			TriggerCreateStep.SelectType,
			TriggerCreateStep.Configure,
			TriggerCreateStep.Review
		);
	}

	[Fact]
	public void GetTriggerConfigFields_Scheduler_Returns_Expected_Fields()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		// Default type is Scheduler after reset
		var fields = ui.GetTriggerConfigFields();

		fields.Should().HaveCount(5); // Enabled, Cron, Interval, MaxRuns, InputHandler
		fields[0].Name.Should().Be("Enabled");
		fields[0].IsBoolean.Should().BeTrue();
		fields[1].Name.Should().Be("Cron Expression");
		fields[1].IsBoolean.Should().BeFalse();
		fields[2].Name.Should().Be("Interval (seconds)");
		fields[3].Name.Should().Be("Max Runs");
		fields[4].Name.Should().Be("Input Handler Prompt");
	}

	[Fact]
	public void GetTriggerConfigFields_Loop_Returns_Expected_Fields()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Loop);
		var fields = ui.GetTriggerConfigFields();

		fields.Should().HaveCount(5); // Enabled, Delay, MaxIterations, ContinueOnFailure, InputHandler
		fields[0].Name.Should().Be("Enabled");
		fields[1].Name.Should().Be("Delay (seconds)");
		fields[2].Name.Should().Be("Max Iterations");
		fields[3].Name.Should().Be("Continue on Failure");
		fields[3].IsBoolean.Should().BeTrue();
		fields[4].Name.Should().Be("Input Handler Prompt");
	}

	[Fact]
	public void GetTriggerConfigFields_Webhook_Returns_Expected_Fields()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Webhook);
		var fields = ui.GetTriggerConfigFields();

		fields.Should().HaveCount(4); // Enabled, Secret, MaxConcurrent, InputHandler
		fields[0].Name.Should().Be("Enabled");
		fields[1].Name.Should().Be("Secret");
		fields[2].Name.Should().Be("Max Concurrent");
		fields[3].Name.Should().Be("Input Handler Prompt");
	}

	[Fact]
	public void GetTriggerConfigFields_Email_Returns_Expected_Fields()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Email);
		var fields = ui.GetTriggerConfigFields();

		fields.Should().HaveCount(7); // Enabled, FolderPath, PollInterval, MaxItems, SubjectContains, SenderContains, InputHandler
		fields[0].Name.Should().Be("Enabled");
		fields[1].Name.Should().Be("Folder Path");
		fields[2].Name.Should().Be("Poll Interval (seconds)");
		fields[3].Name.Should().Be("Max Items per Poll");
		fields[4].Name.Should().Be("Subject Contains");
		fields[5].Name.Should().Be("Sender Contains");
		fields[6].Name.Should().Be("Input Handler Prompt");
	}

	[Fact]
	public void BuildTriggerConfig_Scheduler_Creates_Valid_Config()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		// Set cron expression via field setter
		ui.SetTriggerConfigFieldValue(1, "0 */5 * * *"); // Cron
		ui.SetTriggerConfigFieldValue(3, "10"); // MaxRuns

		var config = ui.BuildTriggerConfig();

		config.Should().BeOfType<SchedulerTriggerConfig>();
		config.Type.Should().Be(TriggerType.Scheduler);
		config.Enabled.Should().BeTrue();

		var scheduler = (SchedulerTriggerConfig)config;
		scheduler.Cron.Should().Be("0 */5 * * *");
		scheduler.MaxRuns.Should().Be(10);
	}

	[Fact]
	public void BuildTriggerConfig_Loop_Creates_Valid_Config()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Loop);
		ui.SetTriggerConfigFieldValue(1, "30"); // DelaySeconds
		ui.SetTriggerConfigFieldValue(2, "5"); // MaxIterations

		var config = ui.BuildTriggerConfig();

		config.Should().BeOfType<LoopTriggerConfig>();
		config.Type.Should().Be(TriggerType.Loop);

		var loop = (LoopTriggerConfig)config;
		loop.DelaySeconds.Should().Be(30);
		loop.MaxIterations.Should().Be(5);
		loop.ContinueOnFailure.Should().BeFalse();
	}

	[Fact]
	public void BuildTriggerConfig_Webhook_Creates_Valid_Config()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Webhook);
		ui.SetTriggerConfigFieldValue(1, "my-secret-key"); // Secret
		ui.SetTriggerConfigFieldValue(2, "3"); // MaxConcurrent

		var config = ui.BuildTriggerConfig();

		config.Should().BeOfType<WebhookTriggerConfig>();
		config.Type.Should().Be(TriggerType.Webhook);

		var webhook = (WebhookTriggerConfig)config;
		webhook.Secret.Should().Be("my-secret-key");
		webhook.MaxConcurrent.Should().Be(3);
	}

	[Fact]
	public void BuildTriggerConfig_Email_Creates_Valid_Config()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Email);
		ui.SetTriggerConfigFieldValue(1, "Inbox/Teams"); // FolderPath
		ui.SetTriggerConfigFieldValue(4, "status update"); // SubjectContains

		var config = ui.BuildTriggerConfig();

		config.Should().BeOfType<EmailTriggerConfig>();
		config.Type.Should().Be(TriggerType.Email);

		var email = (EmailTriggerConfig)config;
		email.FolderPath.Should().Be("Inbox/Teams");
		email.SubjectContains.Should().Be("status update");
		email.PollIntervalSeconds.Should().Be(60); // default
		email.MaxItemsPerPoll.Should().Be(10); // default
	}

	[Fact]
	public void BuildTriggerConfig_With_InputHandler()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		var fields = ui.GetTriggerConfigFields();
		// Last field is always InputHandler
		ui.SetTriggerConfigFieldValue(fields.Count - 1, "Extract the project path from the webhook body");

		var config = ui.BuildTriggerConfig();
		config.InputHandlerPrompt.Should().Be("Extract the project path from the webhook body");
	}

	[Fact]
	public void BuildTriggerConfig_Empty_InputHandler_Is_Null()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		var config = ui.BuildTriggerConfig();
		config.InputHandlerPrompt.Should().BeNull();
	}

	[Fact]
	public void ValidateTriggerConfig_No_Orchestration_Returns_Error()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		var error = ui.ValidateTriggerConfig();
		error.Should().Be("No orchestration selected");
	}

	[Fact]
	public void ValidateTriggerConfig_Scheduler_No_Cron_Or_Interval_Returns_Error()
	{
		var (ui, registry) = CreateTestUIWithRegistry();
		ui.ResetTriggerCreateState();
		// Register a real orchestration in the registry
		RegisterFakeOrchestration(registry, "test-orch", "Test Orch");
		SetOrchestrationId(ui, "test-orch");
		// Don't set cron or interval
		var error = ui.ValidateTriggerConfig();
		error.Should().Contain("cron expression or interval");
	}

	[Fact]
	public void ValidateTriggerConfig_Scheduler_Invalid_Interval_Returns_Error()
	{
		var (ui, registry) = CreateTestUIWithRegistry();
		ui.ResetTriggerCreateState();
		RegisterFakeOrchestration(registry, "test-orch", "Test Orch");
		SetOrchestrationId(ui, "test-orch");
		ui.SetTriggerConfigFieldValue(2, "-5"); // Invalid interval
		var error = ui.ValidateTriggerConfig();
		error.Should().Contain("positive integer");
	}

	[Fact]
	public void BuildTriggerReviewSummary_Contains_Key_Info()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		ui.SetTriggerConfigFieldValue(1, "0 0 * * *"); // Cron

		var summary = ui.BuildTriggerReviewSummary();

		summary.Should().Contain(s => s.Label == "Trigger Type" && s.Value == "Scheduler");
		summary.Should().Contain(s => s.Label == "Enabled" && s.Value == "Yes");
		summary.Should().Contain(s => s.Label == "Cron" && s.Value == "0 0 * * *");
	}

	[Fact]
	public void BuildTriggerReviewSummary_Loop_Shows_Delay()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Loop);
		ui.SetTriggerConfigFieldValue(1, "10"); // DelaySeconds

		var summary = ui.BuildTriggerReviewSummary();
		summary.Should().Contain(s => s.Label == "Trigger Type" && s.Value == "Loop");
		summary.Should().Contain(s => s.Label == "Delay" && s.Value == "10s");
	}

	[Fact]
	public void BuildTriggerReviewSummary_Webhook_Shows_Secret_Set()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Webhook);
		ui.SetTriggerConfigFieldValue(1, "super-secret"); // Secret

		var summary = ui.BuildTriggerReviewSummary();
		summary.Should().Contain(s => s.Label == "Secret" && s.Value == "(set)");
	}

	[Fact]
	public void BuildTriggerReviewSummary_Webhook_Shows_No_Secret()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Webhook);
		// Don't set secret

		var summary = ui.BuildTriggerReviewSummary();
		summary.Should().Contain(s => s.Label == "Secret" && s.Value == "(none)");
	}

	[Fact]
	public void ResetTriggerCreateState_Resets_All_Fields()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Webhook);
		ui.SetTriggerConfigFieldValue(1, "my-secret");

		// Now reset
		ui.ResetTriggerCreateState();
		var config = ui.BuildTriggerConfig();
		config.Should().BeOfType<SchedulerTriggerConfig>(); // Back to default
		config.Type.Should().Be(TriggerType.Scheduler);
	}

	[Fact]
	public void SetTriggerConfigFieldValue_Email_All_Fields()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Email);

		ui.SetTriggerConfigFieldValue(1, "Inbox/Archive");
		ui.SetTriggerConfigFieldValue(2, "120");
		ui.SetTriggerConfigFieldValue(3, "20");
		ui.SetTriggerConfigFieldValue(4, "urgent");
		ui.SetTriggerConfigFieldValue(5, "boss@example.com");
		ui.SetTriggerConfigFieldValue(6, "Transform this email");

		var config = (EmailTriggerConfig)ui.BuildTriggerConfig();
		config.FolderPath.Should().Be("Inbox/Archive");
		config.PollIntervalSeconds.Should().Be(120);
		config.MaxItemsPerPoll.Should().Be(20);
		config.SubjectContains.Should().Be("urgent");
		config.SenderContains.Should().Be("boss@example.com");
		config.InputHandlerPrompt.Should().Be("Transform this email");
	}

	[Fact]
	public void GetTriggerConfigFields_Default_Values_Are_Correct()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();

		// Check default values for Scheduler
		var fields = ui.GetTriggerConfigFields();
		fields[0].Value.Should().Be("Yes"); // Enabled default
		fields[1].Value.Should().Be(""); // Cron default
		fields[2].Value.Should().Be(""); // Interval default
		fields[3].Value.Should().Be(""); // MaxRuns default
		fields[4].Value.Should().Be(""); // InputHandler default
	}

	[Fact]
	public void GetTriggerConfigFields_Loop_Default_Values()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Loop);

		var fields = ui.GetTriggerConfigFields();
		fields[1].Value.Should().Be("0"); // Delay default
		fields[2].Value.Should().Be(""); // MaxIterations default
		fields[3].Value.Should().Be("No"); // ContinueOnFailure default
	}

	[Fact]
	public void GetTriggerConfigFields_Email_Default_Values()
	{
		var ui = CreateTestUI();
		ui.ResetTriggerCreateState();
		SetTriggerType(ui, TriggerType.Email);

		var fields = ui.GetTriggerConfigFields();
		fields[1].Value.Should().Be("Inbox"); // FolderPath default
		fields[2].Value.Should().Be("60"); // PollInterval default
		fields[3].Value.Should().Be("10"); // MaxItems default
	}

	// Helper: create a TerminalUI with mock/null dependencies for testing
	private static TerminalUI CreateTestUI()
	{
		var (ui, _) = CreateTestUIWithRegistry();
		return ui;
	}

	// Helper: create a TerminalUI and return the registry for test manipulation
	private static (TerminalUI ui, OrchestrationRegistry registry) CreateTestUIWithRegistry()
	{
		var registry = new OrchestrationRegistry();
		var checkpointStore = new NullCheckpointStore();
		var reporter = new TerminalOrchestrationReporter();
		var callback = new TerminalExecutionCallback(reporter);
		var activeInfos = new System.Collections.Concurrent.ConcurrentDictionary<string, ActiveExecutionInfo>();
		var hostOptions = new OrchestrationHostOptions();

		var ui = new TerminalUI(
			registry,
			null!, // TriggerManager - not needed for config building/validation tests
			null!, // FileSystemRunStore - not needed
			checkpointStore,
			activeInfos,
			reporter,
			callback,
			hostOptions);

		return (ui, registry);
	}

	// Helper: register a fake orchestration entry in the registry using reflection
	private static void RegisterFakeOrchestration(OrchestrationRegistry registry, string id, string name)
	{
		// Use reflection to access the internal _entries dictionary
		var entriesField = typeof(OrchestrationRegistry).GetField("_entries",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		var entries = (System.Collections.Concurrent.ConcurrentDictionary<string, OrchestrationEntry>)entriesField!.GetValue(registry)!;
		entries[id] = new OrchestrationEntry
		{
			Id = id,
			Path = $"/fake/{id}.json",
			Orchestration = new Orchestration
			{
				Name = name,
				Description = $"Test orchestration {name}",
				Steps = []
			},
			RegisteredAt = DateTimeOffset.UtcNow
		};
	}

	// Helper: set the trigger type on a TerminalUI (simulates user selection in wizard step 2)
	private static void SetTriggerType(TerminalUI ui, TriggerType type)
	{
		// Use reflection to set _triggerCreateType since it's private
		var field = typeof(TerminalUI).GetField("_triggerCreateType",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		field!.SetValue(ui, type);
	}

	// Helper: set the orchestration ID on a TerminalUI
	private static void SetOrchestrationId(TerminalUI ui, string id)
	{
		var field = typeof(TerminalUI).GetField("_triggerCreateOrchestrationId",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		field!.SetValue(ui, id);
	}
}
