using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Orchestra.Client.Run;

/// <summary>
/// Prompts the user via Spectre.Console:
/// <list type="bullet">
///   <item>If the wait has <c>choices</c>, presents a <see cref="SelectionPrompt{T}"/> followed by an optional comment text prompt.</item>
///   <item>If the wait is free-form (<c>choices</c> empty), presents a single <see cref="TextPrompt{T}"/> for the reply.</item>
/// </list>
/// The optional <c>--by</c> identifier is captured at construction time and applied to every response.
/// </summary>
public sealed partial class InteractiveHumanInputPrompter : IHumanInputPrompter
{
	private readonly IAnsiConsole _console;
	private readonly string? _respondedBy;
	private readonly ILogger<InteractiveHumanInputPrompter> _logger;

	public InteractiveHumanInputPrompter(IAnsiConsole console, string? respondedBy, ILogger<InteractiveHumanInputPrompter> logger)
	{
		_console = console;
		_respondedBy = respondedBy;
		_logger = logger;
	}

	public Task<HumanInputResponse> PromptAsync(AwaitingInputInfo info, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		LogPrompting(_logger, info.StepName, info.Choices.Count);

		string? choice = null;
		string? reply = null;

		if (info.Choices.Count > 0)
		{
			var selection = new SelectionPrompt<string>()
				.Title("[yellow]Choose:[/]")
				.AddChoices(info.Choices)
				.HighlightStyle(Style.Parse("cyan bold"));

			choice = _console.Prompt(selection);

			var commentPrompt = new TextPrompt<string>("[grey]Add a comment? (optional, blank to skip):[/]")
				.AllowEmpty();
			var comment = _console.Prompt(commentPrompt);
			if (!string.IsNullOrWhiteSpace(comment))
			{
				reply = comment;
			}
		}
		else
		{
			var replyPrompt = new TextPrompt<string>("[yellow]Reply:[/]");
			reply = _console.Prompt(replyPrompt);
		}

		_console.WriteLine();
		return Task.FromResult(new HumanInputResponse(choice, reply, _respondedBy));
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Prompting human for input on step '{StepName}' (choices={ChoicesCount})")]
	private static partial void LogPrompting(ILogger logger, string stepName, int choicesCount);
}
