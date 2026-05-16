namespace Orchestra.Engine;

/// <summary>
/// Structured details from an agent session error, captured at the SDK/CLI boundary.
///
/// Field shape mirrors the GitHub Copilot SDK's <c>SessionErrorData</c> payload so
/// nothing is silently dropped between the CLI and Orchestra's run record:
/// <list type="bullet">
///   <item><see cref="ErrorType"/>: free-form category (e.g. <c>"authentication"</c>,
///   <c>"authorization"</c>, <c>"quota"</c>, <c>"rate_limit"</c>, <c>"context_limit"</c>,
///   <c>"query"</c>) as classified by the upstream CLI.</item>
///   <item><see cref="StatusCode"/>: HTTP status of the upstream request, when applicable.</item>
///   <item><see cref="ProviderCallId"/>: the <c>x-github-request-id</c> for support escalations.</item>
///   <item><see cref="Stack"/>: CLI/V8 stack trace when supplied (large; persisted verbatim).</item>
///   <item><see cref="Url"/>: optional user-openable URL surfaced by the upstream service.</item>
/// </list>
///
/// Lives on <see cref="ExecutionResult.ErrorDetails"/> and
/// <see cref="StepRunRecord.ErrorDetails"/> so structured fields are persisted in
/// <c>run.json</c> alongside the existing <c>errorMessage</c>. Producers and consumers
/// must keep this record JSON-stable.
/// </summary>
public sealed record AgentSessionErrorDetails
{
	/// <summary>
	/// Category of error from the upstream provider, e.g. <c>"authentication"</c>,
	/// <c>"authorization"</c>, <c>"quota"</c>, <c>"rate_limit"</c>, <c>"context_limit"</c>,
	/// <c>"query"</c>. Free-form so a future SDK can add categories without recompiling.
	/// </summary>
	public string? ErrorType { get; init; }

	/// <summary>
	/// HTTP status code from the upstream provider's request, when applicable.
	/// Modeled as <see cref="long"/> to match the SDK's nullable Int64 surface.
	/// </summary>
	public long? StatusCode { get; init; }

	/// <summary>
	/// GitHub request tracing id (the <c>x-github-request-id</c> response header) for
	/// correlating with server-side logs during support escalations. Gold for triage.
	/// </summary>
	public string? ProviderCallId { get; init; }

	/// <summary>
	/// Optional URL surfaced by the upstream service that the user can open in a browser.
	/// </summary>
	public string? Url { get; init; }

	/// <summary>
	/// CLI/V8 stack trace when the upstream side supplied one. Persisted verbatim so a
	/// future analysis tool can correlate against the bundled CLI's source.
	/// </summary>
	public string? Stack { get; init; }

	/// <summary>
	/// True when the CLI itself signalled that it had already exhausted its own retry
	/// budget before surfacing this error (the bundled <c>copilot.exe</c> retries the
	/// upstream model API internally; when those retries are exhausted it emits a
	/// <c>session.error</c> with a message like <c>"Failed to get response from the AI
	/// model; retried 5 times"</c>). When this flag is set the CLI process is typically
	/// about to shut down — swapping to a fresh CLI worker often clears the upstream
	/// routing / connection-pool issue. Set by <c>CopilotSessionHandler.HandleError</c>
	/// when the SDK message matches the well-known exhaustion pattern, and used by
	/// <c>CopilotAgent</c>'s swap loop to decide whether to retry on a new worker.
	/// </summary>
	public bool ExhaustedCliRetries { get; init; }
}
