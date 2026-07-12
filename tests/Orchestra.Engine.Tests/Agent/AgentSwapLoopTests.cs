using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Agent;

/// <summary>
/// Direct coverage for the shared <see cref="AgentSwapLoop"/> — the provider-neutral
/// swap/resume loop both Copilot and OpenCode delegate to. Verifies failure classification,
/// the budget loop, swap-event emission, metrics/reporter signalling, and resume-vs-cold-restart
/// mode selection without any provider SDK.
/// </summary>
public class AgentSwapLoopTests
{
	private sealed class FakeClientUnhealthy(string reason) : Exception("unhealthy"), IAgentClientUnhealthyException
	{
		public string TriggeringSessionId => "sid";
		public string TriggeringFailureReason { get; } = reason;
		public string? ProbeDetails => null;
	}

	private sealed class FakeSessionFailed(AgentSessionErrorDetails details) : Exception("session failed"), IAgentSessionFailedException
	{
		public AgentSessionErrorDetails? Details { get; } = details;
	}

	private sealed class CountingMetrics : ISwapMetricsSink
	{
		public int Count;
		public void RecordSwapTriggered() => Interlocked.Increment(ref Count);
	}

	[Theory]
	[InlineData("transport_lost", "transport_lost")]
	[InlineData("resume_locked", "resume_locked")]
	[InlineData("resume_session_missing", "resume_session_missing")]
	[InlineData("anything-else", "transport_lost")]
	public void TryClassifyNeutral_ClientUnhealthy_MapsReason(string triggering, string expected)
	{
		AgentSwapLoop.TryClassifyNeutral(new FakeClientUnhealthy(triggering), out var reason).Should().BeTrue();
		reason.Should().Be(expected);
	}

	[Fact]
	public void TryClassifyNeutral_SessionFailed_ExhaustedCliRetries_And_TransientUpstream()
	{
		AgentSwapLoop.TryClassifyNeutral(
			new FakeSessionFailed(new AgentSessionErrorDetails { ExhaustedCliRetries = true }), out var r1).Should().BeTrue();
		r1.Should().Be("cli_exhausted_retries");

		AgentSwapLoop.TryClassifyNeutral(
			new FakeSessionFailed(new AgentSessionErrorDetails { TransientUpstreamFailure = true }), out var r2).Should().BeTrue();
		r2.Should().Be("transient_upstream");
	}

	[Fact]
	public void TryClassifyNeutral_NonRecoverable_ReturnsFalse()
	{
		AgentSwapLoop.TryClassifyNeutral(new InvalidOperationException("nope"), out _).Should().BeFalse();
		AgentSwapLoop.TryClassifyNeutral(
			new FakeSessionFailed(new AgentSessionErrorDetails()), out _).Should().BeFalse();
	}

	[Fact]
	public async Task RunAsync_SwapEligibleFailure_RetriesOnFreshWorker_ColdRestart_AndSucceeds()
	{
		var metrics = new CountingMetrics();
		var reporter = Substitute.For<IOrchestrationReporter>();
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var loop = new AgentSwapLoop(
			SwapPolicy.ColdRestartOnly(budgetPerStep: 1),
			reporter,
			stepName: "step",
			NullLogger.Instance,
			metrics);

		var attempts = 0;
		var result = await loop.RunAsync(
			runAttempt: (ctx, _) =>
			{
				attempts++;
				if (ctx.SwapAttempt == 0)
				{
					ctx.SessionIdBox.Value = "ses-1";
					throw new FakeClientUnhealthy("transport_lost");
				}

				return Task.FromResult(new AgentResult { Content = "ok" });
			},
			writer: channel.Writer,
			cancellationToken: CancellationToken.None);

		result.Content.Should().Be("ok");
		attempts.Should().Be(2);
		metrics.Count.Should().Be(1);
		reporter.Received(1).ReportCliSwapTriggered("step", Arg.Any<string?>(), 1, 1, "transport_lost", "cold_restart");

		var events = await DrainAsync(channel);
		var swap = events.Should().ContainSingle(e => e.Type == AgentEventType.CliInstanceSwapped).Subject;
		swap.SwapReason.Should().Be("transport_lost");
		swap.SwapMode.Should().Be("cold_restart");
		swap.SwapAttempt.Should().Be(1);
		swap.SwapBudget.Should().Be(1);
	}

	[Fact]
	public async Task RunAsync_ResumeEnabled_WithPriorSessionId_UsesResumeMode()
	{
		var reporter = Substitute.For<IOrchestrationReporter>();
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var loop = new AgentSwapLoop(
			new SwapPolicy(BudgetPerStep: 1, ResumeEnabled: true),
			reporter,
			stepName: "step",
			NullLogger.Instance);

		string? resumeTarget = null;
		var result = await loop.RunAsync(
			runAttempt: (ctx, _) =>
			{
				if (ctx.SwapAttempt == 0)
				{
					ctx.SessionIdBox.Value = "ses-1";
					throw new FakeClientUnhealthy("transport_lost");
				}

				resumeTarget = ctx.PriorSessionId;
				return Task.FromResult(new AgentResult { Content = "ok" });
			},
			writer: channel.Writer,
			cancellationToken: CancellationToken.None);

		result.Content.Should().Be("ok");
		resumeTarget.Should().Be("ses-1", "resume must target the prior attempt's session id");

		var events = await DrainAsync(channel);
		events.Should().ContainSingle(e => e.Type == AgentEventType.CliInstanceSwapped)
			.Which.SwapMode.Should().Be("resume");
	}

	[Fact]
	public async Task RunAsync_BudgetExhausted_ThrowsBudgetExhausted_PreservingInner()
	{
		var metrics = new CountingMetrics();
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var loop = new AgentSwapLoop(
			SwapPolicy.ColdRestartOnly(budgetPerStep: 1),
			Substitute.For<IOrchestrationReporter>(),
			stepName: "step",
			NullLogger.Instance,
			metrics);

		var act = () => loop.RunAsync(
			runAttempt: (_, _) => throw new FakeClientUnhealthy("transport_lost"),
			writer: channel.Writer,
			cancellationToken: CancellationToken.None);

		// The terminal exception makes the give-up explicit rather than leaving the inner
		// failure's (potentially misleading) message as the last word, but the original
		// exception is preserved as InnerException so the engine still categorises the step
		// via the marker interface.
		var ex = (await act.Should().ThrowAsync<AgentSwapBudgetExhaustedException>()).Which;
		ex.InnerException.Should().BeOfType<FakeClientUnhealthy>();
		ex.SwapBudget.Should().Be(1);
		ex.SwapAttempts.Should().Be(1);
		ex.Reason.Should().Be("transport_lost");
		ex.Message.Should().Contain("swap budget").And.NotContain("falling back to cold restart");
		metrics.Count.Should().Be(1, "one swap consumed before the budget was exhausted");
	}

	[Fact]
	public async Task RunAsync_NonRecoverableFailure_DoesNotSwap_AndPropagates()
	{
		var metrics = new CountingMetrics();
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var loop = new AgentSwapLoop(
			SwapPolicy.ColdRestartOnly(budgetPerStep: 3),
			Substitute.For<IOrchestrationReporter>(),
			stepName: "step",
			NullLogger.Instance,
			metrics);

		var act = () => loop.RunAsync(
			runAttempt: (_, _) => throw new InvalidOperationException("validation"),
			writer: channel.Writer,
			cancellationToken: CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
		metrics.Count.Should().Be(0);
		(await DrainAsync(channel)).Should().NotContain(e => e.Type == AgentEventType.CliInstanceSwapped);
	}

	[Fact]
	public async Task RunAsync_ProviderClassifier_TakesPrecedence_OverNeutral()
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var loop = new AgentSwapLoop(
			SwapPolicy.ColdRestartOnly(budgetPerStep: 1),
			Substitute.For<IOrchestrationReporter>(),
			stepName: "step",
			NullLogger.Instance);

		// A plain exception the neutral classifier rejects, made swap-eligible by the provider.
		static bool ProviderClassifier(Exception ex, out string reason)
		{
			if (ex is InvalidOperationException)
			{
				reason = "abnormal_shutdown";
				return true;
			}

			reason = string.Empty;
			return false;
		}

		var attempts = 0;
		var result = await loop.RunAsync(
			runAttempt: (ctx, _) =>
			{
				attempts++;
				if (ctx.SwapAttempt == 0)
				{
					throw new InvalidOperationException("boom");
				}

				return Task.FromResult(new AgentResult { Content = "ok" });
			},
			writer: channel.Writer,
			providerClassifier: ProviderClassifier,
			cancellationToken: CancellationToken.None);

		result.Content.Should().Be("ok");
		attempts.Should().Be(2);
		(await DrainAsync(channel)).Should().ContainSingle(e => e.Type == AgentEventType.CliInstanceSwapped)
			.Which.SwapReason.Should().Be("abnormal_shutdown");
	}

	private static async Task<List<AgentEvent>> DrainAsync(Channel<AgentEvent> channel)
	{
		var events = new List<AgentEvent>();
		while (channel.Reader.TryRead(out var e))
		{
			events.Add(e);
		}

		await Task.CompletedTask;
		return events;
	}
}
