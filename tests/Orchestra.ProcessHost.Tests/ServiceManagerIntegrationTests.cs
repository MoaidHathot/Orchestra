using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Orchestra.ProcessHost.Tests;

/// <summary>
/// Integration tests for <see cref="ServiceManager"/> and <see cref="ManagedProcess"/>
/// that spawn real (lightweight) processes.
/// </summary>
public class ServiceManagerIntegrationTests
{
	private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	/// <summary>
	/// Creates a cross-platform echo command that writes to stdout and exits.
	/// </summary>
	private static CommandHook CreateEchoHook(string name, HookPhase phase, bool required = true)
	{
		if (IsWindows)
		{
			return new CommandHook
			{
				Name = name,
				Command = "cmd.exe",
				Arguments = ["/c", "echo", "hello"],
				RunAt = phase,
				Required = required,
				TimeoutSeconds = 10,
			};
		}
		return new CommandHook
		{
			Name = name,
			Command = "echo",
			Arguments = ["hello"],
			RunAt = phase,
			Required = required,
			TimeoutSeconds = 10,
		};
	}

	/// <summary>
	/// Creates a cross-platform command that fails with a non-zero exit code.
	/// </summary>
	private static CommandHook CreateFailingHook(string name, HookPhase phase, bool required = true)
	{
		if (IsWindows)
		{
			return new CommandHook
			{
				Name = name,
				Command = "cmd.exe",
				Arguments = ["/c", "exit", "1"],
				RunAt = phase,
				Required = required,
				TimeoutSeconds = 10,
			};
		}
		return new CommandHook
		{
			Name = name,
			Command = "sh",
			Arguments = ["-c", "exit 1"],
			RunAt = phase,
			Required = required,
			TimeoutSeconds = 10,
		};
	}

	/// <summary>
	/// Creates a process service that runs a simple long-running command.
	/// On Windows: ping -t 127.0.0.1 (infinite ping)
	/// On Unix: sleep 3600
	/// </summary>
	private static ProcessService CreateLongRunningProcess(
		string name,
		RestartPolicy restartPolicy = RestartPolicy.Never,
		bool required = false)
	{
		if (IsWindows)
		{
			return new ProcessService
			{
				Name = name,
				Command = "ping",
				Arguments = ["-t", "127.0.0.1"],
				RestartPolicy = restartPolicy,
				ShutdownTimeoutSeconds = 3,
				Required = required,
			};
		}
		return new ProcessService
		{
			Name = name,
			Command = "sleep",
			Arguments = ["3600"],
			RestartPolicy = restartPolicy,
			ShutdownTimeoutSeconds = 3,
			Required = required,
		};
	}

	/// <summary>
	/// Creates a process service that outputs a readiness pattern.
	/// </summary>
	private static ProcessService CreateProcessWithReadiness(string name)
	{
		if (IsWindows)
		{
			return new ProcessService
			{
				Name = name,
				Command = "cmd.exe",
				Arguments = ["/c", "echo Ready to serve && ping -t 127.0.0.1 >nul"],
				Readiness = new ReadinessCheck
				{
					StdoutPattern = "Ready to serve",
					TimeoutSeconds = 10,
				},
				ShutdownTimeoutSeconds = 3,
				Required = true,
			};
		}
		return new ProcessService
		{
			Name = name,
			Command = "sh",
			Arguments = ["-c", "echo 'Ready to serve' && sleep 3600"],
			Readiness = new ReadinessCheck
			{
				StdoutPattern = "Ready to serve",
				TimeoutSeconds = 10,
			},
			ShutdownTimeoutSeconds = 3,
			Required = true,
		};
	}

	[Fact]
	public async Task FullLifecycle_BeforeStartHook_RunsSuccessfully()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var hook = CreateEchoHook("echo-test", HookPhase.BeforeStart);
		await manager.InitializeAsync([hook]);

		// Should complete without errors
		manager.IsInitialized.Should().BeTrue();

		await manager.DisposeAsync();
	}

	[Fact]
	public async Task BeforeStartHook_FailsStartup_WhenRequired()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var hook = CreateFailingHook("fail-test", HookPhase.BeforeStart, required: true);

		var act = () => manager.InitializeAsync([hook]);

		await act.Should().ThrowAsync<ServiceInitializationException>()
			.WithMessage("*fail-test*failed*");

		await manager.DisposeAsync();
	}

	[Fact]
	public async Task BeforeStartHook_ContinuesStartup_WhenNotRequired()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var hook = CreateFailingHook("optional-fail", HookPhase.BeforeStart, required: false);
		await manager.InitializeAsync([hook]);

		// Should not throw
		manager.IsInitialized.Should().BeTrue();

		await manager.DisposeAsync();
	}

	[Fact]
	public async Task ProcessService_StartsAndStopsGracefully()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var process = CreateLongRunningProcess("ping-test");
		await manager.InitializeAsync([process]);

		manager.Processes.Should().ContainKey("ping-test");
		var managed = manager.Processes["ping-test"];
		managed.State.Should().BeOneOf(ProcessState.Running, ProcessState.Ready);

		await manager.StopAsync();

		managed.State.Should().Be(ProcessState.Stopped);
	}

	[Fact]
	public async Task ReadinessCheck_StdoutPattern_DetectsReady()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var process = CreateProcessWithReadiness("ready-test");
		await manager.InitializeAsync([process]);

		manager.Processes.Should().ContainKey("ready-test");
		var managed = manager.Processes["ready-test"];
		managed.State.Should().Be(ProcessState.Ready);

		await manager.DisposeAsync();
	}

	[Fact]
	public async Task AfterStopHook_RunsDuringShutdown()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var afterHook = CreateEchoHook("cleanup", HookPhase.AfterStop);
		await manager.InitializeAsync([afterHook]);

		// afterStop hook should run during StopAsync
		await manager.StopAsync(); // Should not throw
	}

	[Fact]
	public async Task AfterStopHook_FailsDuringShutdown_DoesNotThrow()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var afterHook = CreateFailingHook("bad-cleanup", HookPhase.AfterStop, required: true);
		await manager.InitializeAsync([afterHook]);

		// afterStop hooks never block shutdown
		await manager.StopAsync(); // Should not throw
	}

	[Fact]
	public async Task ForceKill_ProcessService_KillsImmediatelyOnShutdown()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var process = new ProcessService
		{
			Name = "force-kill-svc",
			Command = IsWindows ? "ping" : "sleep",
			Arguments = IsWindows ? ["-t", "127.0.0.1"] : ["3600"],
			ShutdownTimeoutSeconds = 60,
			ForceKill = true,
		};

		await manager.InitializeAsync([process]);

		manager.Processes.Should().ContainKey("force-kill-svc");
		var managed = manager.Processes["force-kill-svc"];
		managed.State.Should().Be(ProcessState.Running);

		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		await manager.StopAsync();
		stopwatch.Stop();

		managed.State.Should().Be(ProcessState.Stopped);
		// With ForceKill, should complete well under the 60s shutdown timeout
		stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task MixedEntries_FullLifecycle()
	{
		var manager = new ServiceManager(NullLogger<ServiceManager>.Instance);

		var beforeHook = CreateEchoHook("setup", HookPhase.BeforeStart);
		var process = CreateLongRunningProcess("background-svc");
		var afterHook = CreateEchoHook("teardown", HookPhase.AfterStop);

		await manager.InitializeAsync([beforeHook, process, afterHook]);

		manager.Processes.Should().ContainKey("background-svc");

		await manager.StopAsync();
	}
}
