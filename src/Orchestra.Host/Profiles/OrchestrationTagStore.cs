using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Profiles;

/// <summary>
/// Manages host-defined tags for orchestrations, persisted to disk.
/// These tags are merged with author-defined tags (from orchestration JSON)
/// to form the effective tag set for each orchestration.
/// </summary>
public partial class OrchestrationTagStore
{
	private readonly ConcurrentDictionary<string, HashSet<string>> _tags = new();
	private readonly string _persistPath;
	private readonly ILogger<OrchestrationTagStore> _logger;
	private readonly Lock _saveLock = new();

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	public OrchestrationTagStore(string dataPath, ILogger<OrchestrationTagStore> logger)
	{
		_persistPath = Path.Combine(dataPath, "orchestration-tags.json");
		_logger = logger;
		LoadFromDisk();
	}

	/// <summary>
	/// Gets the host-managed tags for a specific orchestration.
	/// </summary>
	public string[] GetTags(string orchestrationId)
	{
		return _tags.TryGetValue(orchestrationId, out var tags)
			? [.. tags]
			: [];
	}

	/// <summary>
	/// Gets the effective tags for an orchestration by merging author-defined tags
	/// (from the orchestration JSON) with host-managed tags.
	/// </summary>
	public string[] GetEffectiveTags(string orchestrationId, string[] authorTags)
	{
		var hostTags = GetTags(orchestrationId);
		if (hostTags.Length == 0)
			return authorTags.Length > 0 ? authorTags : [];
		if (authorTags.Length == 0)
			return hostTags;

		var combined = new HashSet<string>(authorTags, StringComparer.OrdinalIgnoreCase);
		foreach (var tag in hostTags)
			combined.Add(tag);
		return [.. combined];
	}

	/// <summary>
	/// Sets the host-managed tags for an orchestration, replacing any existing tags.
	/// </summary>
	public void SetTags(string orchestrationId, string[] tags)
	{
		var normalized = NormalizeTags(tags);
		if (normalized.Count == 0)
			_tags.TryRemove(orchestrationId, out _);
		else
			_tags[orchestrationId] = normalized;

		SaveToDisk();
		LogTagsSet(orchestrationId, normalized.Count);
	}

	/// <summary>
	/// Adds tags to an orchestration's host-managed tags, merging with existing.
	/// </summary>
	public void AddTags(string orchestrationId, string[] tags)
	{
		var normalized = NormalizeTags(tags);
		if (normalized.Count == 0)
			return;

		_tags.AddOrUpdate(
			orchestrationId,
			normalized,
			(_, existing) =>
			{
				foreach (var tag in normalized)
					existing.Add(tag);
				return existing;
			});

		SaveToDisk();
		LogTagsAdded(orchestrationId, normalized.Count);
	}

	/// <summary>
	/// Removes a specific tag from an orchestration's host-managed tags.
	/// </summary>
	public bool RemoveTag(string orchestrationId, string tag)
	{
		if (!_tags.TryGetValue(orchestrationId, out var tags))
			return false;

		var normalizedTag = tag.Trim().ToLowerInvariant();
		var removed = tags.Remove(normalizedTag);

		if (removed)
		{
			if (tags.Count == 0)
				_tags.TryRemove(orchestrationId, out _);
			SaveToDisk();
			LogTagRemoved(orchestrationId, normalizedTag);
		}

		return removed;
	}

	/// <summary>
	/// Removes all host-managed tags for an orchestration.
	/// Called when an orchestration is unregistered.
	/// </summary>
	public void RemoveOrchestration(string orchestrationId)
	{
		if (_tags.TryRemove(orchestrationId, out _))
			SaveToDisk();
	}

	/// <summary>
	/// Gets all known tags across all orchestrations with their usage counts.
	/// Includes both host-managed and author-defined tags.
	/// </summary>
	public Dictionary<string, int> GetAllTagsWithCounts(IEnumerable<(string OrchestrationId, string[] AuthorTags)> orchestrations)
	{
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		foreach (var (orchId, authorTags) in orchestrations)
		{
			var effective = GetEffectiveTags(orchId, authorTags);
			foreach (var tag in effective)
			{
				counts.TryGetValue(tag, out var count);
				counts[tag] = count + 1;
			}
		}

		return counts;
	}

	private void LoadFromDisk()
	{
		if (!File.Exists(_persistPath))
			return;

		try
		{
			var json = File.ReadAllText(_persistPath);
			var data = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json, s_jsonOptions);
			if (data is null)
				return;

			foreach (var (id, tags) in data)
			{
				_tags[id] = NormalizeTags(tags);
			}

			LogTagsLoadedFromDisk(_tags.Count, _persistPath);
		}
		catch (Exception ex)
		{
			LogTagsLoadFailed(ex, _persistPath);
		}
	}

	private void SaveToDisk()
	{
		lock (_saveLock)
		{
			try
			{
				var dir = Path.GetDirectoryName(_persistPath);
				if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
					Directory.CreateDirectory(dir);

				var data = _tags.ToDictionary(
					kvp => kvp.Key,
					kvp => kvp.Value.ToArray());

				var json = JsonSerializer.Serialize(data, s_jsonOptions);
				File.WriteAllText(_persistPath, json);
			}
			catch (Exception ex)
			{
				LogTagsSaveFailed(ex, _persistPath);
			}
		}
	}

	private static HashSet<string> NormalizeTags(string[] tags)
	{
		var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var tag in tags)
		{
			var trimmed = tag.Trim().ToLowerInvariant();
			if (!string.IsNullOrEmpty(trimmed))
				normalized.Add(trimmed);
		}
		return normalized;
	}

	// ── Structured Logging ──

	[LoggerMessage(Level = LogLevel.Information, Message = "Tags set for orchestration '{OrchestrationId}': {Count} tag(s)")]
	private partial void LogTagsSet(string orchestrationId, int count);

	[LoggerMessage(Level = LogLevel.Information, Message = "Tags added to orchestration '{OrchestrationId}': {Count} tag(s)")]
	private partial void LogTagsAdded(string orchestrationId, int count);

	[LoggerMessage(Level = LogLevel.Information, Message = "Tag '{Tag}' removed from orchestration '{OrchestrationId}'")]
	private partial void LogTagRemoved(string orchestrationId, string tag);

	[LoggerMessage(Level = LogLevel.Information, Message = "Loaded tags for {Count} orchestration(s) from {Path}")]
	private partial void LogTagsLoadedFromDisk(int count, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load tags from {Path}")]
	private partial void LogTagsLoadFailed(Exception ex, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save tags to {Path}")]
	private partial void LogTagsSaveFailed(Exception ex, string path);
}
