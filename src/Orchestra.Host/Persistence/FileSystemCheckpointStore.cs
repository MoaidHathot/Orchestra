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
	private readonly KeyedAsyncLock _fileLocks = new();

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
		cancellationToken.ThrowIfCancellationRequested();
		var dir = GetCheckpointDirectory(checkpoint.OrchestrationName, checkpoint.RunId);
		var filePath = Path.Combine(dir, "checkpoint.json");
		using var writeLock = await _fileLocks.AcquireAsync(filePath, cancellationToken).ConfigureAwait(false);
		Directory.CreateDirectory(dir);
		// Each caller owns its staging file. The engine orders snapshots per run; distinct
		// temporary names also keep direct/concurrent store callers from corrupting a write.
		var tempPath = filePath + $".{Guid.NewGuid():N}.tmp";
		try
		{
			await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
				FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await JsonSerializer.SerializeAsync(stream, checkpoint, _jsonOptions, cancellationToken).ConfigureAwait(false);
			}
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(tempPath, filePath, overwrite: true);
		}
		finally
		{
			try { File.Delete(tempPath); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{ LogTemporaryFileCleanupFailed(ex, tempPath); }
		}

		LogCheckpointSaved(checkpoint.OrchestrationName, checkpoint.RunId, checkpoint.CompletedSteps.Count);
	}

	public async Task<CheckpointData?> LoadCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var filePath = GetCheckpointFilePath(orchestrationName, runId);

		try
		{
			return await ReadCheckpointAsync(filePath, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) { throw; }
		catch (FileNotFoundException) { return null; }
		catch (DirectoryNotFoundException) { return null; }
		catch (Exception ex)
		{
			LogCheckpointLoadFailed(ex, orchestrationName, runId);
			return null;
		}
	}

	public async Task DeleteCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using var writeLock = await _fileLocks.AcquireAsync(GetCheckpointFilePath(orchestrationName, runId), cancellationToken).ConfigureAwait(false);
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
			LogDirectoryCleanupFailed(ex, orchestrationDir);
		}

	}

	public async Task<IReadOnlyList<CheckpointData>> ListCheckpointsAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
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
			orchestrationDirs = SnapshotDirectories(_rootPath);
		}

		foreach (var orchestrationDir in orchestrationDirs)
		{
			cancellationToken.ThrowIfCancellationRequested();
			foreach (var runDir in SnapshotDirectories(orchestrationDir))
			{
				var filePath = Path.Combine(runDir, "checkpoint.json");

				try
				{
					var checkpoint = await ReadCheckpointAsync(filePath, cancellationToken).ConfigureAwait(false);
					if (checkpoint is not null)
						checkpoints.Add(checkpoint);
				}
				catch (OperationCanceledException) { throw; }
				catch (FileNotFoundException) { }
				catch (DirectoryNotFoundException) { }
				catch (Exception ex)
				{
					LogCheckpointLoadFailed(ex, Path.GetFileName(orchestrationDir), Path.GetFileName(runDir));
				}
			}
		}

		return checkpoints;
	}

	private async Task<CheckpointData?> ReadCheckpointAsync(string path, CancellationToken cancellationToken)
	{
		// Windows can transiently deny opening/replacing a delete-pending file even with
		// share-delete handles. Coordinate readers of this checkpoint with publication;
		// different runs have independent gates, and file contents are always committed.
		using var readLock = await _fileLocks.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		return await JsonSerializer.DeserializeAsync<CheckpointData>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
	}

	private static string[] SnapshotDirectories(string path)
	{
		try { return Directory.GetDirectories(path); }
		catch (DirectoryNotFoundException) { return []; } // A run can finish during listing.
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

	[LoggerMessage(Level = LogLevel.Debug, Message = "Failed to clean up empty orchestration directory '{Directory}'")]
	private partial void LogDirectoryCleanupFailed(Exception ex, string directory);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Failed to clean up checkpoint temporary file '{Path}'")]
	private partial void LogTemporaryFileCleanupFailed(Exception ex, string path);

	#endregion
}
