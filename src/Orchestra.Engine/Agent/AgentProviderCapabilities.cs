namespace Orchestra.Engine;

/// <summary>
/// How the engine reacts when a step requests a feature the resolved provider does not support.
/// </summary>
public enum CapabilityGapSeverity
{
	/// <summary>Log a warning and proceed — the feature degrades gracefully when omitted.</summary>
	Warning,

	/// <summary>
	/// Fail the step before it runs — silently omitting the feature is unsafe (security boundary)
	/// or breaks the step's contract (it would run without tools/controls it explicitly required).
	/// </summary>
	Error,
}

/// <summary>
/// A requested step feature the resolved provider cannot honor, plus how severely the engine treats it.
/// </summary>
/// <param name="Feature">The <see cref="AgentBuildConfig"/> field name (e.g. <c>"Mcps"</c>).</param>
/// <param name="Severity">Whether the gap fails the step (<see cref="CapabilityGapSeverity.Error"/>) or warns.</param>
public sealed record CapabilityGap(string Feature, CapabilityGapSeverity Severity);

/// <summary>
/// Declares which step-level features an agent provider supports.
///
/// Every <see cref="AgentBuilder"/> must return one of these from
/// <see cref="AgentBuilder.GetCapabilities"/>. The executor compares the declared capabilities
/// against each step's <see cref="AgentBuildConfig"/> and, for any requested-but-unsupported
/// feature, either fails the step (security / contract-breaking features) or logs a warning and
/// proceeds (features that degrade gracefully) — so a provider that forgets to wire a field
/// (e.g. <c>Mcps</c>) surfaces visibly instead of silently dropping the configuration.
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
	/// Features whose silent omission is unsafe (a security boundary) or breaks the step's
	/// contract (it would run without tools/controls it explicitly required). A provider that does
	/// not support one of these fails the step; every other gap degrades with a warning.
	///
	/// All error features are <b>user-explicit opt-ins</b> — they only appear in a step's config
	/// when the author deliberately set them — so they never fire spuriously. (Engine tools, by
	/// contrast, are injected into every step by the engine, so an unsupported-engine-tools gap is a
	/// warning: the step still runs, the model just can't call <c>set_status</c> and friends.)
	///
	/// <list type="bullet">
	///   <item><c>Mcps</c> — the step would run without the tool servers it requires.</item>
	///   <item><c>HumanInput</c> — human approval gating would be bypassed.</item>
	///   <item><c>PermissionPolicy</c> — the permission allow/deny policy would be bypassed.</item>
	///   <item><c>SandboxPolicy</c> — the requested sandbox boundary would not be applied.</item>
	///   <item><c>ExcludedTools</c> — forbidden tools would remain available (least-privilege violation).</item>
	/// </list>
	/// </summary>
	private static readonly HashSet<string> ErrorFeatures = new(StringComparer.Ordinal)
	{
		nameof(AgentBuildConfig.Mcps),
		nameof(AgentBuildConfig.HumanInput),
		nameof(AgentBuildConfig.PermissionPolicy),
		nameof(AgentBuildConfig.SandboxPolicy),
		nameof(AgentBuildConfig.ExcludedTools),
	};

	/// <summary>Classifies a capability gap: error for unsafe / contract-breaking features, else warning.</summary>
	public static CapabilityGapSeverity SeverityOf(string feature)
		=> ErrorFeatures.Contains(feature) ? CapabilityGapSeverity.Error : CapabilityGapSeverity.Warning;

	/// <summary>
	/// Enumerates the features that <paramref name="config"/> requests but this provider does not
	/// support, each tagged with its <see cref="CapabilityGapSeverity"/>. The executor fails the
	/// step on any <see cref="CapabilityGapSeverity.Error"/> gap and warns on the rest.
	/// </summary>
	public IEnumerable<CapabilityGap> FindUnsupported(AgentBuildConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		foreach (var feature in FindUnsupportedNames(config))
		{
			yield return new CapabilityGap(feature, SeverityOf(feature));
		}
	}

	private IEnumerable<string> FindUnsupportedNames(AgentBuildConfig config)
	{
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
