using Orchestra.Exec;
using Spectre.Console;

// Thin entry point: delegate to the testable ExecProgram and translate uncaught failures
// into a non-zero exit code with a friendly message.
try
{
	return await ExecProgram.RunAsync(args);
}
catch (Exception ex)
{
	AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
	return ExecProgram.LaunchErrorExitCode;
}

// Exposed so WebApplicationFactory-style integration tests can reference the entry assembly.
public partial class Program { }
