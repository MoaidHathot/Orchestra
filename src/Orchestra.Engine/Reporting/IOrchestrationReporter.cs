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

	// Audit log
	void ReportAuditLogEntry(string stepName, AuditLogEntry entry);

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
}
