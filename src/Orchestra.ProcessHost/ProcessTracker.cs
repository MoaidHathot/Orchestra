using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Orchestra.ProcessHost;

/// <summary>
/// Tracks managed process PIDs in a file on disk so that orphaned processes from a
/// previous Orchestra session (e.g., after a crash) can be detected and cleaned up
/// on the next startup.
///
/// <para>
/// Each tracked process is recorded with its PID, command, and start time. On cleanup,
/// the start time is compared against the actual process start time to distinguish
/// Orchestra-spawned processes from unrelated processes that happen to reuse the same PID.
/// </para>
/// </summary>
public sealed partial class ProcessTracker : IDisposable
{
	private readonly string _pidFilePath;
	private readonly ILogger _logger;
	private readonly string _sessionId;
	private readonly object _lock = new();
	private readonly Dictionary<string, TrackedProcessEntry> _trackedProcesses = new();
	private bool _disposed;

	/// <summary>
	/// Maximum allowed difference in seconds between the stored and actual process
	/// start times. Accounts for clock precision differences across platforms.
	/// </summary>
	private const double StartTimeToleranceSeconds = 2.0;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true,
	};

	public ProcessTracker(string pidFilePath, ILogger<ProcessTracker> logger)
	{
		_pidFilePath = pidFilePath;
		_logger = logger;
		_sessionId = Guid.NewGuid().ToString("N")[..12];
	}

	/// <summary>
	/// Gets the current session ID for this Orchestra instance.
	/// </summary>
	public string SessionId => _sessionId;

	/// <summary>
	/// Gets the path to the PID file.
	/// </summary>
	internal string PidFilePath => _pidFilePath;

	/// <summary>
	/// Checks for orphaned processes from a previous Orchestra session and kills them.
	/// An orphan is a process whose PID and start time match a record in the PID file
	/// left behind by a previous session that did not shut down cleanly.
	/// </summary>
	/// <returns>The number of orphaned processes that were successfully killed.</returns>
	public int CleanupOrphans()
	{
		if (!File.Exists(_pidFilePath))
			return 0;

		PidFileData? data;
		try
		{
			var json = File.ReadAllText(_pidFilePath);
			data = JsonSerializer.Deserialize<PidFileData>(json, JsonOptions);
		}
		catch (Exception ex)
		{
			LogPidFileCorrupted(_pidFilePath, ex);
			TryDeletePidFile();
			return 0;
		}

		if (data?.Processes is null || data.Processes.Count == 0)
		{
			TryDeletePidFile();
			return 0;
		}

		LogOrphanScanStarted(data.Processes.Count, data.SessionId ?? "unknown");

		var killed = 0;
		foreach (var (name, entry) in data.Processes)
		{
			try
			{
				var process = Process.GetProcessById(entry.ProcessId);

				// Verify start time to ensure we're not killing a process that reused this PID
				if (entry.StartTimeUtc.HasValue)
				{
					DateTime actualStartTimeUtc;
					try
					{
						actualStartTimeUtc = process.StartTime.ToUniversalTime();
					}
					catch (Exception)
					{
						// Cannot read start time (permissions, etc.) — skip to be safe
						LogOrphanStartTimeUnreadable(name, entry.ProcessId);
						continue;
					}

					var drift = Math.Abs((actualStartTimeUtc - entry.StartTimeUtc.Value).TotalSeconds);
					if (drift > StartTimeToleranceSeconds)
					{
						LogOrphanPidReused(name, entry.ProcessId);
						continue;
					}
				}

				LogKillingOrphan(name, entry.ProcessId, entry.Command);
				process.Kill(entireProcessTree: true);
				killed++;
				LogOrphanKilled(name, entry.ProcessId);
			}
			catch (ArgumentException)
			{
				// Process no longer exists — it exited on its own
				LogOrphanAlreadyExited(name, entry.ProcessId);
			}
			catch (InvalidOperationException)
			{
				// Process already exited between GetProcessById and Kill
				LogOrphanAlreadyExited(name, entry.ProcessId);
			}
			catch (Exception ex)
			{
				LogOrphanKillFailed(name, entry.ProcessId, ex);
			}
		}

		TryDeletePidFile();

		if (killed > 0)
			LogOrphanCleanupCompleted(killed);

		return killed;
	}

	/// <summary>
	/// Records a process as being tracked by this Orchestra session.
	/// The process start time is read from the OS for later verification during orphan cleanup.
	/// </summary>
	/// <param name="name">The service name.</param>
	/// <param name="processId">The OS process ID.</param>
	/// <param name="command">The command used to start the process.</param>
	public void TrackProcess(string name, int processId, string command)
	{
		DateTime? startTimeUtc = null;
		try
		{
			var process = Process.GetProcessById(processId);
			startTimeUtc = process.StartTime.ToUniversalTime();
		}
		catch
		{
			// Best effort — start time may not be readable
		}

		lock (_lock)
		{
			_trackedProcesses[name] = new TrackedProcessEntry
			{
				ProcessId = processId,
				Command = command,
				StartTimeUtc = startTimeUtc,
			};
			WritePidFile();
		}
	}

	/// <summary>
	/// Removes a process from tracking (e.g., after it exits or is stopped).
	/// </summary>
	/// <param name="name">The service name to untrack.</param>
	public void UntrackProcess(string name)
	{
		lock (_lock)
		{
			if (_trackedProcesses.Remove(name))
				WritePidFile();
		}
	}

	/// <summary>
	/// Removes the PID file entirely. Called during clean shutdown to indicate
	/// that no orphans should be expected on the next startup.
	/// </summary>
	public void Clear()
	{
		lock (_lock)
		{
			_trackedProcesses.Clear();
			TryDeletePidFile();
		}
	}

	/// <summary>
	/// Writes the current tracked processes to the PID file.
	/// </summary>
	private void WritePidFile()
	{
		try
		{
			var dir = Path.GetDirectoryName(_pidFilePath);
			if (dir is not null && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			var data = new PidFileData
			{
				SessionId = _sessionId,
				Processes = new Dictionary<string, TrackedProcessEntry>(_trackedProcesses),
			};

			var json = JsonSerializer.Serialize(data, JsonOptions);
			File.WriteAllText(_pidFilePath, json);
		}
		catch (Exception ex)
		{
			LogPidFileWriteFailed(_pidFilePath, ex);
		}
	}

	private void TryDeletePidFile()
	{
		try
		{
			if (File.Exists(_pidFilePath))
				File.Delete(_pidFilePath);
		}
		catch (Exception ex)
		{
			LogPidFileDeleteFailed(_pidFilePath, ex);
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			// Don't delete the PID file on dispose — only Clear() should do that
			// (on clean shutdown). If Orchestra crashes, the file remains for orphan detection.
		}
	}

	#region PID File Models

	/// <summary>
	/// On-disk format for the PID tracking file.
	/// </summary>
	internal class PidFileData
	{
		public string? SessionId { get; set; }
		public Dictionary<string, TrackedProcessEntry>? Processes { get; set; }
	}

	/// <summary>
	/// Represents a single tracked process entry in the PID file.
	/// </summary>
	internal class TrackedProcessEntry
	{
		public int ProcessId { get; set; }
		public string Command { get; set; } = "";
		public DateTime? StartTimeUtc { get; set; }
	}

	#endregion

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 200,
		Level = LogLevel.Warning,
		Message = "PID file at '{PidFilePath}' is corrupted and will be deleted")]
	private partial void LogPidFileCorrupted(string pidFilePath, Exception ex);

	[LoggerMessage(
		EventId = 201,
		Level = LogLevel.Information,
		Message = "Scanning for orphaned processes from previous session '{SessionId}' ({Count} tracked)")]
	private partial void LogOrphanScanStarted(int count, string sessionId);

	[LoggerMessage(
		EventId = 202,
		Level = LogLevel.Information,
		Message = "Killing orphaned process '{ServiceName}' (PID {ProcessId}, command: {Command})")]
	private partial void LogKillingOrphan(string serviceName, int processId, string command);

	[LoggerMessage(
		EventId = 203,
		Level = LogLevel.Information,
		Message = "Orphaned process '{ServiceName}' (PID {ProcessId}) killed successfully")]
	private partial void LogOrphanKilled(string serviceName, int processId);

	[LoggerMessage(
		EventId = 204,
		Level = LogLevel.Debug,
		Message = "Orphaned process '{ServiceName}' (PID {ProcessId}) already exited")]
	private partial void LogOrphanAlreadyExited(string serviceName, int processId);

	[LoggerMessage(
		EventId = 205,
		Level = LogLevel.Debug,
		Message = "PID {ProcessId} for service '{ServiceName}' was reused by a different process, skipping")]
	private partial void LogOrphanPidReused(string serviceName, int processId);

	[LoggerMessage(
		EventId = 206,
		Level = LogLevel.Debug,
		Message = "Cannot read start time for PID {ProcessId} (service '{ServiceName}'), skipping to avoid killing unrelated process")]
	private partial void LogOrphanStartTimeUnreadable(string serviceName, int processId);

	[LoggerMessage(
		EventId = 207,
		Level = LogLevel.Warning,
		Message = "Failed to kill orphaned process '{ServiceName}' (PID {ProcessId})")]
	private partial void LogOrphanKillFailed(string serviceName, int processId, Exception ex);

	[LoggerMessage(
		EventId = 208,
		Level = LogLevel.Information,
		Message = "Orphan cleanup completed: {Count} process(es) killed")]
	private partial void LogOrphanCleanupCompleted(int count);

	[LoggerMessage(
		EventId = 209,
		Level = LogLevel.Warning,
		Message = "Failed to write PID file at '{PidFilePath}'")]
	private partial void LogPidFileWriteFailed(string pidFilePath, Exception ex);

	[LoggerMessage(
		EventId = 210,
		Level = LogLevel.Warning,
		Message = "Failed to delete PID file at '{PidFilePath}'")]
	private partial void LogPidFileDeleteFailed(string pidFilePath, Exception ex);

	#endregion
}
