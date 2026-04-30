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
		_reporter.Received(1).ReportContentDelta("test-step", "Hello", Arg.Any<ActorContext>());
		_reporter.Received(1).ReportContentDelta("test-step", " World", Arg.Any<ActorContext>());
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
		_reporter.Received(1).ReportReasoningDelta("test-step", "Let me think", Arg.Any<ActorContext>());
		_reporter.Received(1).ReportReasoningDelta("test-step", " about this...", Arg.Any<ActorContext>());
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
		_reporter.Received(1).ReportToolExecutionStarted("test-step", "read_file", "{\"path\": \"test.txt\"}", "filesystem", Arg.Any<ActorContext>());
		_reporter.Received(1).ReportToolExecutionCompleted("test-step", "read_file", true, "file contents", null, Arg.Any<ActorContext>());

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
		_reporter.Received(1).ReportContentDelta("test-step", string.Empty, Arg.Any<ActorContext>());
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
			_reporter.ReportContentDelta("coordinator-step", "Searching...", Arg.Any<ActorContext>());
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
		_reporter.Received(1).ReportContentDelta("test-step", "Handling failure gracefully", Arg.Any<ActorContext>());
	}

	#endregion

	#region Warning and Info Events

	[Fact]
	public async Task ProcessEventsAsync_Warning_ReportsWarningAndCollectsIt()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.Warning,
				ErrorMessage = "Failed to start MCP server 'icm'",
				DiagnosticType = "mcp_server_error"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSessionWarning("mcp_server_error", "Failed to start MCP server 'icm'");
		var trace = processor.BuildTrace("sys", "user");
		Assert.Single(trace.Warnings);
		Assert.Equal("[mcp_server_error] Failed to start MCP server 'icm'", trace.Warnings[0]);
	}

	[Fact]
	public async Task ProcessEventsAsync_Warning_WithNullValues_HandlesGracefully()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.Warning,
				ErrorMessage = null,
				DiagnosticType = null
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSessionWarning("unknown", "Unknown warning");
		var trace = processor.BuildTrace("sys", "user");
		Assert.Single(trace.Warnings);
		Assert.Equal("[unknown] Unknown warning", trace.Warnings[0]);
	}

	[Fact]
	public async Task ProcessEventsAsync_MultipleWarnings_CollectsAll()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.Warning,
				ErrorMessage = "First warning",
				DiagnosticType = "type_a"
			},
			new AgentEvent
			{
				Type = AgentEventType.Warning,
				ErrorMessage = "Second warning",
				DiagnosticType = "type_b"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var trace = processor.BuildTrace("sys", "user");
		Assert.Equal(2, trace.Warnings.Count);
		Assert.Equal("[type_a] First warning", trace.Warnings[0]);
		Assert.Equal("[type_b] Second warning", trace.Warnings[1]);
	}

	[Fact]
	public async Task ProcessEventsAsync_Info_ReportsInfo()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.Info,
				Content = "MCP server 'icm' connected",
				DiagnosticType = "mcp_connected"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSessionInfo("mcp_connected", "MCP server 'icm' connected");
	}

	[Fact]
	public async Task ProcessEventsAsync_Info_WithNullValues_HandlesGracefully()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.Info,
				Content = null,
				DiagnosticType = null
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSessionInfo("unknown", "");
	}

	#endregion

	#region MCP Servers Loaded

	[Fact]
	public async Task ProcessEventsAsync_McpServersLoaded_ReportsMcpServersLoaded()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected", Source: "local"),
			new("graph", "Failed", Source: "remote", Error: "Connection refused")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportMcpServersLoaded(
			Arg.Is<IReadOnlyList<McpServerStatusInfo>>(list =>
				list.Count == 2 && list[0].Name == "icm" && list[1].Name == "graph"));
	}

	[Fact]
	public async Task ProcessEventsAsync_McpServersLoaded_CollectsMcpServerStatuses()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected", Source: "local"),
			new("graph", "Failed", Source: "remote", Error: "Connection refused")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Equal(2, processor.McpServerStatuses.Count);
		Assert.Equal("icm", processor.McpServerStatuses[0].Name);
		Assert.Equal("Connected", processor.McpServerStatuses[0].Status);
		Assert.Equal("local", processor.McpServerStatuses[0].Source);
		Assert.Null(processor.McpServerStatuses[0].Error);
		Assert.Equal("graph", processor.McpServerStatuses[1].Name);
		Assert.Equal("Failed", processor.McpServerStatuses[1].Status);
		Assert.Equal("Connection refused", processor.McpServerStatuses[1].Error);
	}

	[Fact]
	public async Task ProcessEventsAsync_McpServersLoaded_FailedServer_AutoGeneratesWarning()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected"),
			new("graph", "Failed", Error: "Connection refused")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var trace = processor.BuildTrace("sys", "user");
		Assert.Single(trace.Warnings);
		Assert.Contains("graph", trace.Warnings[0]);
		Assert.Contains("failed to connect", trace.Warnings[0]);
		Assert.Contains("Connection refused", trace.Warnings[0]);
	}

	[Fact]
	public async Task ProcessEventsAsync_McpServersLoaded_FailedServerWithoutError_AutoGeneratesWarningWithoutErrorDetail()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("graph", "Failed")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var trace = processor.BuildTrace("sys", "user");
		Assert.Single(trace.Warnings);
		Assert.Contains("graph", trace.Warnings[0]);
		Assert.Contains("failed to connect", trace.Warnings[0]);
		Assert.DoesNotContain(":", trace.Warnings[0].Split("connect")[1]);
	}

	[Fact]
	public async Task ProcessEventsAsync_McpServersLoaded_AllConnected_NoAutoWarnings()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected"),
			new("graph", "Connected")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var trace = processor.BuildTrace("sys", "user");
		Assert.Empty(trace.Warnings);
	}

	[Fact]
	public async Task ProcessEventsAsync_McpServersLoaded_NullStatuses_HandlesGracefully()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = null
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Empty(processor.McpServerStatuses);
		_reporter.Received(1).ReportMcpServersLoaded(
			Arg.Is<IReadOnlyList<McpServerStatusInfo>>(list => list.Count == 0));
	}

	[Fact]
	public async Task ProcessEventsAsync_McpServersLoaded_MultipleFailedServers_GeneratesMultipleWarnings()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Failed", Error: "timeout"),
			new("graph", "Failed", Error: "auth error"),
			new("search", "Connected")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var trace = processor.BuildTrace("sys", "user");
		Assert.Equal(2, trace.Warnings.Count);
		Assert.Contains("icm", trace.Warnings[0]);
		Assert.Contains("timeout", trace.Warnings[0]);
		Assert.Contains("graph", trace.Warnings[1]);
		Assert.Contains("auth error", trace.Warnings[1]);
	}

	#endregion

	#region MCP Server Status Changed

	[Fact]
	public async Task ProcessEventsAsync_McpServerStatusChanged_ReportsStatusChange()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServerStatusChanged,
				McpServerName = "icm",
				McpServerStatus = "Connected"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportMcpServerStatusChanged("icm", "Connected");
	}

	[Fact]
	public async Task ProcessEventsAsync_McpServerStatusChanged_WithNullValues_UsesDefaults()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServerStatusChanged,
				McpServerName = null,
				McpServerStatus = null
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportMcpServerStatusChanged("unknown", "unknown");
	}

	[Fact]
	public async Task ProcessEventsAsync_McpServerStatusChanged_DoesNotAffectResponseSegments()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Before" },
			new AgentEvent
			{
				Type = AgentEventType.McpServerStatusChanged,
				McpServerName = "icm",
				McpServerStatus = "Connected"
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = " after" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert - MCP status events should NOT split response segments
		Assert.Single(processor.ResponseSegments);
		Assert.Equal("Before after", processor.ResponseSegments[0]);
	}

	#endregion

	#region BuildTrace with Runtime MCP Statuses

	[Fact]
	public async Task BuildTrace_WithRuntimeMcpStatuses_OverridesConfigDescriptions()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected", Source: "local"),
			new("graph", "Failed", Source: "remote", Error: "timeout")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		await processor.ProcessEventsAsync(events);

		// Act — config descriptions should be overridden by runtime statuses
		var trace = processor.BuildTrace(
			systemPrompt: "sys",
			userPromptRaw: "user",
			mcpServers: new List<string> { "icm (local: dnx Icm.Mcp ...)", "graph (remote: https://graph.example.com)" }
		);

		// Assert
		Assert.Equal(2, trace.McpServers.Count);
		Assert.Contains("icm", trace.McpServers[0]);
		Assert.Contains("Connected", trace.McpServers[0]);
		Assert.Contains("local", trace.McpServers[0]);
		Assert.Contains("graph", trace.McpServers[1]);
		Assert.Contains("Failed", trace.McpServers[1]);
		Assert.Contains("timeout", trace.McpServers[1]);
	}

	[Fact]
	public async Task BuildPartialTrace_WithRuntimeMcpStatuses_OverridesConfigDescriptions()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected", Source: "local")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		await processor.ProcessEventsAsync(events);

		// Act
		var trace = processor.BuildPartialTrace(
			systemPrompt: "sys",
			userPromptRaw: "user",
			mcpServers: new List<string> { "icm (local: dnx Icm.Mcp ...)" }
		);

		// Assert
		Assert.Single(trace.McpServers);
		Assert.Contains("Connected", trace.McpServers[0]);
	}

	[Fact]
	public async Task BuildTrace_WithRuntimeMcpStatuses_NoConfigDescriptions_UsesRuntimeOnly()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		await processor.ProcessEventsAsync(events);

		// Act
		var trace = processor.BuildTrace("sys", "user");

		// Assert
		Assert.Single(trace.McpServers);
		Assert.Contains("icm", trace.McpServers[0]);
		Assert.Contains("Connected", trace.McpServers[0]);
	}

	#endregion

	#region BuildTrace with MCP Servers and Warnings

	[Fact]
	public void BuildTrace_WithMcpServers_IncludesMcpServersInTrace()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var mcpServers = new List<string>
		{
			"icm (local: dnx Icm.Mcp ...)",
			"graph (remote: https://graph.example.com)"
		};

		// Act
		var trace = processor.BuildTrace(
			systemPrompt: "System",
			userPromptRaw: "User",
			mcpServers: mcpServers
		);

		// Assert
		Assert.Equal(2, trace.McpServers.Count);
		Assert.Equal("icm (local: dnx Icm.Mcp ...)", trace.McpServers[0]);
		Assert.Equal("graph (remote: https://graph.example.com)", trace.McpServers[1]);
	}

	[Fact]
	public void BuildTrace_WithoutMcpServers_HasEmptyList()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");

		// Act
		var trace = processor.BuildTrace("sys", "user");

		// Assert
		Assert.Empty(trace.McpServers);
	}

	[Fact]
	public void BuildPartialTrace_WithMcpServers_IncludesMcpServersInTrace()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var mcpServers = new List<string> { "icm (local: dnx Icm.Mcp ...)" };

		// Act
		var trace = processor.BuildPartialTrace("sys", "user", mcpServers);

		// Assert
		Assert.Single(trace.McpServers);
		Assert.Equal("icm (local: dnx Icm.Mcp ...)", trace.McpServers[0]);
	}

	[Fact]
	public void BuildPartialTrace_WithoutMcpServers_HasEmptyList()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");

		// Act
		var trace = processor.BuildPartialTrace("sys", "user");

		// Assert
		Assert.Empty(trace.McpServers);
	}

	[Fact]
	public async Task BuildTrace_AfterWarnings_IncludesWarningsInTrace()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.Warning,
				ErrorMessage = "MCP failure",
				DiagnosticType = "mcp_error"
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Response" }
		);

		// Act
		await processor.ProcessEventsAsync(events);
		var trace = processor.BuildTrace("sys", "user", mcpServers: new List<string> { "icm (local: dnx Icm.Mcp)" });

		// Assert
		Assert.Single(trace.Warnings);
		Assert.Equal("[mcp_error] MCP failure", trace.Warnings[0]);
		Assert.Single(trace.McpServers);
		Assert.Equal("icm (local: dnx Icm.Mcp)", trace.McpServers[0]);
	}

	[Fact]
	public async Task BuildPartialTrace_AfterWarnings_IncludesWarningsInTrace()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.Warning,
				ErrorMessage = "MCP failure",
				DiagnosticType = "mcp_error"
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);
		var trace = processor.BuildPartialTrace("sys", "user", mcpServers: new List<string> { "icm (local: dnx Icm.Mcp)" });

		// Assert
		Assert.Single(trace.Warnings);
		Assert.Equal("[mcp_error] MCP failure", trace.Warnings[0]);
		Assert.Single(trace.McpServers);
	}

	[Fact]
	public async Task ProcessEventsAsync_WarningsDoNotAffectResponseSegments()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Before warning" },
			new AgentEvent
			{
				Type = AgentEventType.Warning,
				ErrorMessage = "Some warning",
				DiagnosticType = "test_warning"
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = " after warning" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert - Warning should NOT split response segments
		Assert.Single(processor.ResponseSegments);
		Assert.Equal("Before warning after warning", processor.ResponseSegments[0]);
	}

	#endregion

	#region GetFailedMcpServers

	[Fact]
	public async Task GetFailedMcpServers_NoMcpEvents_ReturnsEmpty()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Hello" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Empty(processor.GetFailedMcpServers());
	}

	[Fact]
	public async Task GetFailedMcpServers_AllConnected_ReturnsEmpty()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected"),
			new("graph", "Connected")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Empty(processor.GetFailedMcpServers());
	}

	[Fact]
	public async Task GetFailedMcpServers_OneServerFailed_ReturnsFailedServerName()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Connected"),
			new("graph", "Failed", Error: "Connection refused")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var failed = processor.GetFailedMcpServers();
		Assert.Single(failed);
		Assert.Equal("graph", failed[0]);
	}

	[Fact]
	public async Task GetFailedMcpServers_MultipleServersFailed_ReturnsAllFailedNames()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "Failed", Error: "Timeout"),
			new("graph", "Failed", Error: "Connection refused"),
			new("other", "Connected")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var failed = processor.GetFailedMcpServers();
		Assert.Equal(2, failed.Count);
		Assert.Contains("icm", failed);
		Assert.Contains("graph", failed);
	}

	[Fact]
	public async Task GetFailedMcpServers_CaseInsensitiveStatusComparison()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var statuses = new List<McpServerStatusInfo>
		{
			new("icm", "FAILED"),
			new("graph", "failed")
		};
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = statuses
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var failed = processor.GetFailedMcpServers();
		Assert.Equal(2, failed.Count);
	}

	#endregion

	#region Compaction Events

	[Fact]
	public async Task ProcessEventsAsync_CompactionStart_CollectsWarning()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.CompactionStart }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		_reporter.Received(1).ReportSessionWarning("compaction", "Context compaction started");
		var trace = processor.BuildTrace("sys", "user");
		Assert.Single(trace.Warnings);
		Assert.Contains("compaction", trace.Warnings[0]);
	}

	[Fact]
	public async Task ProcessEventsAsync_CompactionComplete_CollectsWarningWithTokenCounts()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.CompactionStart },
			new AgentEvent
			{
				Type = AgentEventType.CompactionComplete,
				CompactionTokensBefore = 50000,
				CompactionTokensAfter = 25000
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		var trace = processor.BuildTrace("sys", "user");
		Assert.Equal(2, trace.Warnings.Count);
		Assert.Contains("50000", trace.Warnings[1]);
		Assert.Contains("25000", trace.Warnings[1]);
	}

	#endregion

	#region Audit Log

	[Fact]
	public void AddAuditLogEntry_CollectsEntries()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");

		// Act
		processor.AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 1,
			Timestamp = DateTimeOffset.UtcNow,
			EventType = AuditEventType.SessionStart,
			SessionSource = "startup"
		});
		processor.AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 2,
			Timestamp = DateTimeOffset.UtcNow,
			EventType = AuditEventType.PreToolUse,
			ToolName = "read_file",
			ToolArguments = "{\"path\": \"test.txt\"}"
		});
		processor.AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 3,
			Timestamp = DateTimeOffset.UtcNow,
			EventType = AuditEventType.PostToolUse,
			ToolName = "read_file",
			ToolSuccess = true,
			ToolResult = "contents"
		});

		// Assert
		Assert.Equal(3, processor.AuditLog.Count);
		Assert.Equal(AuditEventType.SessionStart, processor.AuditLog[0].EventType);
		Assert.Equal(0, processor.AuditLog[0].Sequence);
		Assert.Equal(AuditEventType.PreToolUse, processor.AuditLog[1].EventType);
		Assert.Equal("read_file", processor.AuditLog[1].ToolName);
		Assert.Equal(1, processor.AuditLog[1].Sequence);
		Assert.Equal(AuditEventType.PostToolUse, processor.AuditLog[2].EventType);
		Assert.True(processor.AuditLog[2].ToolSuccess);
		Assert.Equal(2, processor.AuditLog[2].Sequence);
	}

	#endregion

	#region Hook Events

	[Fact]
	public async Task ProcessEventsAsync_HookStart_AddsAuditLogEntry()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.HookStart,
				HookInvocationId = "inv-001",
				HookType = "preToolUse",
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.AuditLog);
		Assert.Equal(AuditEventType.HookStart, processor.AuditLog[0].EventType);
		Assert.Equal("inv-001", processor.AuditLog[0].HookInvocationId);
		Assert.Equal("preToolUse", processor.AuditLog[0].HookType);
	}

	[Fact]
	public async Task ProcessEventsAsync_HookEnd_Success_AddsAuditLogEntry()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.HookEnd,
				HookInvocationId = "inv-001",
				HookType = "preToolUse",
				HookSuccess = true,
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.AuditLog);
		Assert.Equal(AuditEventType.HookEnd, processor.AuditLog[0].EventType);
		Assert.True(processor.AuditLog[0].HookSuccess);
		var trace = processor.BuildTrace("sys", "user");
		Assert.Empty(trace.Warnings);
	}

	[Fact]
	public async Task ProcessEventsAsync_HookEnd_Failure_AddsWarning()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.HookEnd,
				HookInvocationId = "inv-002",
				HookType = "postToolUse",
				HookSuccess = false,
				ErrorMessage = "Hook timed out",
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.AuditLog);
		Assert.False(processor.AuditLog[0].HookSuccess);
		var trace = processor.BuildTrace("sys", "user");
		Assert.Single(trace.Warnings);
		Assert.Contains("hook_failed", trace.Warnings[0]);
		Assert.Contains("postToolUse", trace.Warnings[0]);
		_reporter.Received(1).ReportSessionWarning("hook_failed", Arg.Is<string>(s => s.Contains("postToolUse")));
	}

	[Fact]
	public async Task ProcessEventsAsync_HookStartAndEnd_CorrelatesByInvocationId()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.HookStart,
				HookInvocationId = "inv-100",
				HookType = "preToolUse",
			},
			new AgentEvent
			{
				Type = AgentEventType.HookEnd,
				HookInvocationId = "inv-100",
				HookType = "preToolUse",
				HookSuccess = true,
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Equal(2, processor.AuditLog.Count);
		Assert.Equal("inv-100", processor.AuditLog[0].HookInvocationId);
		Assert.Equal("inv-100", processor.AuditLog[1].HookInvocationId);
	}

	#endregion

	#region Turn Start Events

	[Fact]
	public async Task ProcessEventsAsync_TurnStart_AddsAuditLogEntry()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.TurnStart,
				TurnId = "1",
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.AuditLog);
		Assert.Equal(AuditEventType.TurnStart, processor.AuditLog[0].EventType);
		Assert.Equal("1", processor.AuditLog[0].TurnId);
	}

	#endregion

	#region Turn End Events

	[Fact]
	public async Task ProcessEventsAsync_TurnEnd_AddsAuditLogEntry()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.TurnEnd,
				TurnId = "1",
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.AuditLog);
		Assert.Equal(AuditEventType.TurnEnd, processor.AuditLog[0].EventType);
		Assert.Equal("1", processor.AuditLog[0].TurnId);
	}

	[Fact]
	public async Task ProcessEventsAsync_TurnStartAndEnd_BothRecordedInAuditLog()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.TurnStart, TurnId = "1" },
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Hello" },
			new AgentEvent { Type = AgentEventType.TurnEnd, TurnId = "1" }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Equal(2, processor.AuditLog.Count);
		Assert.Equal(AuditEventType.TurnStart, processor.AuditLog[0].EventType);
		Assert.Equal(AuditEventType.TurnEnd, processor.AuditLog[1].EventType);
		Assert.Equal("1", processor.AuditLog[0].TurnId);
		Assert.Equal("1", processor.AuditLog[1].TurnId);
	}

	#endregion

	#region Session Usage Info Events

	[Fact]
	public async Task ProcessEventsAsync_SessionUsageInfo_AddsAuditLogEntry()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent
			{
				Type = AgentEventType.SessionUsageInfo,
				TokenLimit = 128000,
				CurrentTokens = 15000,
			}
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.AuditLog);
		Assert.Equal(AuditEventType.SessionUsageInfo, processor.AuditLog[0].EventType);
		Assert.Equal(128000, processor.AuditLog[0].TokenLimit);
		Assert.Equal(15000, processor.AuditLog[0].CurrentTokens);
	}

	[Fact]
	public async Task ProcessEventsAsync_SessionUsageInfo_DoesNotAffectResponseSegments()
	{
		// Arrange
		var processor = new AgentEventProcessor(_reporter, "test-step");
		var events = CreateAsyncEnumerable(
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Hello" },
			new AgentEvent { Type = AgentEventType.SessionUsageInfo, TokenLimit = 128000, CurrentTokens = 5000 }
		);

		// Act
		await processor.ProcessEventsAsync(events);

		// Assert
		Assert.Single(processor.ResponseSegments);
		Assert.Equal("Hello", processor.ResponseSegments[0]);
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

	#region SDK 0.3.0 Telemetry forwarding

	[Fact]
	public async Task ProcessEventsAsync_AutoModeSwitchRequested_CallsReporterAndAddsAuditEntry()
	{
		var processor = new AgentEventProcessor(_reporter, "step-x");
		var events = CreateAsyncEnumerable(new AgentEvent
		{
			Type = AgentEventType.AutoModeSwitchRequested,
			AutoModeRequestId = "req-99",
			AutoModeErrorCode = "rate_limited",
		});

		await processor.ProcessEventsAsync(events);

		_reporter.Received(1).ReportAutoModeSwitchRequested("step-x", "req-99", "rate_limited");
		_reporter.Received(1).ReportSessionInfo("auto_mode_switch_requested", Arg.Any<string>());
		Assert.Contains(processor.AuditLog, e => e.EventType == AuditEventType.AutoModeSwitchRequested
			&& e.AutoModeRequestId == "req-99" && e.AutoModeErrorCode == "rate_limited");
	}

	[Fact]
	public async Task ProcessEventsAsync_AutoModeSwitchCompleted_CallsReporterAndAddsAuditEntry()
	{
		var processor = new AgentEventProcessor(_reporter, "step-x");
		var events = CreateAsyncEnumerable(new AgentEvent
		{
			Type = AgentEventType.AutoModeSwitchCompleted,
			AutoModeRequestId = "req-99",
			AutoModeResponse = "claude-sonnet-4.5",
		});

		await processor.ProcessEventsAsync(events);

		_reporter.Received(1).ReportAutoModeSwitchCompleted("step-x", "req-99", "claude-sonnet-4.5");
		Assert.Contains(processor.AuditLog, e => e.EventType == AuditEventType.AutoModeSwitchCompleted
			&& e.AutoModeRequestId == "req-99" && e.AutoModeResponse == "claude-sonnet-4.5");
	}

	[Fact]
	public async Task ProcessEventsAsync_SystemNotification_CallsReporterAndAddsAuditEntry()
	{
		var processor = new AgentEventProcessor(_reporter, "step-x");
		var events = CreateAsyncEnumerable(new AgentEvent
		{
			Type = AgentEventType.SystemNotification,
			NotificationKind = "shell_completed",
			NotificationMessage = "shell finished",
		});

		await processor.ProcessEventsAsync(events);

		_reporter.Received(1).ReportSystemNotification("step-x", "shell_completed", "shell finished");
		Assert.Contains(processor.AuditLog, e => e.EventType == AuditEventType.SystemNotification
			&& e.NotificationKind == "shell_completed" && e.NotificationMessage == "shell finished");
	}

	[Fact]
	public async Task ProcessEventsAsync_QuotaSnapshotWithEntries_CallsReporter()
	{
		var processor = new AgentEventProcessor(_reporter, "step-x");
		var snapshots = new Dictionary<string, AgentQuotaSnapshot>
		{
			["premium-requests"] = new(
				EntitlementRequests: 1500,
				UsedRequests: 500,
				RemainingPercentage: 0.667,
				Overage: 0,
				IsUnlimitedEntitlement: false,
				UsageAllowedWithExhaustedQuota: true,
				OverageAllowedWithExhaustedQuota: false,
				ResetDate: DateTimeOffset.UtcNow.AddDays(7))
		};
		var events = CreateAsyncEnumerable(new AgentEvent
		{
			Type = AgentEventType.QuotaSnapshot,
			QuotaSnapshots = snapshots,
		});

		await processor.ProcessEventsAsync(events);

		_reporter.Received(1).ReportQuotaSnapshot("step-x", snapshots);
		Assert.Contains(processor.AuditLog, e => e.EventType == AuditEventType.QuotaSnapshot);
	}

	[Fact]
	public async Task ProcessEventsAsync_QuotaSnapshotEmpty_NoReporterCall()
	{
		var processor = new AgentEventProcessor(_reporter, "step-x");
		var events = CreateAsyncEnumerable(new AgentEvent
		{
			Type = AgentEventType.QuotaSnapshot,
			QuotaSnapshots = null,
		});

		await processor.ProcessEventsAsync(events);

		_reporter.DidNotReceive().ReportQuotaSnapshot(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, AgentQuotaSnapshot>>());
		Assert.DoesNotContain(processor.AuditLog, e => e.EventType == AuditEventType.QuotaSnapshot);
	}

	#endregion
}
