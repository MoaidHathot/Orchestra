using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Host.Persistence;

/// <summary>
/// File-system backed version store for persisting orchestration version history and snapshots.
/// Layout:
///   {rootPath}/versions/{orchestration-id}/history.json        — version entries (metadata only)
///   {rootPath}/versions/{orchestration-id}/snapshots/{hash}.json — full orchestration JSON snapshot
/// </summary>
public partial class FileSystemOrchestrationVersionStore : IOrchestrationVersionStore
{
	private readonly string _rootPath;
	private readonly JsonSerializerOptions _jsonOptions;
	private readonly ILogger<FileSystemOrchestrationVersionStore> _logger;

	public FileSystemOrchestrationVersionStore(string rootPath, ILogger<FileSystemOrchestrationVersionStore> logger)
	{
		_rootPath = Path.Combine(rootPath, "versions");
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
	/// Gets the root path for version storage.
	/// </summary>
	public string RootPath => _rootPath;

	public async Task SaveVersionAsync(string orchestrationId, OrchestrationVersionEntry version, string orchestrationJson, CancellationToken cancellationToken = default)
	{
		var orchestrationDir = GetOrchestrationDirectory(orchestrationId);
		var snapshotsDir = Path.Combine(orchestrationDir, "snapshots");
		Directory.CreateDirectory(snapshotsDir);

		// Check if this content hash already exists (idempotent)
		var snapshotPath = Path.Combine(snapshotsDir, $"{version.ContentHash}.json");
		if (File.Exists(snapshotPath))
		{
			LogVersionAlreadyExists(orchestrationId, version.ContentHash);
			return;
		}

		// Save the snapshot (atomic write)
		var tempPath = snapshotPath + ".tmp";
		await File.WriteAllTextAsync(tempPath, orchestrationJson, cancellationToken);
		File.Move(tempPath, snapshotPath, overwrite: true);

		// Append to history
		var history = await LoadHistoryAsync(orchestrationId, cancellationToken);
		var mutableHistory = new List<OrchestrationVersionEntry>(history) { version };

		await SaveHistoryAsync(orchestrationId, mutableHistory, cancellationToken);

		LogVersionSaved(orchestrationId, version.ContentHash, version.DeclaredVersion);
	}

	public async Task<IReadOnlyList<OrchestrationVersionEntry>> ListVersionsAsync(string orchestrationId, CancellationToken cancellationToken = default)
	{
		var history = await LoadHistoryAsync(orchestrationId, cancellationToken);
		// Return newest first
		return history.OrderByDescending(v => v.Timestamp).ToList();
	}

	public async Task<string?> GetSnapshotAsync(string orchestrationId, string contentHash, CancellationToken cancellationToken = default)
	{
		var snapshotPath = Path.Combine(GetOrchestrationDirectory(orchestrationId), "snapshots", $"{contentHash}.json");
		if (!File.Exists(snapshotPath))
			return null;

		try
		{
			return await File.ReadAllTextAsync(snapshotPath, cancellationToken);
		}
		catch (Exception ex)
		{
			LogSnapshotLoadFailed(ex, orchestrationId, contentHash);
			return null;
		}
	}

	public async Task<OrchestrationVersionEntry?> GetLatestVersionAsync(string orchestrationId, CancellationToken cancellationToken = default)
	{
		var history = await LoadHistoryAsync(orchestrationId, cancellationToken);
		return history.OrderByDescending(v => v.Timestamp).FirstOrDefault();
	}

	public Task DeleteAllVersionsAsync(string orchestrationId, CancellationToken cancellationToken = default)
	{
		var orchestrationDir = GetOrchestrationDirectory(orchestrationId);
		if (Directory.Exists(orchestrationDir))
		{
			try
			{
				Directory.Delete(orchestrationDir, recursive: true);
				LogVersionsDeleted(orchestrationId);
			}
			catch (Exception ex)
			{
				LogVersionsDeleteFailed(ex, orchestrationId);
			}
		}

		return Task.CompletedTask;
	}

	// ── Static Utility Methods ──

	/// <summary>
	/// Computes the SHA-256 content hash of an orchestration JSON string.
	/// Returns a lowercase hex-encoded hash string.
	/// </summary>
	public static string ComputeContentHash(string orchestrationJson)
	{
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(orchestrationJson));
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}

	/// <summary>
	/// Generates a change description by comparing two orchestration version entries.
	/// </summary>
	public static string GenerateChangeDescription(OrchestrationVersionEntry? previous, OrchestrationVersionEntry current)
	{
		if (previous is null)
			return "Initial version";

		var parts = new List<string>();

		if (previous.DeclaredVersion != current.DeclaredVersion)
			parts.Add($"Version changed: {previous.DeclaredVersion} -> {current.DeclaredVersion}");

		if (previous.StepCount != current.StepCount)
		{
			var diff = current.StepCount - previous.StepCount;
			parts.Add(diff > 0 ? $"Steps: +{diff}" : $"Steps: {diff}");
		}

		if (previous.OrchestrationName != current.OrchestrationName)
			parts.Add($"Renamed: {previous.OrchestrationName} -> {current.OrchestrationName}");

		return parts.Count > 0 ? string.Join("; ", parts) : "Content updated";
	}

	/// <summary>
	/// Computes a simple line-by-line diff between two JSON strings.
	/// Returns a list of diff lines with +/- prefixes.
	/// </summary>
	public static IReadOnlyList<DiffLine> ComputeDiff(string oldJson, string newJson)
	{
		var oldLines = oldJson.ReplaceLineEndings("\n").Split('\n');
		var newLines = newJson.ReplaceLineEndings("\n").Split('\n');
		var result = new List<DiffLine>();

		// Use a simple LCS-based diff algorithm
		var lcs = ComputeLcs(oldLines, newLines);
		var i = 0;
		var j = 0;
		var k = 0;

		while (i < oldLines.Length || j < newLines.Length)
		{
			if (k < lcs.Length && i < oldLines.Length && j < newLines.Length && oldLines[i] == lcs[k] && newLines[j] == lcs[k])
			{
				result.Add(new DiffLine { Type = DiffLineType.Unchanged, Content = oldLines[i] });
				i++;
				j++;
				k++;
			}
			else if (j < newLines.Length && (k >= lcs.Length || newLines[j] != lcs[k]))
			{
				result.Add(new DiffLine { Type = DiffLineType.Added, Content = newLines[j] });
				j++;
			}
			else if (i < oldLines.Length && (k >= lcs.Length || oldLines[i] != lcs[k]))
			{
				result.Add(new DiffLine { Type = DiffLineType.Removed, Content = oldLines[i] });
				i++;
			}
		}

		return result;
	}

	private static string[] ComputeLcs(string[] a, string[] b)
	{
		var m = a.Length;
		var n = b.Length;
		var dp = new int[m + 1, n + 1];

		for (var i = 1; i <= m; i++)
		{
			for (var j = 1; j <= n; j++)
			{
				dp[i, j] = a[i - 1] == b[j - 1]
					? dp[i - 1, j - 1] + 1
					: Math.Max(dp[i - 1, j], dp[i, j - 1]);
			}
		}

		// Backtrack to find the LCS
		var result = new List<string>();
		var x = m;
		var y = n;
		while (x > 0 && y > 0)
		{
			if (a[x - 1] == b[y - 1])
			{
				result.Add(a[x - 1]);
				x--;
				y--;
			}
			else if (dp[x - 1, y] > dp[x, y - 1])
			{
				x--;
			}
			else
			{
				y--;
			}
		}

		result.Reverse();
		return [.. result];
	}

	// ── Private Helpers ──

	private string GetOrchestrationDirectory(string orchestrationId)
		=> Path.Combine(_rootPath, SanitizePath(orchestrationId));

	private async Task<List<OrchestrationVersionEntry>> LoadHistoryAsync(string orchestrationId, CancellationToken cancellationToken)
	{
		var historyPath = Path.Combine(GetOrchestrationDirectory(orchestrationId), "history.json");
		if (!File.Exists(historyPath))
			return [];

		try
		{
			var json = await File.ReadAllTextAsync(historyPath, cancellationToken);
			return JsonSerializer.Deserialize<List<OrchestrationVersionEntry>>(json, _jsonOptions) ?? [];
		}
		catch (Exception ex)
		{
			LogHistoryLoadFailed(ex, orchestrationId);
			return [];
		}
	}

	private async Task SaveHistoryAsync(string orchestrationId, List<OrchestrationVersionEntry> history, CancellationToken cancellationToken)
	{
		var orchestrationDir = GetOrchestrationDirectory(orchestrationId);
		Directory.CreateDirectory(orchestrationDir);

		var historyPath = Path.Combine(orchestrationDir, "history.json");
		var json = JsonSerializer.Serialize(history, _jsonOptions);

		// Atomic write
		var tempPath = historyPath + ".tmp";
		await File.WriteAllTextAsync(tempPath, json, cancellationToken);
		File.Move(tempPath, historyPath, overwrite: true);
	}

	private static string SanitizePath(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var sanitized = new char[name.Length];
		for (var i = 0; i < name.Length; i++)
			sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
		return new string(sanitized);
	}

	#region Source-Generated Logging

	[LoggerMessage(Level = LogLevel.Debug, Message = "Version already exists for orchestration '{OrchestrationId}', hash '{ContentHash}'.")]
	private partial void LogVersionAlreadyExists(string orchestrationId, string contentHash);

	[LoggerMessage(Level = LogLevel.Information, Message = "Version saved for orchestration '{OrchestrationId}', hash '{ContentHash}', declared version '{DeclaredVersion}'.")]
	private partial void LogVersionSaved(string orchestrationId, string contentHash, string declaredVersion);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load snapshot for orchestration '{OrchestrationId}', hash '{ContentHash}'.")]
	private partial void LogSnapshotLoadFailed(Exception ex, string orchestrationId, string contentHash);

	[LoggerMessage(Level = LogLevel.Information, Message = "All versions deleted for orchestration '{OrchestrationId}'.")]
	private partial void LogVersionsDeleted(string orchestrationId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete versions for orchestration '{OrchestrationId}'.")]
	private partial void LogVersionsDeleteFailed(Exception ex, string orchestrationId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load version history for orchestration '{OrchestrationId}'.")]
	private partial void LogHistoryLoadFailed(Exception ex, string orchestrationId);

	#endregion
}

/// <summary>
/// Represents a single line in a diff output.
/// </summary>
public class DiffLine
{
	public required DiffLineType Type { get; init; }
	public required string Content { get; init; }
}

/// <summary>
/// The type of change a diff line represents.
/// </summary>
public enum DiffLineType
{
	Unchanged,
	Added,
	Removed
}
