namespace Orchestra.Engine;

public class ExecutionResult
{
	public required string Content { get; init; }
	public required ExecutionStatus Status { get; init; }
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// The raw content before output handler was applied.
	/// Null when no output handler exists or for non-succeeded results.
	/// </summary>
	public string? RawContent { get; init; }

	public static ExecutionResult Succeeded(string content, string? rawContent = null) => new()
	{
		Content = content,
		Status = ExecutionStatus.Succeeded,
		RawContent = rawContent,
	};

	public static ExecutionResult Failed(string errorMessage) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Failed,
		ErrorMessage = errorMessage,
	};

	public static ExecutionResult Skipped(string reason) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Skipped,
		ErrorMessage = reason,
	};
}
