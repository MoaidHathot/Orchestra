namespace Orchestra.Engine;

public abstract class AgentBuilder
{
	protected string? Model { get; private set; }
	protected string? SystemPrompt { get; private set; }
	protected Mcp[] Mcps { get; private set; } = [];
	protected ReasoningLevel? ReasoningLevel { get; private set; }
	protected SystemPromptMode? SystemPromptMode { get; private set; }

	public AgentBuilder WithModel(string model)
	{
		Model = model;
		return this;
	}

	public AgentBuilder WithSystemPrompt(string systemPrompt)
	{
		SystemPrompt = systemPrompt;
		return this;
	}

	public AgentBuilder WithMcp(params Mcp[] mcps)
	{
		Mcps = mcps;
		return this;
	}

	public AgentBuilder WithReasoningLevel(ReasoningLevel? reasoningLevel)
	{
		ReasoningLevel = reasoningLevel;
		return this;
	}

	public AgentBuilder WithSystemPromptMode(SystemPromptMode? systemPromptMode)
	{
		SystemPromptMode = systemPromptMode;
		return this;
	}

	public abstract Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default);
}
