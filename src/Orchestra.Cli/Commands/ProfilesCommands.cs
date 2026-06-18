using System.ComponentModel;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// profiles list
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProfilesListCommand : AsyncCommand<ManagedCommandSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ManagedCommandSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.ListProfilesAsync();
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// profiles get|activate|deactivate|delete <id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProfileIdSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Profile ID")]
	public string Id { get; set; } = string.Empty;
}

public sealed class ProfilesGetCommand : AsyncCommand<ProfileIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ProfileIdSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.GetProfileAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class ProfilesActivateCommand : AsyncCommand<ProfileIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ProfileIdSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.ActivateProfileAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class ProfilesDeactivateCommand : AsyncCommand<ProfileIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ProfileIdSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.DeactivateProfileAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class ProfilesDeleteCommand : AsyncCommand<ProfileIdSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ProfileIdSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.DeleteProfileAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}
