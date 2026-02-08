namespace Orchestra.Engine;

public class NullOrchestrationReporter : IOrchestrationReporter
{
	public static readonly NullOrchestrationReporter Instance = new();

	public void ReportSessionStarted(string requestedModel, string? selectedModel) { }
	public void ReportModelChange(string? previousModel, string newModel) { }
	public void ReportUsage(string stepName, string model, AgentUsage usage) { }
	public void ReportToolExecutionStarted(string stepName) { }
	public void ReportToolExecutionCompleted(string stepName) { }
	public void ReportStepError(string stepName, string errorMessage) { }
	public void ReportStepCompleted(string stepName, AgentResult result) { }
	public void ReportModelMismatch(ModelMismatchInfo mismatch) { }
	public void ReportStepOutput(string stepName, string content) { }
}
