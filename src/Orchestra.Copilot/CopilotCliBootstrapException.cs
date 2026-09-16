namespace Orchestra.Copilot;

/// <summary>
/// Thrown when the first-run Copilot CLI download fails against every npm registry the
/// bootstrap knows about. The message lists each URL that was tried with the reason it
/// failed, followed by the two environment overrides that get a machine unstuck
/// (<c>ORCHESTRA_COPILOT_NPM_REGISTRY</c> and <c>ORCHESTRA_COPILOT_CLI_PATH</c>).
/// </summary>
/// <remarks>
/// Exists because the raw transport error is useless on its own: a corporate network that
/// blocks <c>registry.npmjs.org</c> at the TLS layer surfaces as a bare
/// <see cref="System.Net.Http.HttpRequestException"/> ("The SSL connection could not be
/// established") repeated once per run scope, with no hint that a one-line override fixes it.
/// </remarks>
public sealed class CopilotCliBootstrapException : Exception
{
	/// <summary>Creates the exception with a fully composed, user-facing message.</summary>
	public CopilotCliBootstrapException(string message, Exception? innerException = null)
		: base(message, innerException)
	{
	}
}
