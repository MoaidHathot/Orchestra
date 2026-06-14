namespace Orchestra.Engine;

public class PromptOrchestrationStep : OrchestrationStep
{
	public required string SystemPrompt { get; init; }
	public required string UserPrompt { get; init; }
	public string? InputHandlerPrompt { get; init; }
	public string? OutputHandlerPrompt { get; init; }
	public required string Model { get; init; }
	public ReasoningLevel? ReasoningLevel { get; init; }

	/// <summary>
	/// Optional verbosity for the model's reasoning summary (none / concise / detailed).
	/// Maps onto the Copilot SDK's <c>SessionConfig.ReasoningSummary</c>. Null = provider default.
	/// </summary>
	public ReasoningSummaryLevel? ReasoningSummary { get; init; }

	/// <summary>
	/// Optional context-window tier (default / longContext). Maps onto the Copilot SDK's
	/// <c>SessionConfig.ContextTier</c>. Null = provider default.
	/// </summary>
	public ContextTier? ContextTier { get; init; }

	/// <summary>
	/// Optional working directory for the agent's shell/file tools and config discovery
	/// (custom instructions, <c>.github/agents</c>, <c>.github/mcp.json</c>). Resolved at
	/// execution time (supports <c>${env:*}</c>/<c>{{vars.*}}</c>) and validated to exist.
	/// Null = the runtime's default working directory.
	/// </summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>
	/// Optional GitHub token used to authenticate this step's Copilot session, overriding
	/// the host-level default. Resolved at execution time (e.g. <c>${env:GITHUB_TOKEN}</c>).
	/// Null = inherit the host's configured auth (orchestra.json <c>copilot.gitHubToken</c>
	/// / <c>useLoggedInUser</c>, else the CLI's stored credentials).
	/// </summary>
	public string? GitHubToken { get; init; }

	public SystemPromptMode? SystemPromptMode { get; init; }
	public Mcp[] Mcps { get; internal set; } = [];

	/// <summary>
	/// Optional loop configuration for retry/check patterns.
	/// When set, after this step runs the executor checks if the output matches
	/// <see cref="LoopConfig.ExitPattern"/>. If not, it re-runs the target step
	/// with feedback and re-checks, up to <see cref="LoopConfig.MaxIterations"/> times.
	/// </summary>
	public LoopConfig? Loop { get; init; }

	/// <summary>
	/// Raw MCP names from JSON, used internally during parsing to resolve to <see cref="Mcps"/>.
	/// </summary>
	internal string[] McpNames { get; init; } = [];

	/// <summary>
	/// Optional list of subagents that the main step orchestrator can delegate to.
	/// When provided, the implementation will use multi-agent orchestration,
	/// allowing the runtime to automatically delegate to subagents based on user intent.
	/// </summary>
	public Subagent[] Subagents { get; init; } = [];

	/// <summary>
	/// Optional list of directories containing Agent Skills (SKILL.md files).
	/// Skills provide specialized knowledge and workflows that the agent can discover
	/// and activate on demand. Paths are passed as-is to the underlying SDK.
	/// </summary>
	public string[] SkillDirectories { get; internal set; } = [];

	/// <summary>
	/// Optional configuration for infinite sessions (automatic context compaction).
	/// When null, the SDK default is used (infinite sessions enabled).
	/// </summary>
	public InfiniteSessionConfig? InfiniteSessions { get; init; }

	/// <summary>
	/// Section-level overrides for the system prompt when using Customize mode.
	/// Keys are section identifiers (e.g., "tone", "guidelines").
	/// </summary>
	public Dictionary<string, SystemPromptSectionOverride>? SystemPromptSections { get; init; }

	/// <summary>
	/// Optional image attachments to send with the prompt.
	/// Supports file paths and base64 blob data.
	/// </summary>
	public ImageAttachment[] Attachments { get; internal set; } = [];

	/// <summary>
	/// Optional list of opt-in engine tool names that this Prompt step grants the agent
	/// access to. Always-on tools (<c>orchestra_set_status</c>, <c>orchestra_complete</c>,
	/// file save/read) are unaffected. Currently used to opt in to <c>request_user_input</c>
	/// (the LLM-decided human-in-the-loop tool). Falls back to the orchestration's
	/// <see cref="Orchestration.DefaultEnableTools"/> when null.
	/// </summary>
	public string[]? EnableTools { get; init; }
}
