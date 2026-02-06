namespace OrchestrationEngine.Core.Events;

/// <summary>
/// Base type for all agent events streamed during execution.
/// </summary>
public abstract record AgentEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Streaming reasoning content delta.
/// </summary>
public sealed record ReasoningDeltaEvent(string Delta) : AgentEvent;

/// <summary>
/// Complete reasoning content.
/// </summary>
public sealed record ReasoningCompleteEvent(string Content) : AgentEvent;

/// <summary>
/// Streaming response content delta.
/// </summary>
public sealed record ResponseDeltaEvent(string Delta) : AgentEvent;

/// <summary>
/// Complete response content.
/// </summary>
public sealed record ResponseCompleteEvent(string Content) : AgentEvent;

/// <summary>
/// Tool execution is starting.
/// </summary>
public sealed record ToolCallStartEvent(string ToolName, string Arguments) : AgentEvent;

/// <summary>
/// Tool execution completed.
/// </summary>
public sealed record ToolCallEndEvent(string ToolName, string Result) : AgentEvent;

/// <summary>
/// An error occurred during execution.
/// </summary>
public sealed record ErrorEvent(string Message, Exception? Exception = null) : AgentEvent;

/// <summary>
/// Agent execution is complete.
/// </summary>
public sealed record CompletedEvent : AgentEvent;
