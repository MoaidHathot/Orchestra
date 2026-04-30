namespace Orchestra.Engine;

/// <summary>
/// Lineage information about a parent execution that is launching a child orchestration.
/// Set by the parent's caller (or step executor) so the child can record its place in the
/// nesting chain. Used to enforce maximum nesting depth and to surface parent → child
/// relationships in run records and the active executions API.
/// </summary>
public sealed class ParentExecutionContext
{
	/// <summary>
	/// The execution ID of the immediate parent.
	/// </summary>
	public required string ParentExecutionId { get; init; }

	/// <summary>
	/// The name of the step in the parent orchestration that is launching this child.
	/// Null when the launch is not associated with a specific step (e.g. an external MCP caller).
	/// </summary>
	public string? ParentStepName { get; init; }

	/// <summary>
	/// The execution ID at the root of the nesting chain. Equal to <see cref="ParentExecutionId"/>
	/// when the parent is itself top-level, or the original root when the parent is a nested child.
	/// May be null if the parent is unknown to the launcher; the launcher will fall back to
	/// <see cref="ParentExecutionId"/> in that case.
	/// </summary>
	public string? RootExecutionId { get; init; }

	/// <summary>
	/// The nesting depth of the parent (0 = top-level). The child's depth will be Depth + 1.
	/// </summary>
	public int Depth { get; init; }
}
