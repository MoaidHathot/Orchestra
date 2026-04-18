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
	private readonly List<string> _warnings = [];
	private readonly List<McpServerStatusInfo> _mcpServerStatuses = [];
	private readonly List<ConversationMessage> _conversationHistory = [];
	private readonly List<AuditLogEntry> _auditLog = [];

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
	/// Gets the collected audit log entries.
	/// </summary>
	public IReadOnlyList<AuditLogEntry> AuditLog => _auditLog;

	/// <summary>
	/// Adds an audit log entry, automatically assigning the sequence number.
	/// </summary>
	public void AddAuditLogEntry(AuditLogEntry entry)
	{
		entry.Sequence = _auditLog.Count;
		_auditLog.Add(entry);
	}

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

			case AgentEventType.SubagentSelected:
				HandleSubagentSelected(evt);
				break;

			case AgentEventType.SubagentStarted:
				HandleSubagentStarted(evt);
				break;

			case AgentEventType.SubagentCompleted:
				HandleSubagentCompleted(evt);
				break;

			case AgentEventType.SubagentFailed:
				HandleSubagentFailed(evt);
				break;

			case AgentEventType.SubagentDeselected:
				HandleSubagentDeselected();
				break;

			case AgentEventType.Error:
				HandleError(evt);
				break;

			case AgentEventType.Warning:
				HandleWarning(evt);
			break;

			case AgentEventType.Info:
				HandleInfo(evt);
				break;

			case AgentEventType.McpServersLoaded:
				HandleMcpServersLoaded(evt);
				break;

			case AgentEventType.McpServerStatusChanged:
				HandleMcpServerStatusChanged(evt);
				break;

			case AgentEventType.CompactionStart:
				HandleCompactionStart(evt);
				break;

			case AgentEventType.CompactionComplete:
				HandleCompactionComplete(evt);
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
			var segment = _currentResponseBuilder.ToString();
			_responseSegments.Add(segment);
			_conversationHistory.Add(new ConversationMessage
			{
				Role = "assistant",
				Content = segment,
				Timestamp = DateTimeOffset.UtcNow,
			});
			_currentResponseBuilder.Clear();
		}

		// Record tool call start in conversation history
		_conversationHistory.Add(new ConversationMessage
		{
			Role = "assistant",
			Content = $"[tool_call] {evt.ToolName ?? "unknown"}({evt.ToolArguments ?? ""})",
			ToolCallId = evt.ToolCallId,
			ToolName = evt.ToolName,
			Timestamp = DateTimeOffset.UtcNow,
		});

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

		// Record tool result in conversation history
		_conversationHistory.Add(new ConversationMessage
		{
			Role = "tool",
			Content = evt.ToolSuccess == true ? evt.ToolResult : $"[error] {evt.ToolError}",
			ToolCallId = evt.ToolCallId,
			ToolName = evt.ToolName,
			Timestamp = DateTimeOffset.UtcNow,
		});

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

	private void HandleWarning(AgentEvent evt)
	{
		var warningMessage = $"[{evt.DiagnosticType ?? "unknown"}] {evt.ErrorMessage ?? "Unknown warning"}";
		_warnings.Add(warningMessage);
		_reporter.ReportSessionWarning(evt.DiagnosticType ?? "unknown", evt.ErrorMessage ?? "Unknown warning");
	}

	private void HandleInfo(AgentEvent evt)
	{
		_reporter.ReportSessionInfo(evt.DiagnosticType ?? "unknown", evt.Content ?? "");
	}

	private void HandleMcpServersLoaded(AgentEvent evt)
	{
		var statuses = evt.McpServerStatuses ?? [];
		_mcpServerStatuses.Clear();
		_mcpServerStatuses.AddRange(statuses);

		_reporter.ReportMcpServersLoaded(statuses);

		// Auto-generate warnings for any failed servers
		foreach (var server in statuses)
		{
			if (string.Equals(server.Status, "Failed", StringComparison.OrdinalIgnoreCase))
			{
				var errorDetail = server.Error is not null ? $": {server.Error}" : "";
				var warningMessage = $"MCP server '{server.Name}' failed to connect{errorDetail}";
				_warnings.Add($"[mcp_server_failed] {warningMessage}");
			}
		}
	}

	private void HandleMcpServerStatusChanged(AgentEvent evt)
	{
		_reporter.ReportMcpServerStatusChanged(
			evt.McpServerName ?? "unknown",
			evt.McpServerStatus ?? "unknown");
	}

	private void HandleCompactionStart(AgentEvent evt)
	{
		var warningMessage = "[compaction] Context compaction started";
		_warnings.Add(warningMessage);
		_reporter.ReportSessionWarning("compaction", "Context compaction started");
	}

	private void HandleCompactionComplete(AgentEvent evt)
	{
		var message = $"[compaction] Context compaction complete — tokens before: {evt.CompactionTokensBefore}, after: {evt.CompactionTokensAfter}";
		_warnings.Add(message);
		_reporter.ReportSessionInfo("compaction", $"Context compaction complete — tokens before: {evt.CompactionTokensBefore}, after: {evt.CompactionTokensAfter}");
	}

	private void HandleSubagentSelected(AgentEvent evt)
	{
		_reporter.ReportSubagentSelected(
			_stepName,
			evt.SubagentName ?? "unknown",
			evt.SubagentDisplayName,
			evt.SubagentTools);
	}

	private void HandleSubagentStarted(AgentEvent evt)
	{
		_reporter.ReportSubagentStarted(
			_stepName,
			evt.ToolCallId,
			evt.SubagentName ?? "unknown",
			evt.SubagentDisplayName,
			evt.SubagentDescription);
	}

	private void HandleSubagentCompleted(AgentEvent evt)
	{
		_reporter.ReportSubagentCompleted(
			_stepName,
			evt.ToolCallId,
			evt.SubagentName ?? "unknown",
			evt.SubagentDisplayName);
	}

	private void HandleSubagentFailed(AgentEvent evt)
	{
		_reporter.ReportSubagentFailed(
			_stepName,
			evt.ToolCallId,
			evt.SubagentName ?? "unknown",
			evt.SubagentDisplayName,
			evt.ErrorMessage);
	}

	private void HandleSubagentDeselected()
	{
		_reporter.ReportSubagentDeselected(_stepName);
	}

	private void FinalizeCurrentResponse()
	{
		if (_currentResponseBuilder.Length > 0)
		{
			var segment = _currentResponseBuilder.ToString();
			_responseSegments.Add(segment);
			_conversationHistory.Add(new ConversationMessage
			{
				Role = "assistant",
				Content = segment,
				Timestamp = DateTimeOffset.UtcNow,
			});
			_currentResponseBuilder.Clear();
		}
	}

	/// <summary>
	/// Gets the MCP server statuses collected at runtime from the SDK.
	/// </summary>
	public IReadOnlyList<McpServerStatusInfo> McpServerStatuses => _mcpServerStatuses;

	/// <summary>
	/// Returns the names of MCP servers that failed to connect or load tools.
	/// Used by PromptExecutor to fail steps early when required MCP servers are unavailable.
	/// </summary>
	public IReadOnlyList<string> GetFailedMcpServers()
	{
		return _mcpServerStatuses
			.Where(s => string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase))
			.Select(s => s.Name)
			.ToList();
	}

	/// <summary>
	/// Builds a StepExecutionTrace from the collected data.
	/// </summary>
	public StepExecutionTrace BuildTrace(
		string? systemPrompt,
		string? userPromptRaw,
		string? userPromptProcessed = null,
		string? finalResponse = null,
		string? outputHandlerResult = null,
		List<string>? mcpServers = null)
	{
		// Add final response to conversation history if available
		var history = new List<ConversationMessage>(_conversationHistory);
		if (systemPrompt is not null)
			history.Insert(0, new ConversationMessage { Role = "system", Content = systemPrompt, Timestamp = DateTimeOffset.UtcNow });
		if (userPromptProcessed is not null)
			history.Insert(systemPrompt is not null ? 1 : 0, new ConversationMessage { Role = "user", Content = userPromptProcessed, Timestamp = DateTimeOffset.UtcNow });

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
			McpServers = BuildMcpServerList(mcpServers),
			Warnings = _warnings.ToList(),
			ConversationHistory = history,
			AuditLog = _auditLog.ToList(),
		};
	}

	/// <summary>
	/// Builds a partial trace (typically used when an error occurs).
	/// </summary>
	public StepExecutionTrace BuildPartialTrace(string? systemPrompt, string? userPromptRaw, List<string>? mcpServers = null)
	{
		return new StepExecutionTrace
		{
			SystemPrompt = systemPrompt,
			UserPromptRaw = userPromptRaw,
			Reasoning = Reasoning,
			ToolCalls = _toolCalls,
			ResponseSegments = _responseSegments.ToList(),
			McpServers = BuildMcpServerList(mcpServers),
			Warnings = _warnings.ToList(),
			ConversationHistory = new List<ConversationMessage>(_conversationHistory),
			AuditLog = _auditLog.ToList(),
		};
	}

	/// <summary>
	/// Merges MCP server config descriptions with runtime statuses.
	/// If we have runtime statuses, use them (more informative); otherwise, fall back to config descriptions.
	/// </summary>
	private List<string> BuildMcpServerList(List<string>? configDescriptions)
	{
		if (_mcpServerStatuses.Count > 0)
		{
			return _mcpServerStatuses.Select(s =>
			{
				var err = s.Error is not null ? $" — {s.Error}" : "";
				var source = s.Source is not null ? $", source: {s.Source}" : "";
				return $"{s.Name} (status: {s.Status}{source}{err})";
			}).ToList();
		}

		return configDescriptions ?? [];
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
