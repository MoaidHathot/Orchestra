using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Orchestra.Client.Run;

/// <summary>
/// Default observer for `orchestra run`: shows step transitions, HITL prompts, and a
/// terminal summary. Suppresses content-deltas, traces, and other firehose events
/// (those go to <see cref="VerboseRunObserver"/>).
/// </summary>
public sealed partial class ConsoleRunObserver : IRunObserver
{
	private readonly IAnsiConsole _console;
	private readonly ILogger<ConsoleRunObserver> _logger;

	public ConsoleRunObserver(IAnsiConsole console, ILogger<ConsoleRunObserver> logger)
	{
		_console = console;
		_logger = logger;
	}

	public void OnExecutionStarted(string executionId)
	{
		LogExecutionStarted(_logger, executionId);
	}

	public void OnRunContext(string orchestrationName, string runId)
	{
		_console.MarkupLine($"[bold]Run started:[/] [cyan]{Markup.Escape(orchestrationName)}[/] / [dim]{Markup.Escape(runId)}[/]");
		_console.WriteLine();
		LogRunContext(_logger, orchestrationName, runId);
	}

	public void OnStepStarted(string stepName)
	{
		_console.MarkupLine($"[grey]\u25b6[/] [cyan]{Markup.Escape(stepName)}[/] [grey]started[/]");
	}

	public void OnStepCompleted(string stepName)
	{
		_console.MarkupLine($"[green]\u2713[/] [cyan]{Markup.Escape(stepName)}[/] [grey]completed[/]");
	}

	public void OnStepError(string stepName, string error)
	{
		_console.MarkupLine($"[red]\u2717[/] [cyan]{Markup.Escape(stepName)}[/] [red]error:[/] {Markup.Escape(error)}");
	}

	public void OnStepCancelled(string stepName)
	{
		_console.MarkupLine($"[yellow]\u25cb[/] [cyan]{Markup.Escape(stepName)}[/] [yellow]cancelled[/]");
	}

	public void OnStepSkipped(string stepName, string reason)
	{
		_console.MarkupLine($"[grey]\u2022[/] [cyan]{Markup.Escape(stepName)}[/] [grey]skipped: {Markup.Escape(reason)}[/]");
	}

	public void OnAwaitingInput(AwaitingInputInfo info)
	{
		_console.WriteLine();
		_console.MarkupLine($"[yellow]\u25cf[/] [cyan]{Markup.Escape(info.StepName)}[/] [yellow]awaiting input[/]");
		_console.WriteLine();

		// Indent the prompt body for readability.
		foreach (var line in info.Prompt.Replace("\r\n", "\n").Split('\n'))
		{
			_console.MarkupLine($"  [white]{Markup.Escape(line)}[/]");
		}
		_console.WriteLine();
	}

	public void OnInputReceived(string stepName, string? choice, string? reply, string? respondedBy)
	{
		var who = string.IsNullOrEmpty(respondedBy) ? string.Empty : $" by [dim]{Markup.Escape(respondedBy)}[/]";
		var summary = (choice, reply) switch
		{
			(not null, null) => $"choice=[green]{Markup.Escape(choice)}[/]",
			(null, not null) => $"reply=[green]{Markup.Escape(Truncate(reply, 80))}[/]",
			(not null, not null) => $"choice=[green]{Markup.Escape(choice)}[/] reply=[green]{Markup.Escape(Truncate(reply, 80))}[/]",
			_ => "(empty)",
		};
		_console.MarkupLine($"[green]\u2192[/] [cyan]{Markup.Escape(stepName)}[/] response accepted{who}: {summary}");
	}

	public void OnInputTimeout(string stepName, string onTimeout)
	{
		_console.MarkupLine($"[yellow]!![/] [cyan]{Markup.Escape(stepName)}[/] input timed out [dim](onTimeout={Markup.Escape(onTimeout)})[/]");
	}

	public void OnOrchestrationDone(string status)
	{
		_console.WriteLine();
		var color = string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase) ? "green" : "yellow";
		_console.MarkupLine($"[{color}]Run finished:[/] {Markup.Escape(status)}");
	}

	public void OnOrchestrationCancelled(string? reason)
	{
		_console.WriteLine();
		_console.MarkupLine($"[yellow]Run cancelled[/]{(string.IsNullOrEmpty(reason) ? string.Empty : $": {Markup.Escape(reason)}")}");
	}

	public void OnOrchestrationError(string error)
	{
		_console.WriteLine();
		_console.MarkupLine($"[red]Run failed:[/] {Markup.Escape(error)}");
	}

	public void OnUnknownEvent(string eventType, JsonElement payload)
	{
		// Compact mode: ignore. Verbose mode handles its own rendering.
	}

	public void OnStreamInterrupted(string? reason)
	{
		_console.WriteLine();
		_console.MarkupLine($"[yellow]Stream interrupted[/]{(string.IsNullOrEmpty(reason) ? string.Empty : $": {Markup.Escape(reason)}")}");
	}

	private static string Truncate(string s, int max) =>
		s.Length <= max ? s : s[..max] + "\u2026";

	[LoggerMessage(Level = LogLevel.Debug, Message = "Execution started: {ExecutionId}")]
	private static partial void LogExecutionStarted(ILogger logger, string executionId);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Run context received: orchestration={OrchestrationName} runId={RunId}")]
	private static partial void LogRunContext(ILogger logger, string orchestrationName, string runId);
}
