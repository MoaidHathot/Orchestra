namespace Orchestra.Engine;

public class ExecutionResult
{
	public required string Content { get; init; }
	public required ExecutionStatus Status { get; init; }
	public string? ErrorMessage { get; init; }

	public static ExecutionResult Succeeded(string content) => new()
	{
		Content = content,
		Status = ExecutionStatus.Succeeded,
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
