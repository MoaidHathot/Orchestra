using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestra.Copilot.Tests;

public class SessionFaultBrokerTests
{
	private const int ScopeId = 42;

	private static SessionFaultBroker CreateBroker(Func<CancellationToken, Task<ProbeResult>> probe)
		=> new(ScopeId, probe, NullLogger<SessionFaultBroker>.Instance);

	[Fact]
	public async Task HealthyProbe_LeavesSiblingsUntouched_AndReturnsTrue()
	{
		// Arrange
		var probeCount = 0;
		var broker = CreateBroker(_ =>
		{
			Interlocked.Increment(ref probeCount);
			return Task.FromResult(new ProbeResult(true, "ping ok; state=Connected"));
		});

		Exception? siblingAFault = null;
		Exception? siblingBFault = null;
		using var _a = broker.RegisterSession("session-failed", _ => { });
		using var _b = broker.RegisterSession("sibling-a", ex => siblingAFault = ex);
		using var _c = broker.RegisterSession("sibling-b", ex => siblingBFault = ex);

		// Act
		var clientHealthy = await broker.ProbeAndMaybeFaultSiblingsAsync(
			"session-failed", "model api error", CancellationToken.None);

		// Assert
		clientHealthy.Should().BeTrue();
		siblingAFault.Should().BeNull();
		siblingBFault.Should().BeNull();
		probeCount.Should().Be(1);
	}

	[Fact]
	public async Task UnhealthyProbe_FaultsAllSiblings_ExcludingOriginator_AndReturnsFalse()
	{
		// Arrange
		var broker = CreateBroker(_ =>
			Task.FromResult(new ProbeResult(false, "ping timeout; state=Error")));

		Exception? originatorFault = null;
		Exception? siblingAFault = null;
		Exception? siblingBFault = null;
		using var _orig = broker.RegisterSession("session-failed", ex => originatorFault = ex);
		using var _a = broker.RegisterSession("sibling-a", ex => siblingAFault = ex);
		using var _b = broker.RegisterSession("sibling-b", ex => siblingBFault = ex);

		// Act
		var clientHealthy = await broker.ProbeAndMaybeFaultSiblingsAsync(
			"session-failed", "model api error", CancellationToken.None);

		// Assert
		clientHealthy.Should().BeFalse();

		// Originator is NOT faulted by the broker — it re-throws its own exception.
		originatorFault.Should().BeNull();

		// Both siblings receive a CopilotClientUnhealthyException with full context.
		siblingAFault.Should().BeOfType<CopilotClientUnhealthyException>();
		var exA = (CopilotClientUnhealthyException)siblingAFault!;
		exA.TriggeringSessionId.Should().Be("session-failed");
		exA.TriggeringFailureReason.Should().Be("model api error");
		exA.ProbeDetails.Should().Contain("ping timeout");
		exA.Message.Should().Contain("sibling-a");
		exA.Message.Should().Contain("session-failed");

		siblingBFault.Should().BeOfType<CopilotClientUnhealthyException>();
		var exB = (CopilotClientUnhealthyException)siblingBFault!;
		exB.TriggeringSessionId.Should().Be("session-failed");
		exB.Message.Should().Contain("sibling-b");
	}

	[Fact]
	public async Task ProbeRunsOnlyOnce_AcrossCascadingFailures()
	{
		// Arrange
		var probeCount = 0;
		var probeGate = new TaskCompletionSource();
		var broker = CreateBroker(async _ =>
		{
			Interlocked.Increment(ref probeCount);
			// Hold the first probe open so the other two failures pile up against the lock
			// and exercise the cached-decision fast path on release.
			await probeGate.Task.ConfigureAwait(false);
			return new ProbeResult(false, "unhealthy");
		});

		var s1Count = 0;
		var s2Count = 0;
		var s3Count = 0;
		using var r1 = broker.RegisterSession("s1", _ => Interlocked.Increment(ref s1Count));
		using var r2 = broker.RegisterSession("s2", _ => Interlocked.Increment(ref s2Count));
		using var r3 = broker.RegisterSession("s3", _ => Interlocked.Increment(ref s3Count));

		// Act — three cascading failures arriving roughly together.
		var t1 = broker.ProbeAndMaybeFaultSiblingsAsync("s1", "fail1", CancellationToken.None);
		var t2 = broker.ProbeAndMaybeFaultSiblingsAsync("s2", "fail2", CancellationToken.None);
		var t3 = broker.ProbeAndMaybeFaultSiblingsAsync("s3", "fail3", CancellationToken.None);

		// Release the probe so all three can progress.
		probeGate.SetResult();
		var results = await Task.WhenAll(t1, t2, t3);

		// Assert
		results.Should().AllSatisfy(r => r.Should().BeFalse());
		probeCount.Should().Be(1, "the broker probes at most once per lifetime");

		// Only s1 (the first failure) drives the sibling-fault loop — it faults s2 and s3.
		// s2/s3 arriving later get the cached decision and do NOT re-fault anyone.
		s1Count.Should().Be(0, "originator of the probe is not faulted");
		s2Count.Should().Be(1, "sibling faulted exactly once by the originating failure");
		s3Count.Should().Be(1, "sibling faulted exactly once by the originating failure");
	}

	[Fact]
	public async Task DisposedRegistration_DoesNotReceiveFault()
	{
		// Arrange
		var broker = CreateBroker(_ =>
			Task.FromResult(new ProbeResult(false, "unhealthy")));

		Exception? aliveFault = null;
		Exception? disposedFault = null;
		using var _orig = broker.RegisterSession("session-failed", _ => { });
		var disposedReg = broker.RegisterSession("disposed-sibling", ex => disposedFault = ex);
		using var _alive = broker.RegisterSession("alive-sibling", ex => aliveFault = ex);

		// Dispose one sibling BEFORE the failure occurs.
		disposedReg.Dispose();

		// Act
		await broker.ProbeAndMaybeFaultSiblingsAsync(
			"session-failed", "model api error", CancellationToken.None);

		// Assert
		disposedFault.Should().BeNull();
		aliveFault.Should().BeOfType<CopilotClientUnhealthyException>();
	}

	[Fact]
	public async Task ProbeThrowing_IsTreatedAsUnhealthy_AndFaultsSiblings()
	{
		// Arrange
		var broker = CreateBroker(_ =>
			throw new InvalidOperationException("ping pipe broken"));

		Exception? siblingFault = null;
		using var _orig = broker.RegisterSession("session-failed", _ => { });
		using var _sib = broker.RegisterSession("sibling", ex => siblingFault = ex);

		// Act
		var clientHealthy = await broker.ProbeAndMaybeFaultSiblingsAsync(
			"session-failed", "model api error", CancellationToken.None);

		// Assert
		clientHealthy.Should().BeFalse();
		siblingFault.Should().BeOfType<CopilotClientUnhealthyException>();
		var ex = (CopilotClientUnhealthyException)siblingFault!;
		ex.ProbeDetails.Should().Contain("probe threw");
		ex.ProbeDetails.Should().Contain("InvalidOperationException");
		ex.ProbeDetails.Should().Contain("ping pipe broken");
	}

	[Fact]
	public async Task SiblingCallbackThrowing_DoesNotPreventOtherSiblingsFromBeingFaulted()
	{
		// Arrange
		var broker = CreateBroker(_ =>
			Task.FromResult(new ProbeResult(false, "unhealthy")));

		Exception? sibling2Fault = null;
		Exception? sibling3Fault = null;
		using var _orig = broker.RegisterSession("session-failed", _ => { });
		using var _s1 = broker.RegisterSession("sibling-1-throws",
			_ => throw new InvalidOperationException("callback boom"));
		using var _s2 = broker.RegisterSession("sibling-2", ex => sibling2Fault = ex);
		using var _s3 = broker.RegisterSession("sibling-3", ex => sibling3Fault = ex);

		// Act
		var clientHealthy = await broker.ProbeAndMaybeFaultSiblingsAsync(
			"session-failed", "model api error", CancellationToken.None);

		// Assert — broker swallows callback exceptions and continues.
		clientHealthy.Should().BeFalse();
		sibling2Fault.Should().BeOfType<CopilotClientUnhealthyException>();
		sibling3Fault.Should().BeOfType<CopilotClientUnhealthyException>();
	}

	[Fact]
	public void Latch_StartsFalse_AndAllReasonsAreNull()
	{
		var broker = CreateBroker(_ => Task.FromResult(new ProbeResult(true, "ok")));

		broker.IsClientUnhealthy.Should().BeFalse();
		broker.UnhealthyReason.Should().BeNull();
		broker.UnhealthyTriggeringSessionId.Should().BeNull();
		broker.UnhealthyTriggeringFailureReason.Should().BeNull();
	}

	[Fact]
	public async Task Latch_StaysFalse_AfterHealthyProbe()
	{
		var broker = CreateBroker(_ => Task.FromResult(new ProbeResult(true, "ping ok")));
		using var _orig = broker.RegisterSession("session-failed", _ => { });

		await broker.ProbeAndMaybeFaultSiblingsAsync("session-failed", "transient", CancellationToken.None);

		broker.IsClientUnhealthy.Should().BeFalse();
		broker.UnhealthyReason.Should().BeNull();
		broker.UnhealthyTriggeringSessionId.Should().BeNull();
		broker.UnhealthyTriggeringFailureReason.Should().BeNull();
	}

	[Fact]
	public async Task Latch_SetsToTrue_WithFullContext_AfterUnhealthyProbe()
	{
		var broker = CreateBroker(_ => Task.FromResult(new ProbeResult(false, "ping timeout; state=Error")));
		using var _orig = broker.RegisterSession("session-failed", _ => { });

		await broker.ProbeAndMaybeFaultSiblingsAsync("session-failed", "model api error", CancellationToken.None);

		broker.IsClientUnhealthy.Should().BeTrue();
		broker.UnhealthyReason.Should().Be("ping timeout; state=Error");
		broker.UnhealthyTriggeringSessionId.Should().Be("session-failed");
		broker.UnhealthyTriggeringFailureReason.Should().Be("model api error");
	}

	[Fact]
	public async Task Latch_DoesNotChange_OnSecondProbeCall()
	{
		var broker = CreateBroker(_ => Task.FromResult(new ProbeResult(false, "first probe details")));
		using var _orig = broker.RegisterSession("first-session", _ => { });
		using var _other = broker.RegisterSession("second-session", _ => { });

		await broker.ProbeAndMaybeFaultSiblingsAsync("first-session", "first failure", CancellationToken.None);
		await broker.ProbeAndMaybeFaultSiblingsAsync("second-session", "second failure", CancellationToken.None);

		// Latch retains the FIRST triggering context — probe runs at most once.
		broker.IsClientUnhealthy.Should().BeTrue();
		broker.UnhealthyReason.Should().Be("first probe details");
		broker.UnhealthyTriggeringSessionId.Should().Be("first-session");
		broker.UnhealthyTriggeringFailureReason.Should().Be("first failure");
	}
}
