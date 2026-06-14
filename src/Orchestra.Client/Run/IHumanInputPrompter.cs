namespace Orchestra.Client.Run;

/// <summary>
/// Strategy for collecting a human's response to an <see cref="AwaitingInputInfo"/> prompt.
/// </summary>
public interface IHumanInputPrompter
{
	/// <summary>
	/// Returns the user's response. Implementations may throw <see cref="NonInteractiveAbortException"/>
	/// when no human is available to answer (CI, redirected stdin, <c>--no-interactive</c>).
	/// </summary>
	Task<HumanInputResponse> PromptAsync(AwaitingInputInfo info, CancellationToken cancellationToken);
}
