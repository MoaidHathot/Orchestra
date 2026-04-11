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

	// Run context
	void ReportRunContext(RunContext context);
}
