namespace Orchestra.Engine;

public class PromptExecutor : Executor<PromptOrchestrationStep>
{
	private readonly AgentBuilder _agentBuilder;

	public PromptExecutor(AgentBuilder agentBuilder)
	{
		_agentBuilder = agentBuilder;
	}

	public override async Task<ExecutionResult> ExecuteAsync(
		PromptOrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		try
		{
			// Resolve MCP names to full Mcp objects
			var mcps = ResolveMcps(step.AllowedMcps, context.Mcps);

			// Build the user prompt, incorporating dependency outputs and parameters
			var userPrompt = BuildUserPrompt(step, context);

			// Build and run the agent
			var agent = await _agentBuilder
				.WithModel(step.Model)
				.WithSystemPrompt(step.SystemPrompt)
				.WithMcp(mcps)
				.BuildAgentAsync(cancellationToken);

			var task = agent.SendAsync(userPrompt, cancellationToken);

		// Consume events — only log execution-relevant events, not content
		await foreach (var evt in task.WithCancellation(cancellationToken))
		{
			switch (evt.Type)
			{
				case AgentEventType.ToolExecutionStart:
					Console.WriteLine($"  [{step.Name}] Tool executing...");
					break;
				case AgentEventType.ToolExecutionComplete:
					Console.WriteLine($"  [{step.Name}] Tool execution complete.");
					break;
				case AgentEventType.Error:
					Console.Error.WriteLine($"  [{step.Name}] Error: {evt.ErrorMessage}");
					break;
			}
		}

			var result = await task.GetResultAsync();
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
			Console.Error.WriteLine($"\n[Step '{step.Name}' failed] {ex.Message}");
			return ExecutionResult.Failed(ex.Message);
		}
	}

	private static Mcp[] ResolveMcps(string[] allowedMcpNames, Mcp[] availableMcps)
	{
		if (allowedMcpNames.Length == 0)
			return [];

		var lookup = availableMcps.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
		var resolved = new Mcp[allowedMcpNames.Length];

		for (var i = 0; i < allowedMcpNames.Length; i++)
		{
			var name = allowedMcpNames[i];
			if (!lookup.TryGetValue(name, out var mcp))
				throw new InvalidOperationException($"MCP '{name}' referenced by step is not defined in MCP configuration.");

			resolved[i] = mcp;
		}

		return resolved;
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
			.BuildAgentAsync(cancellationToken);

		var task = agent.SendAsync(content, cancellationToken);
		var result = await task.GetResultAsync();

		return result.Content;
	}
}
