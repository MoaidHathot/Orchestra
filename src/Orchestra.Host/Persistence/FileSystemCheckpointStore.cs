using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Host.Persistence;

/// <summary>
/// File-system backed checkpoint store for persisting orchestration execution checkpoints.
/// Layout:
///   {rootPath}/checkpoints/{orchestration-name}/{runId}/checkpoint.json
/// </summary>
public partial class FileSystemCheckpointStore : ICheckpointStore
{
	private readonly string _rootPath;
	private readonly JsonSerializerOptions _jsonOptions;
	private readonly ILogger<FileSystemCheckpointStore> _logger;

	public FileSystemCheckpointStore(string rootPath, ILogger<FileSystemCheckpointStore> logger)
	{
		_rootPath = Path.Combine(rootPath, "checkpoints");
		_logger = logger;
		_jsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Converters = { new JsonStringEnumConverter() }
		};

		Directory.CreateDirectory(_rootPath);
	}

	/// <summary>
	/// Gets the root path for checkpoints.
	/// </summary>
	public string RootPath => _rootPath;

	public async Task SaveCheckpointAsync(CheckpointData checkpoint, CancellationToken cancellationToken = default)
	{
		var dir = GetCheckpointDirectory(checkpoint.OrchestrationName, checkpoint.RunId);
		Directory.CreateDirectory(dir);

		var filePath = Path.Combine(dir, "checkpoint.json");
		var json = JsonSerializer.Serialize(checkpoint, _jsonOptions);

		// Write to temp file first, then move for atomicity
		var tempPath = filePath + ".tmp";
		await File.WriteAllTextAsync(tempPath, json, cancellationToken);

		// On Windows, File.Move with overwrite is atomic at the filesystem level
		File.Move(tempPath, filePath, overwrite: true);

		LogCheckpointSaved(checkpoint.OrchestrationName, checkpoint.RunId, checkpoint.CompletedSteps.Count);
	}

	public async Task<CheckpointData?> LoadCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
	{
		var filePath = GetCheckpointFilePath(orchestrationName, runId);

		if (!File.Exists(filePath))
			return null;

		try
		{
			var json = await File.ReadAllTextAsync(filePath, cancellationToken);
			return JsonSerializer.Deserialize<CheckpointData>(json, _jsonOptions);
		}
		catch (Exception ex)
		{
			LogCheckpointLoadFailed(ex, orchestrationName, runId);
			return null;
		}
	}

	public Task DeleteCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
	{
		var dir = GetCheckpointDirectory(orchestrationName, runId);

		if (Directory.Exists(dir))
		{
			try
			{
				Directory.Delete(dir, recursive: true);
				LogCheckpointDeleted(orchestrationName, runId);
			}
			catch (Exception ex)
			{
				LogCheckpointDeleteFailed(ex, orchestrationName, runId);
			}
		}

		// Clean up empty orchestration directory
		var orchestrationDir = Path.Combine(_rootPath, SanitizePath(orchestrationName));
		try
		{
			if (Directory.Exists(orchestrationDir) && !Directory.EnumerateFileSystemEntries(orchestrationDir).Any())
			{
				Directory.Delete(orchestrationDir);
			}
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to clean up empty orchestration directory '{Directory}'", orchestrationDir);
		}

		return Task.CompletedTask;
	}

	public async Task<IReadOnlyList<CheckpointData>> ListCheckpointsAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
	{
		var checkpoints = new List<CheckpointData>();

		if (!Directory.Exists(_rootPath))
			return checkpoints;

		IEnumerable<string> orchestrationDirs;

		if (orchestrationName is not null)
		{
			var specificDir = Path.Combine(_rootPath, SanitizePath(orchestrationName));
			orchestrationDirs = Directory.Exists(specificDir) ? [specificDir] : [];
		}
		else
		{
			orchestrationDirs = Directory.EnumerateDirectories(_rootPath);
		}

		foreach (var orchestrationDir in orchestrationDirs)
		{
			foreach (var runDir in Directory.EnumerateDirectories(orchestrationDir))
			{
				var filePath = Path.Combine(runDir, "checkpoint.json");
				if (!File.Exists(filePath))
					continue;

				try
				{
					var json = await File.ReadAllTextAsync(filePath, cancellationToken);
					var checkpoint = JsonSerializer.Deserialize<CheckpointData>(json, _jsonOptions);
					if (checkpoint is not null)
						checkpoints.Add(checkpoint);
				}
				catch (Exception ex)
				{
					LogCheckpointLoadFailed(ex, Path.GetFileName(orchestrationDir), Path.GetFileName(runDir));
				}
			}
		}

		return checkpoints;
	}

	private string GetCheckpointDirectory(string orchestrationName, string runId)
		=> Path.Combine(_rootPath, SanitizePath(orchestrationName), runId);

	private string GetCheckpointFilePath(string orchestrationName, string runId)
		=> Path.Combine(GetCheckpointDirectory(orchestrationName, runId), "checkpoint.json");

	private static string SanitizePath(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var sanitized = new char[name.Length];
		for (var i = 0; i < name.Length; i++)
			sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
		return new string(sanitized);
	}

	#region Source-Generated Logging

	[LoggerMessage(Level = LogLevel.Debug, Message = "Checkpoint saved for orchestration '{OrchestrationName}', run '{RunId}' ({CompletedSteps} steps completed).")]
	private partial void LogCheckpointSaved(string orchestrationName, string runId, int completedSteps);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load checkpoint for orchestration '{OrchestrationName}', run '{RunId}'.")]
	private partial void LogCheckpointLoadFailed(Exception ex, string orchestrationName, string runId);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Checkpoint deleted for orchestration '{OrchestrationName}', run '{RunId}'.")]
	private partial void LogCheckpointDeleted(string orchestrationName, string runId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete checkpoint for orchestration '{OrchestrationName}', run '{RunId}'.")]
	private partial void LogCheckpointDeleteFailed(Exception ex, string orchestrationName, string runId);

	#endregion
}
