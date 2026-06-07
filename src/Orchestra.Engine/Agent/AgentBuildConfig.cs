namespace Orchestra.Engine;

/// <summary>
/// Immutable configuration for a single agent build request.
/// Thread-safe — construct one per call site to avoid shared mutable state.
/// </summary>
public sealed record AgentBuildConfig
{
	public required string Model { get; init; }
	public string? SystemPrompt { get; init; }
	public Mcp[] Mcps { get; init; } = [];
	public Subagent[] Subagents { get; init; } = [];
	public ReasoningLevel? ReasoningLevel { get; init; }
	public SystemPromptMode? SystemPromptMode { get; init; }
	public IOrchestrationReporter Reporter { get; init; } = NullOrchestrationReporter.Instance;
	public IReadOnlyCollection<IEngineTool> EngineTools { get; init; } = [];
	public EngineToolContext? EngineToolCtx { get; init; }

	/// <summary>
	/// Directories containing Agent Skills (SKILL.md files) to load into the session.
	/// Skills provide specialized knowledge and workflows that the agent can discover
	/// and activate on demand.
	/// </summary>
	public string[] SkillDirectories { get; init; } = [];

	/// <summary>
	/// Section-level overrides for the system prompt when using <see cref="Engine.SystemPromptMode.Customize"/>.
	/// Keys are section identifiers (see <see cref="SystemPromptSections"/>).
	/// </summary>
	public Dictionary<string, SystemPromptSectionOverride>? SystemPromptSections { get; init; }

	/// <summary>
	/// Configuration for infinite sessions (automatic context compaction).
	/// When null, the SDK default behavior is used (infinite sessions enabled).
	/// </summary>
	public InfiniteSessionConfig? InfiniteSessionConfig { get; init; }

	/// <summary>
	/// Image attachments to send with the prompt.
	/// </summary>
	public ImageAttachment[] Attachments { get; init; } = [];

	/// <summary>
	/// Optional list of tool names to exclude from the main agent's tool catalog. When
	/// set, the listed tools are unavailable to the main agent (sub-agents are unaffected
	/// unless they declare their own <see cref="Subagent.Tools"/> filter). Tool names use
	/// the SDK's wire vocabulary — typically built-in names such as <c>"shell"</c>,
	/// <c>"edit_file"</c>, <c>"write_file"</c>, <c>"view"</c>, etc.
	/// </summary>
	/// <remarks>
	/// Requires GitHub.Copilot.SDK 1.0.0+ which added <c>DefaultAgentConfig.ExcludedTools</c>
	/// (SDK PR #1098). Empty / null leaves the SDK's full built-in tool catalog enabled,
	/// preserving Orchestra's pre-1.0 behaviour for unchanged orchestrations.
	/// </remarks>
	public string[] ExcludedTools { get; init; } = [];
}
