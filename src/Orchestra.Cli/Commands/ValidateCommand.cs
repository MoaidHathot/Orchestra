using System.ComponentModel;
using System.Text.Json;
using Orchestra.Engine;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="ValidateCommand"/>.
/// </summary>
public sealed class ValidateSettings : CommandSettings
{
	[CommandArgument(0, "<PATH>")]
	[Description("Orchestration file (.json or .yaml) to validate.")]
	public string Path { get; set; } = string.Empty;

	[CommandOption("--format <FORMAT>")]
	[Description("Output format: 'text' (default) or 'json'.")]
	[DefaultValue("text")]
	public string Format { get; set; } = "text";

	public override ValidationResult Validate()
	{
		if (!string.Equals(Format, "text", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(Format, "json", StringComparison.OrdinalIgnoreCase))
		{
			return ValidationResult.Error($"Invalid --format '{Format}' (expected text or json).");
		}

		return ValidationResult.Success();
	}
}

/// <summary>
/// <c>orchestra validate</c> — parse an orchestration file and check its template expressions
/// without running it, registering it, or contacting a server.
/// </summary>
/// <remarks>
/// Runs the same two gates the executor applies before a run starts: the real
/// <see cref="OrchestrationParser"/> and <see cref="TemplateExpressionValidator"/>. That makes it
/// the deterministic counterpart to an agent reviewing its own output — a generated orchestration
/// can be machine-checked instead of trusted. Exits 0 when valid, 1 when invalid, 2 when the file
/// is missing or unreadable, so a Script step can branch on the exit code.
/// </remarks>
public sealed class ValidateCommand : Command<ValidateSettings>
{
	/// <summary>Exit code when the file itself could not be read.</summary>
	public const int FileErrorExitCode = 2;

	private readonly IAnsiConsole _console;

	/// <summary>
	/// Spectre resolves <see cref="IAnsiConsole"/> from the command app's type registrar, which
	/// is what lets tests capture this command's output instead of it going to the real console.
	/// </summary>
	public ValidateCommand(IAnsiConsole console) => _console = console;

	public override int Execute(CommandContext context, ValidateSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		var path = System.IO.Path.GetFullPath(settings.Path);
		var json = string.Equals(settings.Format, "json", StringComparison.OrdinalIgnoreCase);

		if (!File.Exists(path))
		{
			Report(_console, json, path, valid: false, errors: [$"File not found: {path}"]);
			return FileErrorExitCode;
		}

		Orchestration orchestration;
		try
		{
			orchestration = OrchestrationParser.ParseOrchestrationFile(path, availableMcps: []);
		}
		catch (Exception ex)
		{
			// A parse failure is a validation failure, not a tooling error: the file is the
			// thing under test, so report it through the same channel as expression errors.
			Report(_console, json, path, valid: false, errors: [ex.Message]);
			return 1;
		}

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);
		if (result.IsValid)
		{
			Report(_console, json, path, valid: true, errors: [], orchestration: orchestration);
			return 0;
		}

		var errors = result.Errors.Select(FormatError).ToArray();
		Report(_console, json, path, valid: false, errors, orchestration);
		return 1;
	}

	private static string FormatError(TemplateValidationError error)
	{
		var location = (error.StepName, error.FieldName) switch
		{
			(not null, not null) => $"[step '{error.StepName}', field '{error.FieldName}'] ",
			(not null, null) => $"[step '{error.StepName}'] ",
			(null, not null) => $"[field '{error.FieldName}'] ",
			_ => string.Empty,
		};

		var expression = error.Expression is not null ? $" (expression: {error.Expression})" : string.Empty;
		return $"{location}{error.Message}{expression}";
	}

	private static void Report(
		IAnsiConsole console,
		bool json,
		string path,
		bool valid,
		IReadOnlyList<string> errors,
		Orchestration? orchestration = null)
	{
		if (json)
		{
			console.WriteLine(JsonSerializer.Serialize(
				new
				{
					valid,
					path,
					name = orchestration?.Name,
					stepCount = orchestration?.Steps.Length,
					errors,
				},
				new JsonSerializerOptions { WriteIndented = true }));
			return;
		}

		if (valid)
		{
			var name = orchestration?.Name ?? System.IO.Path.GetFileName(path);
			var steps = orchestration?.Steps.Length ?? 0;
			console.MarkupLine($"[green]valid[/]  {Markup.Escape(name)} ({steps} steps)  {Markup.Escape(path)}");
			return;
		}

		console.MarkupLine($"[red]invalid[/]  {Markup.Escape(path)}");
		foreach (var error in errors)
			console.MarkupLine($"  [red]-[/] {Markup.Escape(error)}");
	}
}
