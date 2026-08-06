using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Persistence;

/// <summary>
/// Stores user-curated <see cref="RunAnnotation"/> records (favorite / title / tags / note)
/// for orchestration runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> One small file per annotated run:
/// <c>{dataPath}/annotations/{orchestrationName}/{runId}.json</c>. This mirrors the existing
/// per-run state roots (<c>checkpoints/</c>, <c>pending/</c>, <c>temp/</c>) rather than the
/// single-file <c>orchestration-tags.json</c>.
/// </para>
/// <para>
/// The distinction matters at run volume. Orchestrations number in the dozens; runs number in
/// the thousands per year. Annotations are sparse — only runs a user acted on — so file count
/// tracks annotations, not executions. Per-run files also keep each mutation a ~300-byte write
/// instead of rewriting one growing blob, and contain corruption to a single record. Annotations
/// are the only irreplaceable data in the run store, so blast radius matters.
/// </para>
/// <para>
/// <b>Concurrency.</b> The in-memory map is a <see cref="ConcurrentDictionary{TKey,TValue}"/> of
/// immutable <see cref="RunAnnotation"/> values, so readers never observe a half-mutated record.
/// Disk writes are atomic (temp file + move) and serialized per run id.
/// </para>
/// </remarks>
public partial class RunAnnotationStore
{
	private readonly ConcurrentDictionary<string, RunAnnotation> _annotations =
		new(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
	private readonly string _rootPath;
	private readonly ILogger<RunAnnotationStore> _logger;

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	public RunAnnotationStore(string dataPath, ILogger<RunAnnotationStore> logger)
	{
		_rootPath = Path.Combine(dataPath, "annotations");
		_logger = logger;
		Directory.CreateDirectory(_rootPath);
		LoadFromDisk();
	}

	/// <summary>Absolute path of the annotations root.</summary>
	public string RootPath => _rootPath;

	/// <summary>Number of annotated runs currently held.</summary>
	public int Count => _annotations.Count;

	// ── Reads ──

	/// <summary>Gets the annotation for a run, or <see langword="null"/> when unannotated.</summary>
	public RunAnnotation? Get(string runId) =>
		string.IsNullOrWhiteSpace(runId) ? null
		: _annotations.TryGetValue(runId, out var a) ? a
		: null;

	/// <summary>
	/// Fast favorite check. Used on the retention hot path, which evaluates every indexed run.
	/// </summary>
	public bool IsFavorite(string runId) => Get(runId)?.Favorite == true;

	/// <summary>Snapshot of every annotation, keyed by run id.</summary>
	public IReadOnlyDictionary<string, RunAnnotation> GetAll() =>
		new Dictionary<string, RunAnnotation>(_annotations, StringComparer.OrdinalIgnoreCase);

	/// <summary>Run ids of every favorited run.</summary>
	public IReadOnlyCollection<string> GetFavoriteRunIds() =>
		[.. _annotations.Where(kvp => kvp.Value.Favorite).Select(kvp => kvp.Key)];

	/// <summary>All known run tags with the number of runs carrying each.</summary>
	public Dictionary<string, int> GetAllTagsWithCounts()
	{
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var annotation in _annotations.Values)
		{
			foreach (var tag in annotation.Tags)
			{
				counts.TryGetValue(tag, out var count);
				counts[tag] = count + 1;
			}
		}
		return counts;
	}

	// ── Writes ──

	/// <summary>
	/// Replaces the annotation for a run. An annotation that is empty after normalization is
	/// deleted rather than stored.
	/// </summary>
	/// <returns>The stored annotation, or <see langword="null"/> when it was removed as empty.</returns>
	public RunAnnotation? Set(string runId, RunAnnotation annotation)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runId);

		var normalized = Normalize(annotation);
		if (normalized.IsEmpty)
		{
			Remove(runId, normalized.OrchestrationName);
			return null;
		}

		_annotations[runId] = normalized;
		WriteToDisk(runId, normalized);
		LogAnnotationSet(runId, normalized.Favorite, normalized.Tags.Length);
		return normalized;
	}

	/// <summary>
	/// Applies a partial update. Only non-<see langword="null"/> arguments are changed, so
	/// setting a title cannot silently clear tags.
	/// </summary>
	/// <returns>The stored annotation, or <see langword="null"/> when it became empty and was removed.</returns>
	public RunAnnotation? Patch(
		string runId,
		bool? favorite = null,
		string? title = null,
		string[]? tags = null,
		string? note = null,
		string? orchestrationName = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runId);

		var existing = Get(runId);
		var merged = new RunAnnotation
		{
			Favorite = favorite ?? existing?.Favorite ?? false,
			// Empty string is a deliberate clear; null means "leave alone".
			Title = title is null ? existing?.Title : NullIfBlank(title),
			Tags = tags ?? existing?.Tags ?? [],
			Note = note is null ? existing?.Note : NullIfBlank(note),
			OrchestrationName = orchestrationName ?? existing?.OrchestrationName,
			AnnotatedAt = DateTimeOffset.UtcNow,
		};

		return Set(runId, merged);
	}

	/// <summary>Removes the annotation for a run. Returns <see langword="true"/> when one existed.</summary>
	public bool Remove(string runId, string? orchestrationName = null)
	{
		if (string.IsNullOrWhiteSpace(runId))
			return false;

		var existed = _annotations.TryRemove(runId, out var removed);
		DeleteFromDisk(runId, orchestrationName ?? removed?.OrchestrationName);

		if (existed)
			LogAnnotationRemoved(runId);

		return existed;
	}

	/// <summary>
	/// Removes annotations for several runs. Used when runs are deleted, so annotations do not
	/// outlive their subject.
	/// </summary>
	public int RemoveMany(IEnumerable<string> runIds)
	{
		var removed = 0;
		foreach (var runId in runIds)
		{
			if (Remove(runId))
				removed++;
		}
		return removed;
	}

	/// <summary>
	/// Drops annotations whose run id is absent from <paramref name="liveRunIds"/>.
	/// </summary>
	/// <remarks>
	/// Deliberately explicit rather than automatic on startup: an index that is incomplete or
	/// still loading would otherwise silently destroy curated metadata. Orphans are surfaced to
	/// the caller instead, and only pruned on request.
	/// </remarks>
	public IReadOnlyCollection<string> FindOrphans(IReadOnlySet<string> liveRunIds) =>
		[.. _annotations.Keys.Where(runId => !liveRunIds.Contains(runId))];

	// ── Persistence ──

	private string PathFor(string runId, string? orchestrationName)
	{
		var dir = string.IsNullOrWhiteSpace(orchestrationName)
			? Path.Combine(_rootPath, "_unknown")
			: Path.Combine(_rootPath, Sanitize(orchestrationName));
		return Path.Combine(dir, $"{Sanitize(runId)}.json");
	}

	private void LoadFromDisk()
	{
		if (!Directory.Exists(_rootPath))
			return;

		var loaded = 0;
		var failed = 0;

		try
		{
			foreach (var file in Directory.EnumerateFiles(_rootPath, "*.json", SearchOption.AllDirectories))
			{
				// One bad file must not take down the rest of the curation.
				try
				{
					var json = File.ReadAllText(file);
					var annotation = JsonSerializer.Deserialize<RunAnnotation>(json, s_jsonOptions);
					if (annotation is null || annotation.IsEmpty)
						continue;

					var runId = Path.GetFileNameWithoutExtension(file);
					_annotations[runId] = Normalize(annotation);
					loaded++;
				}
				catch (Exception ex)
				{
					failed++;
					LogAnnotationLoadFailed(ex, file);
				}
			}
		}
		catch (Exception ex)
		{
			LogAnnotationsScanFailed(ex, _rootPath);
			return;
		}

		LogAnnotationsLoaded(loaded, failed, _rootPath);
	}

	private void WriteToDisk(string runId, RunAnnotation annotation)
	{
		var gate = _fileLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
		gate.Wait();
		try
		{
			var path = PathFor(runId, annotation.OrchestrationName);
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			// Atomic replace: a crash mid-write leaves the previous file intact rather than
			// a truncated one.
			var tempPath = path + ".tmp";
			File.WriteAllText(tempPath, JsonSerializer.Serialize(annotation, s_jsonOptions));
			File.Move(tempPath, path, overwrite: true);
		}
		catch (Exception ex)
		{
			LogAnnotationSaveFailed(ex, runId);
		}
		finally
		{
			gate.Release();
		}
	}

	private void DeleteFromDisk(string runId, string? orchestrationName)
	{
		var gate = _fileLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
		gate.Wait();
		try
		{
			if (!string.IsNullOrWhiteSpace(orchestrationName))
			{
				var path = PathFor(runId, orchestrationName);
				if (File.Exists(path))
				{
					File.Delete(path);
					return;
				}
			}

			// Orchestration unknown (or moved): fall back to locating the file by run id.
			var fileName = $"{Sanitize(runId)}.json";
			foreach (var file in Directory.EnumerateFiles(_rootPath, fileName, SearchOption.AllDirectories))
				File.Delete(file);
		}
		catch (Exception ex)
		{
			LogAnnotationDeleteFailed(ex, runId);
		}
		finally
		{
			gate.Release();
		}
	}

	// ── Normalization ──

	private static RunAnnotation Normalize(RunAnnotation annotation) => new()
	{
		Favorite = annotation.Favorite,
		Title = NullIfBlank(annotation.Title),
		Tags = NormalizeTags(annotation.Tags),
		Note = NullIfBlank(annotation.Note),
		OrchestrationName = NullIfBlank(annotation.OrchestrationName),
		AnnotatedAt = annotation.AnnotatedAt == default ? DateTimeOffset.UtcNow : annotation.AnnotatedAt,
	};

	private static string? NullIfBlank(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	private static string[] NormalizeTags(string[]? tags)
	{
		if (tags is null || tags.Length == 0)
			return [];

		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var tag in tags)
		{
			var trimmed = tag?.Trim().ToLowerInvariant();
			if (!string.IsNullOrEmpty(trimmed))
				set.Add(trimmed);
		}

		var result = new string[set.Count];
		set.CopyTo(result);
		Array.Sort(result, StringComparer.Ordinal);
		return result;
	}

	private static string Sanitize(string value)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var chars = value.ToCharArray();
		for (var i = 0; i < chars.Length; i++)
		{
			if (Array.IndexOf(invalid, chars[i]) >= 0)
				chars[i] = '_';
		}
		return new string(chars);
	}

	// ── Structured Logging ──

	[LoggerMessage(Level = LogLevel.Information, Message = "Annotation set for run '{RunId}' (favorite={Favorite}, tags={TagCount})")]
	private partial void LogAnnotationSet(string runId, bool favorite, int tagCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Annotation removed for run '{RunId}'")]
	private partial void LogAnnotationRemoved(string runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Loaded} run annotation(s) from {Path} ({Failed} unreadable)")]
	private partial void LogAnnotationsLoaded(int loaded, int failed, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read run annotation from {Path}")]
	private partial void LogAnnotationLoadFailed(Exception ex, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to scan run annotations under {Path}")]
	private partial void LogAnnotationsScanFailed(Exception ex, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save annotation for run '{RunId}'")]
	private partial void LogAnnotationSaveFailed(Exception ex, string runId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete annotation for run '{RunId}'")]
	private partial void LogAnnotationDeleteFailed(Exception ex, string runId);
}
