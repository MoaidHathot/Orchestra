using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Host.Persistence;

/// <summary>
/// File-system backed implementation of <see cref="IPendingInputStore"/>. Records are
/// stored under <c>{rootPath}/pending/{orchestrationName}/{runId}/{stepName}.json</c>
/// (sanitized). Supports concurrent reads/writes via filesystem-level atomic moves.
/// </summary>
public sealed partial class FileSystemPendingInputStore : IPendingInputStore
{
	private readonly string _rootPath;
	private readonly JsonSerializerOptions _jsonOptions;
	private readonly ILogger<FileSystemPendingInputStore> _logger;

	public FileSystemPendingInputStore(string rootPath, ILogger<FileSystemPendingInputStore> logger)
	{
		_rootPath = Path.Combine(rootPath, "pending");
		_logger = logger;
		_jsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
		};

		Directory.CreateDirectory(_rootPath);
	}

	/// <summary>Root directory where pending records are stored.</summary>
	public string RootPath => _rootPath;

	public async Task SaveAsync(PendingInputRecord record, CancellationToken cancellationToken = default)
	{
		var dir = GetRunDirectory(record.OrchestrationName, record.RunId);
		Directory.CreateDirectory(dir);

		var filePath = GetRecordFilePath(record.OrchestrationName, record.RunId, record.StepName);
		var json = JsonSerializer.Serialize(record, _jsonOptions);

		var tempPath = filePath + ".tmp";
		await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
		File.Move(tempPath, filePath, overwrite: true);

		LogPendingSaved(record.OrchestrationName, record.RunId, record.StepName, record.Kind.ToString());
	}

	public async Task<PendingInputRecord?> GetAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
	{
		var filePath = GetRecordFilePath(orchestrationName, runId, stepName);
		if (!File.Exists(filePath))
			return null;

		try
		{
			var json = await ReadAllTextShareDeleteAsync(filePath, cancellationToken).ConfigureAwait(false);
			return JsonSerializer.Deserialize<PendingInputRecord>(json, _jsonOptions);
		}
		catch (FileNotFoundException)
		{
			// Record was deleted between File.Exists and the open; treat as not-found.
			return null;
		}
		catch (Exception ex)
		{
			LogPendingLoadFailed(ex, orchestrationName, runId, stepName);
			return null;
		}
	}

	public async Task<IReadOnlyList<PendingInputRecord>> ListAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
	{
		var result = new List<PendingInputRecord>();
		if (!Directory.Exists(_rootPath))
			return result;

		// EnumerationOptions.IgnoreInaccessible = true silently skips directory entries that
		// disappear (or become inaccessible) between when they were enumerated and when we
		// open them. This matters because concurrent DeleteAsync calls remove empty
		// {orchestration}/{run} directories while we may still be reading them.
		var enumerationOptions = new EnumerationOptions { IgnoreInaccessible = true };

		IEnumerable<string> orchestrationDirs;
		if (orchestrationName is not null)
		{
			var specific = Path.Combine(_rootPath, SanitizePath(orchestrationName));
			orchestrationDirs = Directory.Exists(specific) ? [specific] : [];
		}
		else
		{
			orchestrationDirs = SafeEnumerateDirectories(_rootPath, enumerationOptions);
		}

		foreach (var orchestrationDir in orchestrationDirs)
		{
			foreach (var runDir in SafeEnumerateDirectories(orchestrationDir, enumerationOptions))
			{
				foreach (var file in SafeEnumerateFiles(runDir, "*.json", enumerationOptions))
				{
					if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
						continue;
					try
					{
						var json = await ReadAllTextShareDeleteAsync(file, cancellationToken).ConfigureAwait(false);
						var record = JsonSerializer.Deserialize<PendingInputRecord>(json, _jsonOptions);
						if (record is not null)
							result.Add(record);
					}
					catch (FileNotFoundException)
					{
						// Record was deleted while we were enumerating; skip it.
					}
					catch (DirectoryNotFoundException)
					{
						// Containing directory was removed concurrently; skip it.
					}
					catch (Exception ex)
					{
						LogPendingLoadFailed(ex, Path.GetFileName(orchestrationDir), Path.GetFileName(runDir), Path.GetFileNameWithoutExtension(file));
					}
				}
			}
		}

		return result;
	}

	/// <summary>
	/// Wraps <see cref="Directory.EnumerateDirectories(string, string, EnumerationOptions)"/>
	/// so that a concurrent removal of <paramref name="path"/> itself (vs. an entry inside it)
	/// is treated as "no entries" rather than thrown back at the caller.
	/// <see cref="EnumerationOptions.IgnoreInaccessible"/> only suppresses errors on inner
	/// entries; the root not existing still throws <see cref="DirectoryNotFoundException"/>.
	/// </summary>
	private static IEnumerable<string> SafeEnumerateDirectories(string path, EnumerationOptions options)
	{
		try
		{
			return Directory.EnumerateDirectories(path, "*", options);
		}
		catch (DirectoryNotFoundException)
		{
			return [];
		}
	}

	/// <summary>
	/// Wraps <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/> with
	/// the same "root-disappeared" guard as <see cref="SafeEnumerateDirectories"/>.
	/// </summary>
	private static IEnumerable<string> SafeEnumerateFiles(string path, string searchPattern, EnumerationOptions options)
	{
		try
		{
			return Directory.EnumerateFiles(path, searchPattern, options);
		}
		catch (DirectoryNotFoundException)
		{
			return [];
		}
	}

	public async Task DeleteAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
	{
		var filePath = GetRecordFilePath(orchestrationName, runId, stepName);
		if (File.Exists(filePath))
		{
			await TryDeleteWithRetryAsync(filePath, orchestrationName, runId, stepName, cancellationToken).ConfigureAwait(false);
		}

		// Clean up empty run + orchestration directories.
		var runDir = GetRunDirectory(orchestrationName, runId);
		TryRemoveEmptyDirectory(runDir);
		var orchestrationDir = Path.Combine(_rootPath, SanitizePath(orchestrationName));
		TryRemoveEmptyDirectory(orchestrationDir);
	}

	/// <summary>
	/// Reads a file's contents using <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>
	/// so that a concurrent <see cref="File.Delete(string)"/> from this process is not blocked by
	/// the read handle. <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> defaults to
	/// <see cref="FileShare.Read"/>, which on Windows denies the delete with
	/// <c>"the process cannot access the file because it is being used by another process"</c>.
	/// </summary>
	private static async Task<string> ReadAllTextShareDeleteAsync(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 4096,
			useAsync: true);
		using var reader = new StreamReader(stream);
		return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Deletes <paramref name="filePath"/> with a short bounded retry loop. On Windows,
	/// <see cref="File.Delete(string)"/> can transiently fail with <see cref="IOException"/>
	/// or <see cref="UnauthorizedAccessException"/> when another process (antivirus, indexer,
	/// or an in-flight reader that opened the file with <see cref="FileShare.Read"/>) is
	/// holding a non-share-delete handle. Total retry budget is ~620ms across 5 backoffs.
	/// </summary>
	private async Task TryDeleteWithRetryAsync(string filePath, string orchestrationName, string runId, string stepName, CancellationToken cancellationToken)
	{
		int[] backoffMs = [20, 40, 80, 160, 320];
		Exception? lastException = null;

		for (var attempt = 0; attempt <= backoffMs.Length; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				File.Delete(filePath);
				LogPendingDeleted(orchestrationName, runId, stepName);
				return;
			}
			catch (FileNotFoundException)
			{
				// Another agent deleted the file between our File.Exists check and now.
				return;
			}
			catch (DirectoryNotFoundException)
			{
				// Containing directory removed concurrently; nothing left to delete.
				return;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				lastException = ex;
				if (attempt >= backoffMs.Length)
					break;

				await Task.Delay(backoffMs[attempt], cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				// Unexpected failure kind (e.g. PathTooLong). Log once and stop — retrying
				// won't help.
				LogPendingDeleteFailed(ex, orchestrationName, runId, stepName);
				return;
			}
		}

		// We exhausted the retry budget without succeeding. Only surface the warning when
		// the file is still present — another agent may have deleted it concurrently.
		if (lastException is not null && File.Exists(filePath))
		{
			LogPendingDeleteFailed(lastException, orchestrationName, runId, stepName);
		}
	}

	private static void TryRemoveEmptyDirectory(string dir)
	{
		try
		{
			if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
			{
				Directory.Delete(dir);
			}
		}
		catch
		{
			// Directory cleanup is best-effort.
		}
	}

	private string GetRunDirectory(string orchestrationName, string runId)
		=> Path.Combine(_rootPath, SanitizePath(orchestrationName), SanitizePath(runId));

	private string GetRecordFilePath(string orchestrationName, string runId, string stepName)
		=> Path.Combine(GetRunDirectory(orchestrationName, runId), SanitizePath(stepName) + ".json");

	private static string SanitizePath(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var sanitized = new char[name.Length];
		for (var i = 0; i < name.Length; i++)
			sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
		return new string(sanitized);
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Pending input record saved for orchestration '{OrchestrationName}', run '{RunId}', step '{StepName}' (kind={Kind}).")]
	private partial void LogPendingSaved(string orchestrationName, string runId, string stepName, string kind);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load pending input record for orchestration '{OrchestrationName}', run '{RunId}', step '{StepName}'.")]
	private partial void LogPendingLoadFailed(Exception ex, string orchestrationName, string runId, string stepName);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Pending input record deleted for orchestration '{OrchestrationName}', run '{RunId}', step '{StepName}'.")]
	private partial void LogPendingDeleted(string orchestrationName, string runId, string stepName);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete pending input record for orchestration '{OrchestrationName}', run '{RunId}', step '{StepName}'.")]
	private partial void LogPendingDeleteFailed(Exception ex, string orchestrationName, string runId, string stepName);
}
