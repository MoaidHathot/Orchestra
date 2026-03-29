namespace Orchestra.Engine;

public class NullOrchestrationReporter : IOrchestrationReporter
{
	public static readonly NullOrchestrationReporter Instance = new();

	public void ReportSessionStarted(string requestedModel, string? selectedModel) { }
	public void ReportModelChange(string? previousModel, string newModel) { }
	public void ReportUsage(string stepName, string model, AgentUsage usage) { }
	public void ReportContentDelta(string stepName, string chunk) { }
	public void ReportReasoningDelta(string stepName, string chunk) { }
	public void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer) { }
	public void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error) { }
	public void ReportStepError(string stepName, string errorMessage) { }
	public void ReportStepCancelled(string stepName) { }
	public void ReportStepCompleted(string stepName, AgentResult result) { }
	public void ReportStepTrace(string stepName, StepExecutionTrace trace) { }
	public void ReportModelMismatch(ModelMismatchInfo mismatch) { }
	public void ReportStepOutput(string stepName, string content) { }
	public void ReportStepStarted(string stepName) { }
	public void ReportStepSkipped(string stepName, string reason) { }
	public void ReportStepRetry(string stepName, int attempt, int maxRetries, string error, TimeSpan delay) { }
	public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations) { }
	public void ReportCheckpointSaved(string runId, string stepName, int completedSteps, int totalSteps) { }

	// Session diagnostics
	public void ReportSessionWarning(string warningType, string message) { }
	public void ReportSessionInfo(string infoType, string message) { }

	// Subagent events
	public void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools) { }
	public void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description) { }
	public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName) { }
	public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error) { }
	public void ReportSubagentDeselected(string stepName) { }
}
