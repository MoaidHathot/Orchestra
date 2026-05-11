namespace Orchestra.Cli.Run;

/// <summary>
/// Decoded payload of an <c>awaiting-input</c> SSE event — the information the user needs
/// to render a prompt and submit a response.
/// </summary>
public sealed record AwaitingInputInfo(
	string OrchestrationName,
	string RunId,
	string StepName,
	string Kind,
	string Prompt,
	IReadOnlyList<string> Choices,
	DateTimeOffset CreatedAt,
	DateTimeOffset? ExpiresAt);

/// <summary>
/// User's response to an <see cref="AwaitingInputInfo"/> prompt.
/// At least one of <see cref="Choice"/> / <see cref="Reply"/> must be non-null.
/// </summary>
public sealed record HumanInputResponse(string? Choice, string? Reply, string? RespondedBy);

/// <summary>
/// Sentinel exception raised by <see cref="NonInteractiveHumanInputPrompter"/> when a HITL
/// prompt arrives but the CLI is running without an interactive stdin. The top-level
/// handler converts this into exit code 2 with a short instructional message.
/// </summary>
public sealed class NonInteractiveAbortException : Exception
{
	public NonInteractiveAbortException(string message) : base(message) { }
}
