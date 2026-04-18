namespace Orchestra.Engine;

public enum SystemPromptMode
{
	Append,
	Replace,

	/// <summary>
	/// Selectively override individual sections of the system prompt
	/// while preserving the rest. Use with <see cref="SystemPromptSectionOverride"/>
	/// to replace, remove, append, or prepend content to specific sections.
	/// </summary>
	Customize,
}
