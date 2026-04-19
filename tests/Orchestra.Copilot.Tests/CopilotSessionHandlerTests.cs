using System.Threading.Channels;
using FluentAssertions;
using GitHub.Copilot.SDK;
using NSubstitute;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

public class CopilotSessionHandlerTests
{
	private readonly Channel<AgentEvent> _channel;
	private readonly IOrchestrationReporter _reporter;
	private readonly TaskCompletionSource _done;
	private readonly CopilotSessionHandler _handler;
	private const string RequestedModel = "claude-opus-4.5";

	public CopilotSessionHandlerTests()
	{
		_channel = Channel.CreateUnbounded<AgentEvent>();
		_reporter = Substitute.For<IOrchestrationReporter>();
		_done = new TaskCompletionSource();
		_handler = new CopilotSessionHandler(_channel.Writer, _reporter, RequestedModel, _done);
	}

	#region Test Data Helpers

	private static SessionStartEvent CreateSessionStartEvent(string selectedModel = "claude-opus-4.5") => new()
	{
		Data = new SessionStartData
		{
			SessionId = "test-session-id",
			Version = 1.0,
			Producer = "test-producer",
			CopilotVersion = "1.0.0",
			StartTime = DateTimeOffset.UtcNow,
			SelectedModel = selectedModel
		}
	};

	private static SessionModelChangeEvent CreateModelChangeEvent(string previousModel, string newModel) => new()
	{
		Data = new SessionModelChangeData { PreviousModel = previousModel, NewModel = newModel }
	};

	private static AssistantUsageEvent CreateUsageEvent(
		string model = "claude-opus-4.5",
		int inputTokens = 100,
		int outputTokens = 50,
		int cacheReadTokens = 10,
		int cacheWriteTokens = 5,
		double cost = 0.001,
		double duration = 1.5) => new()
	{
		Data = new AssistantUsageData
		{
			Model = model,
			InputTokens = inputTokens,
			OutputTokens = outputTokens,
			CacheReadTokens = cacheReadTokens,
			CacheWriteTokens = cacheWriteTokens,
			Cost = cost,
			Duration = duration
		}
	};

	private static AssistantMessageDeltaEvent CreateMessageDeltaEvent(string deltaContent) => new()
	{
		Data = new AssistantMessageDeltaData
		{
			MessageId = "test-message-id",
			DeltaContent = deltaContent
		}
	};

	private static AssistantReasoningDeltaEvent CreateReasoningDeltaEvent(string deltaContent) => new()
	{
		Data = new AssistantReasoningDeltaData
		{
			ReasoningId = "test-reasoning-id",
			DeltaContent = deltaContent
		}
	};

	private static AssistantMessageEvent CreateMessageEvent(string content) => new()
	{
		Data = new AssistantMessageData
		{
			MessageId = "test-message-id",
			Content = content
		}
	};

	private static AssistantReasoningEvent CreateReasoningEvent(string content) => new()
	{
		Data = new AssistantReasoningData
		{
			ReasoningId = "test-reasoning-id",
			Content = content
		}
	};

	private static ToolExecutionStartEvent CreateToolStartEvent(
		string toolCallId,
		string toolName,
		string? mcpToolName = null,
		string? mcpServerName = null,
		Dictionary<string, object>? arguments = null) => new()
	{
		Data = new ToolExecutionStartData
		{
			ToolCallId = toolCallId,
			ToolName = toolName,
			McpToolName = mcpToolName,
			McpServerName = mcpServerName,
			Arguments = arguments
		}
	};

	private static ToolExecutionCompleteEvent CreateToolCompleteEvent(
		string toolCallId,
		bool success) => new()
	{
		Data = new ToolExecutionCompleteData
		{
			ToolCallId = toolCallId,
			Success = success,
			Result = null,
			Error = null
		}
	};

	private static SessionErrorEvent CreateErrorEvent(string message) => new()
	{
		Data = new SessionErrorData
		{
			ErrorType = "TestError",
			Message = message
		}
	};

	private static SessionIdleEvent CreateIdleEvent() => new()
	{
		Data = new SessionIdleData()
	};

	private static SubagentSelectedEvent CreateSubagentSelectedEvent(
		string agentName,
		string? displayName = null,
		string[]? tools = null) => new()
	{
		Data = new SubagentSelectedData
		{
			AgentName = agentName,
			AgentDisplayName = displayName!,
			Tools = tools!
		}
	};

	private static SubagentStartedEvent CreateSubagentStartedEvent(
		string agentName,
		string? toolCallId = null,
		string? displayName = null,
		string? description = null) => new()
	{
		Data = new SubagentStartedData
		{
			ToolCallId = toolCallId!,
			AgentName = agentName,
			AgentDisplayName = displayName!,
			AgentDescription = description!
		}
	};

	private static SubagentCompletedEvent CreateSubagentCompletedEvent(
		string agentName,
		string? toolCallId = null,
		string? displayName = null) => new()
	{
		Data = new SubagentCompletedData
		{
			ToolCallId = toolCallId!,
			AgentName = agentName,
			AgentDisplayName = displayName!
		}
	};

	private static SubagentFailedEvent CreateSubagentFailedEvent(
		string agentName,
		string? toolCallId = null,
		string? displayName = null,
		string? error = null) => new()
	{
		Data = new SubagentFailedData
		{
			ToolCallId = toolCallId!,
			AgentName = agentName,
			AgentDisplayName = displayName!,
			Error = error!
		}
	};

	private static SubagentDeselectedEvent CreateSubagentDeselectedEvent() => new()
	{
		Data = new SubagentDeselectedData()
	};

	private static SessionWarningEvent CreateWarningEvent(string warningType, string message) => new()
	{
		Data = new SessionWarningData
		{
			WarningType = warningType,
			Message = message
		}
	};

	private static SessionInfoEvent CreateInfoEvent(string infoType, string message) => new()
	{
		Data = new SessionInfoData
		{
			InfoType = infoType,
			Message = message
		}
	};

	#endregion

	#region Session Start

	[Fact]
	public void HandleEvent_SessionStart_WritesSessionStartEvent()
	{
		// Arrange
		var sessionStartEvent = CreateSessionStartEvent("claude-opus-4.5");

		// Act
		_handler.HandleEvent(sessionStartEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SessionStart);
		agentEvent.Model.Should().Be("claude-opus-4.5");
	}

	[Fact]
	public void HandleEvent_SessionStart_SetsSelectedModel()
	{
		// Arrange
		var sessionStartEvent = CreateSessionStartEvent("gpt-4-turbo");

		// Act
		_handler.HandleEvent(sessionStartEvent);

		// Assert
		_handler.SelectedModel.Should().Be("gpt-4-turbo");
	}

	[Fact]
	public void HandleEvent_SessionStart_ReportsSessionStarted()
	{
		// Arrange
		var sessionStartEvent = CreateSessionStartEvent("model-a");

		// Act
		_handler.HandleEvent(sessionStartEvent);

		// Assert
		_reporter.Received(1).ReportSessionStarted(RequestedModel, "model-a");
	}

	#endregion

	#region Model Change

	[Fact]
	public void HandleEvent_ModelChange_WritesModelChangeEvent()
	{
		// Arrange
		var modelChangeEvent = CreateModelChangeEvent("model-a", "model-b");

		// Act
		_handler.HandleEvent(modelChangeEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.ModelChange);
		agentEvent.Model.Should().Be("model-b");
		agentEvent.PreviousModel.Should().Be("model-a");
	}

	[Fact]
	public void HandleEvent_ModelChange_ReportsModelChange()
	{
		// Arrange
		var modelChangeEvent = CreateModelChangeEvent("model-a", "model-b");

		// Act
		_handler.HandleEvent(modelChangeEvent);

		// Assert
		_reporter.Received(1).ReportModelChange("model-a", "model-b");
	}

	#endregion

	#region Usage

	[Fact]
	public void HandleEvent_Usage_WritesUsageEvent()
	{
		// Arrange
		var usageEvent = CreateUsageEvent(
			model: "claude-opus-4.5",
			inputTokens: 100,
			outputTokens: 50,
			cacheReadTokens: 10,
			cacheWriteTokens: 5,
			cost: 0.001,
			duration: 1.5);

		// Act
		_handler.HandleEvent(usageEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.Usage);
		agentEvent.Model.Should().Be("claude-opus-4.5");
		agentEvent.Usage.Should().NotBeNull();
		agentEvent.Usage!.InputTokens.Should().Be(100);
		agentEvent.Usage.OutputTokens.Should().Be(50);
	}

	[Fact]
	public void HandleEvent_Usage_SetsActualModel()
	{
		// Arrange
		var usageEvent = CreateUsageEvent(model: "actual-model");

		// Act
		_handler.HandleEvent(usageEvent);

		// Assert
		_handler.ActualModel.Should().Be("actual-model");
	}

	[Fact]
	public void HandleEvent_Usage_SetsUsageProperty()
	{
		// Arrange
		var usageEvent = CreateUsageEvent(
			model: "model",
			inputTokens: 200,
			outputTokens: 100,
			cacheReadTokens: 20,
			cacheWriteTokens: 10,
			cost: 0.002,
			duration: 2.0);

		// Act
		_handler.HandleEvent(usageEvent);

		// Assert
		_handler.Usage.Should().NotBeNull();
		_handler.Usage!.InputTokens.Should().Be(200);
		_handler.Usage.OutputTokens.Should().Be(100);
		_handler.Usage.CacheReadTokens.Should().Be(20);
		_handler.Usage.CacheWriteTokens.Should().Be(10);
		_handler.Usage.Cost.Should().Be(0.002);
		_handler.Usage.Duration.Should().Be(2.0);
	}

	#endregion

	#region Message Delta

	[Fact]
	public void HandleEvent_MessageDelta_WritesMessageDeltaEvent()
	{
		// Arrange
		var deltaEvent = CreateMessageDeltaEvent("Hello ");

		// Act
		_handler.HandleEvent(deltaEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.MessageDelta);
		agentEvent.Content.Should().Be("Hello ");
	}

	#endregion

	#region Reasoning Delta

	[Fact]
	public void HandleEvent_ReasoningDelta_WritesReasoningDeltaEvent()
	{
		// Arrange
		var deltaEvent = CreateReasoningDeltaEvent("Thinking...");

		// Act
		_handler.HandleEvent(deltaEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.ReasoningDelta);
		agentEvent.Content.Should().Be("Thinking...");
	}

	#endregion

	#region Message

	[Fact]
	public void HandleEvent_Message_WritesMessageEvent()
	{
		// Arrange
		var messageEvent = CreateMessageEvent("Final response content");

		// Act
		_handler.HandleEvent(messageEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.Message);
		agentEvent.Content.Should().Be("Final response content");
	}

	[Fact]
	public void HandleEvent_Message_SetsFinalContent()
	{
		// Arrange
		var messageEvent = CreateMessageEvent("The final answer");

		// Act
		_handler.HandleEvent(messageEvent);

		// Assert
		_handler.FinalContent.Should().Be("The final answer");
	}

	#endregion

	#region Reasoning

	[Fact]
	public void HandleEvent_Reasoning_WritesReasoningEvent()
	{
		// Arrange
		var reasoningEvent = CreateReasoningEvent("Full reasoning content");

		// Act
		_handler.HandleEvent(reasoningEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.Reasoning);
		agentEvent.Content.Should().Be("Full reasoning content");
	}

	#endregion

	#region Tool Execution Start

	[Fact]
	public void HandleEvent_ToolExecutionStart_WritesToolExecutionStartEvent()
	{
		// Arrange
		var toolStartEvent = CreateToolStartEvent(
			toolCallId: "call-123",
			toolName: "read_file",
			mcpToolName: "fs_read_file",
			mcpServerName: "filesystem",
			arguments: new Dictionary<string, object> { ["path"] = "/test.txt" });

		// Act
		_handler.HandleEvent(toolStartEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.ToolExecutionStart);
		agentEvent.ToolCallId.Should().Be("call-123");
		agentEvent.ToolName.Should().Be("fs_read_file"); // Uses McpToolName when present
		agentEvent.McpServerName.Should().Be("filesystem");
		agentEvent.ToolArguments.Should().Contain("/test.txt");
	}

	[Fact]
	public void HandleEvent_ToolExecutionStart_UsesToolNameWhenMcpToolNameIsNull()
	{
		// Arrange
		var toolStartEvent = CreateToolStartEvent(
			toolCallId: "call-456",
			toolName: "search",
			mcpToolName: null);

		// Act
		_handler.HandleEvent(toolStartEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.ToolName.Should().Be("search");
	}

	[Fact]
	public void HandleEvent_ToolExecutionStart_TracksToolCallIdForCorrelation()
	{
		// Arrange
		var toolStartEvent = CreateToolStartEvent(toolCallId: "correlation-id", toolName: "my_tool");

		// Act
		_handler.HandleEvent(toolStartEvent);

		// Then complete the tool
		var completeEvent = CreateToolCompleteEvent(toolCallId: "correlation-id", success: true);
		_handler.HandleEvent(completeEvent);

		// Assert - Read both events
		_channel.Reader.TryRead(out _); // Skip start event
		_channel.Reader.TryRead(out var completeAgentEvent).Should().BeTrue();
		completeAgentEvent!.ToolName.Should().Be("my_tool"); // Correlated from start event
	}

	[Fact]
	public void HandleEvent_ToolExecutionStart_HandlesNullArguments()
	{
		// Arrange
		var toolStartEvent = CreateToolStartEvent(
			toolCallId: "call-789",
			toolName: "simple_tool",
			arguments: null);

		// Act
		_handler.HandleEvent(toolStartEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.ToolArguments.Should().BeNull();
	}

	#endregion

	#region Tool Execution Complete

	[Fact]
	public void HandleEvent_ToolExecutionComplete_WritesToolExecutionCompleteEvent()
	{
		// Arrange
		var completeEvent = CreateToolCompleteEvent(
			toolCallId: "call-abc",
			success: true);

		// Act
		_handler.HandleEvent(completeEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.ToolExecutionComplete);
		agentEvent.ToolCallId.Should().Be("call-abc");
		agentEvent.ToolSuccess.Should().BeTrue();
	}

	[Fact]
	public void HandleEvent_ToolExecutionComplete_WithFailure_SetsToolSuccessFalse()
	{
		// Arrange
		var completeEvent = CreateToolCompleteEvent(
			toolCallId: "call-error",
			success: false);

		// Act
		_handler.HandleEvent(completeEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.ToolSuccess.Should().BeFalse();
	}

	#endregion

	#region Error

	[Fact]
	public void HandleEvent_Error_WritesErrorEvent()
	{
		// Arrange
		var errorEvent = CreateErrorEvent("Session error occurred");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.Error);
		agentEvent.ErrorMessage.Should().Be("Session error occurred");
	}

	#endregion

	#region Idle

	[Fact]
	public void HandleEvent_Idle_WritesSessionIdleEvent()
	{
		// Arrange
		var idleEvent = CreateIdleEvent();

		// Act
		_handler.HandleEvent(idleEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SessionIdle);
	}

	[Fact]
	public void HandleEvent_Idle_CompletesTaskCompletionSource()
	{
		// Arrange
		var idleEvent = CreateIdleEvent();

		// Act
		_handler.HandleEvent(idleEvent);

		// Assert
		_done.Task.IsCompleted.Should().BeTrue();
	}

	#endregion

	#region Subagent Selected

	[Fact]
	public void HandleEvent_SubagentSelected_WritesSubagentSelectedEvent()
	{
		// Arrange
		var subagentSelectedEvent = CreateSubagentSelectedEvent(
			agentName: "researcher",
			displayName: "Research Agent",
			tools: ["web_search", "read_file"]);

		// Act
		_handler.HandleEvent(subagentSelectedEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SubagentSelected);
		agentEvent.SubagentName.Should().Be("researcher");
		agentEvent.SubagentDisplayName.Should().Be("Research Agent");
		agentEvent.SubagentTools.Should().BeEquivalentTo(["web_search", "read_file"]);
	}

	[Fact]
	public void HandleEvent_SubagentSelected_WithNullOptionalFields_HandlesGracefully()
	{
		// Arrange
		var subagentSelectedEvent = CreateSubagentSelectedEvent(
			agentName: "minimal-agent",
			displayName: null,
			tools: null);

		// Act
		_handler.HandleEvent(subagentSelectedEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.SubagentName.Should().Be("minimal-agent");
		agentEvent.SubagentDisplayName.Should().BeNull();
		agentEvent.SubagentTools.Should().BeNull();
	}

	#endregion

	#region Subagent Started

	[Fact]
	public void HandleEvent_SubagentStarted_WritesSubagentStartedEvent()
	{
		// Arrange
		var subagentStartedEvent = CreateSubagentStartedEvent(
			agentName: "writer",
			toolCallId: "call-123",
			displayName: "Writer Agent",
			description: "Specializes in writing content");

		// Act
		_handler.HandleEvent(subagentStartedEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SubagentStarted);
		agentEvent.ToolCallId.Should().Be("call-123");
		agentEvent.SubagentName.Should().Be("writer");
		agentEvent.SubagentDisplayName.Should().Be("Writer Agent");
		agentEvent.SubagentDescription.Should().Be("Specializes in writing content");
	}

	[Fact]
	public void HandleEvent_SubagentStarted_WithNullToolCallId_HandlesGracefully()
	{
		// Arrange
		var subagentStartedEvent = CreateSubagentStartedEvent(
			agentName: "simple-agent",
			toolCallId: null);

		// Act
		_handler.HandleEvent(subagentStartedEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.ToolCallId.Should().BeNull();
		agentEvent.SubagentName.Should().Be("simple-agent");
	}

	#endregion

	#region Subagent Completed

	[Fact]
	public void HandleEvent_SubagentCompleted_WritesSubagentCompletedEvent()
	{
		// Arrange
		var subagentCompletedEvent = CreateSubagentCompletedEvent(
			agentName: "researcher",
			toolCallId: "call-456",
			displayName: "Research Agent");

		// Act
		_handler.HandleEvent(subagentCompletedEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SubagentCompleted);
		agentEvent.ToolCallId.Should().Be("call-456");
		agentEvent.SubagentName.Should().Be("researcher");
		agentEvent.SubagentDisplayName.Should().Be("Research Agent");
	}

	#endregion

	#region Subagent Failed

	[Fact]
	public void HandleEvent_SubagentFailed_WritesSubagentFailedEvent()
	{
		// Arrange
		var subagentFailedEvent = CreateSubagentFailedEvent(
			agentName: "failing-agent",
			toolCallId: "call-789",
			displayName: "Failing Agent",
			error: "Agent crashed unexpectedly");

		// Act
		_handler.HandleEvent(subagentFailedEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SubagentFailed);
		agentEvent.ToolCallId.Should().Be("call-789");
		agentEvent.SubagentName.Should().Be("failing-agent");
		agentEvent.SubagentDisplayName.Should().Be("Failing Agent");
		agentEvent.ErrorMessage.Should().Be("Agent crashed unexpectedly");
	}

	[Fact]
	public void HandleEvent_SubagentFailed_WithNullError_HandlesGracefully()
	{
		// Arrange
		var subagentFailedEvent = CreateSubagentFailedEvent(
			agentName: "agent",
			error: null);

		// Act
		_handler.HandleEvent(subagentFailedEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.ErrorMessage.Should().BeNull();
	}

	#endregion

	#region Subagent Deselected

	[Fact]
	public void HandleEvent_SubagentDeselected_WritesSubagentDeselectedEvent()
	{
		// Arrange
		var subagentDeselectedEvent = CreateSubagentDeselectedEvent();

		// Act
		_handler.HandleEvent(subagentDeselectedEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SubagentDeselected);
	}

	#endregion

	#region Full Session Flow

	[Fact]
	public void HandleEvent_FullSessionFlow_ProcessesAllEventsCorrectly()
	{
		// Arrange & Act - Simulate a full session
		_handler.HandleEvent(CreateSessionStartEvent("claude-opus-4.5"));
		_handler.HandleEvent(CreateReasoningDeltaEvent("Let me think..."));
		_handler.HandleEvent(CreateToolStartEvent("tool-1", "read_file"));
		_handler.HandleEvent(CreateToolCompleteEvent("tool-1", true));
		_handler.HandleEvent(CreateMessageDeltaEvent("Based on the file..."));
		_handler.HandleEvent(CreateMessageEvent("The final answer is 42."));
		_handler.HandleEvent(CreateUsageEvent("claude-opus-4.5", 100, 50));
		_handler.HandleEvent(CreateIdleEvent());

		// Assert
		_handler.SelectedModel.Should().Be("claude-opus-4.5");
		_handler.ActualModel.Should().Be("claude-opus-4.5");
		_handler.FinalContent.Should().Be("The final answer is 42.");
		_handler.Usage.Should().NotBeNull();
		_done.Task.IsCompleted.Should().BeTrue();

		// Verify all events were written
		var events = new List<AgentEvent>();
		while (_channel.Reader.TryRead(out var evt))
		{
			events.Add(evt);
		}

		events.Should().HaveCount(8);
		events[0].Type.Should().Be(AgentEventType.SessionStart);
		events[1].Type.Should().Be(AgentEventType.ReasoningDelta);
		events[2].Type.Should().Be(AgentEventType.ToolExecutionStart);
		events[3].Type.Should().Be(AgentEventType.ToolExecutionComplete);
		events[4].Type.Should().Be(AgentEventType.MessageDelta);
		events[5].Type.Should().Be(AgentEventType.Message);
		events[6].Type.Should().Be(AgentEventType.Usage);
		events[7].Type.Should().Be(AgentEventType.SessionIdle);
	}

	[Fact]
	public void HandleEvent_FullSessionFlowWithSubagents_ProcessesAllEventsCorrectly()
	{
		// Arrange & Act - Simulate a session with subagent delegation
		_handler.HandleEvent(CreateSessionStartEvent("claude-opus-4.5"));
		_handler.HandleEvent(CreateMessageDeltaEvent("Let me delegate to a subagent..."));

		// Subagent lifecycle
		_handler.HandleEvent(CreateSubagentSelectedEvent("researcher", "Research Agent", ["web_search"]));
		_handler.HandleEvent(CreateSubagentStartedEvent("researcher", "call-sub-1", "Research Agent", "Finds info"));
		_handler.HandleEvent(CreateToolStartEvent("tool-1", "web_search"));
		_handler.HandleEvent(CreateToolCompleteEvent("tool-1", true));
		_handler.HandleEvent(CreateSubagentCompletedEvent("researcher", "call-sub-1", "Research Agent"));
		_handler.HandleEvent(CreateSubagentDeselectedEvent());

		// Back to main agent
		_handler.HandleEvent(CreateMessageDeltaEvent("Based on the research..."));
		_handler.HandleEvent(CreateMessageEvent("Here is the final answer."));
		_handler.HandleEvent(CreateUsageEvent("claude-opus-4.5", 200, 100));
		_handler.HandleEvent(CreateIdleEvent());

		// Assert
		_handler.FinalContent.Should().Be("Here is the final answer.");
		_done.Task.IsCompleted.Should().BeTrue();

		// Verify all events were written
		var events = new List<AgentEvent>();
		while (_channel.Reader.TryRead(out var evt))
		{
			events.Add(evt);
		}

		events.Should().HaveCount(12);
		events[0].Type.Should().Be(AgentEventType.SessionStart);
		events[1].Type.Should().Be(AgentEventType.MessageDelta);
		events[2].Type.Should().Be(AgentEventType.SubagentSelected);
		events[3].Type.Should().Be(AgentEventType.SubagentStarted);
		events[4].Type.Should().Be(AgentEventType.ToolExecutionStart);
		events[5].Type.Should().Be(AgentEventType.ToolExecutionComplete);
		events[6].Type.Should().Be(AgentEventType.SubagentCompleted);
		events[7].Type.Should().Be(AgentEventType.SubagentDeselected);
		events[8].Type.Should().Be(AgentEventType.MessageDelta);
		events[9].Type.Should().Be(AgentEventType.Message);
		events[10].Type.Should().Be(AgentEventType.Usage);
		events[11].Type.Should().Be(AgentEventType.SessionIdle);
	}

	[Fact]
	public void HandleEvent_SubagentFailureRecovery_ProcessesCorrectly()
	{
		// Arrange & Act - Simulate a session where subagent fails and main agent recovers
		_handler.HandleEvent(CreateSessionStartEvent("claude-opus-4.5"));

		// First subagent fails
		_handler.HandleEvent(CreateSubagentSelectedEvent("researcher", "Research Agent"));
		_handler.HandleEvent(CreateSubagentStartedEvent("researcher", "call-1", "Research Agent"));
		_handler.HandleEvent(CreateSubagentFailedEvent("researcher", "call-1", "Research Agent", "Network error"));

		// Main agent handles the failure
		_handler.HandleEvent(CreateMessageDeltaEvent("The researcher encountered an issue. "));
		_handler.HandleEvent(CreateMessageDeltaEvent("Let me try a different approach."));
		_handler.HandleEvent(CreateMessageEvent("I'll provide the answer directly."));
		_handler.HandleEvent(CreateIdleEvent());

		// Assert
		var events = new List<AgentEvent>();
		while (_channel.Reader.TryRead(out var evt))
		{
			events.Add(evt);
		}

		events.Should().HaveCount(8);
		events[3].Type.Should().Be(AgentEventType.SubagentFailed);
		events[3].ErrorMessage.Should().Be("Network error");
		_handler.FinalContent.Should().Be("I'll provide the answer directly.");
	}

	#endregion

	#region Warning

	[Fact]
	public void HandleEvent_Warning_WritesWarningEvent()
	{
		// Arrange
		var warningEvent = CreateWarningEvent("mcp_server_error", "Failed to start MCP server 'icm'");

		// Act
		_handler.HandleEvent(warningEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.Warning);
		agentEvent.ErrorMessage.Should().Be("Failed to start MCP server 'icm'");
		agentEvent.DiagnosticType.Should().Be("mcp_server_error");
	}

	[Fact]
	public void HandleEvent_Warning_ReportsSessionWarning()
	{
		// Arrange
		var warningEvent = CreateWarningEvent("tool_discovery_failed", "No tools found for server 'icm'");

		// Act
		_handler.HandleEvent(warningEvent);

		// Assert
		_reporter.Received(1).ReportSessionWarning("tool_discovery_failed", "No tools found for server 'icm'");
	}

	#endregion

	#region Info

	[Fact]
	public void HandleEvent_Info_WritesInfoEvent()
	{
		// Arrange
		var infoEvent = CreateInfoEvent("mcp_connected", "MCP server 'icm' connected successfully");

		// Act
		_handler.HandleEvent(infoEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.Info);
		agentEvent.Content.Should().Be("MCP server 'icm' connected successfully");
		agentEvent.DiagnosticType.Should().Be("mcp_connected");
	}

	[Fact]
	public void HandleEvent_Info_ReportsSessionInfo()
	{
		// Arrange
		var infoEvent = CreateInfoEvent("server_status", "All MCP servers started");

		// Act
		_handler.HandleEvent(infoEvent);

		// Assert
		_reporter.Received(1).ReportSessionInfo("server_status", "All MCP servers started");
	}

	#endregion

	#region MCP Servers Loaded

	private static SessionMcpServersLoadedEvent CreateMcpServersLoadedEvent(
		params SessionMcpServersLoadedDataServersItem[] servers) => new()
	{
		Data = new SessionMcpServersLoadedData
		{
			Servers = servers
		}
	};

	private static SessionMcpServersLoadedDataServersItem CreateMcpServerItem(
		string name,
		SessionMcpServersLoadedDataServersItemStatus status,
		string? source = null,
		string? error = null) => new()
	{
		Name = name,
		Status = status,
		Source = source!,
		Error = error!
	};

	[Fact]
	public void HandleEvent_McpServersLoaded_WritesMcpServersLoadedEvent()
	{
		// Arrange
		var evt = CreateMcpServersLoadedEvent(
			CreateMcpServerItem("icm", SessionMcpServersLoadedDataServersItemStatus.Connected, "local"),
			CreateMcpServerItem("graph", SessionMcpServersLoadedDataServersItemStatus.Failed, "remote", "Connection refused"));

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.McpServersLoaded);
		agentEvent.McpServerStatuses.Should().HaveCount(2);

		agentEvent.McpServerStatuses![0].Name.Should().Be("icm");
		agentEvent.McpServerStatuses[0].Status.Should().Be("Connected");
		agentEvent.McpServerStatuses[0].Source.Should().Be("local");
		agentEvent.McpServerStatuses[0].Error.Should().BeNull();

		agentEvent.McpServerStatuses[1].Name.Should().Be("graph");
		agentEvent.McpServerStatuses[1].Status.Should().Be("Failed");
		agentEvent.McpServerStatuses[1].Source.Should().Be("remote");
		agentEvent.McpServerStatuses[1].Error.Should().Be("Connection refused");
	}

	[Fact]
	public void HandleEvent_McpServersLoaded_ReportsMcpServersLoaded()
	{
		// Arrange
		var evt = CreateMcpServersLoadedEvent(
			CreateMcpServerItem("icm", SessionMcpServersLoadedDataServersItemStatus.Connected));

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_reporter.Received(1).ReportMcpServersLoaded(
			Arg.Is<IReadOnlyList<McpServerStatusInfo>>(list =>
				list.Count == 1 && list[0].Name == "icm" && list[0].Status == "Connected"));
	}

	[Fact]
	public void HandleEvent_McpServersLoaded_EmptyServersList_HandlesGracefully()
	{
		// Arrange
		var evt = CreateMcpServersLoadedEvent();

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.McpServersLoaded);
		agentEvent.McpServerStatuses.Should().BeEmpty();
	}

	[Fact]
	public void HandleEvent_McpServersLoaded_AllStatusTypes_MapsCorrectly()
	{
		// Arrange
		var evt = CreateMcpServersLoadedEvent(
			CreateMcpServerItem("s1", SessionMcpServersLoadedDataServersItemStatus.Connected),
			CreateMcpServerItem("s2", SessionMcpServersLoadedDataServersItemStatus.Failed, error: "timeout"),
			CreateMcpServerItem("s3", SessionMcpServersLoadedDataServersItemStatus.Pending),
			CreateMcpServerItem("s4", SessionMcpServersLoadedDataServersItemStatus.Disabled),
			CreateMcpServerItem("s5", SessionMcpServersLoadedDataServersItemStatus.NotConfigured));

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		var statuses = agentEvent!.McpServerStatuses!;
		statuses.Should().HaveCount(5);
		statuses[0].Status.Should().Be("Connected");
		statuses[1].Status.Should().Be("Failed");
		statuses[1].Error.Should().Be("timeout");
		statuses[2].Status.Should().Be("Pending");
		statuses[3].Status.Should().Be("Disabled");
		statuses[4].Status.Should().Be("NotConfigured");
	}

	#endregion

	#region MCP Server Status Changed

	private static SessionMcpServerStatusChangedEvent CreateMcpServerStatusChangedEvent(
		string serverName,
		SessionMcpServersLoadedDataServersItemStatus status) => new()
	{
		Data = new SessionMcpServerStatusChangedData
		{
			ServerName = serverName,
			Status = status
		}
	};

	[Fact]
	public void HandleEvent_McpServerStatusChanged_WritesMcpServerStatusChangedEvent()
	{
		// Arrange
		var evt = CreateMcpServerStatusChangedEvent("icm", SessionMcpServersLoadedDataServersItemStatus.Connected);

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.McpServerStatusChanged);
		agentEvent.McpServerName.Should().Be("icm");
		agentEvent.McpServerStatus.Should().Be("Connected");
	}

	[Fact]
	public void HandleEvent_McpServerStatusChanged_ReportsMcpServerStatusChanged()
	{
		// Arrange
		var evt = CreateMcpServerStatusChangedEvent("graph", SessionMcpServersLoadedDataServersItemStatus.Failed);

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_reporter.Received(1).ReportMcpServerStatusChanged("graph", "Failed");
	}

	#endregion

	#region Full Session Flow with MCP Events

	[Fact]
	public void HandleEvent_SessionWithMcpEvents_ProcessesAllEventsCorrectly()
	{
		// Arrange & Act - Simulate a session with MCP server lifecycle events
		_handler.HandleEvent(CreateSessionStartEvent("claude-opus-4.5"));
		_handler.HandleEvent(CreateMcpServerStatusChangedEvent("icm", SessionMcpServersLoadedDataServersItemStatus.Pending));
		_handler.HandleEvent(CreateMcpServersLoadedEvent(
			CreateMcpServerItem("icm", SessionMcpServersLoadedDataServersItemStatus.Connected, "local"),
			CreateMcpServerItem("graph", SessionMcpServersLoadedDataServersItemStatus.Failed, error: "timeout")));
		_handler.HandleEvent(CreateMessageDeltaEvent("Working with IcM tools..."));
		_handler.HandleEvent(CreateMessageEvent("Done."));
		_handler.HandleEvent(CreateUsageEvent("claude-opus-4.5", 100, 50));
		_handler.HandleEvent(CreateIdleEvent());

		// Assert
		var events = new List<AgentEvent>();
		while (_channel.Reader.TryRead(out var evt))
		{
			events.Add(evt);
		}

		events.Should().HaveCount(7);
		events[0].Type.Should().Be(AgentEventType.SessionStart);
		events[1].Type.Should().Be(AgentEventType.McpServerStatusChanged);
		events[1].McpServerName.Should().Be("icm");
		events[1].McpServerStatus.Should().Be("Pending");
		events[2].Type.Should().Be(AgentEventType.McpServersLoaded);
		events[2].McpServerStatuses.Should().HaveCount(2);
		events[3].Type.Should().Be(AgentEventType.MessageDelta);
		events[4].Type.Should().Be(AgentEventType.Message);
		events[5].Type.Should().Be(AgentEventType.Usage);
		events[6].Type.Should().Be(AgentEventType.SessionIdle);
		_done.Task.IsCompleted.Should().BeTrue();
	}

	#endregion

	#region Full Session Flow with Warnings

	[Fact]
	public void HandleEvent_SessionWithWarnings_ProcessesWarningsAlongsideOtherEvents()
	{
		// Arrange & Act - Simulate a session where MCP server fails
		_handler.HandleEvent(CreateSessionStartEvent("claude-opus-4.5"));
		_handler.HandleEvent(CreateWarningEvent("mcp_server_error", "Failed to start MCP server 'icm'"));
		_handler.HandleEvent(CreateInfoEvent("session_info", "Continuing without MCP tools"));
		_handler.HandleEvent(CreateMessageDeltaEvent("I don't have access to IcM tools..."));
		_handler.HandleEvent(CreateMessageEvent("No IcM MCP tools are available."));
		_handler.HandleEvent(CreateUsageEvent("claude-opus-4.5", 50, 30));
		_handler.HandleEvent(CreateIdleEvent());

		// Assert
		_handler.FinalContent.Should().Be("No IcM MCP tools are available.");
		_done.Task.IsCompleted.Should().BeTrue();

		var events = new List<AgentEvent>();
		while (_channel.Reader.TryRead(out var evt))
		{
			events.Add(evt);
		}

		events.Should().HaveCount(7);
		events[0].Type.Should().Be(AgentEventType.SessionStart);
		events[1].Type.Should().Be(AgentEventType.Warning);
		events[1].ErrorMessage.Should().Be("Failed to start MCP server 'icm'");
		events[1].DiagnosticType.Should().Be("mcp_server_error");
		events[2].Type.Should().Be(AgentEventType.Info);
		events[2].Content.Should().Be("Continuing without MCP tools");
		events[3].Type.Should().Be(AgentEventType.MessageDelta);
		events[4].Type.Should().Be(AgentEventType.Message);
		events[5].Type.Should().Be(AgentEventType.Usage);
		events[6].Type.Should().Be(AgentEventType.SessionIdle);
	}

	#endregion

	#region FinalContent Fallback (Tool Call Scenarios)

	[Fact]
	public void FinalContent_WhenMessageEventHasContent_ReturnsMessageEventContent()
	{
		// Arrange: Normal case - AssistantMessageEvent has content
		_handler.HandleEvent(CreateMessageDeltaEvent("streaming content"));
		_handler.HandleEvent(CreateMessageEvent("Final complete message"));

		// Assert
		_handler.FinalContent.Should().Be("Final complete message");
	}

	[Fact]
	public void FinalContent_WhenMessageEventIsEmpty_FallsBackToAccumulatedDeltas()
	{
		// Arrange: Multi-turn tool call scenario - the model calls a tool, then emits text,
		// but the SDK's AssistantMessageEvent has empty content.
		// This reproduces the exact bug observed in icm-tracker runs.
		_handler.HandleEvent(CreateToolStartEvent("call-1", "orchestra_set_status"));
		_handler.HandleEvent(CreateToolCompleteEvent("call-1", true));
		_handler.HandleEvent(CreateMessageDeltaEvent("[\"770343639\", \"760607426\"]"));
		_handler.HandleEvent(CreateMessageEvent("")); // SDK reports empty content

		// Assert - Should fall back to accumulated delta content
		_handler.FinalContent.Should().Be("[\"770343639\", \"760607426\"]");
	}

	[Fact]
	public void FinalContent_WhenNoMessageEventAndDeltasExist_ReturnsAccumulatedDeltas()
	{
		// Arrange: No AssistantMessageEvent fired at all, only deltas
		_handler.HandleEvent(CreateMessageDeltaEvent("Hello "));
		_handler.HandleEvent(CreateMessageDeltaEvent("world"));

		// Assert
		_handler.FinalContent.Should().Be("Hello world");
	}

	[Fact]
	public void FinalContent_WhenNoMessageEventAndNoDeltas_ReturnsNull()
	{
		// Arrange: No message events at all (e.g., only tool calls)
		_handler.HandleEvent(CreateToolStartEvent("call-1", "some_tool"));
		_handler.HandleEvent(CreateToolCompleteEvent("call-1", true));

		// Assert
		_handler.FinalContent.Should().BeNull();
	}

	[Fact]
	public void FinalContent_ToolCallThenText_AccumulatesAllDeltaContent()
	{
		// Arrange: Simulate the exact icm-tracker check-watchlist flow:
		// 1. Reasoning (thinking about the watchlist)
		// 2. Tool call: orchestra_set_status(success)
		// 3. Tool result returned
		// 4. Model emits text content as deltas
		// 5. SDK fires AssistantMessageEvent with empty content
		_handler.HandleEvent(CreateReasoningDeltaEvent("The watchlist has 2 incidents..."));
		_handler.HandleEvent(CreateToolStartEvent("call-1", "orchestra_set_status"));
		_handler.HandleEvent(CreateToolCompleteEvent("call-1", true));
		_handler.HandleEvent(CreateMessageDeltaEvent("\n\n"));
		_handler.HandleEvent(CreateMessageDeltaEvent("[\"770343639\""));
		_handler.HandleEvent(CreateMessageDeltaEvent(", \"760607426\"]"));
		_handler.HandleEvent(CreateMessageEvent("")); // Empty content from SDK

		// Assert - Accumulated deltas should be used as fallback
		_handler.FinalContent.Should().Be("\n\n[\"770343639\", \"760607426\"]");
	}

	[Fact]
	public void FinalContent_MultipleToolCallsThenText_AccumulatesCorrectly()
	{
		// Arrange: Model calls multiple tools then emits text
		_handler.HandleEvent(CreateToolStartEvent("call-1", "tool_a"));
		_handler.HandleEvent(CreateToolCompleteEvent("call-1", true));
		_handler.HandleEvent(CreateToolStartEvent("call-2", "tool_b"));
		_handler.HandleEvent(CreateToolCompleteEvent("call-2", true));
		_handler.HandleEvent(CreateMessageDeltaEvent("Result after tools: "));
		_handler.HandleEvent(CreateMessageDeltaEvent("all done"));
		_handler.HandleEvent(CreateMessageEvent("")); // Empty

		// Assert
		_handler.FinalContent.Should().Be("Result after tools: all done");
	}

	[Fact]
	public void FinalContent_MessageEventWithContent_TakesPrecedenceOverDeltas()
	{
		// Arrange: When AssistantMessageEvent has non-empty content, use it
		// (this is the normal case where the SDK correctly provides the content)
		_handler.HandleEvent(CreateMessageDeltaEvent("streaming..."));
		_handler.HandleEvent(CreateMessageEvent("The authoritative final content"));

		// Assert - MessageEvent content takes precedence
		_handler.FinalContent.Should().Be("The authoritative final content");
	}

	#endregion

	#region Tool Call Name Cleanup

	[Fact]
	public void HandleEvent_ToolExecutionComplete_RemovesToolCallIdFromDictionary()
	{
		// Arrange — After correlating a tool name, the entry should be removed
		// from the internal dictionary to avoid unbounded growth in long sessions.
		var startEvent = CreateToolStartEvent(toolCallId: "cleanup-test", toolName: "my_tool");
		var completeEvent = CreateToolCompleteEvent(toolCallId: "cleanup-test", success: true);

		// Act — Start and complete the tool
		_handler.HandleEvent(startEvent);
		_handler.HandleEvent(completeEvent);

		// Now try to complete the same tool call ID again
		var duplicateCompleteEvent = CreateToolCompleteEvent(toolCallId: "cleanup-test", success: true);
		_handler.HandleEvent(duplicateCompleteEvent);

		// Assert — The second completion should NOT have a tool name
		// because the first completion removed it from the dictionary
		_channel.Reader.TryRead(out _); // start event
		_channel.Reader.TryRead(out var firstComplete); // first complete — has tool name
		_channel.Reader.TryRead(out var secondComplete); // second complete — no tool name

		firstComplete!.ToolName.Should().Be("my_tool");
		secondComplete!.ToolName.Should().BeNull("tool call ID should be removed after first correlation");
	}

	[Fact]
	public void HandleEvent_MultipleToolCalls_EachCleanedUpIndependently()
	{
		// Arrange
		_handler.HandleEvent(CreateToolStartEvent(toolCallId: "call-a", toolName: "tool_a"));
		_handler.HandleEvent(CreateToolStartEvent(toolCallId: "call-b", toolName: "tool_b"));

		// Complete call-a first
		_handler.HandleEvent(CreateToolCompleteEvent(toolCallId: "call-a", success: true));

		// call-b should still be tracked
		_handler.HandleEvent(CreateToolCompleteEvent(toolCallId: "call-b", success: true));

		// Assert
		_channel.Reader.TryRead(out _); // start a
		_channel.Reader.TryRead(out _); // start b
		_channel.Reader.TryRead(out var completeA);
		_channel.Reader.TryRead(out var completeB);

		completeA!.ToolName.Should().Be("tool_a");
		completeB!.ToolName.Should().Be("tool_b");
	}

	#endregion

	#region Error Event Completes TCS

	[Fact]
	public void HandleEvent_Error_FaultsTaskCompletionSourceWithCopilotSessionFailedException()
	{
		// Arrange — Error events should FAULT the TCS so the orchestration sees a real failure
		// rather than silently completing with empty content. This protects against the
		// previous "silent success on session error" behaviour: the AI model can fail mid-stream
		// and we must surface that as a hard failure with a typed exception.
		var errorEvent = CreateErrorEvent("Fatal session error");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		_done.Task.IsCompleted.Should().BeTrue();
		_done.Task.IsFaulted.Should().BeTrue(
			"session errors must fault the TCS so PromptExecutor surfaces a failed step (no silent success)");

		var ex = _done.Task.Exception!.Flatten().InnerException;
		ex.Should().BeOfType<CopilotSessionFailedException>();
		((CopilotSessionFailedException)ex!).Kind.Should().Be(CopilotSessionFailureKind.SessionError);
	}

	[Fact]
	public void HandleEvent_Error_ThenIdle_DoesNotThrow()
	{
		// Arrange — If both error and idle arrive (e.g., SDK cleanup), the second
		// completion attempt should be a no-op, not throw.
		_handler.HandleEvent(CreateErrorEvent("Error first"));

		// Act — Should not throw on second completion
		var act = () => _handler.HandleEvent(CreateIdleEvent());

		// Assert
		act.Should().NotThrow();
		_done.Task.IsCompleted.Should().BeTrue();
		_done.Task.IsFaulted.Should().BeTrue("error fault must not be overridden by a later idle event");
	}

	#endregion

	#region Tool Execution Complete With Null ToolCallId

	[Fact]
	public void HandleEvent_ToolExecutionComplete_WithNullToolCallId_DoesNotThrow()
	{
		// Arrange — The SDK can send a completion without a tool call ID
		var completeEvent = new ToolExecutionCompleteEvent
		{
			Data = new ToolExecutionCompleteData
			{
				ToolCallId = null!,
				Success = true,
				Result = null,
				Error = null
			}
		};

		// Act
		var act = () => _handler.HandleEvent(completeEvent);

		// Assert
		act.Should().NotThrow();
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.ToolName.Should().BeNull();
	}

	#endregion

	#region Hook Events

	[Fact]
	public void HandleEvent_HookStart_WritesHookStartEvent()
	{
		// Arrange
		var hookStartEvent = new HookStartEvent
		{
			Data = new HookStartData
			{
				HookInvocationId = "inv-123",
				HookType = "preToolUse",
				Input = null,
			}
		};

		// Act
		_handler.HandleEvent(hookStartEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.HookStart);
		agentEvent.HookInvocationId.Should().Be("inv-123");
		agentEvent.HookType.Should().Be("preToolUse");
	}

	[Fact]
	public void HandleEvent_HookEnd_WritesHookEndEvent()
	{
		// Arrange
		var hookEndEvent = new HookEndEvent
		{
			Data = new HookEndData
			{
				HookInvocationId = "inv-123",
				HookType = "preToolUse",
				Output = null,
				Success = true,
				Error = null,
			}
		};

		// Act
		_handler.HandleEvent(hookEndEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.HookEnd);
		agentEvent.HookInvocationId.Should().Be("inv-123");
		agentEvent.HookType.Should().Be("preToolUse");
		agentEvent.HookSuccess.Should().BeTrue();
	}

	[Fact]
	public void HandleEvent_HookEnd_WithFailure_SetsErrorMessage()
	{
		// Arrange
		var hookEndEvent = new HookEndEvent
		{
			Data = new HookEndData
			{
				HookInvocationId = "inv-456",
				HookType = "postToolUse",
				Output = null,
				Success = false,
				Error = new HookEndDataError { Message = "Hook failed" },
			}
		};

		// Act
		_handler.HandleEvent(hookEndEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.HookEnd);
		agentEvent.HookSuccess.Should().BeFalse();
		agentEvent.ErrorMessage.Should().NotBeNull();
	}

	#endregion

	#region Turn Start Events

	[Fact]
	public void HandleEvent_TurnStart_WritesTurnStartEvent()
	{
		// Arrange
		var turnStartEvent = new AssistantTurnStartEvent
		{
			Data = new AssistantTurnStartData
			{
				TurnId = "1",
				InteractionId = "interaction-abc",
			}
		};

		// Act
		_handler.HandleEvent(turnStartEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.TurnStart);
		agentEvent.TurnId.Should().Be("1");
	}

	#endregion

	#region Session Usage Info Events

	[Fact]
	public void HandleEvent_SessionUsageInfo_WritesSessionUsageInfoEvent()
	{
		// Arrange
		var usageInfoEvent = new SessionUsageInfoEvent
		{
			Data = new SessionUsageInfoData
			{
				TokenLimit = 128000,
				CurrentTokens = 5000,
				MessagesLength = 10,
			}
		};

		// Act
		_handler.HandleEvent(usageInfoEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SessionUsageInfo);
		agentEvent.TokenLimit.Should().Be(128000);
		agentEvent.CurrentTokens.Should().Be(5000);
	}

	#endregion

	#region Turn End Events

	[Fact]
	public void HandleEvent_TurnEnd_WritesTurnEndEvent()
	{
		// Arrange
		var turnEndEvent = new AssistantTurnEndEvent
		{
			Data = new AssistantTurnEndData
			{
				TurnId = "1",
			}
		};

		// Act
		_handler.HandleEvent(turnEndEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.TurnEnd);
		agentEvent.TurnId.Should().Be("1");
	}

	#endregion

	#region External Tool Events

	[Fact]
	public void HandleEvent_ExternalToolRequested_WritesToolExecutionStartEvent()
	{
		// Arrange
		var externalToolEvent = new ExternalToolRequestedEvent
		{
			Data = new ExternalToolRequestedData
			{
				RequestId = "req-123",
				SessionId = "test-session",
				ToolCallId = "call-456",
				ToolName = "my_external_tool",
				Arguments = new Dictionary<string, object> { ["arg1"] = "value1" },
			}
		};

		// Act
		_handler.HandleEvent(externalToolEvent);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.ToolExecutionStart);
		agentEvent.ToolCallId.Should().Be("call-456");
		agentEvent.ToolName.Should().Be("my_external_tool");
		agentEvent.ToolArguments.Should().Contain("arg1");
	}

	[Fact]
	public void HandleEvent_ExternalToolCompleted_DoesNotWriteEvent()
	{
		// Arrange
		var externalToolCompletedEvent = new ExternalToolCompletedEvent
		{
			Data = new ExternalToolCompletedData
			{
				RequestId = "req-123",
			}
		};

		// Act
		_handler.HandleEvent(externalToolCompletedEvent);

		// Assert — silently consumed, no event written
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	#endregion

	#region Informational Events (Silently Consumed)

	[Fact]
	public void HandleEvent_PendingMessagesModified_DoesNotWriteEvent()
	{
		// Arrange
		var evt = new PendingMessagesModifiedEvent
		{
			Data = new PendingMessagesModifiedData()
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert — no event should be written to the channel
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionCustomAgentsUpdated_DoesNotWriteEvent()
	{
		// Arrange
		var evt = new SessionCustomAgentsUpdatedEvent
		{
			Data = new SessionCustomAgentsUpdatedData
			{
				Agents = [],
				Warnings = [],
				Errors = [],
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionToolsUpdated_DoesNotWriteEvent()
	{
		// Arrange
		var evt = new SessionToolsUpdatedEvent
		{
			Data = new SessionToolsUpdatedData
			{
				Model = "claude-opus-4.5",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_UserMessage_DoesNotWriteEvent()
	{
		// Arrange
		var evt = new UserMessageEvent
		{
			Data = new UserMessageData
			{
				Content = "test message",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_AssistantStreamingDelta_DoesNotWriteEvent()
	{
		// Arrange
		var evt = new AssistantStreamingDeltaEvent
		{
			Data = new AssistantStreamingDeltaData
			{
				TotalResponseSizeBytes = 1024,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	#endregion
}
