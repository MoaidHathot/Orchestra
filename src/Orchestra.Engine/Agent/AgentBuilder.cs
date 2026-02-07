namespace Orchestra.Engine;

public abstract class AgentBuilder
{
	protected string? Model { get; private set; }
	protected string? SystemPrompt { get; private set; }
	protected Mcp[] Mcps { get; private set; } = [];

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

	public abstract Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default);
}
