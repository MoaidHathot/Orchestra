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
			var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
			return JsonSerializer.Deserialize<PendingInputRecord>(json, _jsonOptions);
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

		IEnumerable<string> orchestrationDirs;
		if (orchestrationName is not null)
		{
			var specific = Path.Combine(_rootPath, SanitizePath(orchestrationName));
			orchestrationDirs = Directory.Exists(specific) ? [specific] : [];
		}
		else
		{
			orchestrationDirs = Directory.EnumerateDirectories(_rootPath);
		}

		foreach (var orchestrationDir in orchestrationDirs)
		{
			foreach (var runDir in Directory.EnumerateDirectories(orchestrationDir))
			{
				foreach (var file in Directory.EnumerateFiles(runDir, "*.json"))
				{
					if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
						continue;
					try
					{
						var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
						var record = JsonSerializer.Deserialize<PendingInputRecord>(json, _jsonOptions);
						if (record is not null)
							result.Add(record);
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

	public Task DeleteAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
	{
		var filePath = GetRecordFilePath(orchestrationName, runId, stepName);
		if (File.Exists(filePath))
		{
			try
			{
				File.Delete(filePath);
				LogPendingDeleted(orchestrationName, runId, stepName);
			}
			catch (Exception ex)
			{
				LogPendingDeleteFailed(ex, orchestrationName, runId, stepName);
			}
		}

		// Clean up empty run + orchestration directories.
		var runDir = GetRunDirectory(orchestrationName, runId);
		TryRemoveEmptyDirectory(runDir);
		var orchestrationDir = Path.Combine(_rootPath, SanitizePath(orchestrationName));
		TryRemoveEmptyDirectory(orchestrationDir);

		return Task.CompletedTask;
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
