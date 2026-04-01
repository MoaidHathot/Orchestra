using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orchestra.Engine;

namespace Orchestra.Host.Persistence;

/// <summary>
/// Enhanced file-system backed run store for hosting applications.
/// Layout:
///   {rootPath}/executions/{orchestration-name}/{name}_{version}_{trigger}_{timestamp}_{execution-id}/
///     orchestration.json               - copy of orchestration at execution time
///     run.json                         - full OrchestrationRunRecord
///     {step-name}-inputs.json          - raw + handled inputs for the step
///     {step-name}-outputs.json         - raw + handled outputs for the step
///     {step-name}-result.json          - final result or exception
///     result.md                        - human-readable final output
/// </summary>
public class FileSystemRunStore : IRunStore
{
	private readonly string _rootPath;
	private readonly JsonSerializerOptions _jsonOptions;

	// In-memory index for fast lookups - populated on first access.
	// A single lock protects all mutations to the inner List<RunIndex> values.
	// ConcurrentDictionary is still used for lock-free reads of the dictionary itself,
	// but ALL reads/writes to the inner lists must hold _indexWriteLock.
	private readonly ConcurrentDictionary<string, List<RunIndex>> _indexByOrchestration = new();
	private readonly ConcurrentDictionary<string, List<RunIndex>> _indexByTrigger = new();
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileWriteLocks = new();
	private readonly object _indexWriteLock = new();
	private volatile bool _indexLoaded;
	private readonly SemaphoreSlim _indexLoadLock = new(1, 1);

	public FileSystemRunStore(string rootPath)
	{
		_rootPath = Path.Combine(rootPath, "executions");
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
	/// Gets the root path for executions.
	/// </summary>
	public string RootPath => _rootPath;

	/// <summary>
	/// Saves a run record with the enhanced folder structure.
	/// </summary>
	public async Task SaveRunAsync(
		OrchestrationRunRecord record,
		Orchestration? orchestration = null,
		CancellationToken cancellationToken = default)
	{
		// Ensure index is loaded before we write anything to avoid duplicates:
		// If we write first and then load, we'd read back what we just wrote.
		await EnsureIndexLoadedAsync(cancellationToken);

		// Format: {name}_{version}_{trigger}_{timestamp}_{id}
		var sanitizedName = SanitizePath(record.OrchestrationName);
		var version = SanitizePath(record.OrchestrationVersion);
		var trigger = SanitizePath(record.TriggeredBy);
		var timestamp = record.StartedAt.ToString("yyyyMMdd-HHmmss");

		var folderName = $"{sanitizedName}_{version}_{trigger}_{timestamp}_{SanitizePath(record.RunId)}";
		var runDir = Path.Combine(_rootPath, sanitizedName, folderName);

		// Serialize file writes per orchestration to avoid Windows file locking conflicts
		// when multiple concurrent saves target the same orchestration directory.
		var writeLock = _fileWriteLocks.GetOrAdd(record.OrchestrationName, _ => new SemaphoreSlim(1, 1));
		await writeLock.WaitAsync(cancellationToken);
		try
		{
			Directory.CreateDirectory(runDir);

			// Write the orchestration copy if provided
			if (orchestration is not null)
			{
				var orchestrationJson = JsonSerializer.Serialize(orchestration, _jsonOptions);
				await File.WriteAllTextAsync(Path.Combine(runDir, "orchestration.json"), orchestrationJson, cancellationToken);
			}

			// Write the full run record
			var runJson = JsonSerializer.Serialize(record, _jsonOptions);
			await File.WriteAllTextAsync(Path.Combine(runDir, "run.json"), runJson, cancellationToken);

			// Write individual step files with enhanced structure
			foreach (var (key, stepRecord) in record.AllStepRecords)
			{
				var stepName = SanitizePath(stepRecord.StepName);
				var suffix = stepRecord.LoopIteration is { } iteration and > 0
					? $"-iteration-{iteration}"
					: "";

				// Write inputs file (raw dependency outputs + parameters + prompt sent)
				var inputs = new StepInputsRecord
				{
					Parameters = stepRecord.Parameters,
					RawDependencyOutputs = stepRecord.RawDependencyOutputs,
					PromptSent = stepRecord.PromptSent
				};
				var inputsJson = JsonSerializer.Serialize(inputs, _jsonOptions);
				await File.WriteAllTextAsync(Path.Combine(runDir, $"{stepName}{suffix}-inputs.json"), inputsJson, cancellationToken);

				// Write outputs file (raw content before handler + final content)
				var outputs = new StepOutputsRecord
				{
					RawContent = stepRecord.RawContent,
					Content = stepRecord.Content,
					ActualModel = stepRecord.ActualModel,
					Usage = stepRecord.Usage
				};
				var outputsJson = JsonSerializer.Serialize(outputs, _jsonOptions);
				await File.WriteAllTextAsync(Path.Combine(runDir, $"{stepName}{suffix}-outputs.json"), outputsJson, cancellationToken);

				// Write result file (status + timing + error if any)
				var result = new StepResultRecord
				{
					Status = stepRecord.Status,
					StartedAt = stepRecord.StartedAt,
					CompletedAt = stepRecord.CompletedAt,
					Duration = stepRecord.Duration,
					ErrorMessage = stepRecord.ErrorMessage
				};
				var resultJson = JsonSerializer.Serialize(result, _jsonOptions);
				await File.WriteAllTextAsync(Path.Combine(runDir, $"{stepName}{suffix}-result.json"), resultJson, cancellationToken);
			}

			// Write a human-readable result summary
			var resultContent = record.FinalContent;
			if (!string.IsNullOrWhiteSpace(resultContent))
			{
				await File.WriteAllTextAsync(Path.Combine(runDir, "result.md"), resultContent, cancellationToken);
			}
		}
		finally
		{
			writeLock.Release();
		}

		// Update in-memory index — thread-safe
		var (failedStep, errorMsg) = ExtractFailureInfo(record);
		var index = new RunIndex
		{
			RunId = record.RunId,
			OrchestrationName = record.OrchestrationName,
			OrchestrationVersion = record.OrchestrationVersion,
			TriggeredBy = record.TriggeredBy,
			StartedAt = record.StartedAt,
			CompletedAt = record.CompletedAt,
			Status = record.Status,
			TriggerId = record.TriggerId,
			FolderPath = runDir,
			FailedStepName = failedStep,
			ErrorMessage = errorMsg,
			CompletionReason = record.CompletionReason,
			CompletedByStep = record.CompletedByStep,
			IsIncomplete = record.IsIncomplete,
		};

		lock (_indexWriteLock)
		{
			_indexByOrchestration
				.GetOrAdd(record.OrchestrationName, _ => [])
				.Add(index);

			if (record.TriggerId is { } tid)
			{
				_indexByTrigger
					.GetOrAdd(tid, _ => [])
					.Add(index);
			}
		}
	}

	// IRunStore implementation (delegates to enhanced method)
	public Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default)
		=> SaveRunAsync(record, null, cancellationToken);

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(
		string orchestrationName, int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		List<RunIndex> snapshot;
		lock (_indexWriteLock)
		{
			if (!_indexByOrchestration.TryGetValue(orchestrationName, out var indices))
				return [];
			snapshot = [.. indices];
		}

		var sorted = snapshot
			.OrderByDescending(i => i.StartedAt)
			.Take(limit ?? int.MaxValue);

		return await LoadRecordsAsync(sorted, cancellationToken);
	}

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListAllRunsAsync(
		int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		List<RunIndex> snapshot;
		lock (_indexWriteLock)
		{
			snapshot = _indexByOrchestration.Values
				.SelectMany(v => v)
				.ToList();
		}

		var sorted = snapshot
			.OrderByDescending(i => i.StartedAt)
			.Take(limit ?? int.MaxValue);

		return await LoadRecordsAsync(sorted, cancellationToken);
	}

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsByTriggerAsync(
		string triggerId, int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		List<RunIndex> snapshot;
		lock (_indexWriteLock)
		{
			if (!_indexByTrigger.TryGetValue(triggerId, out var indices))
				return [];
			snapshot = [.. indices];
		}

		var sorted = snapshot
			.OrderByDescending(i => i.StartedAt)
			.Take(limit ?? int.MaxValue);

		return await LoadRecordsAsync(sorted, cancellationToken);
	}

	public async Task<OrchestrationRunRecord?> GetRunAsync(
		string orchestrationName, string runId, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		RunIndex? match;
		lock (_indexWriteLock)
		{
			if (!_indexByOrchestration.TryGetValue(orchestrationName, out var indices))
				return null;
			match = indices.FirstOrDefault(i => i.RunId == runId);
		}

		if (match is null)
			return null;

		return await LoadRecordAsync(match.FolderPath, cancellationToken);
	}

	/// <summary>
	/// Deletes a run record and its associated files.
	/// </summary>
	public async Task<bool> DeleteRunAsync(
		string orchestrationName, string runId, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		RunIndex? match;
		lock (_indexWriteLock)
		{
			if (!_indexByOrchestration.TryGetValue(orchestrationName, out var indices))
				return false;

			match = indices.FirstOrDefault(i => i.RunId == runId);
			if (match is null)
				return false;

			// Remove from indices while holding the lock
			indices.Remove(match);

			// Also remove from trigger index if applicable
			if (match.TriggerId is { } tid && _indexByTrigger.TryGetValue(tid, out var triggerIndices))
			{
				triggerIndices.RemoveAll(i => i.RunId == runId);
			}
		}

		// Delete the folder and all its contents (outside lock to avoid holding it during I/O)
		if (Directory.Exists(match.FolderPath))
		{
			try
			{
				Directory.Delete(match.FolderPath, recursive: true);
			}
			catch
			{
				// Folder deletion failed, but index was already updated
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Gets lightweight run summaries for the history panel.
	/// </summary>
	public async Task<IReadOnlyList<RunIndex>> GetRunSummariesAsync(
		int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		lock (_indexWriteLock)
		{
			return _indexByOrchestration.Values
				.SelectMany(v => v)
				.OrderByDescending(i => i.StartedAt)
				.Take(limit ?? int.MaxValue)
				.ToList();
		}
	}

	/// <summary>
	/// Gets lightweight run summaries for a specific orchestration.
	/// </summary>
	public async Task<IReadOnlyList<RunIndex>> GetRunSummariesAsync(
		string orchestrationName, int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		lock (_indexWriteLock)
		{
			if (!_indexByOrchestration.TryGetValue(orchestrationName, out var indices))
				return [];

			return indices
				.OrderByDescending(i => i.StartedAt)
				.Take(limit ?? int.MaxValue)
				.ToList();
		}
	}

	private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken)
	{
		if (_indexLoaded) return;

		await _indexLoadLock.WaitAsync(cancellationToken);
		try
		{
			if (_indexLoaded) return;

			if (!Directory.Exists(_rootPath)) return;

			foreach (var orchestrationDir in Directory.EnumerateDirectories(_rootPath))
			{
				foreach (var runDir in Directory.EnumerateDirectories(orchestrationDir))
				{
					var runJsonPath = Path.Combine(runDir, "run.json");
					if (!File.Exists(runJsonPath)) continue;

					try
					{
						var json = await File.ReadAllTextAsync(runJsonPath, cancellationToken);
						var record = JsonSerializer.Deserialize<OrchestrationRunRecord>(json, _jsonOptions);
						if (record is null) continue;

						var (failedStep2, errorMsg2) = ExtractFailureInfo(record);
						var index = new RunIndex
						{
							RunId = record.RunId,
							OrchestrationName = record.OrchestrationName,
							OrchestrationVersion = record.OrchestrationVersion,
							TriggeredBy = record.TriggeredBy,
							StartedAt = record.StartedAt,
							CompletedAt = record.CompletedAt,
							Status = record.Status,
							TriggerId = record.TriggerId,
							FolderPath = runDir,
							FailedStepName = failedStep2,
							ErrorMessage = errorMsg2,
							CompletionReason = record.CompletionReason,
							CompletedByStep = record.CompletedByStep,
							IsIncomplete = record.IsIncomplete,
						};

						// During initial load we are the only writer (protected by _indexLoadLock),
						// but we still take _indexWriteLock for consistency.
						lock (_indexWriteLock)
						{
							_indexByOrchestration
								.GetOrAdd(record.OrchestrationName, _ => [])
								.Add(index);

							if (record.TriggerId is { } tid)
							{
								_indexByTrigger
									.GetOrAdd(tid, _ => [])
									.Add(index);
							}
						}
					}
					catch
					{
						// Skip corrupt records
					}
				}
			}

			_indexLoaded = true;
		}
		finally
		{
			_indexLoadLock.Release();
		}
	}

	/// <summary>
	/// Extracts the error/cancellation info from the first relevant step in a run record.
	/// </summary>
	private static (string? StepName, string? ErrorMessage) ExtractFailureInfo(OrchestrationRunRecord record)
	{
		if (record.Status == ExecutionStatus.Cancelled)
		{
			var cancelledStep = record.AllStepRecords.Values
				.Where(s => s.Status == ExecutionStatus.Cancelled && !string.IsNullOrEmpty(s.ErrorMessage))
				.OrderBy(s => s.StartedAt)
				.FirstOrDefault();

			return cancelledStep != null
				? (cancelledStep.StepName, cancelledStep.ErrorMessage)
				: (null, "Cancelled");
		}

		if (record.Status != ExecutionStatus.Failed)
			return (null, null);

		var failedStep = record.AllStepRecords.Values
			.Where(s => s.Status == ExecutionStatus.Failed && !string.IsNullOrEmpty(s.ErrorMessage))
			.OrderBy(s => s.StartedAt)
			.FirstOrDefault();

		return failedStep != null
			? (failedStep.StepName, failedStep.ErrorMessage)
			: (null, null);
	}

	private async Task<IReadOnlyList<OrchestrationRunRecord>> LoadRecordsAsync(
		IEnumerable<RunIndex> indices, CancellationToken cancellationToken)
	{
		var records = new List<OrchestrationRunRecord>();
		foreach (var idx in indices)
		{
			var record = await LoadRecordAsync(idx.FolderPath, cancellationToken);
			if (record is not null)
				records.Add(record);
		}
		return records;
	}

	private async Task<OrchestrationRunRecord?> LoadRecordAsync(string folderPath, CancellationToken cancellationToken)
	{
		var runJsonPath = Path.Combine(folderPath, "run.json");
		if (!File.Exists(runJsonPath)) return null;

		try
		{
			var json = await File.ReadAllTextAsync(runJsonPath, cancellationToken);
			return JsonSerializer.Deserialize<OrchestrationRunRecord>(json, _jsonOptions);
		}
		catch
		{
			return null;
		}
	}

	private static string SanitizePath(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var sanitized = new char[name.Length];
		for (var i = 0; i < name.Length; i++)
			sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
		return new string(sanitized);
	}
}

/// <summary>
/// Lightweight index entry for fast history lookups.
/// </summary>
public class RunIndex
{
	public required string RunId { get; init; }
	public required string OrchestrationName { get; init; }
	public string OrchestrationVersion { get; init; } = "1.0.0";
	public string TriggeredBy { get; init; } = "manual";
	public required DateTimeOffset StartedAt { get; init; }
	public DateTimeOffset CompletedAt { get; init; }
	public required ExecutionStatus Status { get; init; }
	public string? TriggerId { get; init; }
	public required string FolderPath { get; init; }
	public TimeSpan Duration => CompletedAt - StartedAt;

	/// <summary>
	/// Name of the first step that failed, if the run failed.
	/// </summary>
	public string? FailedStepName { get; init; }

	/// <summary>
	/// Error message from the first failed step, if the run failed.
	/// </summary>
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// When set, indicates the orchestration was completed early by the orchestra_complete tool.
	/// </summary>
	public string? CompletionReason { get; init; }

	/// <summary>
	/// The name of the step that triggered early completion via orchestra_complete.
	/// </summary>
	public string? CompletedByStep { get; init; }

	/// <summary>
	/// When true, indicates the orchestration did not fully complete.
	/// This covers cases where all terminal steps had NoAction/Skipped status,
	/// or the orchestration was completed early via orchestra_complete.
	/// </summary>
	public bool IsIncomplete { get; init; }
}

/// <summary>
/// Record of inputs for a step execution.
/// </summary>
public class StepInputsRecord
{
	public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
	public IReadOnlyDictionary<string, string> RawDependencyOutputs { get; init; } = new Dictionary<string, string>();
	public string? PromptSent { get; init; }
}

/// <summary>
/// Record of outputs for a step execution.
/// </summary>
public class StepOutputsRecord
{
	public string? RawContent { get; init; }
	public required string Content { get; init; }
	public string? ActualModel { get; init; }
	public TokenUsage? Usage { get; init; }
}

/// <summary>
/// Record of result/status for a step execution.
/// </summary>
public class StepResultRecord
{
	public required ExecutionStatus Status { get; init; }
	public required DateTimeOffset StartedAt { get; init; }
	public required DateTimeOffset CompletedAt { get; init; }
	public TimeSpan Duration { get; init; }
	public string? ErrorMessage { get; init; }
}
