namespace Orchestra.Engine;

/// <summary>
/// Context-window tier requested for a Prompt step. Maps onto the Copilot SDK's
/// <c>ContextTier</c> (default / long-context). <see cref="LongContext"/> opts the
/// step into the model's extended context window where the provider supports it.
/// When null, the provider/model default applies.
/// </summary>
public enum ContextTier
{
	Default,
	LongContext,
}
