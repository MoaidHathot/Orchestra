using FluentAssertions;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Unit tests for the clock-pause behavior. Verifies that the orchestration timeout CTS
/// is re-armed correctly after wait periods so HITL pauses don't consume the budget.
/// </summary>
public class ClockPauseTrackerTests
{
	[Fact]
	public void TotalWaitElapsed_StartsAtZero()
	{
		using var cts = new CancellationTokenSource();
		var tracker = CreateTracker(cts, timeoutSeconds: 60);

		tracker.TotalWaitElapsed.Should().Be(TimeSpan.Zero);
	}

	[Fact]
	public async Task BeginEndWait_AccumulatesElapsedTime()
	{
		using var cts = new CancellationTokenSource();
		var tracker = CreateTracker(cts, timeoutSeconds: 60);

		tracker.BeginWait();
		await Task.Delay(50);
		tracker.EndWait();

		tracker.TotalWaitElapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
		tracker.TotalWaitElapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
	}

	[Fact]
	public async Task NestedWaits_OnlyAccumulateUnionTime()
	{
		using var cts = new CancellationTokenSource();
		var tracker = CreateTracker(cts, timeoutSeconds: 60);

		tracker.BeginWait();
		await Task.Delay(20);
		tracker.BeginWait();   // Second wait starts while first is still active
		await Task.Delay(20);
		tracker.EndWait();      // First end leaves us with one active wait
		await Task.Delay(20);
		tracker.EndWait();      // Last end accumulates the full ~60ms

		// Three sequential 20ms phases should total ~60ms regardless of overlapping waits.
		// Allow generous bounds for OS scheduling.
		tracker.TotalWaitElapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
		tracker.TotalWaitElapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
	}

	[Fact]
	public void EndWait_WithoutBegin_DoesNotThrow()
	{
		using var cts = new CancellationTokenSource();
		var tracker = CreateTracker(cts, timeoutSeconds: 60);

		var act = () => tracker.EndWait();

		act.Should().NotThrow();
	}

	[Fact]
	public async Task ClockPause_PreventsTimeoutFiring_WithinExtendedDeadline()
	{
		using var cts = new CancellationTokenSource();
		// Tight original timeout: 200ms. Expect to wait 300ms total with a 200ms pause in between.
		// With clock-pause: net compute time ≈ 100ms < 200ms, so cts should NOT be cancelled.
		var tracker = CreateTracker(cts, timeoutSeconds: 1);
		// Manually set the original CancelAfter to simulate engine wiring (the executor would
		// normally do this when it sets up orchestrationTimeoutCts).
		cts.CancelAfter(TimeSpan.FromMilliseconds(200));

		// Start paused immediately, before any compute "elapses" past the deadline.
		tracker.BeginWait();
		await Task.Delay(300); // Spend 300ms "waiting"
		tracker.EndWait();      // EndWait re-arms with extended deadline

		// After re-arm, give the cts ~100ms of its budget back since 100ms hasn't elapsed yet.
		// With pure compute time roughly 0ms, the cts should still have room to spare.
		// Verify the cts is NOT yet cancelled.
		cts.IsCancellationRequested.Should().BeFalse();
		tracker.TotalWaitElapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
	}

	private static ClockPauseTracker CreateTracker(CancellationTokenSource cts, int timeoutSeconds)
	{
		return new ClockPauseTracker(cts, timeoutSeconds, DateTimeOffset.UtcNow);
	}
}
