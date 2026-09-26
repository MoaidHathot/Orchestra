namespace Orchestra.Engine;

/// <summary>
/// The single place the engine's fallback model name lives. Used for internal agent calls
/// that run without any author-supplied model — trigger and child-orchestration
/// <c>inputHandlerPrompt</c> transforms — when neither the caller nor the host configured one.
/// Prompt steps do <em>not</em> fall back to this: a Prompt step must resolve a model through
/// step → orchestration → host defaults, and fails otherwise (see <see cref="HostAgentDefaults"/>).
/// </summary>
/// <remarks>
/// Scaffolding (<c>orchestra init</c>), the Portal's Visual Builder and the shipped examples all
/// read this value (directly or via <c>/api/config</c>) so there is exactly one default to bump.
/// </remarks>
public static class ModelDefaults
{
	/// <summary>Fallback model identifier for internal agent calls.</summary>
	public const string Model = "claude-opus-4.8";
}
