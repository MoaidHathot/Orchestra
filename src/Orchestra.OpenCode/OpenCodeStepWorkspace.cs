using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.OpenCode;

/// <summary>
/// The on-disk artifacts prepared for a dedicated OpenCode step server: the generated
/// <c>opencode.json</c> file, the working directory the server runs in, and any skill folders
/// staged under <c>&lt;workingDir&gt;/.opencode/skills</c>. <see cref="Cleanup"/> removes
/// everything Orchestra created.
/// </summary>
internal sealed record OpenCodeStepWorkspace
{
	public string? ConfigFilePath { get; init; }
	public string? WorkingDirectory { get; init; }
	public IReadOnlyList<string> StagedSkillPaths { get; init; } = [];

	/// <summary>The temp working directory Orchestra created (when the step had no working directory); null otherwise.</summary>
	public string? CreatedWorkingDirectory { get; init; }

	public void Cleanup(ILogger logger)
	{
		TryDeleteFile(ConfigFilePath, logger);
		foreach (var path in StagedSkillPaths)
			TryDeleteDirectory(path, logger);
		TryDeleteDirectory(CreatedWorkingDirectory, logger);
	}

	private static void TryDeleteFile(string? path, ILogger logger)
	{
		if (string.IsNullOrEmpty(path))
			return;
		try { if (File.Exists(path)) File.Delete(path); }
		catch (Exception ex) { LogCleanupError(logger, ex, path); }
	}

	private static void TryDeleteDirectory(string? path, ILogger logger)
	{
		if (string.IsNullOrEmpty(path))
			return;
		try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
		catch (Exception ex) { LogCleanupError(logger, ex, path); }
	}

	private static void LogCleanupError(ILogger logger, Exception ex, string path)
		=> logger.LogDebug(ex, "OpenCode: failed to clean up workspace artifact {Path}", path);
}

/// <summary>
/// Materializes a step's OpenCode workspace: writes the generated config to a JSON file in the
/// run's artifact folder, and stages skill directories under the server's working directory so
/// OpenCode discovers them at <c>.opencode/skills/&lt;name&gt;/SKILL.md</c>.
/// </summary>
internal static class OpenCodeWorkspaceBuilder
{
	public static OpenCodeStepWorkspace Prepare(
		IReadOnlyDictionary<string, object>? config,
		string? stepWorkingDirectory,
		IReadOnlyList<string> skillDirectories,
		string? artifactDirectory)
	{
		var artifact = !string.IsNullOrWhiteSpace(artifactDirectory) && Directory.Exists(artifactDirectory)
			? artifactDirectory!
			: Path.GetTempPath();
		var id = Guid.NewGuid().ToString("N")[..12];

		string? configFile = null;
		if (config is { Count: > 0 })
		{
			configFile = Path.Combine(artifact, $"opencode-config-{id}.json");
			File.WriteAllText(configFile, JsonSerializer.Serialize(config, OpenCodeJson.Options));
		}

		var workingDir = string.IsNullOrWhiteSpace(stepWorkingDirectory) ? null : stepWorkingDirectory;
		string? createdWorkingDir = null;
		var staged = new List<string>();

		if (skillDirectories.Count > 0)
		{
			if (workingDir is null)
			{
				createdWorkingDir = Path.Combine(artifact, $"opencode-cwd-{id}");
				Directory.CreateDirectory(createdWorkingDir);
				workingDir = createdWorkingDir;
			}

			var skillsRoot = Path.Combine(workingDir, ".opencode", "skills");
			foreach (var skillDir in skillDirectories)
				StageSkill(skillDir, skillsRoot, staged);
		}

		return new OpenCodeStepWorkspace
		{
			ConfigFilePath = configFile,
			WorkingDirectory = workingDir,
			StagedSkillPaths = staged,
			CreatedWorkingDirectory = createdWorkingDir,
		};
	}

	private static void StageSkill(string skillDir, string skillsRoot, List<string> staged)
	{
		if (string.IsNullOrWhiteSpace(skillDir) || !Directory.Exists(skillDir))
			return;

		// An Orchestra skill directory either IS a skill (contains SKILL.md directly) or holds
		// multiple skill subfolders. Stage each as `.opencode/skills/<name>/`.
		if (File.Exists(Path.Combine(skillDir, "SKILL.md")))
		{
			CopyAsSkill(skillDir, DirectoryName(skillDir), skillsRoot, staged);
			return;
		}

		foreach (var sub in Directory.GetDirectories(skillDir))
		{
			if (File.Exists(Path.Combine(sub, "SKILL.md")))
				CopyAsSkill(sub, DirectoryName(sub), skillsRoot, staged);
		}
	}

	private static void CopyAsSkill(string source, string rawName, string skillsRoot, List<string> staged)
	{
		var name = OpenCodeConfigBuilder.Slugify(rawName);
		var dest = Path.Combine(skillsRoot, name);
		if (Directory.Exists(dest))
			return; // don't clobber a pre-existing skill in the working directory

		Directory.CreateDirectory(skillsRoot);
		CopyDirectory(source, dest);
		staged.Add(dest);
	}

	private static string DirectoryName(string path)
		=> Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var file in Directory.GetFiles(source))
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
		foreach (var dir in Directory.GetDirectories(source))
			CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
	}
}
