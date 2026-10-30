namespace Orchestra.Engine;

internal static class StepExecutionTraceExtensions
{
	public static StepExecutionTrace WithContext(
		this StepExecutionTrace trace,
		OrchestrationExecutionContext context,
		OrchestrationStep step,
		string[]? currentStepFiles = null)
		=> Copy(trace, new Dictionary<string, string>(context.Parameters),
			context.GetDependencyOutputs(step.DependsOn), context.GetRawDependencyOutputs(step.DependsOn),
			BuildAccessibleStepData(context, step.Name, currentStepFiles ?? []), trace.Plans);

	public static StepExecutionTrace WithPlans(this StepExecutionTrace trace, IReadOnlyList<AgentPlanRevision> plans)
		=> Copy(trace, trace.Parameters, trace.DependencyOutputs, trace.RawDependencyOutputs, trace.AccessibleStepData, plans);

	private static StepExecutionTrace Copy(StepExecutionTrace trace,
		IReadOnlyDictionary<string, string> parameters, IReadOnlyDictionary<string, string> dependencyOutputs,
		IReadOnlyDictionary<string, string> rawDependencyOutputs, IReadOnlyDictionary<string, StepTraceStepData> accessibleStepData,
		IReadOnlyList<AgentPlanRevision> plans)
	{
		return new StepExecutionTrace
		{
			Plans = plans,
			ConfiguredProvider = trace.ConfiguredProvider,
			ActualProvider = trace.ActualProvider,
			Parameters = parameters,
			DependencyOutputs = dependencyOutputs,
			RawDependencyOutputs = rawDependencyOutputs,
			AccessibleStepData = accessibleStepData,
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
		// LINQ's ICollection fast path reads Count and calls CopyTo separately. On the
		// live ConcurrentDictionary, another completed step can change the count between
		// those calls. Its instance ToArray() takes the required atomic snapshot first.
		foreach (var (stepName, result) in context.SnapshotResults().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
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
