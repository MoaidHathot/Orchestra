using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Orchestra.Client.Run;

/// <summary>
/// Observer that prints only the bare minimum: HITL prompts (always) and the terminal summary.
/// Used when <c>--quiet</c> / <c>-q</c> is passed.
/// </summary>
public sealed class QuietRunObserver : IRunObserver
{
	private readonly IAnsiConsole _console;
	private readonly ILogger<QuietRunObserver> _logger;

	public QuietRunObserver(IAnsiConsole console, ILogger<QuietRunObserver> logger)
	{
		_console = console;
		_logger = logger;
	}

	public void OnExecutionStarted(string executionId) { }
	public void OnRunContext(string orchestrationName, string runId) { }
	public void OnStepStarted(string stepName) { }
	public void OnStepCompleted(string stepName) { }
	public void OnStepError(string stepName, string error) { }
	public void OnStepCancelled(string stepName) { }
	public void OnStepSkipped(string stepName, string reason) { }

	public void OnAwaitingInput(AwaitingInputInfo info)
	{
		// Always show HITL — even in quiet mode the user has to act.
		_console.MarkupLine($"[yellow]\u25cf[/] {Markup.Escape(info.StepName)}: awaiting input");
		foreach (var line in info.Prompt.Replace("\r\n", "\n").Split('\n'))
		{
			_console.MarkupLine($"  [white]{Markup.Escape(line)}[/]");
		}
	}

	public void OnInputReceived(string stepName, string? choice, string? reply, string? respondedBy) { }
	public void OnInputTimeout(string stepName, string onTimeout) { }

	public void OnOrchestrationDone(string status) =>
		_console.MarkupLine($"Run finished: {Markup.Escape(status)}");

	public void OnOrchestrationCancelled(string? reason) =>
		_console.MarkupLine($"Run cancelled{(string.IsNullOrEmpty(reason) ? string.Empty : $": {Markup.Escape(reason)}")}");

	public void OnOrchestrationError(string error) =>
		_console.MarkupLine($"[red]Run failed:[/] {Markup.Escape(error)}");

	public void OnUnknownEvent(string eventType, JsonElement payload) { }

	public void OnStreamInterrupted(string? reason) =>
		_console.MarkupLine($"[yellow]Stream interrupted[/]{(string.IsNullOrEmpty(reason) ? string.Empty : $": {Markup.Escape(reason)}")}");
}
