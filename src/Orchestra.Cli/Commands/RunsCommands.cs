using System.ComponentModel;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// runs list
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RunsListSettings : JsonOutputSettings
{
	[CommandOption("--limit <N>")]
	[Description("Maximum number of recent runs to fetch")]
	[DefaultValue(20)]
	public int Limit { get; set; } = 20;
}

public sealed class RunsListCommand : AsyncCommand<RunsListSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunsListSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.ListRunsAsync(settings.Limit);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// runs get <name> <run-id> / runs delete <name> <run-id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RunRefSettings : JsonOutputSettings
{
	[CommandArgument(0, "<ORCHESTRATION>")]
	[Description("Orchestration name (as listed by `orchestra list`)")]
	public string OrchestrationName { get; set; } = string.Empty;

	[CommandArgument(1, "<RUN-ID>")]
	[Description("Run ID (as listed by `orchestra runs list`)")]
	public string RunId { get; set; } = string.Empty;
}

public sealed class RunsGetCommand : AsyncCommand<RunRefSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunRefSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.GetRunAsync(settings.OrchestrationName, settings.RunId);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

public sealed class RunsDeleteCommand : AsyncCommand<RunRefSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunRefSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.DeleteRunAsync(settings.OrchestrationName, settings.RunId);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}
