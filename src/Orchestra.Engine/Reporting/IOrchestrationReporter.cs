namespace Orchestra.Engine;

public interface IOrchestrationReporter
{
	void ReportSessionStarted(string requestedModel, string? selectedModel);
	void ReportModelChange(string? previousModel, string newModel);
	void ReportUsage(string stepName, string model, AgentUsage usage);
	void ReportContentDelta(string stepName, string chunk);
	void ReportReasoningDelta(string stepName, string chunk);
	void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer);
	void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error);
	void ReportStepError(string stepName, string errorMessage);

	/// <summary>
	/// Reports a step error with optional structured details from the underlying agent
	/// session (Copilot SDK <c>SessionErrorData</c>: ErrorType / StatusCode /
	/// ProviderCallId / Url / Stack). The default implementation drops the details and
	/// forwards to the legacy <see cref="ReportStepError(string,string)"/> for
	/// back-compat with reporters that don't surface structured error metadata.
	/// </summary>
	void ReportStepError(string stepName, string errorMessage, AgentSessionErrorDetails? errorDetails)
		=> ReportStepError(stepName, errorMessage);
	void ReportStepCancelled(string stepName);
	void ReportStepCompleted(string stepName, AgentResult result, OrchestrationStepType stepType);
	void ReportStepTrace(string stepName, StepExecutionTrace trace);
	void ReportModelMismatch(ModelMismatchInfo mismatch);
	void ReportStepOutput(string stepName, string content);
	void ReportStepStarted(string stepName);
	void ReportStepSkipped(string stepName, string reason);
	void ReportStepRetry(string stepName, int attempt, int maxRetries, string error, TimeSpan delay);
	void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations);
	void ReportCheckpointSaved(string runId, string stepName, int completedSteps, int totalSteps);
	void ReportSavedFile(string stepName, string filePath);

	// Session diagnostics
	void ReportSessionWarning(string warningType, string message);
	void ReportSessionInfo(string infoType, string message);

	// MCP server lifecycle
	void ReportMcpServersLoaded(IReadOnlyList<McpServerStatusInfo> servers);
	void ReportMcpServerStatusChanged(string serverName, string status);

	// Subagent events
	void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools);
	void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description);
	void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName);
	void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error);
	void ReportSubagentDeselected(string stepName);

	// Step status indication (step set its status but is still in progress)
	void ReportStepStatusSet(string stepName, string status, string reason);

	// Run context
	void ReportRunContext(RunContext context);

	// Hook lifecycle
	void ReportHookExecuted(HookExecutionRecord hookExecution);

	// Audit log
	void ReportAuditLogEntry(string stepName, AuditLogEntry entry);

	// ── Auto-mode switch telemetry (SDK 0.3.0). Default no-op for back-compat. ──

	/// <summary>
	/// Reports that the SDK requested a model switch because the current model hit a rate-limit
	/// or transient failure. <paramref name="errorCode"/> is the SDK-supplied trigger reason.
	/// </summary>
	void ReportAutoModeSwitchRequested(string stepName, string requestId, string? errorCode) { }

	/// <summary>
	/// Reports that an auto-mode switch completed and the next model is now active.
	/// <paramref name="response"/> is typically the new model name or a status string.
	/// </summary>
	void ReportAutoModeSwitchCompleted(string stepName, string requestId, string? response) { }

	// ── System notifications (SDK 0.3.0 typed discriminator). ──

	/// <summary>
	/// Reports a CLI-level system notification (agent idle/completed, shell completed,
	/// new inbox message, etc.). <paramref name="kind"/> is the SDK discriminator.
	/// </summary>
	void ReportSystemNotification(string stepName, string kind, string? message) { }

	// ── Quota snapshots (SDK 0.3.0 — alongside AssistantUsageEvent). ──

	/// <summary>
	/// Reports per-bucket quota / entitlement snapshots so the Portal can show plan
	/// utilization in real time. Default no-op for reporters that don't render telemetry.
	/// </summary>
	void ReportQuotaSnapshot(string stepName, IReadOnlyDictionary<string, AgentQuotaSnapshot> snapshots) { }

	// ── Actor-aware overloads (default-implemented for backward compatibility) ──
	//
	// These let consumers (CopilotSessionHandler / AgentEventProcessor) attribute
	// streaming events to either the main agent or a specific sub-agent invocation.
	// Reporters that care about the actor (e.g. SseReporter for the Portal) override
	// these; reporters that don't fall through to the legacy actor-less overloads.

	/// <summary>
	/// Reports a content delta produced by <paramref name="actor"/>. Default implementation
	/// ignores the actor and forwards to the legacy <see cref="ReportContentDelta(string,string)"/>.
	/// </summary>
	void ReportContentDelta(string stepName, string chunk, ActorContext actor)
		=> ReportContentDelta(stepName, chunk);

	/// <summary>
	/// Reports a reasoning delta produced by <paramref name="actor"/>. Default implementation
	/// ignores the actor and forwards to the legacy <see cref="ReportReasoningDelta(string,string)"/>.
	/// </summary>
	void ReportReasoningDelta(string stepName, string chunk, ActorContext actor)
		=> ReportReasoningDelta(stepName, chunk);

	/// <summary>
	/// Reports a tool-start event produced by <paramref name="actor"/>. Default implementation
	/// ignores the actor and forwards to the legacy overload.
	/// </summary>
	void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer, ActorContext actor)
		=> ReportToolExecutionStarted(stepName, toolName, arguments, mcpServer);

	/// <summary>
	/// Reports a tool-complete event produced by <paramref name="actor"/>. Default implementation
	/// ignores the actor and forwards to the legacy overload.
	/// </summary>
	void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error, ActorContext actor)
		=> ReportToolExecutionCompleted(stepName, toolName, success, result, error);

	// ── Human-in-the-loop ──

	/// <summary>
	/// Reports that a step has begun awaiting human input. Fired by the engine when an
	/// Approval step or an <c>orchestra_request_user_input</c> tool call begins waiting.
	/// Default implementation is a no-op for back-compat with reporters that don't
	/// surface HITL events (e.g. <see cref="NullOrchestrationReporter"/>).
	/// </summary>
	void ReportAwaitingInput(PendingInputRecord record) { }

	/// <summary>
	/// Reports that a previously-awaiting wait received the user's response.
	/// </summary>
	void ReportInputReceived(string orchestrationName, string runId, string stepName, UserInputResponse response) { }

	/// <summary>
	/// Reports that a HITL wait expired without a response. <paramref name="onTimeout"/>
	/// is the configured behavior that's about to take effect (fail / defaultResponse / cancel).
	/// </summary>
	void ReportInputTimeout(string orchestrationName, string runId, string stepName, ApprovalTimeoutBehavior onTimeout) { }

	// ── Mid-run step record publication ──

	/// <summary>
	/// Publishes a fully-built <see cref="StepRunRecord"/> as soon as the engine assigns it
	/// into the run's accumulator dictionaries. Lets host-side surfaces (e.g. data-plane
	/// MCP tools, REST endpoints) serve completed-step content BEFORE the run finalizes its
	/// <c>run.json</c>. Default implementation is a no-op for back-compat with reporters
	/// that don't need mid-run drill-in.
	/// </summary>
	/// <param name="key">Canonical key used in <see cref="OrchestrationRunRecord.AllStepRecords"/>:
	/// the step's name for non-loop steps, or <c>stepName:iteration-N</c> for loop iterations.
	/// Re-publishing the same key overwrites the prior entry (loops update the canonical
	/// record at <c>stepName</c> and add per-iteration records under <c>stepName:iteration-N</c>).</param>
	/// <param name="record">The fully-built step record. References the same instance the
	/// run will eventually persist, so reading via the reporter exposes identical data.</param>
	void PublishStepRecord(string key, StepRunRecord record) { }
}
