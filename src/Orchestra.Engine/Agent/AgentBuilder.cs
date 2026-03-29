namespace Orchestra.Engine;

public abstract class AgentBuilder
{
	protected string? Model { get; private set; }
	protected string? SystemPrompt { get; private set; }
	protected Mcp[] Mcps { get; private set; } = [];
	protected Subagent[] Subagents { get; private set; } = [];
	protected ReasoningLevel? ReasoningLevel { get; private set; }
	protected SystemPromptMode? SystemPromptMode { get; private set; }
	protected IOrchestrationReporter Reporter { get; private set; } = NullOrchestrationReporter.Instance;

	/// <summary>
	/// Engine tools to inject into the agent session. These are built-in tools
	/// that allow the LLM to interact with the orchestration engine (e.g., signal failure).
	/// </summary>
	protected IReadOnlyCollection<IEngineTool> EngineTools { get; private set; } = [];

	/// <summary>
	/// Shared context for engine tool side effects. Set when engine tools are provided,
	/// allowing the caller to inspect results after agent execution.
	/// </summary>
	protected EngineToolContext? EngineToolCtx { get; private set; }

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

	public AgentBuilder WithSubagents(params Subagent[] subagents)
	{
		Subagents = subagents;
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

	public AgentBuilder WithReporter(IOrchestrationReporter reporter)
	{
		Reporter = reporter;
		return this;
	}

	/// <summary>
	/// Configures engine tools to inject into the agent session.
	/// The <paramref name="context"/> is shared between the caller and the tools,
	/// allowing the caller to inspect side effects after execution.
	/// </summary>
	public AgentBuilder WithEngineTools(IReadOnlyCollection<IEngineTool> tools, EngineToolContext context)
	{
		EngineTools = tools;
		EngineToolCtx = context;
		return this;
	}

	/// <summary>
	/// Builds an agent from the current mutable builder state.
	/// WARNING: Not safe for concurrent use — if multiple threads call With*() + BuildAgentAsync()
	/// on the same builder instance, state can be overwritten between calls.
	/// Prefer <see cref="BuildAgentAsync(AgentBuildConfig, CancellationToken)"/> for concurrent scenarios.
	/// </summary>
	public abstract Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Builds an agent from an immutable configuration snapshot.
	/// Thread-safe — the config is constructed locally by the caller, eliminating shared mutable state.
	/// </summary>
	public abstract Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default);
}
