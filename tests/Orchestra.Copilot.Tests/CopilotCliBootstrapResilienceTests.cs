using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests for the resilience behaviour of <see cref="CopilotCliBootstrap"/>'s process-wide
/// download cache.
/// </summary>
/// <remarks>
/// The bootstrap used to memoise its download in a <c>static readonly Lazy</c>. A
/// <see cref="Lazy{T}"/> caches faults as eagerly as it caches values, so one transient network
/// failure during the ~100 MB first-run download poisoned every subsequent run in that process
/// — a long-lived portal would keep replaying the same exception until restarted. These tests
/// reach through reflection because the reset is deliberately private; the observable contract
/// is "a faulted attempt does not persist".
/// </remarks>
[CollectionDefinition("copilot-bootstrap-static", DisableParallelization = true)]
public sealed class CopilotBootstrapStaticCollection { }

/// <summary>Serialized: these tests swap a static field on the shared bootstrap cache.</summary>
[Collection("copilot-bootstrap-static")]
public class CopilotCliBootstrapResilienceTests
{
	private static FieldInfo PathField =>
		typeof(CopilotCliBootstrap).GetField("s_path", BindingFlags.NonPublic | BindingFlags.Static)
		?? throw new InvalidOperationException("s_path field not found; the bootstrap cache was renamed.");

	private static MethodInfo ResetIfFaulted =>
		typeof(CopilotCliBootstrap).GetMethod("ResetIfFaulted", BindingFlags.NonPublic | BindingFlags.Static)
		?? throw new InvalidOperationException("ResetIfFaulted not found.");

	[Fact]
	public void PathCache_IsNotReadOnly_SoAFaultedAttemptCanBeReplaced()
	{
		// A `static readonly Lazy` is precisely the shape that caused the bug.
		PathField.IsInitOnly.Should().BeFalse(
			"the download cache must be replaceable so a faulted attempt can be dropped");
	}

	[Fact]
	public async Task ResetIfFaulted_ReplacesAFaultedAttempt()
	{
		var original = PathField.GetValue(null);
		try
		{
			var faulted = new Lazy<Task<string>>(
				() => Task.FromException<string>(new IOException("simulated transient failure")),
				LazyThreadSafetyMode.ExecutionAndPublication);

			// Force the fault to materialise, mirroring a real failed download.
			await faulted.Value.ContinueWith(static _ => { }, TaskScheduler.Default);

			PathField.SetValue(null, faulted);
			ResetIfFaulted.Invoke(null, [faulted]);

			PathField.GetValue(null).Should().NotBeSameAs(faulted,
				"the next caller must get a fresh attempt rather than replaying the cached exception");
		}
		finally
		{
			PathField.SetValue(null, original);
		}
	}

	[Fact]
	public async Task ResetIfFaulted_LeavesASucceededAttemptAlone()
	{
		var original = PathField.GetValue(null);
		try
		{
			var succeeded = new Lazy<Task<string>>(
				() => Task.FromResult("/some/path/copilot"),
				LazyThreadSafetyMode.ExecutionAndPublication);

			await succeeded.Value;

			PathField.SetValue(null, succeeded);
			ResetIfFaulted.Invoke(null, [succeeded]);

			PathField.GetValue(null).Should().BeSameAs(succeeded,
				"a completed download must stay cached; re-downloading 100 MB per call would be worse than the bug");
		}
		finally
		{
			PathField.SetValue(null, original);
		}
	}

	[Fact]
	public async Task ResetIfFaulted_DoesNotClobberAReplacementInstalledByAnotherThread()
	{
		// Two callers can fail concurrently. The loser must not discard the winner's fresh
		// attempt, or a download already in flight would be abandoned and restarted.
		var original = PathField.GetValue(null);
		try
		{
			var stale = new Lazy<Task<string>>(
				() => Task.FromException<string>(new IOException("older failure")),
				LazyThreadSafetyMode.ExecutionAndPublication);
			await stale.Value.ContinueWith(static _ => { }, TaskScheduler.Default);

			var replacement = new Lazy<Task<string>>(
				() => Task.FromResult("/fresh/copilot"),
				LazyThreadSafetyMode.ExecutionAndPublication);

			PathField.SetValue(null, replacement);
			ResetIfFaulted.Invoke(null, [stale]);

			PathField.GetValue(null).Should().BeSameAs(replacement);
		}
		finally
		{
			PathField.SetValue(null, original);
		}
	}

	[Fact]
	public void AcquireDownloadLock_ExistsWithAWaitDeadline()
	{
		// Opening the lock file with FileShare.None throws immediately when a peer holds it.
		// Without a wait, two `orchestra run` invocations starting together on a cold cache
		// meant the second died with a raw "file in use" IOException on its very first run.
		typeof(CopilotCliBootstrap)
			.GetMethod("AcquireDownloadLockAsync", BindingFlags.NonPublic | BindingFlags.Static)
			.Should().NotBeNull("concurrent cold-cache starts must wait for the peer, not crash");

		var timeout = typeof(CopilotCliBootstrap)
			.GetField("LockAcquisitionTimeout", BindingFlags.NonPublic | BindingFlags.Static)
			?.GetValue(null);

		timeout.Should().BeOfType<TimeSpan>()
			.Which.Should().BeGreaterThan(TimeSpan.FromMinutes(1),
				"the wait has to outlast a realistic ~100 MB download on a slow connection");
	}
}
