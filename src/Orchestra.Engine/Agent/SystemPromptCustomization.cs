namespace Orchestra.Engine;

/// <summary>
/// Override for an individual section of the system prompt.
/// Used with <see cref="SystemPromptMode.Customize"/> to surgically control
/// specific sections while preserving the rest.
/// </summary>
public class SystemPromptSectionOverride
{
	/// <summary>
	/// The action to apply to this section.
	/// </summary>
	public required SystemPromptSectionAction Action { get; init; }

	/// <summary>
	/// The content to use for Replace, Append, or Prepend actions.
	/// Ignored for Remove.
	/// </summary>
	public string? Content { get; init; }
}

/// <summary>
/// Actions that can be applied to a system prompt section.
/// </summary>
public enum SystemPromptSectionAction
{
	/// <summary>Replace the section content entirely.</summary>
	Replace,

	/// <summary>Remove the section from the prompt.</summary>
	Remove,

	/// <summary>Append content to the end of the section.</summary>
	Append,

	/// <summary>Prepend content to the beginning of the section.</summary>
	Prepend,
}

/// <summary>
/// Well-known section identifiers for the system prompt.
/// These correspond to the Copilot SDK's SystemPromptSections constants.
/// </summary>
public static class SystemPromptSections
{
	public const string Identity = "identity";
	public const string Tone = "tone";
	public const string ToolEfficiency = "tool_efficiency";
	public const string EnvironmentContext = "environment_context";
	public const string CodeChangeRules = "code_change_rules";
	public const string Guidelines = "guidelines";
	public const string Safety = "safety";
	public const string ToolInstructions = "tool_instructions";
	public const string CustomInstructions = "custom_instructions";
	public const string LastInstructions = "last_instructions";
}
