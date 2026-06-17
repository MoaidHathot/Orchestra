namespace Orchestra.Engine;

/// <summary>
/// Declares which step-level features an agent provider supports.
///
/// Every <see cref="AgentBuilder"/> must return one of these from
/// <see cref="AgentBuilder.GetCapabilities"/>. The executor compares the declared capabilities
/// against each step's <see cref="AgentBuildConfig"/> and, for any feature the step actually uses
/// that the provider does not support, <b>fails the step before it runs</b> (it never silently
/// drops configuration). A provider that forgets to wire a field (e.g. <c>Mcps</c>) therefore
/// surfaces as a fail-fast error rather than surprising behavior.
///
/// Flags default to <c>false</c>: a provider opts in to each feature it actually implements.
/// </summary>
public sealed record AgentProviderCapabilities
{
	/// <summary>Provider key this capability set describes (e.g. "copilot", "opencode").</summary>
	public required string Provider { get; init; }

	/// <summary>Per-step MCP servers (<see cref="AgentBuildConfig.Mcps"/>).</summary>
	public bool Mcps { get; init; }

	/// <summary>Inline sub-agents (<see cref="AgentBuildConfig.Subagents"/>).</summary>
	public bool Subagents { get; init; }

	/// <summary>Reasoning effort level (<see cref="AgentBuildConfig.ReasoningLevel"/>).</summary>
	public bool ReasoningLevel { get; init; }

	/// <summary>Reasoning-summary verbosity (<see cref="AgentBuildConfig.ReasoningSummary"/>).</summary>
	public bool ReasoningSummary { get; init; }

	/// <summary>Context-window tier (<see cref="AgentBuildConfig.ContextTier"/>).</summary>
	public bool ContextTier { get; init; }

	/// <summary>Per-step working directory (<see cref="AgentBuildConfig.WorkingDirectory"/>).</summary>
	public bool WorkingDirectory { get; init; }

	/// <summary>Per-step GitHub token override (<see cref="AgentBuildConfig.GitHubToken"/>).</summary>
	public bool GitHubToken { get; init; }

	/// <summary>Human-in-the-loop elicitation routing (<see cref="AgentBuildConfig.HumanInput"/>).</summary>
	public bool HumanInput { get; init; }

	/// <summary>Permission policy resolution (<see cref="AgentBuildConfig.PermissionPolicy"/>).</summary>
	public bool PermissionPolicy { get; init; }

	/// <summary>Sandbox policy (<see cref="AgentBuildConfig.SandboxPolicy"/>).</summary>
	public bool SandboxPolicy { get; init; }

	/// <summary>
	/// Non-Replace system-prompt modes (<see cref="AgentBuildConfig.SystemPromptMode"/> set to
	/// <c>Append</c> or <c>Customize</c>). <c>Replace</c> is the universal baseline — every provider
	/// sends the step system prompt as-is — so it never counts as unsupported.
	/// </summary>
	public bool SystemPromptMode { get; init; }

	/// <summary>Section-level system-prompt overrides (<see cref="AgentBuildConfig.SystemPromptSections"/>).</summary>
	public bool SystemPromptSections { get; init; }

	/// <summary>Engine tools injected into the session (<see cref="AgentBuildConfig.EngineTools"/>).</summary>
	public bool EngineTools { get; init; }

	/// <summary>Agent Skills directory discovery (<see cref="AgentBuildConfig.SkillDirectories"/>).</summary>
	public bool SkillDirectories { get; init; }

	/// <summary>Infinite sessions / automatic compaction (<see cref="AgentBuildConfig.InfiniteSessionConfig"/>).</summary>
	public bool InfiniteSession { get; init; }

	/// <summary>Image attachments (<see cref="AgentBuildConfig.Attachments"/>).</summary>
	public bool Attachments { get; init; }

	/// <summary>Tool exclusion list (<see cref="AgentBuildConfig.ExcludedTools"/>).</summary>
	public bool ExcludedTools { get; init; }

	/// <summary>
	/// A capability set with every feature enabled. Convenience for in-memory / test builders that
	/// faithfully echo whatever configuration they are handed.
	/// </summary>
	public static AgentProviderCapabilities All(string provider) => new()
	{
		Provider = provider,
		Mcps = true,
		Subagents = true,
		ReasoningLevel = true,
		ReasoningSummary = true,
		ContextTier = true,
		WorkingDirectory = true,
		GitHubToken = true,
		HumanInput = true,
		PermissionPolicy = true,
		SandboxPolicy = true,
		SystemPromptMode = true,
		SystemPromptSections = true,
		EngineTools = true,
		SkillDirectories = true,
		InfiniteSession = true,
		Attachments = true,
		ExcludedTools = true,
	};

	/// <summary>
	/// Enumerates the names of the features that <paramref name="config"/> actually uses but this
	/// provider does not support. The executor fails the step when this is non-empty, so an
	/// unsupported feature can never be silently dropped.
	/// </summary>
	public IEnumerable<string> FindUnsupported(AgentBuildConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		if (!Mcps && config.Mcps.Length > 0)
		{
			yield return nameof(config.Mcps);
		}

		if (!Subagents && config.Subagents.Length > 0)
		{
			yield return nameof(config.Subagents);
		}

		if (!ReasoningLevel && config.ReasoningLevel is not null)
		{
			yield return nameof(config.ReasoningLevel);
		}

		if (!ReasoningSummary && config.ReasoningSummary is not null)
		{
			yield return nameof(config.ReasoningSummary);
		}

		if (!ContextTier && config.ContextTier is not null)
		{
			yield return nameof(config.ContextTier);
		}

		if (!WorkingDirectory && !string.IsNullOrEmpty(config.WorkingDirectory))
		{
			yield return nameof(config.WorkingDirectory);
		}

		if (!GitHubToken && !string.IsNullOrEmpty(config.GitHubToken))
		{
			yield return nameof(config.GitHubToken);
		}

		if (!HumanInput && config.HumanInput)
		{
			yield return nameof(config.HumanInput);
		}

		if (!PermissionPolicy && config.PermissionPolicy is not null)
		{
			yield return nameof(config.PermissionPolicy);
		}

		if (!SandboxPolicy && config.SandboxPolicy is not null)
		{
			yield return nameof(config.SandboxPolicy);
		}

		if (!SystemPromptMode && config.SystemPromptMode is Engine.SystemPromptMode.Append or Engine.SystemPromptMode.Customize)
		{
			yield return nameof(config.SystemPromptMode);
		}

		if (!SystemPromptSections && config.SystemPromptSections is { Count: > 0 })
		{
			yield return nameof(config.SystemPromptSections);
		}

		if (!EngineTools && config.EngineTools.Count > 0)
		{
			yield return nameof(config.EngineTools);
		}

		if (!SkillDirectories && config.SkillDirectories.Length > 0)
		{
			yield return nameof(config.SkillDirectories);
		}

		if (!InfiniteSession && config.InfiniteSessionConfig is not null)
		{
			yield return nameof(config.InfiniteSessionConfig);
		}

		if (!Attachments && config.Attachments.Length > 0)
		{
			yield return nameof(config.Attachments);
		}

		if (!ExcludedTools && config.ExcludedTools.Length > 0)
		{
			yield return nameof(config.ExcludedTools);
		}
	}
}
