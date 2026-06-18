using System.ComponentModel;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// tags list
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TagsListCommand : AsyncCommand<ManagedCommandSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ManagedCommandSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.ListTagsAsync();
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// tags get <orchestration-id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TagsGetSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<ORCHESTRATION-ID>")]
	[Description("Orchestration ID whose tags to fetch")]
	public string Id { get; set; } = string.Empty;
}

public sealed class TagsGetCommand : AsyncCommand<TagsGetSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TagsGetSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.GetOrchestrationTagsAsync(settings.Id);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// tags add <orchestration-id> <tag1,tag2,...>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TagsAddSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<ORCHESTRATION-ID>")]
	[Description("Orchestration ID to tag")]
	public string Id { get; set; } = string.Empty;

	[CommandArgument(1, "<TAGS>")]
	[Description("Comma-separated tags to add (e.g. \"prod,nightly\")")]
	public string Tags { get; set; } = string.Empty;
}

public sealed class TagsAddCommand : AsyncCommand<TagsAddSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TagsAddSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var tags = settings.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (tags.Length == 0)
			{
				throw new ArgumentException("At least one tag must be provided.");
			}
			var result = await client.AddTagsAsync(settings.Id, tags);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// tags remove <orchestration-id> <tag>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TagsRemoveSettings : ManagedCommandSettings
{
	[CommandArgument(0, "<ORCHESTRATION-ID>")]
	[Description("Orchestration ID")]
	public string Id { get; set; } = string.Empty;

	[CommandArgument(1, "<TAG>")]
	[Description("Single tag to remove")]
	public string Tag { get; set; } = string.Empty;
}

public sealed class TagsRemoveCommand : AsyncCommand<TagsRemoveSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, TagsRemoveSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.RemoveTagAsync(settings.Id, settings.Tag);
			OutputWriter.Write(result, settings.Format);
		});
}
