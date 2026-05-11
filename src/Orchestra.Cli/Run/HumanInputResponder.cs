using Microsoft.Extensions.Logging;

namespace Orchestra.Cli.Run;

/// <summary>
/// Submits a HITL response to the server. Abstracted so tests can substitute a stub.
/// </summary>
public interface IHumanInputResponder
{
	/// <summary>
	/// Sends the response. Returns when the server returns 200; throws after exhausting retries
	/// or on non-transient failures.
	/// </summary>
	Task RespondAsync(AwaitingInputInfo info, HumanInputResponse response, CancellationToken cancellationToken);
}

/// <summary>
/// Submits a <see cref="HumanInputResponse"/> back to the server via the existing
/// <c>POST /api/orchestrations/{name}/runs/{runId}/respond</c> endpoint, with bounded
/// retries on transient HTTP failures.
/// </summary>
public sealed partial class HumanInputResponder : IHumanInputResponder
{
	private readonly OrchestraClient _client;
	private readonly ILogger<HumanInputResponder> _logger;

	public HumanInputResponder(OrchestraClient client, ILogger<HumanInputResponder> logger)
	{
		_client = client;
		_logger = logger;
	}

	public async Task RespondAsync(AwaitingInputInfo info, HumanInputResponse response, CancellationToken cancellationToken)
	{
		const int maxAttempts = 3;
		Exception? lastError = null;
		for (var attempt = 1; attempt <= maxAttempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				await _client.RespondAsync(
					info.OrchestrationName,
					info.RunId,
					info.StepName,
					response.Choice,
					response.Reply,
					response.RespondedBy)
					.ConfigureAwait(false);
				LogResponseAccepted(_logger, info.StepName, attempt);
				return;
			}
			catch (HttpRequestException ex) when (IsTransient(ex) && attempt < maxAttempts)
			{
				LogTransientFailure(_logger, info.StepName, attempt, ex);
				lastError = ex;
				var delay = TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1)));
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			}
		}

		throw lastError ?? new InvalidOperationException("Failed to submit response.");
	}

	private static bool IsTransient(HttpRequestException ex)
	{
		// 5xx and missing-status (network errors) are transient; everything else (404 stale, 400
		// validation) is not — bouncing makes that worse.
		return ex.StatusCode is null
			|| (int)ex.StatusCode >= 500;
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "HITL response accepted for step '{StepName}' on attempt {Attempt}")]
	private static partial void LogResponseAccepted(ILogger logger, string stepName, int attempt);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Transient failure submitting HITL response for step '{StepName}' (attempt {Attempt}); will retry")]
	private static partial void LogTransientFailure(ILogger logger, string stepName, int attempt, Exception ex);
}
