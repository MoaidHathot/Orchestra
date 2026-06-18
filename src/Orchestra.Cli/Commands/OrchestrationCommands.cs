using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// list
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Settings for <see cref="ListCommand"/>. Adds client-side narrowing on top of the
/// server's full-registry response — see <see cref="OrchestrationFilter"/> for semantics.
/// </summary>
public sealed class ListSettings : ManagedCommandSettings
{
	[CommandOption("-f|--filter <TEXT>")]
	[Description("Substring (case-insensitive) match on name, description, or path")]
	public string? Filter { get; set; }

	[CommandOption("-t|--tag <TAG>")]
	[Description("Only include orchestrations carrying ALL of the given tag(s). Repeat to AND.")]
	public string[] Tags { get; set; } = [];

	[CommandOption("--enabled")]
	[Description("Only include orchestrations whose trigger is enabled")]
	public bool Enabled { get; set; }

	[CommandOption("--disabled")]
	[Description("Only include orchestrations whose trigger is disabled")]
	public bool Disabled { get; set; }

	public override ValidationResult Validate()
	{
		if (Enabled && Disabled)
		{
			return ValidationResult.Error("Cannot use --enabled and --disabled together.");
		}
		return base.Validate();
	}

	internal OrchestrationFilter.Criteria ToCriteria() => new(
		Filter: Filter,
		Tags: Tags,
		Enabled: Enabled ? true : Disabled ? false : null);
}

public sealed class ListCommand : AsyncCommand<ListSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ListSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var raw = await client.ListOrchestrationsAsync();
			var filtered = OrchestrationFilter.Apply(raw, settings.ToCriteria());
			OutputWriter.Write(filtered, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// get <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class GetSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Orchestration ID (or declared name)")]
	public string Id { get; set; } = string.Empty;
}

public sealed class GetCommand : AsyncCommand<GetSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, GetSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.GetOrchestrationAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// register <path>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RegisterSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<PATH>")]
	[Description("Path to an orchestration .json/.yaml file")]
	public string Path { get; set; } = string.Empty;
}

public sealed class RegisterCommand : AsyncCommand<RegisterSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RegisterSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.RegisterOrchestrationAsync(settings.Path);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// remove <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RemoveSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Orchestration ID to remove")]
	public string Id { get; set; } = string.Empty;
}

public sealed class RemoveCommand : AsyncCommand<RemoveSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RemoveSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.RemoveOrchestrationAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// scan <directory>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ScanSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<DIRECTORY>")]
	[Description("Directory to scan for orchestration files")]
	public string Directory { get; set; } = string.Empty;
}

public sealed class ScanCommand : AsyncCommand<ScanSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ScanSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.ScanDirectoryAsync(settings.Directory);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// enable <id> / disable <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EnableDisableSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Orchestration ID")]
	public string Id { get; set; } = string.Empty;
}

public sealed class EnableCommand : AsyncCommand<EnableDisableSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, EnableDisableSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.EnableOrchestrationAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class DisableCommand : AsyncCommand<EnableDisableSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, EnableDisableSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.DisableOrchestrationAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}
