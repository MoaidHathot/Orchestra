using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// runs list
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RunsListSettings : ManagedCommandSettings
{
	[CommandOption("--limit <N>")]
	[Description("Maximum number of recent runs to fetch")]
	[DefaultValue(20)]
	public int Limit { get; set; } = 20;

	[CommandOption("--favorites")]
	[Description("Show only runs marked as favorites")]
	public bool Favorites { get; set; }

	[CommandOption("--tag <NAME>")]
	[Description("Show only runs carrying any of these annotation tags (repeatable)")]
	public string[] Tags { get; set; } = [];
}

public sealed class RunsListCommand : AsyncCommand<RunsListSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunsListSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var tags = settings.Tags.Length > 0 ? string.Join(",", settings.Tags) : null;
			var result = await client.ListRunsAsync(settings.Limit, settings.Favorites ? true : null, tags);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// runs get <name> <run-id> / runs delete <name> <run-id>
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Identifies a single run. Not sealed: the delete and annotate commands extend it with
/// their own options.
/// </summary>
public class RunRefSettings : ManagedCommandSettings
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
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.GetRunAsync(settings.OrchestrationName, settings.RunId);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class RunsDeleteSettings : RunRefSettings
{
	[CommandOption("--force")]
	[Description("Delete even if the run is marked as a favorite")]
	public bool Force { get; set; }
}

public sealed class RunsDeleteCommand : AsyncCommand<RunsDeleteSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunsDeleteSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.DeleteRunAsync(settings.OrchestrationName, settings.RunId, settings.Force);
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// runs favorite / unfavorite / annotate / annotations
//
// Run records are immutable, so this curation lives in its own store keyed by run
// id. A title is what makes a machine-named run findable again; favorites are also
// exempt from retention deletion.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RunsFavoriteCommand : AsyncCommand<RunRefSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunRefSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.FavoriteRunAsync(settings.OrchestrationName, settings.RunId);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class RunsUnfavoriteCommand : AsyncCommand<RunRefSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunRefSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.UnfavoriteRunAsync(settings.OrchestrationName, settings.RunId);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class RunsAnnotateSettings : RunRefSettings
{
	[CommandOption("--title <TEXT>")]
	[Description("Human-readable name for the run")]
	public string? Title { get; set; }

	[CommandOption("--tag <NAME>")]
	[Description("Annotation tag (repeatable). Replaces the existing tag set")]
	public string[] Tags { get; set; } = [];

	[CommandOption("--note <TEXT>")]
	[Description("Free-form note - caveats, findings, or why the run was kept")]
	public string? Note { get; set; }

	[CommandOption("--favorite")]
	[Description("Also mark the run as a favorite")]
	public bool Favorite { get; set; }

	[CommandOption("--clear")]
	[Description("Remove the annotation entirely")]
	public bool Clear { get; set; }
}

public sealed class RunsAnnotateCommand : AsyncCommand<RunsAnnotateSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunsAnnotateSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			if (settings.Clear)
			{
				var cleared = await client.RemoveRunAnnotationAsync(settings.OrchestrationName, settings.RunId);
				OutputWriter.Write(cleared, settings.Format);
				return;
			}

			// PATCH, not PUT: supplying --title alone must not wipe existing tags or the note.
			var result = await client.PatchRunAnnotationAsync(
				settings.OrchestrationName,
				settings.RunId,
				favorite: settings.Favorite ? true : null,
				title: settings.Title,
				tags: settings.Tags.Length > 0 ? settings.Tags : null,
				note: settings.Note);

			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class RunsAnnotationsSettings : ManagedCommandSettings
{
	[CommandOption("--orphans")]
	[Description("Show only annotations whose run no longer exists")]
	public bool Orphans { get; set; }
}

public sealed class RunsAnnotationsCommand : AsyncCommand<RunsAnnotationsSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunsAnnotationsSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.ListRunAnnotationsAsync(settings.Orphans);
			OutputWriter.Write(result, settings.Format);
		});
}

public sealed class RunsAnnotationsPruneCommand : AsyncCommand<ManagedCommandSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, ManagedCommandSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var result = await client.PruneRunAnnotationsAsync();
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// runs export
//
// A run's artifacts live in two places: the execution folder, and the temp store
// where steps write files via orchestra_save_file. The latter is usually where the
// real deliverable is — a step producing a large document saves it and returns only
// a summary inline. Export gathers both.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RunsExportSettings : ManagedCommandSettings
{
	[CommandArgument(0, "[ORCHESTRATION]")]
	[Description("Orchestration name. Omit when selecting runs with --tag or --favorites")]
	public string? OrchestrationName { get; set; }

	[CommandArgument(1, "[RUN-ID]")]
	[Description("Run ID. Omit when selecting runs with --tag or --favorites")]
	public string? RunId { get; set; }

	[CommandOption("--out <DIR>")]
	[Description("Directory to write the export into (default: current directory)")]
	public string? Out { get; set; }

	// `--format` already means the CLI's output shape (json/table) on every managed command,
	// so the export shape gets its own flag rather than overloading it.
	[CommandOption("--as|--export-format <SHAPE>")]
	[Description("What to export: bundle (default), report, or data")]
	[DefaultValue("bundle")]
	public string ExportFormat { get; set; } = "bundle";

	[CommandOption("--zip")]
	[Description("Write a .zip archive instead of a directory")]
	public bool Zip { get; set; }

	[CommandOption("--favorites")]
	[Description("Export every favorited run")]
	public bool Favorites { get; set; }

	[CommandOption("--tag <NAME>")]
	[Description("Export every run carrying any of these tags (repeatable)")]
	public string[] Tags { get; set; } = [];

	[CommandOption("--limit <N>")]
	[Description("Maximum number of runs to export in bulk mode")]
	[DefaultValue(100)]
	public int Limit { get; set; } = 100;

	public override Spectre.Console.ValidationResult Validate()
	{
		var bulk = Favorites || Tags.Length > 0;
		var single = !string.IsNullOrWhiteSpace(OrchestrationName) && !string.IsNullOrWhiteSpace(RunId);

		if (bulk && single)
			return Spectre.Console.ValidationResult.Error("Specify either a single run or a --tag/--favorites selector, not both.");
		if (!bulk && !single)
			return Spectre.Console.ValidationResult.Error("Specify <ORCHESTRATION> <RUN-ID>, or select runs with --tag/--favorites.");

		var allowed = new[] { "bundle", "report", "data" };
		if (!allowed.Contains(ExportFormat, StringComparer.OrdinalIgnoreCase))
			return Spectre.Console.ValidationResult.Error($"Unknown --as '{ExportFormat}'. Use bundle, report, or data.");

		return Spectre.Console.ValidationResult.Success();
	}
}

public sealed class RunsExportCommand : AsyncCommand<RunsExportSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunsExportSettings settings)
		=> await ManagedSession.RunAsync(settings, async client =>
		{
			var outDir = Path.GetFullPath(settings.Out ?? Directory.GetCurrentDirectory());
			Directory.CreateDirectory(outDir);

			var targets = await ResolveTargetsAsync(client, settings);
			if (targets.Count == 0)
			{
				OutputWriter.Write(
					JsonSerializer.SerializeToElement(new { exported = 0, runs = Array.Empty<object>() }),
					settings.Format);
				return;
			}

			var exported = new List<object>();
			foreach (var (orchestration, runId) in targets)
			{
				var (content, fileName, isArchive) =
					await client.ExportRunAsync(orchestration, runId, settings.ExportFormat);

				var path = WriteExport(outDir, content, fileName, isArchive, settings.Zip);
				exported.Add(new { orchestrationName = orchestration, runId, path });
			}

			OutputWriter.Write(
				JsonSerializer.SerializeToElement(new { exported = exported.Count, runs = exported }),
				settings.Format);
		});

	private static async Task<List<(string Orchestration, string RunId)>> ResolveTargetsAsync(
		Orchestra.Client.OrchestraClient client, RunsExportSettings settings)
	{
		if (!string.IsNullOrWhiteSpace(settings.OrchestrationName) && !string.IsNullOrWhiteSpace(settings.RunId))
			return [(settings.OrchestrationName, settings.RunId)];

		// Bulk selection reuses the history filters so `--tag`/`--favorites` mean exactly the
		// same thing here as they do in `runs list`.
		var tags = settings.Tags.Length > 0 ? string.Join(",", settings.Tags) : null;
		var listed = await client.ListRunsAsync(settings.Limit, settings.Favorites ? true : null, tags);

		var targets = new List<(string, string)>();
		if (listed.TryGetProperty("runs", out var runs))
		{
			foreach (var run in runs.EnumerateArray())
			{
				var name = run.TryGetProperty("orchestrationName", out var n) ? n.GetString() : null;
				var id = run.TryGetProperty("runId", out var r) ? r.GetString() : null;
				if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
					targets.Add((name, id));
			}
		}

		return targets;
	}

	/// <summary>
	/// Writes a downloaded export to disk. The server always streams an archive for bundle/data;
	/// unless <c>--zip</c> was asked for, it is expanded so the result is browsable.
	/// </summary>
	private static string WriteExport(string outDir, byte[] content, string fileName, bool isArchive, bool keepZip)
	{
		if (!isArchive)
		{
			var filePath = Path.Combine(outDir, fileName);
			File.WriteAllBytes(filePath, content);
			return filePath;
		}

		if (keepZip)
		{
			var zipPath = Path.Combine(outDir, fileName);
			File.WriteAllBytes(zipPath, content);
			return zipPath;
		}

		var folderName = Path.GetFileNameWithoutExtension(fileName);
		var destination = Path.Combine(outDir, folderName);
		if (Directory.Exists(destination))
			Directory.Delete(destination, recursive: true);
		Directory.CreateDirectory(destination);

		using var stream = new MemoryStream(content);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
		archive.ExtractToDirectory(destination, overwriteFiles: true);

		return destination;
	}
}
