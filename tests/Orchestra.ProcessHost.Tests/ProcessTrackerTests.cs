using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Orchestra.ProcessHost.Tests;

/// <summary>
/// Unit tests for <see cref="ProcessTracker"/>.
/// Tests orphan detection, PID file tracking, and cleanup behavior.
/// </summary>
public class ProcessTrackerTests : IDisposable
{
	private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
	private readonly string _tempDir;
	private readonly string _pidFilePath;

	public ProcessTrackerTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_pidFilePath = Path.Combine(_tempDir, ".orchestra.pids.json");
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_tempDir))
				Directory.Delete(_tempDir, recursive: true);
		}
		catch
		{
			// Best effort cleanup
		}
	}

	private ProcessTracker CreateTracker() =>
		new(_pidFilePath, NullLogger<ProcessTracker>.Instance);

	#region CleanupOrphans

	[Fact]
	public void CleanupOrphans_NoPidFile_ReturnsZero()
	{
		using var tracker = CreateTracker();
		var result = tracker.CleanupOrphans();
		result.Should().Be(0);
	}

	[Fact]
	public void CleanupOrphans_EmptyPidFile_ReturnsZero()
	{
		File.WriteAllText(_pidFilePath, """{"sessionId":"abc","processes":{}}""");
		using var tracker = CreateTracker();

		var result = tracker.CleanupOrphans();

		result.Should().Be(0);
		File.Exists(_pidFilePath).Should().BeFalse("empty PID file should be deleted");
	}

	[Fact]
	public void CleanupOrphans_CorruptedPidFile_ReturnsZeroAndDeletesFile()
	{
		File.WriteAllText(_pidFilePath, "NOT VALID JSON {{{{");
		using var tracker = CreateTracker();

		var result = tracker.CleanupOrphans();

		result.Should().Be(0);
		File.Exists(_pidFilePath).Should().BeFalse("corrupted PID file should be deleted");
	}

	[Fact]
	public void CleanupOrphans_ProcessAlreadyExited_ReturnsZero()
	{
		// Write a PID file with a PID that doesn't exist
		var data = new ProcessTracker.PidFileData
		{
			SessionId = "old-session",
			Processes = new Dictionary<string, ProcessTracker.TrackedProcessEntry>
			{
				["dead-service"] = new()
				{
					ProcessId = 999999, // Very unlikely to exist
					Command = "nonexistent-command",
					StartTimeUtc = DateTime.UtcNow.AddHours(-1),
				}
			}
		};
		WritePidFile(data);
		using var tracker = CreateTracker();

		var result = tracker.CleanupOrphans();

		result.Should().Be(0, "process doesn't exist so nothing to kill");
		File.Exists(_pidFilePath).Should().BeFalse("PID file should be cleaned up after scan");
	}

	[Fact]
	public void CleanupOrphans_PidReused_SkipsProcess()
	{
		// Use the current process PID but with a wrong start time
		var currentProcess = Process.GetCurrentProcess();
		var data = new ProcessTracker.PidFileData
		{
			SessionId = "old-session",
			Processes = new Dictionary<string, ProcessTracker.TrackedProcessEntry>
			{
				["reused-pid-service"] = new()
				{
					ProcessId = currentProcess.Id,
					Command = "some-old-command",
					// Set start time far in the past so it won't match
					StartTimeUtc = DateTime.UtcNow.AddDays(-30),
				}
			}
		};
		WritePidFile(data);
		using var tracker = CreateTracker();

		var result = tracker.CleanupOrphans();

		// Should NOT kill the current process (PID reuse detection)
		result.Should().Be(0);
	}

	[Fact]
	public async Task CleanupOrphans_RealOrphan_KillsProcess()
	{
		// Start a real process that simulates an orphan
		var process = StartLongRunningProcess();
		try
		{
			var actualStartTime = process.StartTime.ToUniversalTime();

			var data = new ProcessTracker.PidFileData
			{
				SessionId = "old-session",
				Processes = new Dictionary<string, ProcessTracker.TrackedProcessEntry>
				{
					["orphan-service"] = new()
					{
						ProcessId = process.Id,
						Command = "test-orphan",
						StartTimeUtc = actualStartTime,
					}
				}
			};
			WritePidFile(data);
			using var tracker = CreateTracker();

			var result = tracker.CleanupOrphans();

			result.Should().Be(1);
			// Give the process a moment to exit after kill
			await Task.Delay(500);
			process.HasExited.Should().BeTrue("orphan should have been killed");
		}
		finally
		{
			try { process.Kill(entireProcessTree: true); } catch { }
			process.Dispose();
		}
	}

	#endregion

	#region TrackProcess

	[Fact]
	public void TrackProcess_CreatesPidFile()
	{
		using var tracker = CreateTracker();
		File.Exists(_pidFilePath).Should().BeFalse("no PID file yet");

		tracker.TrackProcess("my-service", 12345, "my-command");

		File.Exists(_pidFilePath).Should().BeTrue("PID file should be created");
		var data = ReadPidFile();
		data.Should().NotBeNull();
		data!.SessionId.Should().Be(tracker.SessionId);
		data.Processes.Should().ContainKey("my-service");
		data.Processes!["my-service"].ProcessId.Should().Be(12345);
		data.Processes["my-service"].Command.Should().Be("my-command");
	}

	[Fact]
	public void TrackProcess_MultipleCalls_TracksAll()
	{
		using var tracker = CreateTracker();

		tracker.TrackProcess("svc-a", 100, "cmd-a");
		tracker.TrackProcess("svc-b", 200, "cmd-b");

		var data = ReadPidFile();
		data!.Processes.Should().HaveCount(2);
		data.Processes.Should().ContainKey("svc-a");
		data.Processes.Should().ContainKey("svc-b");
	}

	[Fact]
	public void TrackProcess_SameName_OverwritesPrevious()
	{
		using var tracker = CreateTracker();

		tracker.TrackProcess("svc", 100, "cmd-old");
		tracker.TrackProcess("svc", 200, "cmd-new");

		var data = ReadPidFile();
		data!.Processes.Should().HaveCount(1);
		data.Processes!["svc"].ProcessId.Should().Be(200);
		data.Processes["svc"].Command.Should().Be("cmd-new");
	}

	#endregion

	#region UntrackProcess

	[Fact]
	public void UntrackProcess_RemovesEntry()
	{
		using var tracker = CreateTracker();
		tracker.TrackProcess("svc-a", 100, "cmd-a");
		tracker.TrackProcess("svc-b", 200, "cmd-b");

		tracker.UntrackProcess("svc-a");

		var data = ReadPidFile();
		data!.Processes.Should().HaveCount(1);
		data.Processes.Should().NotContainKey("svc-a");
		data.Processes.Should().ContainKey("svc-b");
	}

	[Fact]
	public void UntrackProcess_NonExistentName_NoOp()
	{
		using var tracker = CreateTracker();
		tracker.TrackProcess("svc", 100, "cmd");

		tracker.UntrackProcess("does-not-exist");

		var data = ReadPidFile();
		data!.Processes.Should().HaveCount(1);
	}

	#endregion

	#region Clear

	[Fact]
	public void Clear_DeletesPidFile()
	{
		using var tracker = CreateTracker();
		tracker.TrackProcess("svc", 100, "cmd");
		File.Exists(_pidFilePath).Should().BeTrue();

		tracker.Clear();

		File.Exists(_pidFilePath).Should().BeFalse("PID file should be deleted on clear");
	}

	[Fact]
	public void Clear_NoPidFile_NoOp()
	{
		using var tracker = CreateTracker();

		// Should not throw
		tracker.Clear();

		File.Exists(_pidFilePath).Should().BeFalse();
	}

	#endregion

	#region TrackProcess with real process (StartTime verification)

	[Fact]
	public async Task TrackProcess_RealProcess_RecordsStartTime()
	{
		var process = StartLongRunningProcess();
		try
		{
			using var tracker = CreateTracker();
			tracker.TrackProcess("real-svc", process.Id, "test-command");

			var data = ReadPidFile();
			data!.Processes!["real-svc"].StartTimeUtc.Should().NotBeNull(
				"start time should be recorded from the real process");
			data.Processes["real-svc"].ProcessId.Should().Be(process.Id);
		}
		finally
		{
			try { process.Kill(entireProcessTree: true); } catch { }
			process.Dispose();
		}
	}

	#endregion

	#region Helpers

	private void WritePidFile(ProcessTracker.PidFileData data)
	{
		var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true,
		});
		File.WriteAllText(_pidFilePath, json);
	}

	private ProcessTracker.PidFileData? ReadPidFile()
	{
		if (!File.Exists(_pidFilePath))
			return null;
		var json = File.ReadAllText(_pidFilePath);
		return JsonSerializer.Deserialize<ProcessTracker.PidFileData>(json, new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		});
	}

	/// <summary>
	/// Starts a lightweight long-running process suitable for testing.
	/// </summary>
	private static Process StartLongRunningProcess()
	{
		var startInfo = IsWindows
			? new ProcessStartInfo("ping", ["-t", "127.0.0.1"])
			: new ProcessStartInfo("sleep", ["3600"]);

		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;

		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start test process");

		return process;
	}

	#endregion
}
