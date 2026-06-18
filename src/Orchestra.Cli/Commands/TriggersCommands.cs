using System.ComponentModel;
using Orchestra.Client;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// triggers list
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TriggersListCommand : AsyncCommand<ManagedCommandSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ManagedCommandSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.ListTriggersAsync();
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// triggers enable <id> / triggers disable <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TriggerIdSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Trigger ID (typically the orchestration ID)")]
	public string Id { get; set; } = string.Empty;
}

public sealed class TriggersEnableCommand : AsyncCommand<TriggerIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TriggerIdSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.EnableTriggerAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class TriggersDisableCommand : AsyncCommand<TriggerIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TriggerIdSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.DisableTriggerAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// triggers fire <id> [--param k=v ...]
//
// Live-runtime verb: firing a trigger starts a run, which only makes sense against a running
// server that will actually execute (and keep executing) it — so this stays server-required and
// does NOT inherit the managed connect-or-spawn mode.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TriggersFireSettings : JsonOutputSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Trigger ID to fire")]
	public string Id { get; set; } = string.Empty;

	[CommandOption("--param <KEY=VALUE>")]
	[Description("Repeated runtime parameter for the launched run")]
	public string[] Params { get; set; } = [];
}

public sealed class TriggersFireCommand : AsyncCommand<TriggersFireSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TriggersFireSettings settings)
		=> await LiveServerCommand.RunAsync(settings, "triggers fire", async client =>
		{
			var result = await client.FireTriggerAsync(settings.Id, ParameterParser.Parse(settings.Params));
			OutputWriter.Write(result, settings.Format);
		});
}
