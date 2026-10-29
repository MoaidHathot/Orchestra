using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.OpenCode;

/// <summary>Owned, temporary V2 configuration. Explicit skills stay at their original paths.</summary>
internal sealed record OpenCodeStepWorkspace
{
	public string? ConfigFilePath { get; init; }
	public string? WorkingDirectory { get; init; }
	public IReadOnlyList<string> StagedSkillPaths { get; init; } = [];
	public string? CreatedWorkingDirectory { get; init; }

	public void Cleanup(ILogger logger)
	{
		try
		{ if (ConfigFilePath is not null) File.Delete(ConfigFilePath); }
		catch (Exception ex) { OpenCodeWorkspaceBuilder.LogCleanupError(logger, ex, ConfigFilePath); }
	}
}

internal static partial class OpenCodeWorkspaceBuilder
{
	[LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode: failed to clean up workspace artifact {Path}")]
	internal static partial void LogCleanupError(ILogger logger, Exception ex, string? path);

	public static OpenCodeStepWorkspace Prepare(
		IReadOnlyDictionary<string, object>? config,
		string? stepWorkingDirectory,
		IReadOnlyList<string> skillDirectories,
		string? artifactDirectory)
	{
		var artifact = !string.IsNullOrWhiteSpace(artifactDirectory) && Directory.Exists(artifactDirectory)
			? artifactDirectory : Path.GetTempPath();
		var resolved = config is null ? new Dictionary<string, object>() : new Dictionary<string, object>(config);
		if (skillDirectories.Count > 0)
			resolved["skills"] = skillDirectories.ToArray();
		string? configFile = null;
		if (resolved.Count > 0)
		{
			configFile = Path.Combine(artifact, $"opencode-config-{Guid.NewGuid():N}.json");
			File.WriteAllText(configFile, JsonSerializer.Serialize(resolved, OpenCodeJson.Options));
		}
		return new OpenCodeStepWorkspace { ConfigFilePath = configFile, WorkingDirectory = stepWorkingDirectory };
	}
}
