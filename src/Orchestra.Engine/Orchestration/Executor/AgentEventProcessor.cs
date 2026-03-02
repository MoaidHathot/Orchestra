using System.Text;

namespace Orchestra.Engine;

/// <summary>
/// Processes agent events from an async stream, collecting trace data and reporting events.
/// Extracted from PromptExecutor to reduce complexity and improve testability.
/// </summary>
public class AgentEventProcessor
{
	private readonly IOrchestrationReporter _reporter;
	private readonly string _stepName;

	// Trace data collectors
	private readonly StringBuilder _reasoningBuilder = new();
	private readonly List<ToolCallRecord> _toolCalls = [];
	private readonly List<string> _responseSegments = [];
	private readonly StringBuilder _currentResponseBuilder = new();
	private readonly Dictionary<string, PendingToolCall> _pendingToolCalls = [];

	public AgentEventProcessor(IOrchestrationReporter reporter, string stepName)
	{
		_reporter = reporter;
		_stepName = stepName;
	}

	/// <summary>
	/// Gets the collected reasoning content.
	/// </summary>
	public string? Reasoning => _reasoningBuilder.Length > 0 ? _reasoningBuilder.ToString() : null;

	/// <summary>
	/// Gets the collected tool calls.
	/// </summary>
	public IReadOnlyList<ToolCallRecord> ToolCalls => _toolCalls;

	/// <summary>
	/// Gets the collected response segments.
	/// </summary>
	public IReadOnlyList<string> ResponseSegments => _responseSegments;

	/// <summary>
	/// Processes all events from the agent stream, reporting them and collecting trace data.
	/// </summary>
	public async Task ProcessEventsAsync(
		IAsyncEnumerable<AgentEvent> events,
		CancellationToken cancellationToken = default)
	{
		await foreach (var evt in events.WithCancellation(cancellationToken))
		{
			ProcessEvent(evt);
		}

		// Save any remaining response content after stream ends
		FinalizeCurrentResponse();
	}

	/// <summary>
	/// Processes a single agent event.
	/// </summary>
	private void ProcessEvent(AgentEvent evt)
	{
		switch (evt.Type)
		{
			case AgentEventType.MessageDelta:
				HandleMessageDelta(evt);
				break;

			case AgentEventType.ReasoningDelta:
				HandleReasoningDelta(evt);
				break;

			case AgentEventType.ToolExecutionStart:
				HandleToolExecutionStart(evt);
				break;

			case AgentEventType.ToolExecutionComplete:
				HandleToolExecutionComplete(evt);
				break;

			case AgentEventType.Error:
				HandleError(evt);
				break;
		}
	}

	private void HandleMessageDelta(AgentEvent evt)
	{
		_reporter.ReportContentDelta(_stepName, evt.Content ?? string.Empty);
		_currentResponseBuilder.Append(evt.Content ?? string.Empty);
	}

	private void HandleReasoningDelta(AgentEvent evt)
	{
		_reporter.ReportReasoningDelta(_stepName, evt.Content ?? string.Empty);
		_reasoningBuilder.Append(evt.Content ?? string.Empty);
	}

	private void HandleToolExecutionStart(AgentEvent evt)
	{
		_reporter.ReportToolExecutionStarted(_stepName, evt.ToolName ?? "unknown", evt.ToolArguments, evt.McpServerName);

		// Save current response segment before tool call (if any content)
		if (_currentResponseBuilder.Length > 0)
		{
			_responseSegments.Add(_currentResponseBuilder.ToString());
			_currentResponseBuilder.Clear();
		}

		// Track pending tool call
		if (evt.ToolCallId is not null)
		{
			_pendingToolCalls[evt.ToolCallId] = new PendingToolCall(
				evt.ToolName ?? "unknown",
				evt.ToolArguments,
				evt.McpServerName,
				DateTimeOffset.UtcNow
			);
		}
		else
		{
			// No call ID, create record immediately
			_toolCalls.Add(new ToolCallRecord
			{
				ToolName = evt.ToolName ?? "unknown",
				Arguments = evt.ToolArguments,
				McpServer = evt.McpServerName,
				StartedAt = DateTimeOffset.UtcNow,
			});
		}
	}

	private void HandleToolExecutionComplete(AgentEvent evt)
	{
		_reporter.ReportToolExecutionCompleted(_stepName, evt.ToolName ?? "unknown", evt.ToolSuccess ?? false, evt.ToolResult, evt.ToolError);

		// Complete the pending tool call record
		if (evt.ToolCallId is not null && _pendingToolCalls.TryGetValue(evt.ToolCallId, out var pending))
		{
			_pendingToolCalls.Remove(evt.ToolCallId);
			_toolCalls.Add(new ToolCallRecord
			{
				CallId = evt.ToolCallId,
				ToolName = pending.ToolName,
				Arguments = pending.Arguments,
				McpServer = pending.McpServer,
				Success = evt.ToolSuccess ?? false,
				Result = evt.ToolResult,
				Error = evt.ToolError,
				StartedAt = pending.StartedAt,
				CompletedAt = DateTimeOffset.UtcNow,
			});
		}
		else
		{
			// No matching pending call, create complete record
			_toolCalls.Add(new ToolCallRecord
			{
				CallId = evt.ToolCallId,
				ToolName = evt.ToolName ?? "unknown",
				Success = evt.ToolSuccess ?? false,
				Result = evt.ToolResult,
				Error = evt.ToolError,
				CompletedAt = DateTimeOffset.UtcNow,
			});
		}
	}

	private void HandleError(AgentEvent evt)
	{
		_reporter.ReportStepError(_stepName, evt.ErrorMessage ?? "Unknown error");
	}

	private void FinalizeCurrentResponse()
	{
		if (_currentResponseBuilder.Length > 0)
		{
			_responseSegments.Add(_currentResponseBuilder.ToString());
			_currentResponseBuilder.Clear();
		}
	}

	/// <summary>
	/// Builds a StepExecutionTrace from the collected data.
	/// </summary>
	public StepExecutionTrace BuildTrace(
		string? systemPrompt,
		string? userPromptRaw,
		string? userPromptProcessed = null,
		string? finalResponse = null,
		string? outputHandlerResult = null)
	{
		return new StepExecutionTrace
		{
			SystemPrompt = systemPrompt,
			UserPromptRaw = userPromptRaw,
			UserPromptProcessed = userPromptProcessed,
			Reasoning = Reasoning,
			ToolCalls = _toolCalls,
			ResponseSegments = _responseSegments.ToList(),
			FinalResponse = finalResponse,
			OutputHandlerResult = outputHandlerResult,
		};
	}

	/// <summary>
	/// Builds a partial trace (typically used when an error occurs).
	/// </summary>
	public StepExecutionTrace BuildPartialTrace(string? systemPrompt, string? userPromptRaw)
	{
		return new StepExecutionTrace
		{
			SystemPrompt = systemPrompt,
			UserPromptRaw = userPromptRaw,
			Reasoning = Reasoning,
			ToolCalls = _toolCalls,
			ResponseSegments = _responseSegments.ToList(),
		};
	}

	/// <summary>
	/// Represents a pending tool call awaiting completion.
	/// </summary>
	private sealed record PendingToolCall(
		string ToolName,
		string? Arguments,
		string? McpServer,
		DateTimeOffset StartedAt);
}
