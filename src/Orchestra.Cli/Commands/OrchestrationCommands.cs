using System.ComponentModel;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// list
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Settings for <see cref="ListCommand"/>. Adds client-side narrowing on top of the
/// server's full-registry response — see <see cref="OrchestrationFilter"/> for semantics.
/// </summary>
public sealed class ListSettings : JsonOutputSettings
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

	public override Spectre.Console.ValidationResult Validate()
	{
		if (Enabled && Disabled)
		{
			return Spectre.Console.ValidationResult.Error("Cannot use --enabled and --disabled together.");
		}
		return Spectre.Console.ValidationResult.Success();
	}

	internal OrchestrationFilter.Criteria ToCriteria() => new(
		Filter: Filter,
		Tags: Tags,
		Enabled: Enabled ? true : Disabled ? false : null);
}

public sealed class ListCommand : AsyncCommand<ListSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ListSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var raw = await client.ListOrchestrationsAsync();
		var filtered = OrchestrationFilter.Apply(raw, settings.ToCriteria());
		OutputWriter.Write(filtered, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// get <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class GetSettings : JsonOutputSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Orchestration ID (or declared name)")]
	public string Id { get; set; } = string.Empty;
}

public sealed class GetCommand : AsyncCommand<GetSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, GetSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.GetOrchestrationAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// register <path>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RegisterSettings : JsonOutputSettings
{
	[CommandArgument(0, "<PATH>")]
	[Description("Path to an orchestration .json/.yaml file")]
	public string Path { get; set; } = string.Empty;
}

public sealed class RegisterCommand : AsyncCommand<RegisterSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RegisterSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.RegisterOrchestrationAsync(settings.Path);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// remove <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RemoveSettings : JsonOutputSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Orchestration ID to remove")]
	public string Id { get; set; } = string.Empty;
}

public sealed class RemoveCommand : AsyncCommand<RemoveSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RemoveSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.RemoveOrchestrationAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// scan <directory>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ScanSettings : JsonOutputSettings
{
	[CommandArgument(0, "<DIRECTORY>")]
	[Description("Directory to scan for orchestration files")]
	public string Directory { get; set; } = string.Empty;
}

public sealed class ScanCommand : AsyncCommand<ScanSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ScanSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.ScanDirectoryAsync(settings.Directory);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// enable <id> / disable <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EnableDisableSettings : JsonOutputSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Orchestration ID")]
	public string Id { get; set; } = string.Empty;
}

public sealed class EnableCommand : AsyncCommand<EnableDisableSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, EnableDisableSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.EnableOrchestrationAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

public sealed class DisableCommand : AsyncCommand<EnableDisableSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, EnableDisableSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.DisableOrchestrationAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}
