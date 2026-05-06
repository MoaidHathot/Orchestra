using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Orchestra.ProcessHost.Tests;

/// <summary>
/// Unit tests for <see cref="ServiceManager"/>.
/// Uses a <see cref="TestableServiceManager"/> subclass that overrides
/// process creation and command execution to avoid real process spawning.
/// </summary>
public class ServiceManagerTests : IAsyncLifetime
{
	private TestableServiceManager _manager = null!;

	public Task InitializeAsync()
	{
		_manager = new TestableServiceManager();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _manager.DisposeAsync();
	}

	#region InitializeAsync

	[Fact]
	public async Task InitializeAsync_WithEmptyArray_LogsAndReturns()
	{
		await _manager.InitializeAsync([]);

		_manager.IsInitialized.Should().BeTrue();
		_manager.Processes.Should().BeEmpty();
		_manager.CreateProcessCalls.Should().BeEmpty();
		_manager.RunCommandCalls.Should().BeEmpty();
	}

	[Fact]
	public async Task InitializeAsync_CalledTwice_ThrowsInvalidOperationException()
	{
		await _manager.InitializeAsync([]);

		var act = () => _manager.InitializeAsync([]);

		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*already been initialized*");
	}

	[Fact]
	public async Task InitializeAsync_DuplicateNames_ThrowsArgumentException()
	{
		var entry1 = CreateProcessService("my-service");
		var entry2 = CreateProcessService("my-service");

		var act = () => _manager.InitializeAsync([entry1, entry2]);

		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage("*Duplicate service name*my-service*");
	}

	#endregion

	#region BeforeStart Hooks

	[Fact]
	public async Task InitializeAsync_RunsBeforeStartHooksConcurrently()
	{
		var hook1 = CreateCommandHook("hook1", HookPhase.BeforeStart);
		var hook2 = CreateCommandHook("hook2", HookPhase.BeforeStart);
		var releaseHooks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_manager.CommandBlockers["hook1"] = releaseHooks.Task;
		_manager.CommandBlockers["hook2"] = releaseHooks.Task;

		var initTask = _manager.InitializeAsync([hook1, hook2]);
		await _manager.WaitForCommandCallsAsync(2);
		initTask.IsCompleted.Should().BeFalse("both hooks should be running at the same time and waiting on the shared blocker");

		releaseHooks.SetResult();
		await initTask;

		_manager.RunCommandCalls.Should().HaveCount(2);
		_manager.RunCommandCalls.Select(h => h.Name).Should().BeEquivalentTo(["hook1", "hook2"]);
	}

	[Fact]
	public async Task InitializeAsync_BeforeStartHookFails_Required_ThrowsServiceInitializationException()
	{
		var hook = CreateCommandHook("failing-hook", HookPhase.BeforeStart, required: true);
		_manager.CommandResults["failing-hook"] = (1, "some error");

		var act = () => _manager.InitializeAsync([hook]);

		await act.Should().ThrowAsync<ServiceInitializationException>()
			.WithMessage("*failing-hook*failed*");
	}

	[Fact]
	public async Task InitializeAsync_BeforeStartHookFails_NotRequired_ContinuesSuccessfully()
	{
		var hook = CreateCommandHook("optional-hook", HookPhase.BeforeStart, required: false);
		_manager.CommandResults["optional-hook"] = (1, "some error");

		await _manager.InitializeAsync([hook]);

		// Should not throw — continues despite failure
		_manager.RunCommandCalls.Should().HaveCount(1);
	}

	[Fact]
	public async Task InitializeAsync_BeforeStartHookSucceeds_Required_Continues()
	{
		var hook = CreateCommandHook("good-hook", HookPhase.BeforeStart, required: true);
		_manager.CommandResults["good-hook"] = (0, "");

		await _manager.InitializeAsync([hook]);

		_manager.RunCommandCalls.Should().HaveCount(1);
	}

	#endregion

	#region Process Services

	[Fact]
	public async Task InitializeAsync_StartsProcessServices()
	{
		var process = CreateProcessService("my-process");

		await _manager.InitializeAsync([process]);

		_manager.CreateProcessCalls.Should().HaveCount(1);
		_manager.CreateProcessCalls[0].Should().Be("my-process");
		_manager.Processes.Should().ContainKey("my-process");
	}

	[Fact]
	public async Task InitializeAsync_ProcessFailsToStart_Required_ThrowsServiceInitializationException()
	{
		var process = CreateProcessService("broken-process", required: true);
		_manager.ProcessStartResults["broken-process"] = false;

		var act = () => _manager.InitializeAsync([process]);

		await act.Should().ThrowAsync<ServiceInitializationException>()
			.WithMessage("*broken-process*failed to start*");
	}

	[Fact]
	public async Task InitializeAsync_ProcessFailsToStart_NotRequired_ContinuesSuccessfully()
	{
		var process = CreateProcessService("optional-process", required: false);
		_manager.ProcessStartResults["optional-process"] = false;

		await _manager.InitializeAsync([process]);

		// Should not throw, and process should not be tracked
		_manager.Processes.Should().NotContainKey("optional-process");
	}

	[Fact]
	public async Task InitializeAsync_MixedEntries_ExecutesInCorrectOrder()
	{
		var beforeHook = CreateCommandHook("before", HookPhase.BeforeStart);
		var process = CreateProcessService("svc");
		var afterHook = CreateCommandHook("after", HookPhase.AfterStop);

		await _manager.InitializeAsync([afterHook, process, beforeHook]);

		// beforeStart hooks run first
		_manager.RunCommandCalls.Should().HaveCount(1);
		_manager.RunCommandCalls[0].Name.Should().Be("before");

		// processes started
		_manager.CreateProcessCalls.Should().HaveCount(1);
		_manager.CreateProcessCalls[0].Should().Be("svc");
	}

	#endregion

	#region StopAsync

	[Fact]
	public async Task StopAsync_RunsAfterStopHooks()
	{
		var afterHook = CreateCommandHook("cleanup", HookPhase.AfterStop);

		await _manager.InitializeAsync([afterHook]);

		// No hooks run during init for afterStop
		_manager.RunCommandCalls.Should().BeEmpty();

		await _manager.StopAsync();

		// afterStop hooks run during stop
		_manager.RunCommandCalls.Should().HaveCount(1);
		_manager.RunCommandCalls[0].Name.Should().Be("cleanup");
	}

	[Fact]
	public async Task StopAsync_RunsAfterStopHooksConcurrently()
	{
		var hook1 = CreateCommandHook("cleanup1", HookPhase.AfterStop);
		var hook2 = CreateCommandHook("cleanup2", HookPhase.AfterStop);
		var releaseHooks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_manager.CommandBlockers["cleanup1"] = releaseHooks.Task;
		_manager.CommandBlockers["cleanup2"] = releaseHooks.Task;

		await _manager.InitializeAsync([hook1, hook2]);
		var stopTask = _manager.StopAsync();
		await _manager.WaitForCommandCallsAsync(2);
		stopTask.IsCompleted.Should().BeFalse("both afterStop hooks should be running concurrently and waiting on the shared blocker");

		releaseHooks.SetResult();
		await stopTask;

		_manager.RunCommandCalls.Select(h => h.Name).Should().BeEquivalentTo(["cleanup1", "cleanup2"]);
	}

	[Fact]
	public async Task StopAsync_AfterStopHookFails_DoesNotThrow()
	{
		var afterHook = CreateCommandHook("failing-cleanup", HookPhase.AfterStop);
		_manager.CommandResults["failing-cleanup"] = (1, "cleanup failed");

		await _manager.InitializeAsync([afterHook]);
		await _manager.StopAsync(); // Should not throw
	}

	[Fact]
	public async Task StopAsync_CalledBeforeInit_NoOp()
	{
		await _manager.StopAsync(); // Should not throw
	}

	[Fact]
	public async Task StopAsync_CalledTwice_OnlyRunsOnce()
	{
		var afterHook = CreateCommandHook("cleanup", HookPhase.AfterStop);
		await _manager.InitializeAsync([afterHook]);

		await _manager.StopAsync();
		var firstCallCount = _manager.RunCommandCalls.Count;

		await _manager.StopAsync();
		_manager.RunCommandCalls.Count.Should().Be(firstCallCount, "second StopAsync should be a no-op");
	}

	#endregion

	#region Helpers

	private static ProcessService CreateProcessService(
		string name,
		RestartPolicy restartPolicy = RestartPolicy.Never,
		bool required = false) => new()
	{
		Name = name,
		Command = "test-command",
		Arguments = ["--arg1"],
		RestartPolicy = restartPolicy,
		Required = required,
	};

	private static CommandHook CreateCommandHook(
		string name,
		HookPhase phase,
		bool required = true) => new()
	{
		Name = name,
		Command = "test-hook",
		Arguments = ["--run"],
		RunAt = phase,
		Required = required,
	};

	#endregion

	/// <summary>
	/// Test subclass that bypasses real process creation and command execution.
	/// Tracks calls for assertion and allows configuring results.
	/// </summary>
	private sealed class TestableServiceManager : ServiceManager
	{
		/// <summary>
		/// Names of processes that were requested to be created.
		/// </summary>
		public List<string> CreateProcessCalls { get; } = [];

		/// <summary>
		/// Command hooks that were executed.
		/// </summary>
		private readonly List<CommandHook> _runCommandCalls = [];
		public IReadOnlyList<CommandHook> RunCommandCalls
		{
			get
			{
				lock (_runCommandCallsLock)
				{
					return _runCommandCalls.ToArray();
				}
			}
		}
		private readonly object _runCommandCallsLock = new();
		private TaskCompletionSource<int>? _waitForCommandCalls;

		/// <summary>
		/// Configurable results for command hooks, keyed by hook name.
		/// Default: exit code 0, empty stderr.
		/// </summary>
		public Dictionary<string, (int ExitCode, string Stderr)> CommandResults { get; } = [];

		/// <summary>
		/// Optional blocker tasks for command hooks, keyed by hook name.
		/// </summary>
		public Dictionary<string, Task> CommandBlockers { get; } = [];

		/// <summary>
		/// Configurable start results for processes, keyed by process name.
		/// Default: true (success).
		/// </summary>
		public Dictionary<string, bool> ProcessStartResults { get; } = [];

		public TestableServiceManager()
			: base(NullLogger<ServiceManager>.Instance, processTracker: null)
		{
		}

		internal override Task<ManagedProcess?> CreateAndStartProcessAsync(
			ProcessService config, CancellationToken cancellationToken)
		{
			CreateProcessCalls.Add(config.Name);

			if (ProcessStartResults.TryGetValue(config.Name, out var shouldSucceed) && !shouldSucceed)
				return Task.FromResult<ManagedProcess?>(null);

			// Create a real ManagedProcess but don't start a real process.
			// Return a stub that reports as running.
			var managed = new ManagedProcess(config, NullLogger.Instance);
			return Task.FromResult<ManagedProcess?>(managed);
		}

		public Task WaitForCommandCallsAsync(int count)
		{
			lock (_runCommandCallsLock)
			{
				if (_runCommandCalls.Count >= count)
					return Task.CompletedTask;

				_waitForCommandCalls = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
				return _waitForCommandCalls.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
		}

		internal override async Task<(int ExitCode, string Stderr)> RunCommandAsync(
			CommandHook hook, CancellationToken cancellationToken)
		{
			lock (_runCommandCallsLock)
			{
				_runCommandCalls.Add(hook);
				_waitForCommandCalls?.TrySetResult(_runCommandCalls.Count);
			}

			if (CommandBlockers.TryGetValue(hook.Name, out var blocker))
			{
				await blocker.WaitAsync(cancellationToken);
			}

			if (CommandResults.TryGetValue(hook.Name, out var result))
				return result;

			return (0, string.Empty);
		}
	}
}
