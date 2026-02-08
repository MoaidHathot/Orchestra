namespace Orchestra.Engine;

public interface IOrchestrationReporter
{
	void ReportSessionStarted(string requestedModel, string? selectedModel);
	void ReportModelChange(string? previousModel, string newModel);
	void ReportUsage(string stepName, string model, AgentUsage usage);
	void ReportToolExecutionStarted(string stepName);
	void ReportToolExecutionCompleted(string stepName);
	void ReportStepError(string stepName, string errorMessage);
	void ReportStepCompleted(string stepName, AgentResult result);
	void ReportModelMismatch(ModelMismatchInfo mismatch);
	void ReportStepOutput(string stepName, string content);
}
