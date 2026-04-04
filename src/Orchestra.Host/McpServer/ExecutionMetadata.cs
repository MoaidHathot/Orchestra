namespace Orchestra.Host.McpServer;

/// <summary>
/// Metadata for tracking orchestration execution nesting and lineage.
/// Stored on <see cref="Triggers.ActiveExecutionInfo"/> for parent-child tracking.
/// </summary>
public class ExecutionMetadata
{
	/// <summary>
	/// The execution ID of the parent orchestration that triggered this execution.
	/// Null for top-level executions.
	/// </summary>
	public string? ParentExecutionId { get; init; }

	/// <summary>
	/// The name of the step in the parent orchestration that triggered this execution.
	/// Null for top-level executions.
	/// </summary>
	public string? ParentStepName { get; init; }

	/// <summary>
	/// The execution ID of the root (top-level) orchestration in the nesting chain.
	/// Equals the current execution ID for top-level executions.
	/// </summary>
	public required string RootExecutionId { get; init; }

	/// <summary>
	/// The nesting depth of this execution.
	/// 0 = top-level, 1 = child of top-level, etc.
	/// </summary>
	public int Depth { get; init; }

	/// <summary>
	/// User-provided key-value metadata for tracking purposes
	/// (e.g., correlation IDs, ticket numbers).
	/// </summary>
	public Dictionary<string, string> UserMetadata { get; init; } = [];
}
