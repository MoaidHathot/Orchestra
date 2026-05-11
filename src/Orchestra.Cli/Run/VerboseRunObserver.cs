using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Orchestra.Cli.Run;

/// <summary>
/// Observer that prints every event as a single JSON line. Used when <c>--verbose</c> /
/// <c>-V</c> is passed. Useful for scripting or debugging the wire format.
/// </summary>
public sealed class VerboseRunObserver : IRunObserver
{
	private readonly IAnsiConsole _console;
	private readonly ILogger<VerboseRunObserver> _logger;
	private readonly ConsoleRunObserver _compact;

	public VerboseRunObserver(IAnsiConsole console, ILogger<VerboseRunObserver> logger, ConsoleRunObserver compact)
	{
		_console = console;
		_logger = logger;
		_compact = compact;
	}

	public void OnExecutionStarted(string executionId) => _compact.OnExecutionStarted(executionId);
	public void OnRunContext(string orchestrationName, string runId) => _compact.OnRunContext(orchestrationName, runId);
	public void OnStepStarted(string stepName) => _compact.OnStepStarted(stepName);
	public void OnStepCompleted(string stepName) => _compact.OnStepCompleted(stepName);
	public void OnStepError(string stepName, string error) => _compact.OnStepError(stepName, error);
	public void OnStepCancelled(string stepName) => _compact.OnStepCancelled(stepName);
	public void OnStepSkipped(string stepName, string reason) => _compact.OnStepSkipped(stepName, reason);
	public void OnAwaitingInput(AwaitingInputInfo info) => _compact.OnAwaitingInput(info);
	public void OnInputReceived(string stepName, string? choice, string? reply, string? respondedBy)
		=> _compact.OnInputReceived(stepName, choice, reply, respondedBy);
	public void OnInputTimeout(string stepName, string onTimeout) => _compact.OnInputTimeout(stepName, onTimeout);
	public void OnOrchestrationDone(string status) => _compact.OnOrchestrationDone(status);
	public void OnOrchestrationCancelled(string? reason) => _compact.OnOrchestrationCancelled(reason);
	public void OnOrchestrationError(string error) => _compact.OnOrchestrationError(error);
	public void OnStreamInterrupted(string? reason) => _compact.OnStreamInterrupted(reason);

	public void OnUnknownEvent(string eventType, JsonElement payload)
	{
		_console.MarkupLine($"[grey]{Markup.Escape(eventType)}[/] [dim]{Markup.Escape(CompactJson(payload))}[/]");
	}

	private static string CompactJson(JsonElement payload)
	{
		try
		{
			return JsonSerializer.Serialize(payload, s_compactJsonOptions);
		}
		catch (JsonException)
		{
			return payload.ToString();
		}
	}

	private static readonly JsonSerializerOptions s_compactJsonOptions = new() { WriteIndented = false };
}
