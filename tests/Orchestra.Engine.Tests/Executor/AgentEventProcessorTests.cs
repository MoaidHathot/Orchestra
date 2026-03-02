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

	private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(params T[] items)
	{
		foreach (var item in items)
		{
			yield return item;
		}
		await Task.CompletedTask;
	}
}
