using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orchestra.Cli.Sse;

namespace Orchestra.Cli.Run;

/// <summary>
/// Outcome of a <see cref="RunSession"/>.
/// </summary>
public enum RunSessionOutcome
{
	/// <summary>The orchestration finished successfully (orchestration-done with status Succeeded).</summary>
	Succeeded,

	/// <summary>The orchestration finished but with a non-success status, or via orchestration-cancelled.</summary>
	NonSuccessfulTerminal,

	/// <summary>The orchestration emitted orchestration-error.</summary>
	Errored,

	/// <summary>The user disconnected (Ctrl+C) before any terminal event arrived.</summary>
	Disconnected,

	/// <summary>A HITL prompt arrived but stdin is non-interactive — the session aborted.</summary>
	NonInteractiveAbort,
}

/// <summary>
/// Final result returned from <see cref="RunSession.RunAsync"/>.
/// </summary>
public sealed record RunSessionResult(
	RunSessionOutcome Outcome,
	string? OrchestrationName,
	string? RunId,
	string? FinalStatus,
	string? ErrorMessage);

/// <summary>
/// Drives a live SSE stream end-to-end: parses frames, dispatches to an <see cref="IRunObserver"/>,
/// answers HITL prompts via an <see cref="IHumanInputPrompter"/>, and POSTs responses through
/// a <see cref="HumanInputResponder"/>. Stops on the first terminal event, on cancellation,
/// or on a non-interactive abort.
/// </summary>
public sealed partial class RunSession
{
	private readonly IRunObserver _observer;
	private readonly IHumanInputPrompter _prompter;
	private readonly IHumanInputResponder _responder;
	private readonly ILogger<RunSession> _logger;

	private string? _orchestrationName;
	private string? _runId;

	public RunSession(
		IRunObserver observer,
		IHumanInputPrompter prompter,
		IHumanInputResponder responder,
		ILogger<RunSession> logger)
	{
		_observer = observer;
		_prompter = prompter;
		_responder = responder;
		_logger = logger;
	}

	/// <summary>
	/// Runs the session against an open SSE response. Caller owns the response and is
	/// responsible for disposing it.
	/// </summary>
	/// <param name="response">An already-opened SSE response (e.g. from <see cref="OrchestraClient.OpenRunStreamAsync"/>).</param>
	/// <param name="orchestrationIdHint">
	/// If set (and run-context has not yet arrived), used as an early identifier so HITL responses
	/// can be POSTed even on the very first awaiting-input event before run-context lands.
	/// </param>
	/// <param name="cancellationToken">Token cancelled by Ctrl+C.</param>
	public async Task<RunSessionResult> RunAsync(
		HttpResponseMessage response,
		string? orchestrationIdHint,
		CancellationToken cancellationToken)
	{
		if (!response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			throw new HttpRequestException(
				$"Server returned {(int)response.StatusCode}: {body}",
				inner: null,
				statusCode: response.StatusCode);
		}

		using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var reader = new StreamReader(stream);

		_orchestrationName = orchestrationIdHint;

		try
		{
			await foreach (var frame in SseStreamReader.ReadAsync(reader, cancellationToken).ConfigureAwait(false))
			{
				if (frame.Event == SseEventTypes.Heartbeat || frame.Event.Length == 0)
				{
					continue;
				}

				var dispatch = await DispatchAsync(frame, cancellationToken).ConfigureAwait(false);
				if (dispatch is { } final)
				{
					return final;
				}
			}
		}
		catch (NonInteractiveAbortException ex)
		{
			LogNonInteractiveAbort(_logger, ex.Message);
			return new RunSessionResult(RunSessionOutcome.NonInteractiveAbort, _orchestrationName, _runId, null, ex.Message);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			_observer.OnStreamInterrupted("disconnected");
			LogDisconnected(_logger, _runId ?? "(unknown)");
			return new RunSessionResult(RunSessionOutcome.Disconnected, _orchestrationName, _runId, null, null);
		}

		// Stream ended without a terminal event — treat as disconnect.
		_observer.OnStreamInterrupted("stream ended");
		return new RunSessionResult(RunSessionOutcome.Disconnected, _orchestrationName, _runId, null, null);
	}

	private async Task<RunSessionResult?> DispatchAsync(SseFrame frame, CancellationToken cancellationToken)
	{
		JsonElement payload;
		try
		{
			payload = JsonSerializer.Deserialize<JsonElement>(frame.Data);
		}
		catch (JsonException ex)
		{
			LogJsonParseFailure(_logger, frame.Event, ex);
			return null;
		}

		switch (frame.Event)
		{
			case SseEventTypes.ExecutionStarted:
				if (payload.TryGetProperty("executionId", out var execId) && execId.ValueKind == JsonValueKind.String)
				{
					_observer.OnExecutionStarted(execId.GetString() ?? string.Empty);
				}
				return null;

			case SseEventTypes.RunContext:
			case SseEventTypes.ExecutionInfo:
			{
				if (payload.TryGetProperty("orchestrationName", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
				{
					_orchestrationName = nameProp.GetString();
				}
				if (payload.TryGetProperty("runId", out var runProp) && runProp.ValueKind == JsonValueKind.String)
				{
					_runId = runProp.GetString();
				}
				else if (payload.TryGetProperty("executionId", out var execProp) && execProp.ValueKind == JsonValueKind.String)
				{
					_runId ??= execProp.GetString();
				}
				if (_orchestrationName is not null && _runId is not null)
				{
					_observer.OnRunContext(_orchestrationName, _runId);
				}
				return null;
			}

			case SseEventTypes.StepStarted:
				_observer.OnStepStarted(GetString(payload, "stepName"));
				return null;

			case SseEventTypes.StepCompleted:
				_observer.OnStepCompleted(GetString(payload, "stepName"));
				return null;

			case SseEventTypes.StepError:
				_observer.OnStepError(GetString(payload, "stepName"), GetString(payload, "error"));
				return null;

			case SseEventTypes.StepCancelled:
				_observer.OnStepCancelled(GetString(payload, "stepName"));
				return null;

			case SseEventTypes.StepSkipped:
				_observer.OnStepSkipped(GetString(payload, "stepName"), GetString(payload, "reason"));
				return null;

			case SseEventTypes.AwaitingInput:
			{
				var info = ParseAwaitingInput(payload);
				_orchestrationName ??= info.OrchestrationName;
				_runId ??= info.RunId;
				_observer.OnAwaitingInput(info);

				// PromptAsync may throw NonInteractiveAbortException — caught at the top level.
				var humanResponse = await _prompter.PromptAsync(info, cancellationToken).ConfigureAwait(false);
				await _responder.RespondAsync(info, humanResponse, cancellationToken).ConfigureAwait(false);
				return null;
			}

			case SseEventTypes.InputReceived:
				_observer.OnInputReceived(
					GetString(payload, "stepName"),
					GetNullableString(payload, "choice"),
					GetNullableString(payload, "reply"),
					GetNullableString(payload, "respondedBy"));
				return null;

			case SseEventTypes.InputTimeout:
				_observer.OnInputTimeout(GetString(payload, "stepName"), GetString(payload, "onTimeout"));
				return null;

			case SseEventTypes.OrchestrationDone:
			{
				var status = GetString(payload, "status");
				_observer.OnOrchestrationDone(status);
				var outcome = string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)
					? RunSessionOutcome.Succeeded
					: RunSessionOutcome.NonSuccessfulTerminal;
				return new RunSessionResult(outcome, _orchestrationName, _runId, status, null);
			}

			case SseEventTypes.OrchestrationCancelled:
			{
				var reason = GetNullableString(payload, "reason")
					?? (payload.TryGetProperty("cancellation", out var cancel) && cancel.ValueKind == JsonValueKind.Object
						? GetNullableString(cancel, "reason") ?? GetNullableString(cancel, "kind")
						: null);
				_observer.OnOrchestrationCancelled(reason);
				return new RunSessionResult(RunSessionOutcome.NonSuccessfulTerminal, _orchestrationName, _runId, "Cancelled", reason);
			}

			case SseEventTypes.OrchestrationError:
			{
				var error = GetString(payload, "error");
				_observer.OnOrchestrationError(error);
				return new RunSessionResult(RunSessionOutcome.Errored, _orchestrationName, _runId, "Failed", error);
			}

			default:
				_observer.OnUnknownEvent(frame.Event, payload);
				return null;
		}
	}

	private static AwaitingInputInfo ParseAwaitingInput(JsonElement payload)
	{
		var choices = new List<string>();
		if (payload.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in choicesEl.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
				{
					choices.Add(s);
				}
			}
		}

		DateTimeOffset created = DateTimeOffset.UtcNow;
		if (payload.TryGetProperty("createdAt", out var createdEl)
			&& createdEl.ValueKind == JsonValueKind.String
			&& DateTimeOffset.TryParse(createdEl.GetString(), out var parsedCreated))
		{
			created = parsedCreated;
		}

		DateTimeOffset? expires = null;
		if (payload.TryGetProperty("expiresAt", out var expiresEl)
			&& expiresEl.ValueKind == JsonValueKind.String
			&& DateTimeOffset.TryParse(expiresEl.GetString(), out var parsedExpires))
		{
			expires = parsedExpires;
		}

		return new AwaitingInputInfo(
			OrchestrationName: GetString(payload, "orchestrationName"),
			RunId: GetString(payload, "runId"),
			StepName: GetString(payload, "stepName"),
			Kind: GetNullableString(payload, "kind") ?? "Approval",
			Prompt: GetString(payload, "prompt"),
			Choices: choices,
			CreatedAt: created,
			ExpiresAt: expires);
	}

	private static string GetString(JsonElement payload, string property) =>
		payload.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
			? el.GetString() ?? string.Empty
			: string.Empty;

	private static string? GetNullableString(JsonElement payload, string property) =>
		payload.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
			? el.GetString()
			: null;

	[LoggerMessage(Level = LogLevel.Debug, Message = "Failed to parse JSON payload for SSE event '{EventType}'")]
	private static partial void LogJsonParseFailure(ILogger logger, string eventType, Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run session aborted (non-interactive): {Message}")]
	private static partial void LogNonInteractiveAbort(ILogger logger, string message);

	[LoggerMessage(Level = LogLevel.Information, Message = "Disconnected from run '{RunId}' (server-side run continues)")]
	private static partial void LogDisconnected(ILogger logger, string runId);
}
