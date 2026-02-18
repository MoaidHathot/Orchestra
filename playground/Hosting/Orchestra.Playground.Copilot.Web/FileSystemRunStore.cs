using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orchestra.Engine;

namespace Orchestra.Playground.Copilot.Web;

/// <summary>
/// File-system backed run store.
/// Layout:
///   {rootPath}/{orchestrationName}/{runId}_{timestamp}/
///     run.json                       — full OrchestrationRunRecord
///     step-{stepName}.json           — per-step record
///     step-{stepName}-iteration-{N}.json — loop iteration records
/// </summary>
public class FileSystemRunStore : IRunStore
{
	private readonly string _rootPath;
	private readonly JsonSerializerOptions _jsonOptions;

	// In-memory index for fast lookups — populated on first access
	private readonly ConcurrentDictionary<string, List<RunIndex>> _indexByOrchestration = new();
	private readonly ConcurrentDictionary<string, List<RunIndex>> _indexByTrigger = new();
	private volatile bool _indexLoaded;
	private readonly SemaphoreSlim _indexLock = new(1, 1);

	public FileSystemRunStore(string rootPath)
	{
		_rootPath = rootPath;
		_jsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Converters = { new JsonStringEnumConverter() }
		};

		Directory.CreateDirectory(_rootPath);
	}

	public async Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default)
	{
		var folderName = $"{record.RunId}_{record.StartedAt:yyyyMMdd-HHmmss}";
		var sanitizedName = SanitizePath(record.OrchestrationName);
		var runDir = Path.Combine(_rootPath, sanitizedName, folderName);
		Directory.CreateDirectory(runDir);

		// Write the full run record
		var runJson = JsonSerializer.Serialize(record, _jsonOptions);
		await File.WriteAllTextAsync(Path.Combine(runDir, "run.json"), runJson, cancellationToken);

		// Write individual step files
		foreach (var (key, stepRecord) in record.AllStepRecords)
		{
			var fileName = stepRecord.LoopIteration is { } iteration and > 0
				? $"step-{SanitizePath(stepRecord.StepName)}-iteration-{iteration}.json"
				: $"step-{SanitizePath(stepRecord.StepName)}.json";

			var stepJson = JsonSerializer.Serialize(stepRecord, _jsonOptions);
			await File.WriteAllTextAsync(Path.Combine(runDir, fileName), stepJson, cancellationToken);
		}

		// Write a human-readable result summary
		var resultContent = record.FinalContent;
		if (!string.IsNullOrWhiteSpace(resultContent))
		{
			await File.WriteAllTextAsync(Path.Combine(runDir, "result.md"), resultContent, cancellationToken);
		}

		// Update in-memory index
		var index = new RunIndex
		{
			RunId = record.RunId,
			OrchestrationName = record.OrchestrationName,
			StartedAt = record.StartedAt,
			Status = record.Status,
			TriggerId = record.TriggerId,
			FolderPath = runDir
		};

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

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(
		string orchestrationName, int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		if (!_indexByOrchestration.TryGetValue(orchestrationName, out var indices))
			return [];

		var sorted = indices
			.OrderByDescending(i => i.StartedAt)
			.Take(limit ?? int.MaxValue);

		return await LoadRecordsAsync(sorted, cancellationToken);
	}

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListAllRunsAsync(
		int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		var all = _indexByOrchestration.Values
			.SelectMany(v => v)
			.OrderByDescending(i => i.StartedAt)
			.Take(limit ?? int.MaxValue);

		return await LoadRecordsAsync(all, cancellationToken);
	}

	public async Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsByTriggerAsync(
		string triggerId, int? limit = null, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		if (!_indexByTrigger.TryGetValue(triggerId, out var indices))
			return [];

		var sorted = indices
			.OrderByDescending(i => i.StartedAt)
			.Take(limit ?? int.MaxValue);

		return await LoadRecordsAsync(sorted, cancellationToken);
	}

	public async Task<OrchestrationRunRecord?> GetRunAsync(
		string orchestrationName, string runId, CancellationToken cancellationToken = default)
	{
		await EnsureIndexLoadedAsync(cancellationToken);

		if (!_indexByOrchestration.TryGetValue(orchestrationName, out var indices))
			return null;

		var match = indices.FirstOrDefault(i => i.RunId == runId);
		if (match is null)
			return null;

		return await LoadRecordAsync(match.FolderPath, cancellationToken);
	}

	private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken)
	{
		if (_indexLoaded) return;

		await _indexLock.WaitAsync(cancellationToken);
		try
		{
			if (_indexLoaded) return;

			if (!Directory.Exists(_rootPath)) return;

			foreach (var orchestrationDir in Directory.EnumerateDirectories(_rootPath))
			{
				var orchestrationName = Path.GetFileName(orchestrationDir);

				foreach (var runDir in Directory.EnumerateDirectories(orchestrationDir))
				{
					var runJsonPath = Path.Combine(runDir, "run.json");
					if (!File.Exists(runJsonPath)) continue;

					try
					{
						var json = await File.ReadAllTextAsync(runJsonPath, cancellationToken);
						var record = JsonSerializer.Deserialize<OrchestrationRunRecord>(json, _jsonOptions);
						if (record is null) continue;

						var index = new RunIndex
						{
							RunId = record.RunId,
							OrchestrationName = record.OrchestrationName,
							StartedAt = record.StartedAt,
							Status = record.Status,
							TriggerId = record.TriggerId,
							FolderPath = runDir
						};

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
			_indexLock.Release();
		}
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

	private class RunIndex
	{
		public required string RunId { get; init; }
		public required string OrchestrationName { get; init; }
		public required DateTimeOffset StartedAt { get; init; }
		public required ExecutionStatus Status { get; init; }
		public string? TriggerId { get; init; }
		public required string FolderPath { get; init; }
	}
}
