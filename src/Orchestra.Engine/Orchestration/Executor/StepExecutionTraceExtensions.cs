namespace Orchestra.Engine;

internal static class StepExecutionTraceExtensions
{
	public static StepExecutionTrace WithContext(
		this StepExecutionTrace trace,
		OrchestrationExecutionContext context,
		OrchestrationStep step,
		string[]? currentStepFiles = null)
	{
		return new StepExecutionTrace
		{
			Parameters = new Dictionary<string, string>(context.Parameters),
			DependencyOutputs = context.GetDependencyOutputs(step.DependsOn),
			RawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn),
			AccessibleStepData = BuildAccessibleStepData(context, step.Name, currentStepFiles ?? []),
			Command = trace.Command,
			CommandArguments = [.. trace.CommandArguments],
			Shell = trace.Shell,
			ScriptSource = trace.ScriptSource,
			WorkingDirectory = trace.WorkingDirectory,
			Environment = new Dictionary<string, string>(trace.Environment),
			Stdin = trace.Stdin,
			SystemPrompt = trace.SystemPrompt,
			UserPromptRaw = trace.UserPromptRaw,
			UserPromptProcessed = trace.UserPromptProcessed,
			Reasoning = trace.Reasoning,
			ToolCalls = [.. trace.ToolCalls],
			ResponseSegments = [.. trace.ResponseSegments],
			FinalResponse = trace.FinalResponse,
			OutputHandlerResult = trace.OutputHandlerResult,
			McpServers = [.. trace.McpServers],
			Warnings = [.. trace.Warnings],
			ConversationHistory = [.. trace.ConversationHistory],
			AuditLog = [.. trace.AuditLog],
		};
	}

	private static Dictionary<string, StepTraceStepData> BuildAccessibleStepData(
		OrchestrationExecutionContext context,
		string currentStepName,
		string[] currentStepFiles)
	{
		var accessible = new Dictionary<string, StepTraceStepData>(StringComparer.OrdinalIgnoreCase);
		foreach (var (stepName, result) in context.Results.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
		{
			accessible[stepName] = new StepTraceStepData
			{
				Status = result.Status.ToString(),
				Output = result.Content,
				RawOutput = result.RawContent ?? result.Content,
				Files = context.TempFileStore?.GetFilesForStep(stepName) ?? [],
			};
		}

		if (currentStepFiles.Length > 0 && !accessible.ContainsKey(currentStepName))
		{
			accessible[currentStepName] = new StepTraceStepData
			{
				Files = currentStepFiles,
			};
		}

		return accessible;
	}
}
