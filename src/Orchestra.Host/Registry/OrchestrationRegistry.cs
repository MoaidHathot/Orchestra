using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;

namespace Orchestra.Host.Registry;

/// <summary>
/// In-memory registry of loaded orchestrations with persistence support.
/// </summary>
public partial class OrchestrationRegistry
{
	private readonly ConcurrentDictionary<string, OrchestrationEntry> _entries = new();
	private readonly string _persistPath;
	private readonly string? _managedOrchestrationsPath;
	private readonly ILogger<OrchestrationRegistry> _logger;
	private readonly IOrchestrationVersionStore? _versionStore;
	private readonly JsonSerializerOptions _jsonOptions;

	public OrchestrationRegistry(string? persistPath = null, ILogger<OrchestrationRegistry>? logger = null, IOrchestrationVersionStore? versionStore = null, string? dataPath = null)
	{
		_persistPath = persistPath ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"OrchestraHost",
			"registered-orchestrations.json");
		_logger = logger ?? NullLoggerFactory.Instance.CreateLogger<OrchestrationRegistry>();
		_versionStore = versionStore;
		_jsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		// Set up managed orchestrations directory
		if (dataPath is not null)
		{
			_managedOrchestrationsPath = Path.Combine(dataPath, "orchestrations");
			Directory.CreateDirectory(_managedOrchestrationsPath);
		}
	}

	/// <summary>
	/// Gets the number of registered orchestrations.
	/// </summary>
	public int Count => _entries.Count;

	/// <summary>
	/// Gets or sets the global MCPs (from orchestra.mcp.json) that are available
	/// when parsing orchestration files. Set by the host during initialization
	/// so that orchestrations referencing global MCP names can resolve them.
	/// </summary>
	public Engine.Mcp[] GlobalMcps { get; set; } = [];

	/// <summary>
	/// Gets the version store used for tracking orchestration version history.
	/// May be null if no version store was configured.
	/// </summary>
	public IOrchestrationVersionStore? VersionStore => _versionStore;

	/// <summary>
	/// Registers an orchestration from a file path.
	/// If a managed orchestrations directory is configured, the file is copied there
	/// and the managed path is stored instead of the original.
	/// </summary>
	public OrchestrationEntry Register(string path, Orchestration? preloaded = null, bool persist = true, string? originalSourcePath = null)
	{
		// Read the raw content for hashing and snapshots (always as JSON for internal storage)
		string? rawJson = null;
		if (File.Exists(path))
		{
			var rawContent = File.ReadAllText(path);
			rawJson = OrchestrationParser.IsYamlFile(path) ? OrchestrationParser.ConvertYamlToJson(rawContent) : rawContent;
		}

		var orchestration = preloaded ?? OrchestrationParser.ParseOrchestrationFile(path, GlobalMcps);

		// Use the original source path for ID generation when available.
		// This ensures a stable ID when the orchestration file has been copied
		// to a managed location (whose path differs from the original).
		var idPath = originalSourcePath ?? path;
		var id = GenerateId(orchestration.Name, idPath);

		// Compute content hash if we have the raw JSON
		var contentHash = rawJson is not null ? FileSystemOrchestrationVersionStore.ComputeContentHash(rawJson) : null;

		// Copy to managed location if configured
		var effectivePath = path;
		string? sourcePath = originalSourcePath;
		if (_managedOrchestrationsPath is not null && rawJson is not null)
		{
			effectivePath = CopyToManagedLocation(id, orchestration.Name, rawJson);
			// Track the original source path so we can regenerate the same ID on reload.
			// If we're already loading from a managed copy (originalSourcePath provided),
			// preserve that original source path.
			sourcePath ??= path;
		}

		var entry = new OrchestrationEntry
		{
			Id = id,
			Path = effectivePath,
			SourcePath = sourcePath,
			Orchestration = orchestration,
			RegisteredAt = DateTimeOffset.UtcNow,
			ContentHash = contentHash
		};

		// Check if this is a new or changed version, and auto-snapshot
		if (_versionStore is not null && rawJson is not null && contentHash is not null)
		{
			_ = SnapshotVersionAsync(id, entry, rawJson, contentHash);
		}

		_entries[id] = entry;

		if (persist)
			SaveToDisk();

		return entry;
	}

	/// <summary>
	/// Registers an orchestration directly from JSON content (no source file needed).
	/// The content is written to the managed location if configured, or a temp directory otherwise.
	/// </summary>
	public OrchestrationEntry RegisterFromJson(string json, Orchestration? preloaded = null, bool persist = true)
	{
		var orchestration = preloaded ?? OrchestrationParser.ParseOrchestration(json, GlobalMcps);

		// Write to managed location or temp
		string filePath;
		if (_managedOrchestrationsPath is not null)
		{
			// Generate an ID using a temporary path for consistent hashing
			var tempId = GenerateId(orchestration.Name, $"json-import:{orchestration.Name}");
			filePath = CopyToManagedLocation(tempId, orchestration.Name, json);
		}
		else
		{
			// Fallback: write to a temp directory (legacy behavior)
			var tempDir = Path.Combine(Path.GetTempPath(), "orchestra-host");
			Directory.CreateDirectory(tempDir);
			var fileName = $"{SanitizePath(orchestration.Name)}.json";
			filePath = Path.Combine(tempDir, fileName);
			File.WriteAllText(filePath, json);
		}

		return Register(filePath, preloaded: orchestration, persist: persist);
	}

	private async Task SnapshotVersionAsync(string orchestrationId, OrchestrationEntry entry, string rawJson, string contentHash)
	{
		try
		{
			var previousVersion = await _versionStore!.GetLatestVersionAsync(orchestrationId);

			var versionEntry = new OrchestrationVersionEntry
			{
				ContentHash = contentHash,
				DeclaredVersion = entry.Orchestration.Version,
				Timestamp = DateTimeOffset.UtcNow,
				OrchestrationName = entry.Orchestration.Name,
				StepCount = entry.Orchestration.Steps.Length,
				ChangeDescription = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previousVersion, new OrchestrationVersionEntry
				{
					ContentHash = contentHash,
					DeclaredVersion = entry.Orchestration.Version,
					Timestamp = DateTimeOffset.UtcNow,
					OrchestrationName = entry.Orchestration.Name,
					StepCount = entry.Orchestration.Steps.Length
				})
			};

			await _versionStore.SaveVersionAsync(orchestrationId, versionEntry, rawJson);
		}
		catch (Exception ex)
		{
			LogVersionSnapshotFailed(ex, orchestrationId);
		}
	}

	/// <summary>
	/// Gets an orchestration entry by ID.
	/// </summary>
	public OrchestrationEntry? Get(string id) =>
		_entries.TryGetValue(id, out var entry) ? entry : null;

	/// <summary>
	/// Gets all registered orchestration entries.
	/// </summary>
	public IEnumerable<OrchestrationEntry> GetAll() => _entries.Values;

	/// <summary>
	/// Removes an orchestration by ID.
	/// </summary>
	public bool Remove(string id)
	{
		var removed = _entries.TryRemove(id, out _);
		if (removed)
			SaveToDisk();
		return removed;
	}

	/// <summary>
	/// Clears all registered orchestrations.
	/// </summary>
	public void Clear()
	{
		_entries.Clear();
		SaveToDisk();
	}

	/// <summary>
	/// Save registered orchestration paths to disk for persistence.
	/// </summary>
	public void SaveToDisk()
	{
		try
		{
			var dir = Path.GetDirectoryName(_persistPath);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			var data = _entries.Values.Select(e => new PersistedOrchestration
			{
				Path = e.Path,
				SourcePath = e.SourcePath
			}).ToList();

			var json = JsonSerializer.Serialize(data, _jsonOptions);
			File.WriteAllText(_persistPath, json);
			LogOrchestrationsPersistedToDisk(data.Count, _persistPath);
		}
		catch (Exception ex)
		{
			LogOrchestrationsSaveFailed(ex);
		}
	}

	/// <summary>
	/// Load registered orchestrations from disk.
	/// </summary>
	public int LoadFromDisk()
	{
		if (!File.Exists(_persistPath))
		{
			LogNoPersistedOrchestrationsFound(_persistPath);
			return 0;
		}

		try
		{
			var json = File.ReadAllText(_persistPath);
			var data = JsonSerializer.Deserialize<List<PersistedOrchestration>>(json, new JsonSerializerOptions
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase
			}) ?? [];

			var loaded = 0;
			foreach (var item in data)
			{
				if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
				{
					LogSkippingMissingOrchestrationFile(item.Path ?? "(null)");
					continue;
				}

				try
				{
					Register(item.Path, persist: false, originalSourcePath: item.SourcePath);
					loaded++;
				}
				catch (Exception ex)
				{
					LogOrchestrationLoadFailed(ex, item.Path);
				}
			}

			LogOrchestrationsLoadedFromDisk(loaded, _persistPath);
			return loaded;
		}
		catch (Exception ex)
		{
			LogOrchestrationsLoadFailed(ex);
			return 0;
		}
	}

	/// <summary>
	/// Scans a directory for orchestration files and registers them.
	/// </summary>
	public int ScanDirectory(string directory)
	{
		if (!Directory.Exists(directory))
			return 0;

		var loaded = 0;
		foreach (var file in OrchestrationParser.GetOrchestrationFiles(directory))
		{
			try
			{
				Register(file, persist: false);
				loaded++;
			}
			catch (Exception ex)
			{
				LogSkippingInvalidOrchestrationFile(ex, file);
			}
		}

		if (loaded > 0)
			SaveToDisk();

		return loaded;
	}

	/// <summary>
	/// Synchronizes the registry with a directory of orchestration files.
	/// Registers new files, updates changed files (by content hash), and removes
	/// entries whose source files no longer exist in the directory.
	/// </summary>
	/// <returns>A <see cref="SyncResult"/> describing what changed.</returns>
	public SyncResult SyncDirectory(string directory, bool recursive = false)
	{
		if (!Directory.Exists(directory))
			return new SyncResult();

		var searchOption = recursive
			? SearchOption.AllDirectories
			: SearchOption.TopDirectoryOnly;

		var files = OrchestrationParser.GetOrchestrationFiles(directory, searchOption);
		var fullDirPath = Path.GetFullPath(directory)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			+ Path.DirectorySeparatorChar;

		int added = 0, updated = 0, unchanged = 0, failed = 0;
		var processedIds = new HashSet<string>();

		foreach (var file in files)
		{
			try
			{
				// Read content and compute hash
				var rawContent = File.ReadAllText(file);
				var rawJson = OrchestrationParser.IsYamlFile(file)
					? OrchestrationParser.ConvertYamlToJson(rawContent)
					: rawContent;
				var contentHash = FileSystemOrchestrationVersionStore.ComputeContentHash(rawJson);

				// Parse metadata to get the name for ID generation
				var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);
				var id = GenerateId(orchestration.Name, file);
				processedIds.Add(id);

				// Check if already registered with the same content
				var existing = Get(id);
				if (existing is not null && existing.ContentHash == contentHash)
				{
					unchanged++;
					continue;
				}

				Register(file, persist: false);

				if (existing is not null)
					updated++;
				else
					added++;
			}
			catch (Exception ex)
			{
				LogSkippingInvalidOrchestrationFile(ex, file);
				failed++;
			}
		}

		// Remove entries whose source files were in this directory but no longer exist
		var removed = 0;
		foreach (var entry in GetAll().ToList())
		{
			var entrySourcePath = Path.GetFullPath(entry.SourcePath ?? entry.Path);
			if (entrySourcePath.StartsWith(fullDirPath, StringComparison.OrdinalIgnoreCase)
				&& !processedIds.Contains(entry.Id))
			{
				Remove(entry.Id);
				removed++;
			}
		}

		if (added > 0 || updated > 0 || removed > 0)
			SaveToDisk();

		LogSyncCompleted(directory, added, updated, removed, unchanged, failed);
		return new SyncResult
		{
			Added = added,
			Updated = updated,
			Removed = removed,
			Unchanged = unchanged,
			Failed = failed
		};
	}

	/// <summary>
	/// Generates a unique ID for an orchestration.
	/// </summary>
	public static string GenerateId(string name, string path)
	{
		var hash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8].ToLowerInvariant();
		return $"{SanitizeId(name)}-{hash[..4]}";
	}

	private static string SanitizeId(string name)
	{
		return new string(name
			.ToLowerInvariant()
			.Select(c => char.IsLetterOrDigit(c) ? c : '-')
			.ToArray())
			.Trim('-');
	}

	/// <summary>
	/// Copies an orchestration JSON to the managed orchestrations directory.
	/// Returns the managed file path.
	/// </summary>
	private string CopyToManagedLocation(string id, string name, string jsonContent)
	{
		var fileName = $"{SanitizePath(name)}-{id.Split('-').Last()}.json";
		var managedPath = Path.Combine(_managedOrchestrationsPath!, fileName);
		File.WriteAllText(managedPath, jsonContent);
		LogOrchestrationCopiedToManaged(name, managedPath);
		return managedPath;
	}

	/// <summary>
	/// Sanitizes a name for use in a file path (removes special characters).
	/// </summary>
	private static string SanitizePath(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		return new string(name
			.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c)
			.ToArray())
			.Trim('-');
	}

	// ── Structured Logging ──

	[LoggerMessage(Level = LogLevel.Information, Message = "Saved {Count} orchestrations to {Path}")]
	private partial void LogOrchestrationsPersistedToDisk(int count, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save orchestrations to disk")]
	private partial void LogOrchestrationsSaveFailed(Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "No persisted orchestrations file found at {Path}")]
	private partial void LogNoPersistedOrchestrationsFound(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Skipping missing orchestration file: {Path}")]
	private partial void LogSkippingMissingOrchestrationFile(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load orchestration from {Path}")]
	private partial void LogOrchestrationLoadFailed(Exception ex, string path);

	[LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} orchestrations from {Path}")]
	private partial void LogOrchestrationsLoadedFromDisk(int count, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load orchestrations from disk")]
	private partial void LogOrchestrationsLoadFailed(Exception ex);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to snapshot version for orchestration '{OrchestrationId}'")]
	private partial void LogVersionSnapshotFailed(Exception ex, string orchestrationId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Orchestration '{Name}' copied to managed location: {ManagedPath}")]
	private partial void LogOrchestrationCopiedToManaged(string name, string managedPath);

	[LoggerMessage(Level = LogLevel.Information, Message = "Directory sync completed for '{Directory}': {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Failed} failed")]
	private partial void LogSyncCompleted(string directory, int added, int updated, int removed, int unchanged, int failed);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Skipping invalid orchestration file '{File}'")]
	private partial void LogSkippingInvalidOrchestrationFile(Exception ex, string file);
}

/// <summary>
/// Minimal data needed to reload an orchestration.
/// </summary>
public class PersistedOrchestration
{
	public string Path { get; set; } = "";

	/// <summary>
	/// The original source path used when first registering the orchestration.
	/// When a managed copy is created, this tracks the original file path so the
	/// same deterministic ID can be regenerated on reload.
	/// </summary>
	public string? SourcePath { get; set; }
}

/// <summary>
/// Entry in the orchestration registry.
/// </summary>
public class OrchestrationEntry
{
	public required string Id { get; init; }
	public required string Path { get; init; }

	/// <summary>
	/// The original source path that was used to generate the entry's ID.
	/// Null when the entry was not copied to a managed location (Path == source).
	/// </summary>
	public string? SourcePath { get; init; }

	public required Orchestration Orchestration { get; init; }
	public DateTimeOffset RegisteredAt { get; init; }

	/// <summary>
	/// SHA-256 content hash of the orchestration JSON source file.
	/// Used to detect content changes and deduplicate version snapshots.
	/// </summary>
	public string? ContentHash { get; init; }
}

/// <summary>
/// Result of a directory synchronization operation.
/// </summary>
public record SyncResult
{
	/// <summary>Number of new orchestrations registered.</summary>
	public int Added { get; init; }

	/// <summary>Number of existing orchestrations updated due to content changes.</summary>
	public int Updated { get; init; }

	/// <summary>Number of orchestrations removed because their source file was deleted.</summary>
	public int Removed { get; init; }

	/// <summary>Number of orchestrations that were already up-to-date.</summary>
	public int Unchanged { get; init; }

	/// <summary>Number of files that failed to parse and were skipped.</summary>
	public int Failed { get; init; }
}
