using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Orchestra.ProcessHost.Tests;

/// <summary>
/// Unit tests for <see cref="ManagedProcess"/>.
/// These tests launch real lightweight processes to verify process lifecycle behavior.
/// </summary>
public class ManagedProcessTests
{
	private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	[Fact]
	public async Task StartAsync_LaunchesProcess_SetsStateToRunning()
	{
		var config = CreatePingProcess("running-test");
		await using var managed = new ManagedProcess(config, NullLogger.Instance);

		var started = await managed.StartAsync();

		started.Should().BeTrue();
		managed.State.Should().Be(ProcessState.Running);
		managed.HasExited.Should().BeFalse();
	}

	[Fact]
	public async Task StopAsync_AlreadyStopped_NoOp()
	{
		var config = CreatePingProcess("noop-test");
		await using var managed = new ManagedProcess(config, NullLogger.Instance);

		// Never started — should be a no-op
		await managed.StopAsync();
		managed.State.Should().Be(ProcessState.Pending);
	}

	[Fact]
	public async Task StopAsync_RunningProcess_StopsGracefully()
	{
		var config = CreatePingProcess("stop-test");
		await using var managed = new ManagedProcess(config, NullLogger.Instance);

		await managed.StartAsync();
		managed.State.Should().Be(ProcessState.Running);

		await managed.StopAsync(timeoutSeconds: 5);
		managed.State.Should().Be(ProcessState.Stopped);
		managed.HasExited.Should().BeTrue();
	}

	[Fact]
	public async Task StartAsync_WithReadinessPattern_WaitsForMatch()
	{
		ProcessService config;
		if (IsWindows)
		{
			config = new ProcessService
			{
				Name = "ready-test",
				Command = "cmd.exe",
				Arguments = ["/c", "echo Server is ready && ping -t 127.0.0.1 >nul"],
				Readiness = new ReadinessCheck
				{
					StdoutPattern = "Server is ready",
					TimeoutSeconds = 10,
				},
				ShutdownTimeoutSeconds = 3,
			};
		}
		else
		{
			config = new ProcessService
			{
				Name = "ready-test",
				Command = "sh",
				Arguments = ["-c", "echo 'Server is ready' && sleep 3600"],
				Readiness = new ReadinessCheck
				{
					StdoutPattern = "Server is ready",
					TimeoutSeconds = 10,
				},
				ShutdownTimeoutSeconds = 3,
			};
		}

		await using var managed = new ManagedProcess(config, NullLogger.Instance);

		var started = await managed.StartAsync();

		started.Should().BeTrue();
		managed.State.Should().Be(ProcessState.Ready);
	}

	[Fact]
	public async Task StartAsync_ReadinessTimeout_NotRequired_ReturnsTrue()
	{
		// Use a pattern that will never match, with a short timeout
		var config = new ProcessService
		{
			Name = "timeout-test",
			Command = IsWindows ? "ping" : "sleep",
			Arguments = IsWindows ? ["-t", "127.0.0.1"] : ["3600"],
			Readiness = new ReadinessCheck
			{
				StdoutPattern = "THIS_WILL_NEVER_MATCH_12345",
				TimeoutSeconds = 2,
			},
			Required = false,
			ShutdownTimeoutSeconds = 3,
		};

		await using var managed = new ManagedProcess(config, NullLogger.Instance);

		var started = await managed.StartAsync();

		// Not required, so it should still return true and downgrade to Running
		started.Should().BeTrue();
		managed.State.Should().Be(ProcessState.Running);
	}

	[Fact]
	public async Task StartAsync_ReadinessTimeout_Required_ReturnsFalse()
	{
		var config = new ProcessService
		{
			Name = "required-timeout-test",
			Command = IsWindows ? "ping" : "sleep",
			Arguments = IsWindows ? ["-t", "127.0.0.1"] : ["3600"],
			Readiness = new ReadinessCheck
			{
				StdoutPattern = "THIS_WILL_NEVER_MATCH_12345",
				TimeoutSeconds = 2,
			},
			Required = true,
			ShutdownTimeoutSeconds = 3,
		};

		await using var managed = new ManagedProcess(config, NullLogger.Instance);

		var started = await managed.StartAsync();

		started.Should().BeFalse();
		managed.State.Should().Be(ProcessState.Failed);
	}

	[Fact]
	public async Task DisposeAsync_StopsRunningProcess()
	{
		var config = CreatePingProcess("dispose-test");
		var managed = new ManagedProcess(config, NullLogger.Instance);

		await managed.StartAsync();
		managed.State.Should().Be(ProcessState.Running);

		await managed.DisposeAsync();

		managed.HasExited.Should().BeTrue();
		managed.State.Should().Be(ProcessState.Stopped);
	}

	[Fact]
	public async Task State_BeforeStart_IsPending()
	{
		var config = CreatePingProcess("pending-test");
		await using var managed = new ManagedProcess(config, NullLogger.Instance);

		managed.State.Should().Be(ProcessState.Pending);
		managed.ExitCode.Should().BeNull();
	}

	[Fact]
	public async Task StopAsync_ForceKill_KillsImmediatelyWithoutTimeout()
	{
		// Use a long shutdown timeout to prove ForceKill bypasses it
		var config = new ProcessService
		{
			Name = "force-kill-test",
			Command = IsWindows ? "ping" : "sleep",
			Arguments = IsWindows ? ["-t", "127.0.0.1"] : ["3600"],
			ShutdownTimeoutSeconds = 60,
			ForceKill = true,
		};

		await using var managed = new ManagedProcess(config, NullLogger.Instance);
		await managed.StartAsync();
		managed.State.Should().Be(ProcessState.Running);

		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		await managed.StopAsync();
		stopwatch.Stop();

		managed.State.Should().Be(ProcessState.Stopped);
		managed.HasExited.Should().BeTrue();
		// With ForceKill, should complete well under the 60s timeout
		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task StopAsync_ForceKill_StopAsync_TimeoutParamIgnored()
	{
		var config = new ProcessService
		{
			Name = "force-kill-timeout-ignored",
			Command = IsWindows ? "ping" : "sleep",
			Arguments = IsWindows ? ["-t", "127.0.0.1"] : ["3600"],
			ShutdownTimeoutSeconds = 3,
			ForceKill = true,
		};

		await using var managed = new ManagedProcess(config, NullLogger.Instance);
		await managed.StartAsync();

		// Pass a large timeout — ForceKill should ignore it
		await managed.StopAsync(timeoutSeconds: 120);

		managed.State.Should().Be(ProcessState.Stopped);
		managed.HasExited.Should().BeTrue();
	}

	[Fact]
	public async Task ProcessId_ReturnsValue_WhenRunning()
	{
		var config = CreatePingProcess("pid-test");
		await using var managed = new ManagedProcess(config, NullLogger.Instance);

		managed.ProcessId.Should().BeNull("process has not started");

		await managed.StartAsync();
		managed.ProcessId.Should().NotBeNull("process should have a PID after starting");
		managed.ProcessId.Should().BeGreaterThan(0);
	}

	private static ProcessService CreatePingProcess(string name)
	{
		if (IsWindows)
		{
			return new ProcessService
			{
				Name = name,
				Command = "ping",
				Arguments = ["-t", "127.0.0.1"],
				ShutdownTimeoutSeconds = 3,
			};
		}
		return new ProcessService
		{
			Name = name,
			Command = "sleep",
			Arguments = ["3600"],
			ShutdownTimeoutSeconds = 3,
		};
	}
}
