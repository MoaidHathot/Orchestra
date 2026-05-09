namespace Orchestra.Engine;

/// <summary>
/// Represents the user's response to a human-in-the-loop wait. Both <see cref="Choice"/>
/// and <see cref="Reply"/> are optional; the resolved step output is
/// <c>Reply ?? Choice ?? string.Empty</c>. Approval step waits with a non-empty
/// <c>choices</c> list MAY validate the choice against the allowed set on receipt.
/// </summary>
public sealed class UserInputResponse
{
	/// <summary>
	/// The constrained choice the user selected, when the wait declared a <c>choices</c> array.
	/// </summary>
	public string? Choice { get; init; }

	/// <summary>
	/// Free-form text reply. May be paired with <see cref="Choice"/> to attach a comment to
	/// a constrained choice (e.g. <c>choice = "reject"</c> with <c>reply = "needs more tests"</c>).
	/// </summary>
	public string? Reply { get; init; }

	/// <summary>
	/// Optional identifier of the responder, propagated to the run record for audit.
	/// </summary>
	public string? RespondedBy { get; init; }

	/// <summary>
	/// When the response was received (server time).
	/// </summary>
	public required DateTimeOffset RespondedAt { get; init; }

	/// <summary>
	/// Resolves the canonical content used as the step's output. <see cref="Reply"/> wins
	/// over <see cref="Choice"/>; if both are null, returns an empty string.
	/// </summary>
	public string ResolveContent() => Reply ?? Choice ?? string.Empty;
}
