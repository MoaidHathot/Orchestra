namespace Orchestra.Engine;

public class PromptExecutor : Executor<PromptOrchestrationStep>
{
	private readonly AgentBuilder _agentBuilder;
	private readonly IOrchestrationReporter _reporter;

	public PromptExecutor(AgentBuilder agentBuilder, IOrchestrationReporter reporter)
	{
		_agentBuilder = agentBuilder;
		_reporter = reporter;
	}

	public override async Task<ExecutionResult> ExecuteAsync(
		PromptOrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// Build the user prompt, incorporating dependency outputs and parameters
			var userPrompt = BuildUserPrompt(step, context);

			// Build and run the agent
			var agent = await _agentBuilder
				.WithModel(step.Model)
				.WithSystemPrompt(step.SystemPrompt)
				.WithMcp(step.Mcps)
				.WithReasoningLevel(step.ReasoningLevel)
				.WithSystemPromptMode(step.SystemPromptMode)
				.WithReporter(_reporter)
				.BuildAgentAsync(cancellationToken);

			var task = agent.SendAsync(userPrompt, cancellationToken);

		// Consume events — only report execution-relevant events, not content
		await foreach (var evt in task.WithCancellation(cancellationToken))
		{
			switch (evt.Type)
			{
				case AgentEventType.ToolExecutionStart:
					_reporter.ReportToolExecutionStarted(step.Name);
					break;
				case AgentEventType.ToolExecutionComplete:
					_reporter.ReportToolExecutionCompleted(step.Name);
					break;
				case AgentEventType.Error:
					_reporter.ReportStepError(step.Name, evt.ErrorMessage ?? "Unknown error");
					break;
			}
		}

			var result = await task.GetResultAsync();

			// Report model and usage metadata if available
			if (result.ActualModel is not null)
			{
				_reporter.ReportStepCompleted(step.Name, result);
			}
			if (result.Usage is not null && result.ActualModel is not null)
			{
				_reporter.ReportUsage(step.Name, result.ActualModel, result.Usage);
			}

			var content = result.Content;

			// Apply output handler if specified
			if (step.OutputHandlerPrompt is not null)
			{
				content = await RunHandlerAsync(step.OutputHandlerPrompt, content, step.Model, cancellationToken);
			}

			return ExecutionResult.Succeeded(content);
		}
		catch (Exception ex)
		{
			_reporter.ReportStepError(step.Name, ex.Message);
			return ExecutionResult.Failed(ex.Message);
		}
	}

	private static string BuildUserPrompt(PromptOrchestrationStep step, OrchestrationExecutionContext context)
	{
		var userPrompt = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);
		var dependencyOutputs = context.GetDependencyOutputs(step.DependsOn);

		if (string.IsNullOrEmpty(dependencyOutputs))
			return userPrompt;

		if (step.InputHandlerPrompt is not null)
		{
			return $"""
				{step.InputHandlerPrompt}

				---
				Previous step outputs:
				{dependencyOutputs}

				---
				Task:
				{userPrompt}
				""";
		}

		return $"""
			{userPrompt}

			---
			Context from previous steps:
			{dependencyOutputs}
			""";
	}

	private static string InjectParameters(string prompt, string[] parameterNames, Dictionary<string, string> parameters)
	{
		if (parameterNames.Length == 0 || parameters.Count == 0)
			return prompt;

		var result = prompt;
		foreach (var name in parameterNames)
		{
			if (parameters.TryGetValue(name, out var value))
			{
				result = result.Replace($"{{{{{name}}}}}", value);
			}
		}

		return result;
	}

	private async Task<string> RunHandlerAsync(
		string handlerPrompt,
		string content,
		string model,
		CancellationToken cancellationToken)
	{
		var systemPrompt = $"""
			You are a content transformer. Your ONLY job is to take the provided content and transform it according to the instructions below.
			You MUST output the FULL transformed content. Do NOT summarize, truncate, or shorten the content.
			Do NOT add conversational responses, commentary, or offers to help. Output ONLY the transformed content.

			Transformation instructions:
			{handlerPrompt}
			""";

		var agent = await _agentBuilder
			.WithModel(model)
			.WithSystemPrompt(systemPrompt)
			.WithMcp()
			.WithReporter(_reporter)
			.BuildAgentAsync(cancellationToken);

		var task = agent.SendAsync(content, cancellationToken);
		var result = await task.GetResultAsync();

		return result.Content;
	}
}
