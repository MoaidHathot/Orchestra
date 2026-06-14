using System.ComponentModel;
using Orchestra.Client;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// triggers list
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TriggersListCommand : AsyncCommand<JsonOutputSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, JsonOutputSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.ListTriggersAsync();
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// triggers enable <id> / triggers disable <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TriggerIdSettings : JsonOutputSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Trigger ID (typically the orchestration ID)")]
	public string Id { get; set; } = string.Empty;
}

public sealed class TriggersEnableCommand : AsyncCommand<TriggerIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TriggerIdSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.EnableTriggerAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

public sealed class TriggersDisableCommand : AsyncCommand<TriggerIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TriggerIdSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.DisableTriggerAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// triggers fire <id> [--param k=v ...]
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
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.FireTriggerAsync(settings.Id, ParameterParser.Parse(settings.Params));
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}
