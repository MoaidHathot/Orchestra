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

	/// <summary>
	/// The most recent SDK-supplied status payload (transport-level connection state).
	/// Replaced on every <c>SessionMcpServersLoadedEvent</c>. Combined with
	/// <see cref="_externalToolCounts"/> in <see cref="RecomputeMcpServerStatuses"/> to
	/// produce the public <see cref="McpServerStatuses"/> list.
	/// </summary>
	private List<McpServerStatusInfo> _sdkMcpServerStatuses = [];

	/// <summary>
	/// Tool counts probed by Orchestra's own MCP proxy (see
	/// <see cref="IMcpResolver.GetGlobalMcpToolCountsAsync"/>). Keyed by MCP name,
	/// case-insensitive. Values are <see langword="null"/> when the probe could not
	/// determine a count (and must be treated as "unknown", not "zero"). Persists
	/// across SDK status refreshes so re-fired <c>McpServersLoaded</c> events
	/// continue to carry the latest probe result.
	/// </summary>
	private readonly Dictionary<string, int?> _externalToolCounts = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The merged view (SDK statuses + probed tool counts). Refreshed by
	/// <see cref="RecomputeMcpServerStatuses"/> whenever either source changes.
	/// </summary>
	private readonly List<McpServerStatusInfo> _mcpServerStatuses = [];
	private readonly List<ConversationMessage> _conversationHistory = [];
	private readonly List<AuditLogEntry> _auditLog = [];

	public AgentEventProcessor(IOrchestrationReporter reporter, string stepName)
	{
		_reporter = reporter;
		_stepName = stepName;
	}

	/// <summary>
	/// The agent provider the step was configured to run on (step <c>provider</c> →
	/// orchestration <c>defaultProvider</c> → host default). Set by the executor before
	/// building traces so every trace carries the configured-vs-actual provider pair.
	/// </summary>
	public string? ConfiguredProvider { get; set; }

	/// <summary>
	/// The agent provider that actually ran the step (resolved builder capability key).
	/// Set by the executor once the provider is resolved.
	/// </summary>
	public string? ActualProvider { get; set; }

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
		try
		{
			await foreach (var evt in events.WithCancellation(cancellationToken))
			{
				ProcessEvent(evt);
			}
		}
		finally
		{
			// Preserve any partial response emitted before cancellation or failure.
			FinalizeCurrentResponse();
		}
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

			case AgentEventType.HookStart:
				HandleHookStart(evt);
				break;

			case AgentEventType.HookEnd:
				HandleHookEnd(evt);
				break;

		case AgentEventType.TurnStart:
			HandleTurnStart(evt);
			break;

		case AgentEventType.TurnEnd:
			HandleTurnEnd(evt);
			break;

			case AgentEventType.SessionUsageInfo:
				HandleSessionUsageInfo(evt);
				break;

			case AgentEventType.AutoModeSwitchRequested:
				HandleAutoModeSwitchRequested(evt);
				break;

			case AgentEventType.AutoModeSwitchCompleted:
				HandleAutoModeSwitchCompleted(evt);
				break;

			case AgentEventType.SystemNotification:
				HandleSystemNotification(evt);
				break;

			case AgentEventType.QuotaSnapshot:
				HandleQuotaSnapshot(evt);
				break;

			case AgentEventType.PermissionRequested:
				HandlePermissionRequested(evt);
				break;

			case AgentEventType.PermissionCompleted:
				HandlePermissionCompleted(evt);
				break;
		}
	}

	private void HandleMessageDelta(AgentEvent evt)
	{
		_reporter.ReportContentDelta(_stepName, evt.Content ?? string.Empty, evt.Actor);
		_currentResponseBuilder.Append(evt.Content ?? string.Empty);
	}

	private void HandleReasoningDelta(AgentEvent evt)
	{
		_reporter.ReportReasoningDelta(_stepName, evt.Content ?? string.Empty, evt.Actor);
		_reasoningBuilder.Append(evt.Content ?? string.Empty);
	}

	private void HandleToolExecutionStart(AgentEvent evt)
	{
		_reporter.ReportToolExecutionStarted(_stepName, evt.ToolName ?? "unknown", evt.ToolArguments, evt.McpServerName, evt.Actor);

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
				DateTimeOffset.UtcNow,
				evt.Actor
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
				ActorAgentName = evt.Actor.AgentName,
				ActorAgentDisplayName = evt.Actor.AgentDisplayName,
				ActorToolCallId = evt.Actor.ToolCallId,
				ActorDepth = evt.Actor.Depth,
			});
		}
	}

	private void HandleToolExecutionComplete(AgentEvent evt)
	{
		_reporter.ReportToolExecutionCompleted(_stepName, evt.ToolName ?? "unknown", evt.ToolSuccess ?? false, evt.ToolResult, evt.ToolError, evt.Actor);

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
				ActorAgentName = pending.Actor.AgentName,
				ActorAgentDisplayName = pending.Actor.AgentDisplayName,
				ActorToolCallId = pending.Actor.ToolCallId,
				ActorDepth = pending.Actor.Depth,
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
				ActorAgentName = evt.Actor.AgentName,
				ActorAgentDisplayName = evt.Actor.AgentDisplayName,
				ActorToolCallId = evt.Actor.ToolCallId,
				ActorDepth = evt.Actor.Depth,
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
		_sdkMcpServerStatuses = statuses.ToList();
		RecomputeMcpServerStatuses();

		// Report the enriched (probe-aware) statuses so subscribers see the same
		// view as the trace and the public McpServerStatuses property.
		_reporter.ReportMcpServersLoaded(_mcpServerStatuses);

		// Auto-generate warnings for any failed servers OR for "Connected but zero tools"
		// servers (the latter is the proxy-deferred-auth failure mode that the SDK
		// alone cannot detect — `Connected` only means transport handshake succeeded).
		foreach (var server in _mcpServerStatuses)
		{
			if (string.Equals(server.Status, "Failed", StringComparison.OrdinalIgnoreCase))
			{
				var errorDetail = server.Error is not null ? $": {server.Error}" : "";
				var warningMessage = $"MCP server '{server.Name}' failed to connect{errorDetail}";
				_warnings.Add($"[mcp_server_failed] {warningMessage}");
			}
			else if (server.ToolCount == 0)
			{
				var warningMessage =
					$"MCP server '{server.Name}' connected (status: {server.Status}) but exposed 0 tools. "
					+ "The upstream backend likely isn't ready yet — check authentication or "
					+ "deferred-connection settings on the proxy/backend.";
				_warnings.Add($"[mcp_server_no_tools] {warningMessage}");
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

	private void HandleHookStart(AgentEvent evt)
	{
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.HookStart,
			HookType = evt.HookType,
			HookInvocationId = evt.HookInvocationId,
			Timestamp = DateTimeOffset.UtcNow,
		});
	}

	private void HandleHookEnd(AgentEvent evt)
	{
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.HookEnd,
			HookType = evt.HookType,
			HookInvocationId = evt.HookInvocationId,
			HookSuccess = evt.HookSuccess,
			Timestamp = DateTimeOffset.UtcNow,
		});

		if (evt.HookSuccess == false)
		{
			var warningMessage = $"[hook_failed] Hook '{evt.HookType}' (invocation: {evt.HookInvocationId}) failed: {evt.ErrorMessage}";
			_warnings.Add(warningMessage);
			_reporter.ReportSessionWarning("hook_failed", $"Hook '{evt.HookType}' failed: {evt.ErrorMessage}");
		}
	}

	private void HandleTurnStart(AgentEvent evt)
	{
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.TurnStart,
			TurnId = evt.TurnId,
			Timestamp = DateTimeOffset.UtcNow,
		});
	}

	private void HandleTurnEnd(AgentEvent evt)
	{
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.TurnEnd,
			TurnId = evt.TurnId,
			Timestamp = DateTimeOffset.UtcNow,
		});
	}

	private void HandleSessionUsageInfo(AgentEvent evt)
	{
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.SessionUsageInfo,
			TokenLimit = evt.TokenLimit,
			CurrentTokens = evt.CurrentTokens,
			Timestamp = DateTimeOffset.UtcNow,
		});
	}

	private void HandleAutoModeSwitchRequested(AgentEvent evt)
	{
		// Auto-mode switches are not "errors" from the orchestration's POV — they are
		// resilience signals (the SDK transparently fell back to a different model).
		// Surface as INFO so the Portal renders them in the warnings panel without
		// failing the step.
		_reporter.ReportAutoModeSwitchRequested(
			_stepName,
			evt.AutoModeRequestId ?? string.Empty,
			evt.AutoModeErrorCode);
		_reporter.ReportSessionInfo(
			"auto_mode_switch_requested",
			$"Auto-mode switch requested (errorCode={evt.AutoModeErrorCode ?? "n/a"}, requestId={evt.AutoModeRequestId ?? "n/a"})");
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.AutoModeSwitchRequested,
			AutoModeRequestId = evt.AutoModeRequestId,
			AutoModeErrorCode = evt.AutoModeErrorCode,
			Timestamp = DateTimeOffset.UtcNow,
		});
	}

	private void HandleAutoModeSwitchCompleted(AgentEvent evt)
	{
		_reporter.ReportAutoModeSwitchCompleted(
			_stepName,
			evt.AutoModeRequestId ?? string.Empty,
			evt.AutoModeResponse);
		_reporter.ReportSessionInfo(
			"auto_mode_switch_completed",
			$"Auto-mode switch completed (requestId={evt.AutoModeRequestId ?? "n/a"}, response={evt.AutoModeResponse ?? "n/a"})");
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.AutoModeSwitchCompleted,
			AutoModeRequestId = evt.AutoModeRequestId,
			AutoModeResponse = evt.AutoModeResponse,
			Timestamp = DateTimeOffset.UtcNow,
		});
	}

	private void HandleSystemNotification(AgentEvent evt)
	{
		_reporter.ReportSystemNotification(
			_stepName,
			evt.NotificationKind ?? "unknown",
			evt.NotificationMessage);
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.SystemNotification,
			NotificationKind = evt.NotificationKind,
			NotificationMessage = evt.NotificationMessage,
			Timestamp = DateTimeOffset.UtcNow,
		});
	}

	private void HandleQuotaSnapshot(AgentEvent evt)
	{
		if (evt.QuotaSnapshots is null || evt.QuotaSnapshots.Count == 0)
			return;

		_reporter.ReportQuotaSnapshot(_stepName, evt.QuotaSnapshots);
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.QuotaSnapshot,
			Timestamp = DateTimeOffset.UtcNow,
		});
	}

	// SDK 1.0.0 per-call permission gate audit. Orchestra approves every request via
	// PermissionHandler.ApproveAll, so the trace value is forensic: it records the
	// sequence of side-effectful actions (read X, run Y, write Z) the agent was permitted
	// to perform. Pairing PermissionRequested with PermissionCompleted via
	// PermissionRequestId lets downstream tooling reconstruct the gate's decision per
	// action; PermissionToolCallId stitches the gate back to the originating tool call.
	private void HandlePermissionRequested(AgentEvent evt)
	{
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.PermissionRequested,
			Timestamp = DateTimeOffset.UtcNow,
			PermissionRequestId = evt.PermissionRequestId,
			PermissionKind = evt.PermissionKind,
			PermissionTarget = evt.PermissionTarget,
			PermissionToolCallId = evt.PermissionToolCallId,
		});
	}

	private void HandlePermissionCompleted(AgentEvent evt)
	{
		AddAuditLogEntry(new AuditLogEntry
		{
			Sequence = 0,
			EventType = AuditEventType.PermissionCompleted,
			Timestamp = DateTimeOffset.UtcNow,
			PermissionRequestId = evt.PermissionRequestId,
			PermissionDecision = evt.PermissionDecision,
			PermissionDecisionReason = evt.PermissionDecisionReason,
			PermissionToolCallId = evt.PermissionToolCallId,
		});
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
	/// Returns the names of MCP servers that are reachable (any non-failed status) but
	/// reported <c>0</c> tools when Orchestra probed them via
	/// <see cref="IMcpResolver.GetGlobalMcpToolCountsAsync"/>. This is the
	/// "Connected but no tools" failure mode that the Copilot SDK's connection-level
	/// status cannot surface on its own.
	/// <para>
	/// MCPs whose tool count is <see langword="null"/> (unknown — probe failed or
	/// the resolver doesn't manage that name) are NOT included; callers must not
	/// conflate "unknown" with "zero".
	/// </para>
	/// </summary>
	public IReadOnlyList<string> GetMcpServersWithoutTools()
	{
		return _mcpServerStatuses
			.Where(s =>
				s.ToolCount == 0
				&& !string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase))
			.Select(s => s.Name)
			.ToList();
	}

	/// <summary>
	/// Applies tool counts probed by an external source (Orchestra's
	/// <see cref="IMcpResolver"/>) to the in-memory MCP status view. Merges into
	/// any existing counts (non-null wins; later calls with a <see langword="null"/>
	/// value do not clobber a previously-known count). Recomputes the public
	/// <see cref="McpServerStatuses"/> snapshot so it reflects both SDK-reported
	/// connection state and locally-probed tool counts.
	/// <para>
	/// Idempotent — safe to call multiple times. Names are matched case-insensitively.
	/// </para>
	/// </summary>
	public void ApplyMcpToolCounts(IReadOnlyDictionary<string, int?> counts)
	{
		ArgumentNullException.ThrowIfNull(counts);

		foreach (var (name, count) in counts)
		{
			if (string.IsNullOrWhiteSpace(name))
				continue;

			// Don't overwrite a known count with null. A later definite probe
			// SHOULD overwrite a stale one, however, so non-null wins unconditionally.
			if (count is null && _externalToolCounts.ContainsKey(name))
				continue;

			_externalToolCounts[name] = count;
		}

		RecomputeMcpServerStatuses();
	}

	/// <summary>
	/// Rebuilds <see cref="_mcpServerStatuses"/> from the SDK-supplied
	/// <see cref="_sdkMcpServerStatuses"/> overlaid with the locally-probed
	/// <see cref="_externalToolCounts"/>. The merge rule is: SDK status fields win;
	/// the tool count comes from the local probe when present, falling back to whatever
	/// the SDK happened to report (currently always <see langword="null"/>, but
	/// future-proofs against an SDK that does ship a tool count one day).
	/// <para>
	/// Names that only appear in the probe (no matching SDK status) are appended with
	/// an <c>"Unknown"</c> status so the trace still records what Orchestra saw — this
	/// matters when the SDK never fires <c>SessionMcpServersLoadedEvent</c> for an MCP
	/// the step explicitly requested.
	/// </para>
	/// </summary>
	private void RecomputeMcpServerStatuses()
	{
		_mcpServerStatuses.Clear();

		var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var sdk in _sdkMcpServerStatuses)
		{
			seenNames.Add(sdk.Name);
			var toolCount = _externalToolCounts.TryGetValue(sdk.Name, out var probed) && probed is not null
				? probed
				: sdk.ToolCount;
			_mcpServerStatuses.Add(sdk with { ToolCount = toolCount });
		}

		foreach (var (name, count) in _externalToolCounts)
		{
			if (seenNames.Contains(name))
				continue;

			// Probe-only entries with a null (unknown) count add no diagnostic value
			// without a matching SDK status — skip them. They remain in
			// `_externalToolCounts` so a later definite probe overwrites correctly.
			if (count is null)
				continue;

			// Probe-only entry — surface it so the trace records what Orchestra
			// observed even when the SDK never reported a status for this MCP.
			_mcpServerStatuses.Add(new McpServerStatusInfo(
				Name: name,
				Status: "Unknown",
				ToolCount: count));
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
			ConfiguredProvider = ConfiguredProvider,
			ActualProvider = ActualProvider,
			SystemPrompt = systemPrompt,
			UserPromptRaw = userPromptRaw,
			UserPromptProcessed = userPromptProcessed,
			Reasoning = Reasoning,
			ToolCalls = BuildToolCallList(includePending: false),
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
	public StepExecutionTrace BuildPartialTrace(
		string? systemPrompt,
		string? userPromptRaw,
		List<string>? mcpServers = null,
		string? userPromptProcessed = null)
	{
		var history = new List<ConversationMessage>(_conversationHistory);
		if (systemPrompt is not null)
			history.Insert(0, new ConversationMessage { Role = "system", Content = systemPrompt, Timestamp = DateTimeOffset.UtcNow });
		if (userPromptProcessed is not null)
			history.Insert(systemPrompt is not null ? 1 : 0, new ConversationMessage { Role = "user", Content = userPromptProcessed, Timestamp = DateTimeOffset.UtcNow });

		return new StepExecutionTrace
		{
			ConfiguredProvider = ConfiguredProvider,
			ActualProvider = ActualProvider,
			SystemPrompt = systemPrompt,
			UserPromptRaw = userPromptRaw,
			UserPromptProcessed = userPromptProcessed,
			Reasoning = Reasoning,
			ToolCalls = BuildToolCallList(includePending: true),
			ResponseSegments = _responseSegments.ToList(),
			McpServers = BuildMcpServerList(mcpServers),
			Warnings = _warnings.ToList(),
			ConversationHistory = history,
			AuditLog = _auditLog.ToList(),
		};
	}

	/// <summary>
	/// Merges MCP server config descriptions with runtime statuses.
	/// If we have runtime statuses, use them (more informative); otherwise, fall back to config descriptions.
	/// <para>
	/// Each status string includes the tool count when Orchestra has probed it
	/// (e.g. <c>"calendar (status: Connected, tools: 0)"</c>) so the trace makes
	/// the "Connected but no tools" failure mode immediately visible without
	/// requiring a reader to cross-reference the Warnings section.
	/// </para>
	/// </summary>
	private List<string> BuildMcpServerList(List<string>? configDescriptions)
	{
		if (_mcpServerStatuses.Count > 0)
		{
			return _mcpServerStatuses.Select(s =>
			{
				var err = s.Error is not null ? $" — {s.Error}" : "";
				var source = s.Source is not null ? $", source: {s.Source}" : "";
				var tools = s.ToolCount is { } count ? $", tools: {count}" : "";
				return $"{s.Name} (status: {s.Status}{source}{tools}{err})";
			}).ToList();
		}

		return configDescriptions ?? [];
	}

	private List<ToolCallRecord> BuildToolCallList(bool includePending)
	{
		var records = _toolCalls.ToList();

		if (!includePending || _pendingToolCalls.Count == 0)
		{
			return records;
		}

		foreach (var (callId, pending) in _pendingToolCalls)
		{
			records.Add(new ToolCallRecord
			{
				CallId = callId,
				ToolName = pending.ToolName,
				Arguments = pending.Arguments,
				McpServer = pending.McpServer,
				StartedAt = pending.StartedAt,
				ActorAgentName = pending.Actor.AgentName,
				ActorAgentDisplayName = pending.Actor.AgentDisplayName,
				ActorToolCallId = pending.Actor.ToolCallId,
				ActorDepth = pending.Actor.Depth,
			});
		}

		return records;
	}

	/// <summary>
	/// Represents a pending tool call awaiting completion.
	/// </summary>
	private sealed record PendingToolCall(
		string ToolName,
		string? Arguments,
		string? McpServer,
		DateTimeOffset StartedAt,
		ActorContext Actor);
}
