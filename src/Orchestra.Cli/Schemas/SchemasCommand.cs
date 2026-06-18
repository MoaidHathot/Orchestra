namespace Orchestra.Tool;

/// <summary>
/// Implements the <c>orchestra schemas</c> CLI command, which copies the bundled
/// JSON schemas (orchestration, mcp, services) into a user-chosen directory so
/// editors can resolve <c>$schema</c> references locally without depending on
/// network access or the Orchestra git repository.
/// </summary>
public static class SchemasCommand
{
	public const string DefaultRelativeOutputDirectory = ".orchestra/schemas";

	private static readonly string[] s_schemaFileNames =
	[
		"orchestration.schema.json",
		"orchestra.mcp.schema.json",
		"orchestra.services.schema.json",
	];

	public static int Execute(
		string[] args,
		TextWriter stdout,
		TextWriter stderr,
		string schemasSourceDirectory,
		string workingDirectory)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(stdout);
		ArgumentNullException.ThrowIfNull(stderr);
		ArgumentNullException.ThrowIfNull(schemasSourceDirectory);
		ArgumentNullException.ThrowIfNull(workingDirectory);

		string? outputArg = null;
		var force = false;
		var showHelp = false;

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "-h":
				case "--help":
					showHelp = true;
					break;
				case "-f":
				case "--force":
					force = true;
					break;
				case "-o":
				case "--output":
					if (i + 1 >= args.Length)
					{
						stderr.WriteLine("Error: --output requires a directory path argument.");
						return 2;
					}
					outputArg = args[++i];
					break;
				default:
					stderr.WriteLine($"Error: unknown argument '{args[i]}'.");
					PrintUsage(stderr);
					return 2;
			}
		}

		if (showHelp)
		{
			PrintUsage(stdout);
			return 0;
		}

		if (!Directory.Exists(schemasSourceDirectory))
		{
			stderr.WriteLine($"Error: bundled schemas directory not found at '{schemasSourceDirectory}'.");
			stderr.WriteLine("This usually means the Orchestra installation is incomplete; try reinstalling the tool.");
			return 1;
		}

		var outputDirectory = ResolveOutputDirectory(outputArg, workingDirectory);

		try
		{
			Directory.CreateDirectory(outputDirectory);
		}
		catch (Exception ex)
		{
			stderr.WriteLine($"Error: could not create output directory '{outputDirectory}': {ex.Message}");
			return 1;
		}

		var copied = 0;
		var skipped = 0;

		foreach (var fileName in s_schemaFileNames)
		{
			var sourcePath = Path.Combine(schemasSourceDirectory, fileName);
			if (!File.Exists(sourcePath))
			{
				stderr.WriteLine($"Error: bundled schema file missing: '{sourcePath}'.");
				return 1;
			}

			var targetPath = Path.Combine(outputDirectory, fileName);
			if (File.Exists(targetPath) && !force)
			{
				stdout.WriteLine($"Skipped (already exists): {targetPath}");
				skipped++;
				continue;
			}

			try
			{
				File.Copy(sourcePath, targetPath, overwrite: true);
				stdout.WriteLine($"Wrote: {targetPath}");
				copied++;
			}
			catch (Exception ex)
			{
				stderr.WriteLine($"Error: failed to copy '{sourcePath}' to '{targetPath}': {ex.Message}");
				return 1;
			}
		}

		stdout.WriteLine();
		stdout.WriteLine($"Done. {copied} written, {skipped} skipped.");
		if (skipped > 0)
			stdout.WriteLine("Pass --force to overwrite existing files.");

		stdout.WriteLine();
		stdout.WriteLine("Reference these in your orchestration files:");
		var relativeForExample = GetRelativePathOrFull(workingDirectory, outputDirectory);
		stdout.WriteLine($"  YAML: # yaml-language-server: $schema={relativeForExample}/orchestration.schema.json");
		stdout.WriteLine($"  JSON: \"$schema\": \"{relativeForExample}/orchestration.schema.json\"");

		return 0;
	}

	private static string ResolveOutputDirectory(string? outputArg, string workingDirectory)
	{
		if (string.IsNullOrWhiteSpace(outputArg))
			return Path.GetFullPath(Path.Combine(workingDirectory, DefaultRelativeOutputDirectory));

		return Path.IsPathRooted(outputArg)
			? Path.GetFullPath(outputArg)
			: Path.GetFullPath(Path.Combine(workingDirectory, outputArg));
	}

	private static string GetRelativePathOrFull(string from, string to)
	{
		try
		{
			var relative = Path.GetRelativePath(from, to);
			return relative.Replace('\\', '/');
		}
		catch
		{
			return to;
		}
	}

	private static void PrintUsage(TextWriter writer)
	{
		writer.WriteLine("Usage: orchestra schemas [--output <dir>] [--force]");
		writer.WriteLine();
		writer.WriteLine("Copies the JSON schemas (orchestration, mcp, services) bundled with the");
		writer.WriteLine("Orchestra tool into a local directory so editors can validate orchestration");
		writer.WriteLine("files using $schema references.");
		writer.WriteLine();
		writer.WriteLine("Options:");
		writer.WriteLine($"  -o, --output <dir>   Target directory (default: ./{DefaultRelativeOutputDirectory})");
		writer.WriteLine("  -f, --force          Overwrite existing schema files");
		writer.WriteLine("  -h, --help           Show this help");
	}
}
