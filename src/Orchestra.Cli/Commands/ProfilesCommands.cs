using System.ComponentModel;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// profiles list
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProfilesListCommand : AsyncCommand<JsonOutputSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, JsonOutputSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.ListProfilesAsync();
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// profiles get|activate|deactivate|delete <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProfileIdSettings : JsonOutputSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Profile ID")]
	public string Id { get; set; } = string.Empty;
}

public sealed class ProfilesGetCommand : AsyncCommand<ProfileIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ProfileIdSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.GetProfileAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

public sealed class ProfilesActivateCommand : AsyncCommand<ProfileIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ProfileIdSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.ActivateProfileAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

public sealed class ProfilesDeactivateCommand : AsyncCommand<ProfileIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ProfileIdSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.DeactivateProfileAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

public sealed class ProfilesDeleteCommand : AsyncCommand<ProfileIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ProfileIdSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.DeleteProfileAsync(settings.Id);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}
