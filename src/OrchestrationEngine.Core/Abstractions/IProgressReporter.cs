using OrchestrationEngine.Core.Events;

namespace OrchestrationEngine.Core.Abstractions;

/// <summary>
/// Step status in the orchestration.
/// </summary>
public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

/// <summary>
/// Agent type for display purposes.
/// </summary>
public enum AgentType
{
    /// <summary>Main orchestration step agent.</summary>
    Step,
    /// <summary>Input transformation handler.</summary>
    InputHandler,
    /// <summary>Output transformation handler.</summary>
    OutputHandler,
    /// <summary>Placeholder resolution agent.</summary>
    PlaceholderResolver
}

/// <summary>
/// Fine-grained status for agent activity.
/// </summary>
public enum AgentStatus
{
    /// <summary>Initializing the agent.</summary>
    Initializing,
    /// <summary>Waiting for LLM response.</summary>
    Thinking,
    /// <summary>Receiving reasoning tokens.</summary>
    Reasoning,
    /// <summary>Receiving response tokens.</summary>
    Streaming,
    /// <summary>Executing a tool call.</summary>
    CallingTool,
    /// <summary>Agent completed.</summary>
    Completed
}

/// <summary>
/// Information about a step for progress reporting.
/// </summary>
public record StepInfo(string Name, StepStatus Status);

/// <summary>
/// Abstraction for reporting orchestration progress.
/// Does not contain TUI-specific methods.
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// Reports the orchestration name.
    /// </summary>
    void ReportOrchestrationName(string name);

    /// <summary>
    /// Reports all steps at the beginning of orchestration.
    /// </summary>
    void ReportSteps(IReadOnlyList<StepInfo> steps);

    /// <summary>
    /// Reports that a step has started.
    /// </summary>
    void ReportStepStarted(string stepName);

    /// <summary>
    /// Reports that a step has completed.
    /// </summary>
    void ReportStepCompleted(string stepName);

    /// <summary>
    /// Reports that a step has failed.
    /// </summary>
    void ReportStepFailed(string stepName, string error);

    /// <summary>
    /// Reports the currently active agent with its type.
    /// </summary>
    void ReportActiveAgent(string agentName, AgentType agentType);

    /// <summary>
    /// Reports the current agent status (thinking, streaming, etc.)
    /// </summary>
    void ReportAgentStatus(AgentStatus status, string? detail = null);

    /// <summary>
    /// Reports an agent event (reasoning, response, tool calls, etc.)
    /// </summary>
    void ReportAgentEvent(AgentEvent agentEvent);

    /// <summary>
    /// Reports orchestration completion.
    /// </summary>
    void ReportOrchestrationCompleted(bool success, string? finalOutput = null);
}
