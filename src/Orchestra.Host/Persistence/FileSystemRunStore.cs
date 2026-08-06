using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public partial class FileSystemRunStore : IRunStore, IDisposable
{
	private readonly string _rootPath;
	private readonly JsonSerializerOptions _jsonOptions;
	private readonly ILogger<FileSystemRunStore> _logger;

	private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileWriteLocks = new();
	private volatile bool _indexLoaded;
	private readonly SemaphoreSlim _indexLoadLock = new(1, 1);

	/// <summary>
	/// SQLite projection of the run history. Derived and always rebuildable — the run artifacts on
	/// disk remain the source of truth. Replaces the in-memory dictionaries that used to be
	/// rebuilt by deserializing every <c>run.json</c> on every process start.
	/// </summary>
	private readonly SqliteRunIndex _index;

	/// <summary>
	/// User-curated run annotations. Optional: when absent (tests, embedded hosts) favorites
	/// simply do not exist and retention behaves exactly as before.
	/// </summary>
	private readonly RunAnnotationStore? _annotations;

	public FileSystemRunStore(string rootPath, ILogger<FileSystemRunStore>? logger = null, RunAnnotationStore? annotations = null)
	{
		_rootPath = Path.Combine(rootPath, "executions");
		_logger = logger ?? NullLogger<FileSystemRunStore>.Instance;
		_annotations = annotations;
		_jsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Converters = { new JsonStringEnumConverter() }
		};

		Directory.CreateDirectory(_rootPath);

		// Kept beside the executions it describes, so copying or deleting that directory keeps the
		// index consistent with its contents.
		_index = new SqliteRunIndex(Path.Combine(_rootPath, ".index.db"), _logger);
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
					SelectedModel = stepRecord.SelectedModel,
					RequestedModelInfo = stepRecord.RequestedModelInfo,
					SelectedModelInfo = stepRecord.SelectedModelInfo,
					ActualModelInfo = stepRecord.ActualModelInfo,
					ConfiguredProvider = stepRecord.ConfiguredProvider,
					ActualProvider = stepRecord.ActualProvider,
					Usage = stepRecord.Usage,
					SavedFiles = stepRecord.SavedFiles,
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
			Cancellation = record.Cancellation,
			HookExecutionCount = record.HookExecutions.Count,
			RetriedFromRunId = record.RetriedFromRunId,
			RetryMode = record.RetryMode,
			ParentExecutionId = record.ParentExecutionId,
			ParentStepName = record.ParentStepName,
			RootExecutionId = record.RootExecutionId,
			NestingDepth = record.NestingDepth,
		};

		// Upsert (not append) keyed on folder path: re-saving the same record overwrites its row
		// instead of producing the duplicate history entry the in-memory index used to.
		_index.Upsert(index);
	}

	// IRunStore implementation (delegates to enhanced method)
	public Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default)
		=> SaveRunAsync(record, null, cancellationToken);

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(
		string orchestrationName, int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		return await LoadRecordsAsync(_index.ListByOrchestration(orchestrationName, limit), cancellationToken);
	}

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListAllRunsAsync(
		int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		return await LoadRecordsAsync(_index.ListAll(limit), cancellationToken);
	}

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsByTriggerAsync(
		string triggerId, int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		return await LoadRecordsAsync(_index.ListByTrigger(triggerId, limit), cancellationToken);
	}

	public async Task<OrchestrationRunRecord?> GetRunAsync(
		string orchestrationName, string runId, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		var match = _index.FindRun(orchestrationName, runId);
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

		var match = _index.FindRun(orchestrationName, runId);
		if (match is null)
			return false;

		// Delete the folder and all its contents before dropping the index row, so a failure
		// leaves the index describing what is actually still on disk.
		if (Directory.Exists(match.FolderPath))
		{
			try
			{
				Directory.Delete(match.FolderPath, recursive: true);
			}
			catch (Exception ex)
			{
				LogRunFolderDeleteFailed(match.FolderPath, ex);
				return false;
			}
		}

		_index.DeleteByFolderPaths([match.FolderPath]);

		// An annotation must not outlive the run it describes.
		_annotations?.Remove(runId, orchestrationName);

		return true;
	}

	/// <summary>
	/// Returns <see langword="true"/> when the run has been marked as a favorite and is therefore
	/// exempt from retention deletion.
	/// </summary>
	public bool IsFavorite(string runId) => _annotations?.IsFavorite(runId) == true;

	/// <summary>
	/// Eagerly loads the run index into memory so that subsequent queries are fast.
	/// Safe to call multiple times; only the first call performs actual I/O.
	/// </summary>
	public Task PreloadIndexAsync(CancellationToken cancellationToken = default)
		=> EnsureIndexLoadedAsync(cancellationToken);

	/// <summary>
	/// Gets lightweight run summaries for the history panel.
	/// </summary>
	public async Task<IReadOnlyList<RunIndex>> GetRunSummariesAsync(
		int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		return _index.ListAll(limit);
	}

	/// <summary>
	/// Finds a run index by run ID across all orchestrations.
	/// Returns null if no matching run is found.
	/// </summary>
	public async Task<RunIndex?> FindRunByIdAsync(string runId, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		return _index.FindByRunId(runId);
	}

	/// <summary>
	/// Gets lightweight run summaries for a specific orchestration.
	/// </summary>
	public async Task<IReadOnlyList<RunIndex>> GetRunSummariesAsync(
		string orchestrationName, int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		return _index.ListByOrchestration(orchestrationName, limit);
	}

	/// <summary>
	/// Returns per-orchestration aggregate stats (run count and most-recent start time)
	/// computed from the persisted run index. Cheaper than flattening with
	/// <see cref="GetRunSummariesAsync(int?, CancellationToken)"/> and then grouping
	/// because we read straight from the per-orchestration index map.
	/// <para>
	/// Used by the public API to surface <c>runCount</c> and <c>lastExecutionTime</c>
	/// that survive process restarts and correctly reflect manual orchestrations —
	/// the in-memory <see cref="Triggers.TriggerRegistration.RunCount"/> /
	/// <see cref="Triggers.TriggerRegistration.LastFireTime"/> are intentionally not
	/// persisted and only cover trigger fires from the current process lifetime.
	/// </para>
	/// </summary>
	/// <remarks>
	/// The returned dictionary uses ordinal case-insensitive keys to match the lookup
	/// semantics used throughout the host (orchestration names are user-supplied and
	/// not guaranteed to have stable casing across sources).
	/// </remarks>
	public async Task<IReadOnlyDictionary<string, OrchestrationRunStats>> GetOrchestrationRunStatsAsync(
		CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		// COUNT/MAX in SQL rather than a full scan of every index entry.
		return _index.GetOrchestrationStats();
	}

	/// <summary>
	/// Scans persisted run records for entries matching the supplied parent or root execution
	/// id. Used by the data-plane <c>list_child_runs</c> tool to scope the listing to the
	/// caller's execution tree without exposing global history.
	/// </summary>
	/// <param name="parentExecutionId">When non-null, only returns runs whose
	/// <see cref="RunIndex.ParentExecutionId"/> equals this id (direct children only).</param>
	/// <param name="rootExecutionId">When non-null and <paramref name="parentExecutionId"/> is
	/// null, only returns runs whose <see cref="RunIndex.RootExecutionId"/> equals this id
	/// (whole subtree, including transitive descendants).</param>
	/// <param name="statusFilter">When non-null, only returns runs whose
	/// <see cref="RunIndex.Status"/> equals this value.</param>
	public async Task<IReadOnlyList<RunIndex>> FindChildRunsAsync(
		string? parentExecutionId,
		string? rootExecutionId,
		ExecutionStatus? statusFilter,
		int? limit = null,
		int? offset = null,
		CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		// Note the guards: `limit is > 0` / `offset is > 0` matched the previous LINQ behaviour,
		// where a zero or negative value meant "no clause" rather than "return nothing".
		return _index.FindChildRuns(
			parentExecutionId,
			rootExecutionId,
			statusFilter,
			limit is > 0 ? limit : null,
			offset is > 0 ? offset : null);
	}

	private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken)
	{
		if (_indexLoaded) return;

		await _indexLoadLock.WaitAsync(cancellationToken);
		try
		{
			if (_indexLoaded) return;

			if (!Directory.Exists(_rootPath)) { _indexLoaded = true; return; }

			// Reconcile the index against the filesystem. Run folders are write-once, so an
			// existing row can never be stale -- only additions and deletions matter, and a
			// directory walk finds both without opening a single run.json.
			var onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var orchestrationDir in Directory.EnumerateDirectories(_rootPath))
			{
				foreach (var runDir in Directory.EnumerateDirectories(orchestrationDir))
				{
					var runJsonPath = Path.Combine(runDir, "run.json");
					if (File.Exists(runJsonPath))
						onDisk[runDir] = runJsonPath;
				}
			}

			var indexed = _index.GetIndexedFolderPaths();

			var removed = indexed.Where(p => !onDisk.ContainsKey(p)).ToList();
			if (removed.Count > 0)
				_index.DeleteByFolderPaths(removed);

			var missing = onDisk.Where(kvp => !indexed.Contains(kvp.Key)).ToList();
			if (missing.Count > 0)
			{
				var sw = System.Diagnostics.Stopwatch.StartNew();

				// Only unindexed runs are read, and each is projected with a streaming reader that
				// skips step traces and content. After the first pass this list is empty.
				var projected = await Task.WhenAll(missing.Select(async entry =>
				{
					try
					{
						var bytes = await File.ReadAllBytesAsync(entry.Value, cancellationToken);
						var projection = RunIndexProjector.Project(bytes, entry.Key);
						if (projection is null)
							LogCorruptRunRecord(entry.Value, new InvalidDataException("run.json could not be projected"));
						return projection;
					}
					catch (Exception ex)
					{
						LogCorruptRunRecord(entry.Value, ex);
						return null;
					}
				}));

				var usable = projected.Where(p => p is not null).Select(p => p!).ToList();
				if (usable.Count > 0)
					_index.UpsertMany(usable);

				sw.Stop();
				LogIndexBuilt(usable.Count, removed.Count, _index.Count, sw.ElapsedMilliseconds);
			}
			else if (removed.Count > 0)
			{
				LogIndexBuilt(0, removed.Count, _index.Count, 0);
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
		catch (Exception ex)
		{
			LogRunRecordLoadFailed(runJsonPath, ex);
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

	/// <summary>
	/// Applies a retention policy to all stored runs.
	/// Deletes runs that exceed the max count per orchestration or max age.
	/// Returns the number of runs deleted.
	/// </summary>
	public async Task<int> ApplyRetentionAsync(
		Hosting.RetentionPolicy policy,
		CancellationToken cancellationToken = default)
	{
		if (policy.IsForever)
			return 0;

		await EnsureIndexLoadedAsync(cancellationToken);

		var toDelete = new List<RunIndex>();

		foreach (var (orchestrationName, _) in _index.GetOrchestrationStats())
		{
			// Favorited runs are exempt from retention entirely, and are excluded from the
			// ranking below rather than merely skipped. The max-count rule deletes by
			// position (i >= N), so leaving favorites in the ranking would let N favorites
			// permanently occupy every keep-slot and block all pruning for the orchestration.
			var sorted = _index.ListByOrchestration(orchestrationName)
				.Where(i => !IsFavorite(i.RunId))
				.ToList();

			for (var i = 0; i < sorted.Count; i++)
			{
				var run = sorted[i];
				var shouldDelete = false;

				// Check max age
				if (policy.MaxRunAgeDays is > 0)
				{
					var age = DateTimeOffset.UtcNow - run.StartedAt;
					if (age.TotalDays > policy.MaxRunAgeDays.Value)
						shouldDelete = true;
				}

				// Check max count per orchestration (keep only the newest N)
				if (policy.MaxRunsPerOrchestration is > 0 && i >= policy.MaxRunsPerOrchestration.Value)
				{
					shouldDelete = true;
				}

				if (shouldDelete)
				{
					toDelete.Add(run);
				}
			}
		}

		var deleted = 0;
		var deletedRunIds = new List<string>();
		var deletedFolders = new List<string>();
		foreach (var run in toDelete)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				if (Directory.Exists(run.FolderPath))
				{
					Directory.Delete(run.FolderPath, recursive: true);
				}

				deleted++;
				deletedRunIds.Add(run.RunId);
				deletedFolders.Add(run.FolderPath);
			}
			catch (Exception ex)
			{
				LogRetentionDeleteFailed(run.FolderPath, ex);
			}
		}

		// Drop index rows only for folders that were actually removed, so a failed delete leaves
		// the index describing what is still on disk.
		if (deletedFolders.Count > 0)
			_index.DeleteByFolderPaths(deletedFolders);

		// Annotations must not outlive their run. Only reached for runs that were not favorited,
		// since favorites are never queued for deletion above.
		if (deletedRunIds.Count > 0)
			_annotations?.RemoveMany(deletedRunIds);

		return deleted;
	}

	/// <summary>
	/// Releases the index database handle. Registered as a singleton, so the DI container disposes
	/// it at shutdown; tests and embedded hosts that create stores directly must dispose them, or
	/// the database file stays locked.
	/// </summary>
	public void Dispose()
	{
		_index.Dispose();
		_indexLoadLock.Dispose();
		GC.SuppressFinalize(this);
	}

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Run index reconciled: +{Added} new, -{Removed} stale, {Total} total ({ElapsedMs} ms)")]
	private partial void LogIndexBuilt(int added, int removed, int total, long elapsedMs);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete run folder '{FolderPath}'")]
	private partial void LogRunFolderDeleteFailed(string folderPath, Exception ex);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Skipping corrupt run record '{FilePath}'")]
	private partial void LogCorruptRunRecord(string filePath, Exception ex);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load run record from '{FilePath}'")]
	private partial void LogRunRecordLoadFailed(string filePath, Exception ex);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete run during retention cleanup '{FolderPath}'")]
	private partial void LogRetentionDeleteFailed(string folderPath, Exception ex);
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

	/// <summary>
	/// Structured cancellation cause when <see cref="Status"/> is <see cref="ExecutionStatus.Cancelled"/>.
	/// Distinguishes external cancel, the orchestration's own <c>timeoutSeconds</c>,
	/// a sync-invoke wrapper timeout, and early completion via <c>orchestra_complete</c>.
	/// Null when the run was not cancelled.
	/// </summary>
	public CancellationDetails? Cancellation { get; init; }

	/// <summary>
	/// Number of hook executions recorded for this run.
	/// </summary>
	public int HookExecutionCount { get; init; }

	/// <summary>
	/// When this run was started as a retry, the RunId of the original source run.
	/// </summary>
	public string? RetriedFromRunId { get; init; }

	/// <summary>
	/// Retry mode descriptor (e.g. "failed", "all", "from-step:&lt;name&gt;") when this run is a retry.
	/// </summary>
	public string? RetryMode { get; init; }

	/// <summary>
	/// When this run was launched by a parent orchestration (via MCP <c>invoke_orchestration</c>
	/// or a step-level child invocation), the parent's <c>RunId</c>. <see langword="null"/> for
	/// top-level runs (manual, scheduler, loop, webhook, mcp top-level, retry, resume).
	/// </summary>
	public string? ParentExecutionId { get; init; }

	/// <summary>
	/// Name of the parent's step that triggered this child run. <see langword="null"/> for
	/// top-level runs and for child runs whose parent did not surface a step name.
	/// </summary>
	public string? ParentStepName { get; init; }

	/// <summary>
	/// The root run's ID at the top of the parent chain. Equal to <see cref="RunId"/> when this
	/// run is itself a root (no parent). Used to group an entire orchestration tree under a single
	/// identifier without walking the chain on every query.
	/// </summary>
	public string? RootExecutionId { get; init; }

	/// <summary>
	/// Depth in the parent/child tree. <c>0</c> for top-level runs, <c>1</c> for direct children, etc.
	/// </summary>
	public int NestingDepth { get; init; }
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
	public string? SelectedModel { get; init; }
	public AvailableModelInfo? RequestedModelInfo { get; init; }
	public AvailableModelInfo? SelectedModelInfo { get; init; }
	public AvailableModelInfo? ActualModelInfo { get; init; }

	/// <summary>The agent provider this step was configured to run on.</summary>
	public string? ConfiguredProvider { get; init; }

	/// <summary>The agent provider that actually ran this step.</summary>
	public string? ActualProvider { get; init; }

	public TokenUsage? Usage { get; init; }
	public string[] SavedFiles { get; init; } = [];
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

/// <summary>
/// Per-orchestration aggregate statistics derived from the persisted run index.
/// </summary>
/// <param name="Count">Total number of recorded runs for the orchestration.</param>
/// <param name="LastStartedAt">Most-recent <see cref="RunIndex.StartedAt"/> across the orchestration's recorded runs.</param>
public sealed record OrchestrationRunStats(int Count, DateTimeOffset LastStartedAt);
