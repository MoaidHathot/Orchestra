namespace Orchestra.Engine;

/// <summary>
/// How a Prompt step's Copilot permission requests (shell, file read/write, url, mcp, etc.)
/// are resolved. Default <see cref="ApproveAll"/> preserves Orchestra's autonomous behavior.
/// </summary>
public enum PermissionMode
{
	/// <summary>Auto-approve every request (default; unchanged behavior).</summary>
	ApproveAll,

	/// <summary>
	/// Approve by default, but reject requests whose kind or target matches any
	/// <see cref="PermissionPolicy.Deny"/> glob (e.g. <c>"shell"</c>, <c>"url"</c>, <c>"*.env"</c>).
	/// </summary>
	DenyList,

	/// <summary>
	/// Route every request to a human operator (opt-in human-in-the-loop). Approvals are
	/// serialized per step. Falls back to "user not available" when no operator context exists.
	/// </summary>
	RequireHumanApproval,
}

/// <summary>
/// Per-step (or orchestration-default) policy controlling how Copilot permission requests
/// are resolved. When null, the engine applies <see cref="PermissionMode.ApproveAll"/>.
/// </summary>
public sealed class PermissionPolicy
{
	public PermissionMode Mode { get; init; } = PermissionMode.ApproveAll;

	/// <summary>
	/// Globs matched (case-insensitively, <c>*</c>/<c>?</c> wildcards) against a permission
	/// request's kind (<c>read</c>, <c>write</c>, <c>shell</c>, <c>url</c>, <c>mcp</c>, …) or its
	/// target (path, command, url, tool name). Used only when <see cref="Mode"/> is
	/// <see cref="PermissionMode.DenyList"/>.
	/// </summary>
	public string[] Deny { get; init; } = [];
}
