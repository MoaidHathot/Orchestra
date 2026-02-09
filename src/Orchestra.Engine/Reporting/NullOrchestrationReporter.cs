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
	public void ReportStepCompleted(string stepName, AgentResult result) { }
	public void ReportModelMismatch(ModelMismatchInfo mismatch) { }
	public void ReportStepOutput(string stepName, string content) { }
	public void ReportStepStarted(string stepName) { }
	public void ReportStepSkipped(string stepName, string reason) { }
}
