using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

public class AgentEventProcessorTests
{
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();

	[Fact]
	public async Task ProcessEventsAsync_MessageDelta_ReportsContentDelta()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Hello" },
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = " World" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportContentDelta("test-step", "Hello");
		_reporter.Received(1).ReportContentDelta("test-step", " World");
	}

	[Fact]
	public async Task ProcessEventsAsync_MessageDelta_CollectsResponseSegments()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Hello" },
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = " World" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.ResponseSegments);
		Assert.Equal("Hello World", processor.ResponseSegments[0]);
	}

	[Fact]
	public async Task ProcessEventsAsync_ReasoningDelta_ReportsAndCollectsReasoning()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.ReasoningDelta, Content = "Let me think" },
			new AgentEvent { Type = AgentEventType.ReasoningDelta, Content = " about this..." }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportReasoningDelta("test-step", "Let me think");
		_reporter.Received(1).ReportReasoningDelta("test-step", " about this...");
		Assert.Equal("Let me think about this...", processor.Reasoning);
	}

	[Fact]
	public async Task ProcessEventsAsync_ToolExecutionStart_ReportsAndTracksToolCall()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "call-1",
				ToolName = "read_file",
				ToolArguments = "{\"path\": \"test.txt\"}",
				McpServerName = "filesystem"
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "call-1",
				ToolName = "read_file",
				ToolSuccess = true,
				ToolResult = "file contents"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportToolExecutionStarted("test-step", "read_file", "{\"path\": \"test.txt\"}", "filesystem");
		_reporter.Received(1).ReportToolExecutionCompleted("test-step", "read_file", true, "file contents", null);

		Assert.Single(processor.ToolCalls);
		var toolCall = processor.ToolCalls[0];
		Assert.Equal("call-1", toolCall.CallId);
		Assert.Equal("read_file", toolCall.ToolName);
		Assert.Equal("{\"path\": \"test.txt\"}", toolCall.Arguments);
		Assert.Equal("filesystem", toolCall.McpServer);
		Assert.True(toolCall.Success);
		Assert.Equal("file contents", toolCall.Result);
	}

	[Fact]
	public async Task ProcessEventsAsync_ToolExecutionWithoutCallId_CreatesRecordImmediately()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolName = "unknown_tool"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.ToolCalls);
		Assert.Null(processor.ToolCalls[0].CallId);
		Assert.Equal("unknown_tool", processor.ToolCalls[0].ToolName);
	}

	[Fact]
	public async Task ProcessEventsAsync_ToolExecutionCompleteWithoutMatchingStart_CreatesCompleteRecord()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "orphan-call",
				ToolName = "orphan_tool",
				ToolSuccess = false,
				ToolError = "Something went wrong"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.ToolCalls);
		Assert.Equal("orphan-call", processor.ToolCalls[0].CallId);
		Assert.Equal("orphan_tool", processor.ToolCalls[0].ToolName);
		Assert.False(processor.ToolCalls[0].Success);
		Assert.Equal("Something went wrong", processor.ToolCalls[0].Error);
	}

	[Fact]
	public async Task ProcessEventsAsync_Error_ReportsError()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.Error, ErrorMessage = "Something went wrong" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportStepError("test-step", "Something went wrong");
	}

	[Fact]
	public async Task ProcessEventsAsync_ToolCallBetweenMessages_CreatesMultipleResponseSegments()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Before tool" },
			new AgentEvent { Type = AgentEventType.ToolExecutionStart, ToolName = "tool1" },
			new AgentEvent { Type = AgentEventType.ToolExecutionComplete, ToolName = "tool1", ToolSuccess = true },
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "After tool" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Equal(2, processor.ResponseSegments.Count);
		Assert.Equal("Before tool", processor.ResponseSegments[0]);
		Assert.Equal("After tool", processor.ResponseSegments[1]);
	}

	[Fact]
	public void BuildTrace_ReturnsCorrectTrace()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");

		// Act
		var trace = processor.BuildTrace(
			systemPrompt: "You are a helpful assistant",
			userPromptRaw: "Help me",
			userPromptProcessed: "Help me\n---\nContext",
			finalResponse: "Here is my response",
			outputHandlerResult: "Processed response"
		);

		// Assert
		Assert.Equal("You are a helpful assistant", trace.SystemPrompt);
		Assert.Equal("Help me", trace.UserPromptRaw);
		Assert.Equal("Help me\n---\nContext", trace.UserPromptProcessed);
		Assert.Equal("Here is my response", trace.FinalResponse);
		Assert.Equal("Processed response", trace.OutputHandlerResult);
	}

	[Fact]
	public void BuildPartialTrace_ReturnsPartialTrace()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");

		// Act
		var trace = processor.BuildPartialTrace(
			systemPrompt: "System prompt",
			userPromptRaw: "User prompt"
		);

		// Assert
		Assert.Equal("System prompt", trace.SystemPrompt);
		Assert.Equal("User prompt", trace.UserPromptRaw);
		Assert.Null(trace.UserPromptProcessed);
		Assert.Null(trace.FinalResponse);
		Assert.Null(trace.OutputHandlerResult);
	}

	[Fact]
	public async Task ProcessEventsAsync_EmptyStream_NoExceptions()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable<AgentEvent>();

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Null(processor.Reasoning);
		Assert.Empty(processor.ToolCalls);
		Assert.Empty(processor.ResponseSegments);
	}

	[Fact]
	public async Task ProcessEventsAsync_NullContent_HandlesGracefully()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = null }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportContentDelta("test-step", string.Empty);
	}

	#region Subagent Events

	[Fact]
	public async Task ProcessEventsAsync_SubagentSelected_ReportsSubagentSelected()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SubagentSelected,
				SubagentName = "researcher",
				SubagentDisplayName = "Research Agent",
				SubagentTools = ["web_search", "read_file"]
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentSelected(
			"test-step",
			"researcher",
			"Research Agent",
			Arg.Is<string[]>(tools => tools.Length == 2 && tools[0] == "web_search" && tools[1] == "read_file"));
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentSelected_WithNullValues_HandlesGracefully()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SubagentSelected,
				SubagentName = null, // Missing name should default to "unknown"
				SubagentDisplayName = null,
				SubagentTools = null
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentSelected("test-step", "unknown", null, null);
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentStarted_ReportsSubagentStarted()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SubagentStarted,
				ToolCallId = "call-123",
				SubagentName = "writer",
				SubagentDisplayName = "Writer Agent",
				SubagentDescription = "Specializes in writing content"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentStarted(
			"test-step",
			"call-123",
			"writer",
			"Writer Agent",
			"Specializes in writing content");
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentStarted_WithNullToolCallId_HandlesGracefully()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SubagentStarted,
				ToolCallId = null,
				SubagentName = "minimal-agent",
				SubagentDisplayName = null,
				SubagentDescription = null
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentStarted("test-step", null, "minimal-agent", null, null);
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentCompleted_ReportsSubagentCompleted()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SubagentCompleted,
				ToolCallId = "call-456",
				SubagentName = "researcher",
				SubagentDisplayName = "Research Agent"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentCompleted("test-step", "call-456", "researcher", "Research Agent");
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentFailed_ReportsSubagentFailed()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SubagentFailed,
				ToolCallId = "call-789",
				SubagentName = "writer",
				SubagentDisplayName = "Writer Agent",
				ErrorMessage = "Subagent encountered an error"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentFailed(
			"test-step",
			"call-789",
			"writer",
			"Writer Agent",
			"Subagent encountered an error");
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentFailed_WithNullError_HandlesGracefully()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SubagentFailed,
				ToolCallId = "call-error",
				SubagentName = "failing-agent",
				SubagentDisplayName = null,
				ErrorMessage = null
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentFailed("test-step", "call-error", "failing-agent", null, null);
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentDeselected_ReportsSubagentDeselected()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.SubagentDeselected }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentDeselected("test-step");
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentFullLifecycle_ReportsAllEvents()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "coordinator-step");
		var events = CreateAsyncEnumerable(
			// Parent starts, selects subagent
			new AgentEvent
			{
				Type = AgentEventType.SubagentSelected,
				SubagentName = "researcher",
				SubagentDisplayName = "Research Agent",
				SubagentTools = ["web_search"]
			},
			// Subagent starts execution
			new AgentEvent
			{
				Type = AgentEventType.SubagentStarted,
				ToolCallId = "call-1",
				SubagentName = "researcher",
				SubagentDisplayName = "Research Agent",
				SubagentDescription = "Finds information"
			},
			// Subagent does some work (message deltas)
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Searching..." },
			// Subagent completes
			new AgentEvent
			{
				Type = AgentEventType.SubagentCompleted,
				ToolCallId = "call-1",
				SubagentName = "researcher",
				SubagentDisplayName = "Research Agent"
			},
			// Return to parent
			new AgentEvent { Type = AgentEventType.SubagentDeselected }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Received.InOrder(() =>
		{
			_reporter.ReportSubagentSelected("coordinator-step", "researcher", "Research Agent", Arg.Any<string[]>());
			_reporter.ReportSubagentStarted("coordinator-step", "call-1", "researcher", "Research Agent", "Finds information");
			_reporter.ReportContentDelta("coordinator-step", "Searching...");
			_reporter.ReportSubagentCompleted("coordinator-step", "call-1", "researcher", "Research Agent");
			_reporter.ReportSubagentDeselected("coordinator-step");
		});
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentWithToolCalls_TracksToolCallsCorrectly()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SubagentStarted,
				ToolCallId = "subagent-call",
				SubagentName = "tool-user"
			},
			// Subagent uses a tool
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "tool-1",
				ToolName = "read_file",
				ToolArguments = "{\"path\": \"data.txt\"}"
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "tool-1",
				ToolName = "read_file",
				ToolSuccess = true,
				ToolResult = "file contents"
			},
			new AgentEvent
			{
				Type = AgentEventType.SubagentCompleted,
				ToolCallId = "subagent-call",
				SubagentName = "tool-user"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert - Tool calls should be tracked
		Assert.Single(processor.ToolCalls);
		Assert.Equal("read_file", processor.ToolCalls[0].ToolName);
		Assert.True(processor.ToolCalls[0].Success);
	}

	[Fact]
	public async Task ProcessEventsAsync_MultipleSubagentsSequential_ReportsAllCorrectly()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "multi-agent-step");
		var events = CreateAsyncEnumerable(
			// First subagent
			new AgentEvent { Type = AgentEventType.SubagentSelected, SubagentName = "agent1" },
			new AgentEvent { Type = AgentEventType.SubagentStarted, ToolCallId = "call-1", SubagentName = "agent1" },
			new AgentEvent { Type = AgentEventType.SubagentCompleted, ToolCallId = "call-1", SubagentName = "agent1" },
			new AgentEvent { Type = AgentEventType.SubagentDeselected },
			// Second subagent
			new AgentEvent { Type = AgentEventType.SubagentSelected, SubagentName = "agent2" },
			new AgentEvent { Type = AgentEventType.SubagentStarted, ToolCallId = "call-2", SubagentName = "agent2" },
			new AgentEvent { Type = AgentEventType.SubagentCompleted, ToolCallId = "call-2", SubagentName = "agent2" },
			new AgentEvent { Type = AgentEventType.SubagentDeselected }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentSelected("multi-agent-step", "agent1", Arg.Any<string?>(), Arg.Any<string[]?>());
		_reporter.Received(1).ReportSubagentSelected("multi-agent-step", "agent2", Arg.Any<string?>(), Arg.Any<string[]?>());
		_reporter.Received(2).ReportSubagentDeselected("multi-agent-step");
	}

	[Fact]
	public async Task ProcessEventsAsync_SubagentFailure_ReportsFailureAndContinues()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.SubagentSelected, SubagentName = "failing-agent" },
			new AgentEvent { Type = AgentEventType.SubagentStarted, ToolCallId = "call-fail", SubagentName = "failing-agent" },
			new AgentEvent
			{
				Type = AgentEventType.SubagentFailed,
				ToolCallId = "call-fail",
				SubagentName = "failing-agent",
				ErrorMessage = "Agent crashed"
			},
			// Continue with message after failure
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Handling failure gracefully" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSubagentFailed("test-step", "call-fail", "failing-agent", Arg.Any<string?>(), "Agent crashed");
		_reporter.Received(1).ReportContentDelta("test-step", "Handling failure gracefully");
	}

	#endregion

	private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(params T[] items)
	{
		foreach (var item in items)
		{
			yield return item;
		}
		await Task.CompletedTask;
	}
}
