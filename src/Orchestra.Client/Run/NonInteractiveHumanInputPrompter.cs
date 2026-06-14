using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Orchestra.Client.Run;

/// <summary>
/// Used when the CLI cannot prompt: stdin is redirected (CI/pipes), or <c>--no-interactive</c>
/// was passed. Prints copy-pasteable instructions and throws <see cref="NonInteractiveAbortException"/>
/// so the top-level handler exits with code 2.
/// </summary>
public sealed partial class NonInteractiveHumanInputPrompter : IHumanInputPrompter
{
	private readonly IAnsiConsole _console;
	private readonly ILogger<NonInteractiveHumanInputPrompter> _logger;

	public NonInteractiveHumanInputPrompter(IAnsiConsole console, ILogger<NonInteractiveHumanInputPrompter> logger)
	{
		_console = console;
		_logger = logger;
	}

	public Task<HumanInputResponse> PromptAsync(AwaitingInputInfo info, CancellationToken cancellationToken)
	{
		LogNonInteractive(_logger, info.StepName);

		_console.WriteLine();
		_console.MarkupLine("[yellow]Awaiting input \u2014 stdin is not interactive.[/]");
		_console.MarkupLine("Run continues on the server. To respond:");
		_console.WriteLine();
		var cmd = BuildRespondCommand(info);
		_console.MarkupLine($"  [cyan]{Markup.Escape(cmd)}[/]");
		_console.WriteLine();

		throw new NonInteractiveAbortException(
			$"Run '{info.RunId}' is awaiting input on step '{info.StepName}'.");
	}

	private static string BuildRespondCommand(AwaitingInputInfo info)
	{
		var baseCmd = $"orchestra respond {Quote(info.OrchestrationName)} {Quote(info.RunId)} {Quote(info.StepName)}";
		if (info.Choices.Count > 0)
		{
			return $"{baseCmd} --choice <{string.Join("|", info.Choices)}>";
		}
		return $"{baseCmd} --reply \"...\"";
	}

	private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

	[LoggerMessage(Level = LogLevel.Information, Message = "HITL prompt arrived but stdin is non-interactive (step '{StepName}'). Aborting with exit code 2.")]
	private static partial void LogNonInteractive(ILogger logger, string stepName);
}
