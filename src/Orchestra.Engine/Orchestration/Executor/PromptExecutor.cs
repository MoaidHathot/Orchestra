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
		// Resolve MCP names to full Mcp objects
		var mcps = ResolveMcps(step.AllowedMcps, context.Mcps);

		// Build the user prompt, incorporating dependency outputs via input handler
		var userPrompt = BuildUserPrompt(step, context);

		// Build and run the agent
		var agent = await _agentBuilder
			.WithModel(step.Model)
			.WithSystemPrompt(step.SystemPrompt)
			.WithMcp(mcps)
			.BuildAgentAsync(cancellationToken);

		var task = agent.SendAsync(userPrompt, cancellationToken);

		// Stream events to console for visibility
		await foreach (var evt in task.WithCancellation(cancellationToken))
		{
			switch (evt.Type)
			{
				case AgentEventType.MessageDelta:
					Console.Write(evt.Content);
					break;
				case AgentEventType.Error:
					Console.Error.WriteLine($"\n[Error] {evt.ErrorMessage}");
					break;
			}
		}

		Console.WriteLine();

		var result = await task.GetResultAsync();
		var content = result.Content;

		// Apply output handler if specified
		if (step.OutputHandlerPrompt is not null)
		{
			content = await RunHandlerAsync(step.OutputHandlerPrompt, content, step.Model, cancellationToken);
		}

		return new ExecutionResult { Content = content };
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
		var dependencyOutputs = context.GetDependencyOutputs(step.DependsOn);

		if (string.IsNullOrEmpty(dependencyOutputs))
			return step.UserPrompt;

		if (step.InputHandlerPrompt is not null)
		{
			return $"""
				{step.InputHandlerPrompt}

				---
				Previous step outputs:
				{dependencyOutputs}

				---
				Task:
				{step.UserPrompt}
				""";
		}

		return $"""
			{step.UserPrompt}

			---
			Context from previous steps:
			{dependencyOutputs}
			""";
	}

	private async Task<string> RunHandlerAsync(
		string handlerPrompt,
		string content,
		string model,
		CancellationToken cancellationToken)
	{
		var agent = await _agentBuilder
			.WithModel(model)
			.WithSystemPrompt(handlerPrompt)
			.WithMcp()
			.BuildAgentAsync(cancellationToken);

		var task = agent.SendAsync(content, cancellationToken);
		var result = await task.GetResultAsync();

		return result.Content;
	}
}
