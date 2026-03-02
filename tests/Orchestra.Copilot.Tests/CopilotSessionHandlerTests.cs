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

	#endregion
}
