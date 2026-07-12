using System.Threading.Channels;
using FluentAssertions;
using GitHub.Copilot;
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
			// SDK 1.0.0 narrowed Version from double to int64; the test fixture value
			// was always a whole number so a long literal preserves the original intent.
			Version = 1L,
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
		double durationSeconds = 1.5) => new()
	{
		// SDK 1.0.0 changes AssistantUsageData types:
		//   * Token counts (Input/Output/CacheRead/CacheWrite) are now long? (auto-widen
		//     from int implicitly so the int parameter shape can stay).
		//   * Duration is TimeSpan? — the fixture used a "seconds as double" convention
		//     so we forward by constructing a TimeSpan from the seconds value.
		//   * Cost is decorated with the GHCP001 evaluation-only diagnostic; we suppress
		//     locally because the field is wire-compatible with 0.3.0.
#pragma warning disable GHCP001
		Data = new AssistantUsageData
		{
			Model = model,
			InputTokens = inputTokens,
			OutputTokens = outputTokens,
			CacheReadTokens = cacheReadTokens,
			CacheWriteTokens = cacheWriteTokens,
			Cost = cost,
			Duration = TimeSpan.FromSeconds(durationSeconds),
		}
#pragma warning restore GHCP001
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
			// SDK 1.0.0 changed Arguments from Dictionary<string, object>? to JsonElement?
			// (the schema-generator emits Dictionary-shaped payloads as JSON elements so
			// the wire encoding is preserved). We serialize the legacy test-fixture dict
			// into a JsonElement on the fly.
			Arguments = arguments is null
				? null
				: System.Text.Json.JsonSerializer.SerializeToElement(arguments),
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

	/// <summary>
	/// Builds a SessionErrorEvent populated with every field that maps to
	/// <see cref="AgentSessionErrorDetails"/>. Used by the structured-error-propagation
	/// tests that assert <see cref="CopilotSessionFailedException.Details"/> carries the
	/// SDK payload through to the engine layer instead of collapsing it into the message.
	/// </summary>
	/// <remarks>
	/// SDK 1.0.0 narrowed <c>SessionErrorData.StatusCode</c> from <c>long?</c> to
	/// <c>int?</c>. The helper accepts <c>long?</c> for backwards compatibility with the
	/// existing call sites and casts down explicitly.
	/// </remarks>
	private static SessionErrorEvent CreateDetailedErrorEvent(
		string message,
		string? errorType = null,
		long? statusCode = null,
		string? providerCallId = null,
		string? url = null,
		string? stack = null) => new()
	{
		Data = new SessionErrorData
		{
			Message = message,
			ErrorType = errorType!,
			StatusCode = statusCode is null ? null : (int?)statusCode.Value,
			ProviderCallId = providerCallId!,
			Url = url!,
			Stack = stack!,
		}
	};

	private static SessionIdleEvent CreateIdleEvent() => new()
	{
		Data = new SessionIdleData()
	};

	private static AssistantIdleEvent CreateAssistantIdleEvent(bool? aborted = null) => new()
	{
		Data = new AssistantIdleData { Aborted = aborted }
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
			durationSeconds: 1.5);

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
			durationSeconds: 2.0);

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

	[Fact]
	public void HandleEvent_AssistantIdle_RootAgent_CompletesTaskCompletionSource()
	{
		// No sub-agent frame is active, so an AssistantIdle belongs to the root agent and
		// acts as a completion fallback (some CLI flows emit it without a SessionIdle).
		_handler.HandleEvent(CreateAssistantIdleEvent());

		_done.Task.IsCompleted.Should().BeTrue();
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SessionIdle);
	}

	[Fact]
	public void HandleEvent_AssistantIdle_Aborted_CompletesWithAbortedMarker()
	{
		_handler.HandleEvent(CreateAssistantIdleEvent(aborted: true));

		_done.Task.IsCompleted.Should().BeTrue();
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.SessionIdle);
		agentEvent.Content.Should().Contain("aborted");
	}

	[Fact]
	public void HandleEvent_AssistantIdle_WhileSubagentActive_DoesNotComplete()
	{
		// A sub-agent is mid-flight. The root agent is blocked awaiting the sub-agent's
		// tool result and cannot be idle, so this AssistantIdle belongs to the sub-agent and
		// must NOT complete the parent session (doing so would truncate the root output).
		_handler.HandleEvent(CreateSubagentStartedEvent(agentName: "researcher", toolCallId: "sub-1"));
		while (_channel.Reader.TryRead(out _))
		{
			// drain the SubagentStarted AgentEvent
		}

		_handler.HandleEvent(CreateAssistantIdleEvent());

		_done.Task.IsCompleted.Should().BeFalse("a sub-agent going idle must not complete the parent session");
		_channel.Reader.TryRead(out _).Should().BeFalse("no SessionIdle should be emitted for a sub-agent idle");
	}

	[Fact]
	public void HandleEvent_AssistantIdle_AfterSubagentCompletes_CompletesRoot()
	{
		// Sub-agent starts then completes → no active frames → a subsequent AssistantIdle is
		// the root agent finishing and completes the session.
		_handler.HandleEvent(CreateSubagentStartedEvent(agentName: "researcher", toolCallId: "sub-1"));
		_handler.HandleEvent(CreateSubagentCompletedEvent(agentName: "researcher", toolCallId: "sub-1"));
		while (_channel.Reader.TryRead(out _))
		{
			// drain sub-agent lifecycle AgentEvents
		}

		_handler.HandleEvent(CreateAssistantIdleEvent());

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
		params McpServersLoadedServer[] servers) => new()
	{
		Data = new SessionMcpServersLoadedData
		{
			Servers = servers
		}
	};

	private static McpServersLoadedServer CreateMcpServerItem(
		string name,
		McpServerStatus status,
		string? source = null,
		string? error = null) => new()
	{
		Name = name,
		Status = status,
		// SDK 1.0.0 changed Source from string to a McpServerSource? enum
		// (User/Workspace/Plugin/Builtin). Tests use the new vocabulary directly so
		// the assertion can match the handler's .ToString() projection.
		Source = source switch
		{
			null => null,
			"User" => McpServerSource.User,
			"Workspace" => McpServerSource.Workspace,
			"Plugin" => McpServerSource.Plugin,
			"Builtin" => McpServerSource.Builtin,
			_ => null,
		},
		Error = error!
	};

	[Fact]
	public void HandleEvent_McpServersLoaded_WritesMcpServersLoadedEvent()
	{
		// Arrange
		var evt = CreateMcpServersLoadedEvent(
			CreateMcpServerItem("icm", McpServerStatus.Connected, "User"),
			CreateMcpServerItem("graph", McpServerStatus.Failed, "Plugin", "Connection refused"));

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.McpServersLoaded);
		agentEvent.McpServerStatuses.Should().HaveCount(2);

		agentEvent.McpServerStatuses![0].Name.Should().Be("icm");
		agentEvent.McpServerStatuses[0].Status.Should().Be("connected");
		agentEvent.McpServerStatuses[0].Source.Should().Be("user");
		agentEvent.McpServerStatuses[0].Error.Should().BeNull();

		agentEvent.McpServerStatuses[1].Name.Should().Be("graph");
		agentEvent.McpServerStatuses[1].Status.Should().Be("failed");
		agentEvent.McpServerStatuses[1].Source.Should().Be("plugin");
		agentEvent.McpServerStatuses[1].Error.Should().Be("Connection refused");
	}

	[Fact]
	public void HandleEvent_McpServersLoaded_ReportsMcpServersLoaded()
	{
		// Arrange
		var evt = CreateMcpServersLoadedEvent(
			CreateMcpServerItem("icm", McpServerStatus.Connected));

		// Act
		_handler.HandleEvent(evt);

		// Assert
		// SDK 1.0.0 changed McpServerStatus from an enum (whose ToString() returned the
		// PascalCase member name "Connected") to a struct whose Value/ToString() returns
		// the lowercase snake_case wire token "connected". The handler surfaces whatever
		// the SDK ToString() returns to the reporter, so the assertion uses the new shape.
		_reporter.Received(1).ReportMcpServersLoaded(
			Arg.Is<IReadOnlyList<McpServerStatusInfo>>(list =>
				list.Count == 1 && list[0].Name == "icm" && list[0].Status == "connected"));
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
			CreateMcpServerItem("s1", McpServerStatus.Connected),
			CreateMcpServerItem("s2", McpServerStatus.Failed, error: "timeout"),
			CreateMcpServerItem("s3", McpServerStatus.Pending),
			CreateMcpServerItem("s4", McpServerStatus.Disabled),
			CreateMcpServerItem("s5", McpServerStatus.NotConfigured));

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		var statuses = agentEvent!.McpServerStatuses!;
		statuses.Should().HaveCount(5);
		statuses[0].Status.Should().Be("connected");
		statuses[1].Status.Should().Be("failed");
		statuses[1].Error.Should().Be("timeout");
		statuses[2].Status.Should().Be("pending");
		statuses[3].Status.Should().Be("disabled");
		statuses[4].Status.Should().Be("not_configured");
	}

	#endregion

	#region MCP Server Status Changed

	private static SessionMcpServerStatusChangedEvent CreateMcpServerStatusChangedEvent(
		string serverName,
		McpServerStatus status) => new()
	{
		Data = new SessionMcpServerStatusChangedData
		{
			ServerName = serverName,
			// Both enums share the same value layout; cast bridges the SDK 0.3.0 split.
			Status = status
		}
	};

	[Fact]
	public void HandleEvent_McpServerStatusChanged_WritesMcpServerStatusChangedEvent()
	{
		// Arrange
		var evt = CreateMcpServerStatusChangedEvent("icm", McpServerStatus.Connected);

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var agentEvent).Should().BeTrue();
		agentEvent!.Type.Should().Be(AgentEventType.McpServerStatusChanged);
		agentEvent.McpServerName.Should().Be("icm");
		agentEvent.McpServerStatus.Should().Be("connected");
	}

	[Fact]
	public void HandleEvent_McpServerStatusChanged_ReportsMcpServerStatusChanged()
	{
		// Arrange
		var evt = CreateMcpServerStatusChangedEvent("graph", McpServerStatus.Failed);

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_reporter.Received(1).ReportMcpServerStatusChanged("graph", "failed");
	}

	#endregion

	#region Full Session Flow with MCP Events

	[Fact]
	public void HandleEvent_SessionWithMcpEvents_ProcessesAllEventsCorrectly()
	{
		// Arrange & Act - Simulate a session with MCP server lifecycle events
		_handler.HandleEvent(CreateSessionStartEvent("claude-opus-4.5"));
		_handler.HandleEvent(CreateMcpServerStatusChangedEvent("icm", McpServerStatus.Pending));
		_handler.HandleEvent(CreateMcpServersLoadedEvent(
			CreateMcpServerItem("icm", McpServerStatus.Connected, "User"),
			CreateMcpServerItem("graph", McpServerStatus.Failed, error: "timeout")));
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
		events[1].McpServerStatus.Should().Be("pending");
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

	[Fact]
	public void HandleEvent_Error_PopulatesAllStructuredFieldsOnException()
	{
		// Arrange — All five SDK SessionErrorData fields must round-trip through to
		// CopilotSessionFailedException.Details so the run record and structured logs
		// retain the upstream classification, HTTP status, request id, URL, and stack
		// instead of silently dropping everything but Message.
		var errorEvent = CreateDetailedErrorEvent(
			message: "Execution failed: Error: Failed to get response from the AI model; retried 5 times (total retry wait time: 5.62 seconds) Last error: Unknown error",
			errorType: "query",
			statusCode: 502,
			providerCallId: "abcd-efgh-1234",
			url: "https://docs.github.com/copilot/troubleshooting",
			stack: "at Provider.send (/cli/index.js:42)\n  at Session.run (/cli/index.js:99)");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		_done.Task.IsFaulted.Should().BeTrue();
		var ex = _done.Task.Exception!.Flatten().InnerException;
		ex.Should().BeOfType<CopilotSessionFailedException>();

		var sessionEx = (CopilotSessionFailedException)ex!;
		sessionEx.Kind.Should().Be(CopilotSessionFailureKind.SessionError);
		sessionEx.Message.Should().Contain("Failed to get response from the AI model");

		sessionEx.Details.Should().NotBeNull(
			"session-error events must produce structured Details on the exception so the engine layer can persist them in run.json");
		sessionEx.Details!.ErrorType.Should().Be("query");
		sessionEx.Details.StatusCode.Should().Be(502);
		sessionEx.Details.ProviderCallId.Should().Be("abcd-efgh-1234");
		sessionEx.Details.Url.Should().Be("https://docs.github.com/copilot/troubleshooting");
		sessionEx.Details.Stack.Should().Contain("at Provider.send");
	}

	[Fact]
	public void HandleEvent_Error_WithOnlyMessage_StillProducesEmptyDetails()
	{
		// Arrange — When the SDK delivers a SessionErrorEvent with only the Message
		// populated, Details must still be non-null (so consumers don't need a
		// null-check for the common case) but all five fields are null/zero.
		var errorEvent = CreateDetailedErrorEvent(message: "Bare error");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		var ex = (CopilotSessionFailedException)_done.Task.Exception!.Flatten().InnerException!;
		ex.Details.Should().NotBeNull();
		ex.Details!.ErrorType.Should().BeNull();
		ex.Details.StatusCode.Should().BeNull();
		ex.Details.ProviderCallId.Should().BeNull();
		ex.Details.Url.Should().BeNull();
		ex.Details.Stack.Should().BeNull();
	}

	[Fact]
	public void CopilotSessionFailedException_ImplementsAgentSessionFailedMarker()
	{
		// Arrange — PromptExecutor walks the exception chain looking for the marker
		// IAgentSessionFailedException so it can extract structured details without
		// taking a hard reference on Orchestra.Copilot. If the implements relationship
		// is broken, the engine layer will silently fall back to "Unknown" categorization.
		_handler.HandleEvent(CreateDetailedErrorEvent(
			message: "boom",
			errorType: "rate_limit",
			statusCode: 429));

		// Act
		var ex = _done.Task.Exception!.Flatten().InnerException;

		// Assert
		ex.Should().BeAssignableTo<IAgentSessionFailedException>(
			"the engine's PromptExecutor relies on this marker to extract Details across the Engine/Copilot project boundary");
		((IAgentSessionFailedException)ex!).Details!.ErrorType.Should().Be("rate_limit");
		((IAgentSessionFailedException)ex!).Details!.StatusCode.Should().Be(429);
	}

	[Fact]
	public void HandleEvent_Error_WithCliExhaustedRetriesMessage_SetsExhaustedCliRetriesFlag()
	{
		// Arrange — Phase 3: the agent's swap loop classifies "Failed to get response
		// from the AI model; retried N times" as a swap-eligible failure. The handler
		// must set Details.ExhaustedCliRetries=true so the classifier can pick it up.
		var errorEvent = CreateDetailedErrorEvent(
			message: "Execution failed: Error: Failed to get response from the AI model; retried 5 times (total retry wait time: 5.62 seconds) Last error: Unknown error",
			errorType: "query");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		var ex = (CopilotSessionFailedException)_done.Task.Exception!.Flatten().InnerException!;
		ex.Details!.ExhaustedCliRetries.Should().BeTrue(
			"the CLI's own retry budget was exhausted; the agent should classify this as swap-eligible");
	}

	[Fact]
	public void HandleEvent_Error_WithUnrelatedMessage_LeavesExhaustedCliRetriesFalse()
	{
		// Arrange — a plain 403/quota error must not be confused with CLI exhaustion.
		_handler.HandleEvent(CreateDetailedErrorEvent(message: "403 Forbidden", errorType: "authorization"));

		// Assert
		var ex = (CopilotSessionFailedException)_done.Task.Exception!.Flatten().InnerException!;
		ex.Details!.ExhaustedCliRetries.Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_Error_With500BrokerError_SetsTransientUpstreamFailureFlag()
	{
		// Arrange — the broker observed at runtime: a 500 wrapping a 403 twirp
		// permission_denied on the user-identity handshake. This is the exact failure
		// shape that took down zts-official-pipeline-auto-discoverer's gate-discovery
		// step on 2026-05-19; the dying CLI's cached auth token is the usual culprit
		// and a cold restart (CLI swap) clears it.
		var errorEvent = CreateDetailedErrorEvent(
			message: "Execution failed: Error: 500 \"can't get copilot user by id: error getting copilot user details: twirp error permission_denied: Error from intermediary with HTTP status code 403 \\\"Forbidden\\\"\\n\" (Request ID: F490:865D5:3A32591:3FE6380:6A0C16FC)",
			errorType: "authorization");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		var ex = (CopilotSessionFailedException)_done.Task.Exception!.Flatten().InnerException!;
		ex.Details!.TransientUpstreamFailure.Should().BeTrue(
			"a 500 broker error with permission_denied is a transient upstream failure a fresh CLI worker is likely to clear");
		ex.Details.ExhaustedCliRetries.Should().BeFalse(
			"the CLI did not surface 'retried N times'; the two flags must stay distinct");
	}

	[Fact]
	public void HandleEvent_Error_WithSessionAuthHandleLost_SetsTransientUpstreamFailureFlag()
	{
		// Arrange — the bundled CLI's session-create call observed at runtime
		// (zts-official-pipeline-tracker, 2026-05-19 20:59:25) surfaces as a plain
		// SessionErrorEvent with no structured StatusCode/ErrorType. The dying CLI
		// has lost its auth handle; a fresh worker recreates the session with valid
		// auth from scratch.
		var errorEvent = CreateDetailedErrorEvent(
			message: "Execution failed: Error: Session was not created with authentication info or custom provider");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		var ex = (CopilotSessionFailedException)_done.Task.Exception!.Flatten().InnerException!;
		ex.Details!.TransientUpstreamFailure.Should().BeTrue(
			"the CLI lost its auth handle mid-flight; a swap to a fresh worker is the documented recovery path");
		ex.Details.ExhaustedCliRetries.Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_Error_With429RateLimit_SetsTransientUpstreamFailureFlag()
	{
		// Arrange — a 429 surfaced via the structured statusCode field. Even with no
		// keyword in the free-form message, a fresh CLI should be tried.
		var errorEvent = CreateDetailedErrorEvent(
			message: "Rate limit exceeded",
			statusCode: 429,
			errorType: "rate_limit");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		var ex = (CopilotSessionFailedException)_done.Task.Exception!.Flatten().InnerException!;
		ex.Details!.TransientUpstreamFailure.Should().BeTrue();
	}

	[Fact]
	public void HandleEvent_Error_With403StatusCode_SetsTransientUpstreamFailureFlag()
	{
		// Arrange — bare 403 via structured status code. The message text alone would
		// not match the regex (no "Error: 403" or "HTTP status code 403"); the
		// structured statusCode path must carry it.
		var errorEvent = CreateDetailedErrorEvent(
			message: "Forbidden",
			statusCode: 403,
			errorType: "authorization");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		var ex = (CopilotSessionFailedException)_done.Task.Exception!.Flatten().InnerException!;
		ex.Details!.TransientUpstreamFailure.Should().BeTrue();
	}

	[Fact]
	public void HandleEvent_Error_With400ValidationError_LeavesTransientUpstreamFailureFalse()
	{
		// Arrange — a 400 validation error is NOT swap-eligible. Retrying the same
		// bad request on a fresh CLI will just fail the same way.
		var errorEvent = CreateDetailedErrorEvent(
			message: "Bad request: validation failed",
			statusCode: 400,
			errorType: "query");

		// Act
		_handler.HandleEvent(errorEvent);

		// Assert
		var ex = (CopilotSessionFailedException)_done.Task.Exception!.Flatten().InnerException!;
		ex.Details!.TransientUpstreamFailure.Should().BeFalse();
		ex.Details.ExhaustedCliRetries.Should().BeFalse();
	}

	[Theory]
	[InlineData("Execution failed: Error: 502 Bad Gateway", true)]
	[InlineData("HTTP status code 503", true)]
	[InlineData("HTTP status code 504", true)]
	[InlineData("HTTP status code 403 from intermediary", true)]
	[InlineData("twirp error permission_denied: ...", true)]
	[InlineData("can't get copilot user by id: ...", true)]
	[InlineData("rate limit exceeded for model", true)]
	[InlineData("Forbidden response from intermediary", true)]
	[InlineData("Execution failed: Error: Session was not created with authentication info or custom provider", true)]
	[InlineData("Session was not created with authentication info", true)]
	[InlineData("HTTP status code 400: bad request", false)]
	[InlineData("validation error: unknown field", false)]
	[InlineData("Unknown error", false)]
	[InlineData("", false)]
	public void LooksLikeTransientUpstreamFailure_RecognisesExpectedShapes(string message, bool expected)
	{
		CopilotSessionHandler.LooksLikeTransientUpstreamFailure(message, statusCode: null)
			.Should().Be(expected, $"message '{message}' classification mismatch");
	}

	[Theory]
	[InlineData(500L, true)]
	[InlineData(502L, true)]
	[InlineData(503L, true)]
	[InlineData(599L, true)]
	[InlineData(429L, true)]
	[InlineData(403L, true)]
	[InlineData(400L, false)]
	[InlineData(401L, false)]
	[InlineData(404L, false)]
	[InlineData(200L, false)]
	[InlineData(null, false)]
	public void LooksLikeTransientUpstreamFailure_StatusCodeClassification(long? statusCode, bool expected)
	{
		CopilotSessionHandler.LooksLikeTransientUpstreamFailure(message: null, statusCode: statusCode)
			.Should().Be(expected, $"status code {statusCode?.ToString() ?? "null"} classification mismatch");
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
				Error = new HookEndError { Message = "Hook failed" },
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
		// SDK 1.0.0 changed ExternalToolRequestedData.Arguments from Dictionary<string, object>?
		// to JsonElement?. Serialize the legacy fixture shape on the fly so we can keep
		// the assertion below — the handler stringifies through JsonSerializer either way.
		var externalToolEvent = new ExternalToolRequestedEvent
		{
			Data = new ExternalToolRequestedData
			{
				RequestId = "req-123",
				SessionId = "test-session",
				ToolCallId = "call-456",
				ToolName = "my_external_tool",
				Arguments = System.Text.Json.JsonSerializer.SerializeToElement(
					new Dictionary<string, object> { ["arg1"] = "value1" }),
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

	// ── SDK 1.0.0 events newly added to the silently-consumed group ──
	//
	// These tests guard against regression: each event below was previously hitting
	// the default arm of CopilotSessionHandler.HandleEvent and tripping the
	// "[unhandled_sdk_event]" warning. Adding them to the silent list means
	// HandleEvent must produce zero AgentEvents. If the SDK ever adds new semantics
	// to one of these events that we DO want to surface, the corresponding test
	// here will need to be updated alongside the handler change.

	[Fact]
	public void HandleEvent_AssistantMessageStart_DoesNotWriteEvent()
	{
		// Arrange — SDK 1.0.0 marker event paired with AssistantMessageDeltaEvent /
		// AssistantMessageEvent (which DO write to the channel). The start marker
		// itself carries no actionable payload beyond MessageId + Phase.
		var evt = new AssistantMessageStartEvent
		{
			Data = new AssistantMessageStartData
			{
				MessageId = "msg-1",
				Phase = "main",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_HookProgress_DoesNotWriteEvent()
	{
		// Arrange — Long-running hooks can stream interim progress messages via
		// this event. We do not forward them to the audit log to keep its size
		// bounded; HookStart/HookEnd entries already mark the lifecycle.
		var evt = new HookProgressEvent
		{
			Data = new HookProgressData
			{
				Message = "still working...",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_McpAppToolCallComplete_DoesNotWriteEvent()
	{
		// Arrange — SDK 1.0.0 emits this alongside ToolExecutionCompleteEvent for
		// MCP tool calls, with richer structured data. The regular event is
		// already wired into HandleToolExecutionComplete, so this one is
		// redundant for current needs. If we ever add "fail step on MCP tool
		// error" semantics, this event would be the cleanest hook point — and
		// this test would need to be replaced with one asserting an emitted
		// failure signal.
		var evt = new McpAppToolCallCompleteEvent
		{
			Data = new McpAppToolCallCompleteData
			{
				ServerName = "workiq",
				ToolName = "ask_work_iq",
				Success = false,
				DurationMs = 63000,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionAutopilotObjectiveChanged_DoesNotWriteEvent()
	{
		// Arrange — Autopilot mode is an SDK-level capability Orchestra does not use.
		var evt = new SessionAutopilotObjectiveChangedEvent
		{
			Data = new SessionAutopilotObjectiveChangedData
			{
				Operation = AutopilotObjectiveChangedOperation.Create,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionCanvasOpened_DoesNotWriteEvent()
	{
		// Arrange — Canvas is an IDE UI surface for extension previews. N/A in a headless host.
		// SessionCanvas* are experimental SDK surfaces (GHCP001); we only assert the handler ignores
		// them, so construct a minimal valid instance.
#pragma warning disable GHCP001
		var evt = new SessionCanvasOpenedEvent
		{
			Data = new SessionCanvasOpenedData
			{
				CanvasId = "canvas-1",
				ExtensionId = "ext-1",
				InstanceId = "inst-1",
			}
		};
#pragma warning restore GHCP001

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionCanvasRegistryChanged_DoesNotWriteEvent()
	{
		// Arrange — Companion to SessionCanvasOpenedEvent; tracks the canvas registry.
#pragma warning disable GHCP001
		var evt = new SessionCanvasRegistryChangedEvent
		{
			Data = new SessionCanvasRegistryChangedData
			{
				Canvases = [],
			}
		};
#pragma warning restore GHCP001

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionCustomNotification_DoesNotWriteEvent()
	{
		// Arrange — Extension-defined notifications. No extensions are wired in
		// Orchestra's controlled host today, so we expect this event to never fire
		// in production runs; the silent default is safe.
		var evt = new SessionCustomNotificationEvent
		{
			Data = new SessionCustomNotificationData
			{
				Name = "test-notification",
				Source = "test-source",
				Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionExtensionsAttachmentsPushed_DoesNotWriteEvent()
	{
		// Arrange — Extensions pushing attachments to the session; not used by Orchestra.
		var evt = new SessionExtensionsAttachmentsPushedEvent
		{
			Data = new SessionExtensionsAttachmentsPushedData
			{
				Attachments = [],
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionPermissionsChanged_FlipOnToAllowAll_EmitsWarning()
	{
		// Arrange — false -> true transition: "always allow" was just enabled mid-session.
		// This is the security-relevant case: every subsequent tool call (including
		// destructive ones) will skip the per-call approval gate until the session ends.
		// We expect a Warning AgentEvent with DiagnosticType "session_permissions_widened"
		// so the message lands in StepExecutionTrace.Warnings and the reporter stream.
		var evt = new SessionPermissionsChangedEvent
		{
			Data = new SessionPermissionsChangedData
			{
				AllowAllPermissions = true,
				PreviousAllowAllPermissions = false,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.Warning);
		emitted.DiagnosticType.Should().Be("session_permissions_widened");
		emitted.ErrorMessage.Should().Contain("false to true");
		_reporter.Received(1).ReportSessionWarning(
			"session_permissions_widened",
			Arg.Is<string>(s => s.Contains("false to true")));
	}

	[Fact]
	public void HandleEvent_SessionPermissionsChanged_FlipOffFromAllowAll_EmitsInfo()
	{
		// Arrange — true -> false transition: "always allow" was just disabled. Less
		// alarming than the widening case (subsequent calls go back to the gated path)
		// but we still emit an audit breadcrumb through the Info channel so the
		// permission posture change is visible in operator dashboards.
		var evt = new SessionPermissionsChangedEvent
		{
			Data = new SessionPermissionsChangedData
			{
				AllowAllPermissions = false,
				PreviousAllowAllPermissions = true,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.Info);
		emitted.DiagnosticType.Should().Be("session_permissions_narrowed");
		emitted.Content.Should().Contain("true to false");
		_reporter.Received(1).ReportSessionInfo(
			"session_permissions_narrowed",
			Arg.Is<string>(s => s.Contains("true to false")));
	}

	[Fact]
	public void HandleEvent_SessionPermissionsChanged_NoActualChange_DoesNotWriteEvent()
	{
		// Arrange — the SDK is expected to emit only on actual transitions, but field
		// reports show occasional pings where current == previous. We silently drop
		// these to keep the warnings list focused on real posture changes.
		var evt = new SessionPermissionsChangedEvent
		{
			Data = new SessionPermissionsChangedData
			{
				AllowAllPermissions = false,
				PreviousAllowAllPermissions = false,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
		_reporter.DidNotReceive().ReportSessionWarning(Arg.Any<string>(), Arg.Any<string>());
		_reporter.DidNotReceive().ReportSessionInfo(Arg.Any<string>(), Arg.Any<string>());
	}

	[Fact]
	public void HandleEvent_SessionScheduleCancelled_DoesNotWriteEvent()
	{
		// Arrange — SDK-side scheduling. Orchestra uses its own scheduler (OrchestraScheduler).
		var evt = new SessionScheduleCancelledEvent
		{
			Data = new SessionScheduleCancelledData
			{
				Id = 1,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void HandleEvent_SessionScheduleCreated_DoesNotWriteEvent()
	{
		// Arrange — SDK-side scheduling. Orchestra uses its own scheduler.
		var evt = new SessionScheduleCreatedEvent
		{
			Data = new SessionScheduleCreatedData
			{
				Id = 1,
				Interval = TimeSpan.FromMinutes(5),
				Prompt = "test prompt",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out _).Should().BeFalse();
	}

	#endregion

	#region SDK 0.3.0 Telemetry — Auto-mode switch / System notifications / Quota snapshots

	[Fact]
	public void HandleEvent_AutoModeSwitchRequestedEvent_EmitsAutoModeSwitchRequested()
	{
		// Arrange
		var evt = new AutoModeSwitchRequestedEvent
		{
			Data = new AutoModeSwitchRequestedData
			{
				RequestId = "req-42",
				ErrorCode = "rate_limited",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.AutoModeSwitchRequested);
		emitted.AutoModeRequestId.Should().Be("req-42");
		emitted.AutoModeErrorCode.Should().Be("rate_limited");
		emitted.AutoModeResponse.Should().BeNull();
	}

	[Fact]
	public void HandleEvent_AutoModeSwitchCompletedEvent_EmitsAutoModeSwitchCompleted()
	{
		// Arrange — SDK 1.0.0 changed AutoModeSwitchCompletedData.Response from
		// a free-form string to a strongly-typed AutoModeSwitchResponse struct
		// (Yes / YesAlways / No). We use the wire-string ctor here because the
		// test originally exercised an arbitrary model-name response; in 1.0.0
		// the value space is constrained to the three known tokens, but the
		// handler still surfaces them via .Value to the engine-level event.
		var evt = new AutoModeSwitchCompletedEvent
		{
			Data = new AutoModeSwitchCompletedData
			{
				RequestId = "req-42",
				Response = AutoModeSwitchResponse.YesAlways,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.AutoModeSwitchCompleted);
		emitted.AutoModeRequestId.Should().Be("req-42");
		emitted.AutoModeResponse.Should().Be("yes_always");
		emitted.AutoModeErrorCode.Should().BeNull();
	}

	[Fact]
	public void HandleEvent_ModelCallFailure_EmitsModelCallFailureEvent_AndDoesNotFault()
	{
		// Arrange — SDK 1.0.0's ModelCallFailureEvent: an individual model API call
		// fails (HTTP 503 from upstream broker, say) but the CLI's own retry loop will
		// recover. Handler should emit a structured AgentEvent for observability and
		// MUST NOT fault the TaskCompletionSource — pre-empting CLI recovery would
		// consume a swap budget for a transient blip.
		var evt = new ModelCallFailureEvent
		{
			Data = new ModelCallFailureData
			{
				Source = ModelCallFailureSource.TopLevel,
				Model = "claude-opus-4.6",
				ErrorMessage = "Upstream broker returned 503 Service Unavailable",
				StatusCode = 503,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert — exactly one AgentEvent emitted, no exception faulted onto _done.
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.ModelCallFailure);
		emitted.ModelCallFailureSource.Should().Be("top_level");
		emitted.ModelCallFailureModel.Should().Be("claude-opus-4.6");
		emitted.ModelCallFailureMessage.Should().Be("Upstream broker returned 503 Service Unavailable");
		emitted.ModelCallFailureStatusCode.Should().Be(503);

		// No additional events follow this single emission.
		_channel.Reader.TryRead(out var nextEvt).Should().BeFalse();
		nextEvt.Should().BeNull();

		// Crucially: the TCS must not be faulted. The CLI retry path owns the
		// session-fail decision; we are observational only on ModelCallFailure.
		_done.Task.IsCompleted.Should().BeFalse(
			"ModelCallFailureEvent is observational — only SessionErrorEvent / SessionShutdownEvent should fault the session TCS");
	}

	[Fact]
	public void HandleEvent_Usage_SurfacesInterTokenLatencyOnAgentEvent()
	{
		// Arrange — SDK 1.0.0 added AssistantUsageData.InterTokenLatency (TimeSpan?).
		// We project it to milliseconds on AgentEvent.InterTokenLatencyMs so consumers
		// can graph streaming-perf without re-querying.
		#pragma warning disable GHCP001 // AssistantUsageData.Cost is marked evaluation-only
		var evt = new AssistantUsageEvent
		{
			Data = new AssistantUsageData
			{
				Model = "claude-opus-4.6",
				InputTokens = 1000,
				OutputTokens = 200,
				InterTokenLatency = TimeSpan.FromMilliseconds(42),
			}
		};
		#pragma warning restore GHCP001

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var usage).Should().BeTrue();
		usage!.Type.Should().Be(AgentEventType.Usage);
		usage.InterTokenLatencyMs.Should().Be(42.0);
	}

	[Fact]
	public void HandleEvent_Info_SurfacesTipAndUrl()
	{
		// Arrange — SDK 1.0.0 added Tip + Url alongside InfoType + Message.
		var evt = new SessionInfoEvent
		{
			Data = new SessionInfoData
			{
				InfoType = "auth_handshake_required",
				Message = "Run `gh auth login` to refresh your token.",
				Tip = "Use `--scopes copilot` for full access.",
				Url = "https://docs.example/auth-refresh",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var info).Should().BeTrue();
		info!.Type.Should().Be(AgentEventType.Info);
		info.InfoTip.Should().Be("Use `--scopes copilot` for full access.");
		info.InfoUrl.Should().Be("https://docs.example/auth-refresh");
	}

	[Fact]
	public void HandleEvent_Warning_SurfacesUrl()
	{
		// Arrange — SDK 1.0.0 added Url to warnings (typically a status-page link).
		var evt = new SessionWarningEvent
		{
			Data = new SessionWarningData
			{
				WarningType = "model_degraded",
				Message = "Model latency elevated.",
				Url = "https://status.example/model-x",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var warning).Should().BeTrue();
		warning!.Type.Should().Be(AgentEventType.Warning);
		warning.WarningUrl.Should().Be("https://status.example/model-x");
	}

	[Fact]
	public void HandleEvent_Shutdown_CapturesStructuredBillingSummary()
	{
		// Arrange — SDK 1.0.0 SessionShutdownEvent carries a structured billing payload
		// that replaces the per-usage QuotaSnapshots / TotalNanoAiu of 0.3.0. Handler
		// must project it into AgentSessionShutdownSummary and expose via
		// ShutdownSummary so CopilotAgent can include it in AgentResult.FinalUsage.
		#pragma warning disable GHCP001 // SessionShutdownData.TotalNanoAiu + ShutdownModelMetric.* are evaluation-only
		var evt = new SessionShutdownEvent
		{
			Data = new SessionShutdownData
			{
				ShutdownType = ShutdownType.Routine,
				// SessionStartTime is `required` on the SDK 1.0.0 SessionShutdownData. The handler
				// does not consume the field but the SDK's record initializer demands it.
				SessionStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				ConversationTokens = 9_001,
				CurrentTokens = 9_500,
				SystemTokens = 250,
				ToolDefinitionsTokens = 1_200,
				TotalApiDuration = TimeSpan.FromSeconds(42),
				TotalNanoAiu = 12_345.678,
				CodeChanges = new ShutdownCodeChanges
				{
					FilesModified = ["src/Foo.cs", "src/Bar.cs"],
					LinesAdded = 17,
					LinesRemoved = 4,
				},
				ModelMetrics = new Dictionary<string, ShutdownModelMetric>(StringComparer.Ordinal)
				{
					["claude-opus-4.6"] = new ShutdownModelMetric
					{
						TotalNanoAiu = 12_000,
						Requests = new ShutdownModelMetricRequests
						{
							Count = 5,
							Cost = 0.123,
						},
						Usage = new ShutdownModelMetricUsage
						{
							InputTokens = 800,
							OutputTokens = 200,
							CacheReadTokens = 50,
							CacheWriteTokens = 25,
							ReasoningTokens = 30,
						}
					}
				}
			}
		};
		#pragma warning restore GHCP001

		// Act
		_handler.HandleEvent(evt);

		// Assert — shutdown summary materialised with full fidelity.
		var summary = _handler.ShutdownSummary;
		summary.Should().NotBeNull();
		summary!.TotalNanoAiu.Should().Be(12_345.678);
		summary.ConversationTokens.Should().Be(9_001);
		summary.CurrentTokens.Should().Be(9_500);
		summary.SystemTokens.Should().Be(250);
		summary.ToolDefinitionsTokens.Should().Be(1_200);
		summary.TotalApiDuration.Should().Be(TimeSpan.FromSeconds(42));

		summary.CodeChanges.Should().NotBeNull();
		summary.CodeChanges!.FilesModified.Should().BeEquivalentTo(["src/Foo.cs", "src/Bar.cs"]);
		summary.CodeChanges.LinesAdded.Should().Be(17);
		summary.CodeChanges.LinesRemoved.Should().Be(4);

		summary.ModelMetrics.Should().NotBeNull();
		summary.ModelMetrics!.Should().ContainKey("claude-opus-4.6");
		var modelMetric = summary.ModelMetrics!["claude-opus-4.6"];
		modelMetric.TotalNanoAiu.Should().Be(12_000);
		modelMetric.Requests!.Count.Should().Be(5);
		modelMetric.Requests.Cost.Should().Be(0.123);
		modelMetric.Usage!.InputTokens.Should().Be(800);
		modelMetric.Usage.OutputTokens.Should().Be(200);
		modelMetric.Usage.CacheReadTokens.Should().Be(50);
		modelMetric.Usage.CacheWriteTokens.Should().Be(25);
		modelMetric.Usage.ReasoningTokens.Should().Be(30);

		// Routine shutdown must complete the TCS — this matches the pre-1.0.0 behaviour.
		_done.Task.IsCompletedSuccessfully.Should().BeTrue();
	}

	[Fact]
	public void HandleEvent_SystemNotificationEvent_EmitsSystemNotificationWithKindAndMessage()
	{
		// Arrange — SDK 0.3.0 system notification with typed discriminator.
		var evt = new SystemNotificationEvent
		{
			Data = new SystemNotificationData
			{
				Kind = new SystemNotificationAgentIdle
				{
					Type = "agent_idle",
					AgentId = "main",
					AgentType = "default",
					Description = "agent is now idle",
				},
				Content = "Main agent is idle",
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.SystemNotification);
		emitted.NotificationKind.Should().Be("agent_idle");
		emitted.NotificationMessage.Should().Be("Main agent is idle");
	}

	[Fact]
	public void HandleEvent_AssistantUsageWithTtftAndReasoningTokens_EmitsUsageWithSdk1_0Fields()
	{
		// Arrange — SDK 1.0.0 narrowed AssistantUsageData's surface vs. 0.3.0:
		//   * QuotaSnapshots dictionary REMOVED (moved to SessionShutdown).
		//   * CopilotUsage / TotalNanoAiu REMOVED from per-usage events.
		//   * TtftMs renamed -> TimeToFirstToken (and re-typed from double to TimeSpan?).
		// This test verifies the handler:
		//   1. Projects TimeToFirstToken (TimeSpan) -> AgentUsage.TimeToFirstTokenMs (double, ms).
		//   2. Surfaces ReasoningTokens unchanged.
		//   3. Leaves TotalNanoAiu/QuotaSnapshots null on the agent-level shape.
		#pragma warning disable GHCP001 // AssistantUsageData.Cost is marked evaluation-only by the SDK
		var evt = new AssistantUsageEvent
		{
			Data = new AssistantUsageData
			{
				Model = "claude-opus-4.5",
				InputTokens = 1000,
				OutputTokens = 200,
				ReasoningTokens = 50,
				TimeToFirstToken = TimeSpan.FromMilliseconds(230),
				Cost = 0.0123,
				Duration = TimeSpan.FromSeconds(4.2),
			}
		};
		#pragma warning restore GHCP001

		// Act
		_handler.HandleEvent(evt);

		// Assert — single Usage event with the SDK 1.0.0 shape; no follow-up QuotaSnapshot
		// event since the SDK no longer carries quota data on per-usage events.
		_channel.Reader.TryRead(out var usage).Should().BeTrue();
		usage!.Type.Should().Be(AgentEventType.Usage);
		usage.Usage.Should().NotBeNull();
		usage.Usage!.ReasoningTokens.Should().Be(50);
		usage.Usage.TimeToFirstTokenMs.Should().Be(230);
		usage.Usage.Cost.Should().Be(0.0123);
		usage.Usage.Duration.Should().Be(4.2);
		usage.Usage.TotalNanoAiu.Should().BeNull("SDK 1.0.0 moved TotalNanoAiu to SessionShutdownData");
		usage.Usage.QuotaSnapshots.Should().BeNull("SDK 1.0.0 no longer emits quota snapshots on per-usage events");

		// No subsequent QuotaSnapshot event must follow.
		_channel.Reader.TryRead(out var follow).Should().BeFalse(
			"the handler must not emit a follow-up QuotaSnapshot event in SDK 1.0.0 because the SDK no longer carries quota data on per-usage events");
		follow.Should().BeNull();
	}

	[Fact]
	public void HandleEvent_AssistantUsageWithoutQuotaSnapshots_DoesNotEmitQuotaEvent()
	{
		// Arrange — usage event with no quota snapshots; only Usage should be emitted.
		var evt = new AssistantUsageEvent
		{
			Data = new AssistantUsageData
			{
				Model = "claude-opus-4.5",
				InputTokens = 100,
				OutputTokens = 20,
			}
		};

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var usage).Should().BeTrue();
		usage!.Type.Should().Be(AgentEventType.Usage);
		_channel.Reader.TryRead(out var follow).Should().BeFalse(
			"a usage event without quota snapshots must not emit a follow-up QuotaSnapshot event");
		follow.Should().BeNull();
	}

	#endregion

	#region Permission Lifecycle Audit (SDK 1.0.0)

	// SDK 1.0.0 emits PermissionRequestedEvent / PermissionCompletedEvent around every
	// side-effectful action (read, write, shell, url, mcp, memory, customTool, hook,
	// extensionManagement, extensionPermissionAccess). Orchestra wires
	// OnPermissionRequest = PermissionHandler.ApproveAll, so the practical effect is
	// "always approved" — but the audit trail captures exactly what the agent was
	// permitted to do, which is invaluable for compliance / forensic review.
	//
	// These tests cover each PermissionRequest subclass + each PermissionResult subclass
	// to lock in the kind/target/decision extraction so future SDK shape changes are
	// caught at the unit-test level.

	private static PermissionRequestedEvent BuildPermissionRequested(string requestId, PermissionRequest request) => new()
	{
		Data = new PermissionRequestedData
		{
			RequestId = requestId,
			PermissionRequest = request,
		}
	};

	private static PermissionCompletedEvent BuildPermissionCompleted(string requestId, PermissionResult result, string? toolCallId = null) => new()
	{
		Data = new PermissionCompletedData
		{
			RequestId = requestId,
			Result = result,
			ToolCallId = toolCallId,
		}
	};

	[Fact]
	public void HandleEvent_PermissionRequested_Read_EmitsPermissionRequestedWithPath()
	{
		// Arrange
		var evt = BuildPermissionRequested("req-1", new PermissionRequestRead
		{
			Path = "/tmp/secret.txt",
			Intention = "Read configuration file",
			ToolCallId = "tc-read-1",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.PermissionRequested);
		emitted.PermissionRequestId.Should().Be("req-1");
		emitted.PermissionKind.Should().Be("read");
		emitted.PermissionTarget.Should().Be("/tmp/secret.txt");
		emitted.PermissionToolCallId.Should().Be("tc-read-1");
	}

	[Fact]
	public void HandleEvent_PermissionRequested_Write_EmitsWithFileName()
	{
		// Arrange — Write requests carry FileName + Diff; we surface FileName as the
		// audit target (the diff is large and would balloon the audit log).
		var evt = BuildPermissionRequested("req-2", new PermissionRequestWrite
		{
			CanOfferSessionApproval = false,
			Diff = "+ new line",
			FileName = "/app/main.py",
			Intention = "Update the entrypoint",
			ToolCallId = "tc-write-1",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.PermissionRequested);
		emitted.PermissionKind.Should().Be("write");
		emitted.PermissionTarget.Should().Be("/app/main.py");
		emitted.PermissionToolCallId.Should().Be("tc-write-1");
	}

	[Fact]
	public void HandleEvent_PermissionRequested_Shell_EmitsWithTruncatedCommand()
	{
		// Arrange — Shell carries FullCommandText which can be arbitrarily long.
		// Verify both the short-path (passes through verbatim) and that the helper
		// truncates a long command for audit storage.
		var longCommand = new string('a', 600);
		var evt = BuildPermissionRequested("req-3", new PermissionRequestShell
		{
			CanOfferSessionApproval = true,
			Commands = [],
			FullCommandText = longCommand,
			HasWriteFileRedirection = false,
			Intention = "Stress test",
			PossiblePaths = [],
			PossibleUrls = [],
			ToolCallId = "tc-shell-1",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionKind.Should().Be("shell");
		emitted.PermissionTarget.Should().HaveLength(501, "audit-side truncation caps shell command text at 500 chars + ellipsis");
		emitted.PermissionTarget.Should().EndWith("…");
		emitted.PermissionToolCallId.Should().Be("tc-shell-1");
	}

	[Fact]
	public void HandleEvent_PermissionRequested_Url_EmitsWithUrl()
	{
		// Arrange
		var evt = BuildPermissionRequested("req-4", new PermissionRequestUrl
		{
			Url = "https://example.com/api",
			Intention = "Fetch external data",
			ToolCallId = "tc-url-1",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionKind.Should().Be("url");
		emitted.PermissionTarget.Should().Be("https://example.com/api");
	}

	[Fact]
	public void HandleEvent_PermissionRequested_Mcp_EmitsServerColonToolTarget()
	{
		// Arrange — MCP target format is "server::tool" so a Portal can group all
		// permission requests for a given server.
		var evt = BuildPermissionRequested("req-5", new PermissionRequestMcp
		{
			ReadOnly = false,
			ServerName = "workiq",
			ToolName = "ask_work_iq",
			ToolTitle = "Ask WorkIQ",
			ToolCallId = "tc-mcp-1",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionKind.Should().Be("mcp");
		emitted.PermissionTarget.Should().Be("workiq::ask_work_iq");
		emitted.PermissionToolCallId.Should().Be("tc-mcp-1");
	}

	[Fact]
	public void HandleEvent_PermissionRequested_Memory_EmitsWithSubject()
	{
		// Arrange
		var evt = BuildPermissionRequested("req-6", new PermissionRequestMemory
		{
			Fact = "user prefers concise output",
			Subject = "user.preferences",
			ToolCallId = "tc-memory-1",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionKind.Should().Be("memory");
		emitted.PermissionTarget.Should().Be("user.preferences");
	}

	[Fact]
	public void HandleEvent_PermissionRequested_CustomTool_EmitsWithToolName()
	{
		// Arrange
		var evt = BuildPermissionRequested("req-7", new PermissionRequestCustomTool
		{
			ToolDescription = "Custom Orchestra engine tool",
			ToolName = "orchestra_set_status",
			ToolCallId = "tc-engine-1",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionKind.Should().Be("customTool");
		emitted.PermissionTarget.Should().Be("orchestra_set_status");
	}

	[Fact]
	public void HandleEvent_PermissionCompleted_Approved_EmitsApprovedDecision()
	{
		// Arrange — the most common case under Orchestra's ApproveAll handler.
		var evt = BuildPermissionCompleted("req-1", new PermissionResultApproved(), toolCallId: "tc-1");

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.Type.Should().Be(AgentEventType.PermissionCompleted);
		emitted.PermissionRequestId.Should().Be("req-1");
		emitted.PermissionDecision.Should().Be("approved");
		emitted.PermissionDecisionReason.Should().BeNull();
		emitted.PermissionToolCallId.Should().Be("tc-1");
	}

	[Fact]
	public void HandleEvent_PermissionCompleted_ApprovedForLocation_EmitsLocationKeyAsReason()
	{
		// Arrange — "approve for this folder" UI grant; LocationKey is the scope.
		var evt = BuildPermissionCompleted("req-2", new PermissionResultApprovedForLocation
		{
			Approval = new UserToolSessionApproval(),
			LocationKey = "/workspace/project-a",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionDecision.Should().Be("approvedForLocation");
		emitted.PermissionDecisionReason.Should().Be("/workspace/project-a");
	}

	[Fact]
	public void HandleEvent_PermissionCompleted_DeniedByRules_EmitsDecisionWithRuleList()
	{
		// Arrange — rule-based denials need the rule list in the reason so an operator
		// can identify which rule(s) blocked the action.
		var evt = BuildPermissionCompleted("req-3", new PermissionResultDeniedByRules
		{
			Rules =
			[
				new PermissionRule { Kind = "shell" },
				new PermissionRule { Kind = "write", Argument = "/etc/**" },
			],
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionDecision.Should().Be("deniedByRules");
		emitted.PermissionDecisionReason.Should().Contain("shell");
		emitted.PermissionDecisionReason.Should().Contain("write:/etc/**");
	}

	[Fact]
	public void HandleEvent_PermissionCompleted_DeniedByContentExclusion_EmitsMessageWithPath()
	{
		// Arrange — content-exclusion denials carry both a message and the offending
		// path; we collapse them into a single reason string.
		var evt = BuildPermissionCompleted("req-4", new PermissionResultDeniedByContentExclusionPolicy
		{
			Message = "Path matches enterprise content exclusion",
			Path = "/secrets/key.pem",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionDecision.Should().Be("deniedByContentExclusionPolicy");
		emitted.PermissionDecisionReason.Should().Contain("enterprise content exclusion");
		emitted.PermissionDecisionReason.Should().Contain("/secrets/key.pem");
	}

	[Fact]
	public void HandleEvent_PermissionCompleted_Cancelled_EmitsReasonFromPayload()
	{
		// Arrange
		var evt = BuildPermissionCompleted("req-5", new PermissionResultCancelled
		{
			Reason = "session_shutdown",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionDecision.Should().Be("cancelled");
		emitted.PermissionDecisionReason.Should().Be("session_shutdown");
	}

	[Fact]
	public void HandleEvent_PermissionCompleted_DeniedInteractivelyByUser_EmitsFeedbackAsReason()
	{
		// Arrange
		var evt = BuildPermissionCompleted("req-6", new PermissionResultDeniedInteractivelyByUser
		{
			Feedback = "don't touch that file",
		});

		// Act
		_handler.HandleEvent(evt);

		// Assert
		_channel.Reader.TryRead(out var emitted).Should().BeTrue();
		emitted!.PermissionDecision.Should().Be("deniedInteractivelyByUser");
		emitted.PermissionDecisionReason.Should().Be("don't touch that file");
	}

	[Fact]
	public void HandleEvent_PermissionLifecycle_RequestedAndCompletedShareRequestId()
	{
		// Arrange — paired emission with a single request id flowing through both events
		// so a downstream consumer can stitch them into a single per-call audit row.
		_handler.HandleEvent(BuildPermissionRequested("req-roundtrip", new PermissionRequestRead
		{
			Path = "/tmp/x",
			Intention = "test",
			ToolCallId = "tc-roundtrip",
		}));
		_handler.HandleEvent(BuildPermissionCompleted("req-roundtrip", new PermissionResultApproved(), toolCallId: "tc-roundtrip"));

		// Assert — both emitted events should share PermissionRequestId.
		_channel.Reader.TryRead(out var req).Should().BeTrue();
		_channel.Reader.TryRead(out var done).Should().BeTrue();
		req!.Type.Should().Be(AgentEventType.PermissionRequested);
		done!.Type.Should().Be(AgentEventType.PermissionCompleted);
		req.PermissionRequestId.Should().Be(done.PermissionRequestId);
		req.PermissionToolCallId.Should().Be(done.PermissionToolCallId);
	}

	#endregion
}
