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
	void ReportStepCompleted(string stepName, AgentResult result);
	void ReportModelMismatch(ModelMismatchInfo mismatch);
	void ReportStepOutput(string stepName, string content);
	void ReportStepStarted(string stepName);
	void ReportStepSkipped(string stepName, string reason);
}
