using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Profiles;

/// <summary>
/// File-system persistence for profiles. Each profile is stored as a separate
/// JSON file in {dataPath}/profiles/{profile-id}.json.
/// </summary>
public partial class ProfileStore
{
	private readonly string _profilesDir;
	private readonly string _historyDir;
	private readonly ILogger<ProfileStore> _logger;
	private readonly ConcurrentDictionary<string, Profile> _profiles = new();

	internal static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	public ProfileStore(string dataPath, ILogger<ProfileStore> logger)
	{
		_profilesDir = Path.Combine(dataPath, "profiles");
		_historyDir = Path.Combine(_profilesDir, "history");
		_logger = logger;
		Directory.CreateDirectory(_profilesDir);
		Directory.CreateDirectory(_historyDir);
	}

	/// <summary>
	/// Loads all profiles from disk into memory.
	/// </summary>
	public IReadOnlyCollection<Profile> LoadAll()
	{
		_profiles.Clear();

		if (!Directory.Exists(_profilesDir))
			return [];

		foreach (var file in Directory.GetFiles(_profilesDir, "*.json", SearchOption.TopDirectoryOnly))
		{
			try
			{
				var json = File.ReadAllText(file);
				var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions);
				if (profile is not null)
					_profiles[profile.Id] = profile;
			}
			catch (Exception ex)
			{
				LogProfileLoadFailed(ex, file);
			}
		}

		LogProfilesLoaded(_profiles.Count, _profilesDir);
		return _profiles.Values.ToArray();
	}

	/// <summary>
	/// Gets all profiles currently in memory.
	/// </summary>
	public IReadOnlyCollection<Profile> GetAll() => _profiles.Values.ToArray();

	/// <summary>
	/// Gets a profile by ID.
	/// </summary>
	public Profile? Get(string id) =>
		_profiles.TryGetValue(id, out var profile) ? profile : null;

	/// <summary>
	/// Checks whether a profile with the given ID exists.
	/// </summary>
	public bool Exists(string id) => _profiles.ContainsKey(id);

	/// <summary>
	/// Saves a profile to memory and disk.
	/// </summary>
	public void Save(Profile profile)
	{
		_profiles[profile.Id] = profile;
		PersistProfile(profile);
	}

	/// <summary>
	/// Removes a profile from memory and disk.
	/// </summary>
	public bool Remove(string id)
	{
		if (!_profiles.TryRemove(id, out _))
			return false;

		var filePath = GetProfilePath(id);
		if (File.Exists(filePath))
		{
			try { File.Delete(filePath); }
			catch (Exception ex) { LogProfileDeleteFailed(ex, filePath); }
		}

		return true;
	}

	/// <summary>
	/// Gets the number of profiles.
	/// </summary>
	public int Count => _profiles.Count;

	/// <summary>
	/// Appends a history entry for a profile.
	/// </summary>
	public void AppendHistory(string profileId, ProfileHistoryEntry entry)
	{
		var historyPath = GetHistoryPath(profileId);

		try
		{
			List<ProfileHistoryEntry> history;
			if (File.Exists(historyPath))
			{
				var existing = File.ReadAllText(historyPath);
				history = JsonSerializer.Deserialize<List<ProfileHistoryEntry>>(existing, JsonOptions) ?? [];
			}
			else
			{
				history = [];
			}

			history.Add(entry);

			// Keep last 500 entries to prevent unbounded growth
			if (history.Count > 500)
				history = history.Skip(history.Count - 500).ToList();

			var json = JsonSerializer.Serialize(history, JsonOptions);
			File.WriteAllText(historyPath, json);
		}
		catch (Exception ex)
		{
			LogHistoryAppendFailed(ex, profileId);
		}
	}

	/// <summary>
	/// Gets the history entries for a profile.
	/// </summary>
	public List<ProfileHistoryEntry> GetHistory(string profileId)
	{
		var historyPath = GetHistoryPath(profileId);
		if (!File.Exists(historyPath))
			return [];

		try
		{
			var json = File.ReadAllText(historyPath);
			return JsonSerializer.Deserialize<List<ProfileHistoryEntry>>(json, JsonOptions) ?? [];
		}
		catch (Exception ex)
		{
			LogHistoryLoadFailed(ex, profileId);
			return [];
		}
	}

	/// <summary>
	/// Generates a profile ID from a name.
	/// </summary>
	public static string GenerateId(string name)
	{
		var sanitized = new string(name
			.ToLowerInvariant()
			.Select(c => char.IsLetterOrDigit(c) ? c : '-')
			.ToArray())
			.Trim('-');

		// Add a short hash to avoid collisions
		var hash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..6].ToLowerInvariant();

		return $"{sanitized}-{hash}";
	}

	private void PersistProfile(Profile profile)
	{
		try
		{
			var json = JsonSerializer.Serialize(profile, JsonOptions);
			File.WriteAllText(GetProfilePath(profile.Id), json);
		}
		catch (Exception ex)
		{
			LogProfileSaveFailed(ex, profile.Id);
		}
	}

	private string GetProfilePath(string id) => Path.Combine(_profilesDir, $"{id}.json");
	private string GetHistoryPath(string profileId) => Path.Combine(_historyDir, $"{profileId}-history.json");

	// ── Structured Logging ──

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load profile from {Path}")]
	private partial void LogProfileLoadFailed(Exception ex, string path);

	[LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} profile(s) from {Path}")]
	private partial void LogProfilesLoaded(int count, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete profile file {Path}")]
	private partial void LogProfileDeleteFailed(Exception ex, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save profile '{ProfileId}'")]
	private partial void LogProfileSaveFailed(Exception ex, string profileId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to append history for profile '{ProfileId}'")]
	private partial void LogHistoryAppendFailed(Exception ex, string profileId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load history for profile '{ProfileId}'")]
	private partial void LogHistoryLoadFailed(Exception ex, string profileId);
}
