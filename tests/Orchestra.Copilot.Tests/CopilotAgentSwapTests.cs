using System.Threading.Channels;
using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests for the CLI swap-and-resume recovery loop in <see cref="CopilotAgent"/>.
///
/// The loop wraps a transport-class failure (broker latched the worker unhealthy, CLI
/// emitted SessionErrorEvent with "retried N times", abnormal shutdown) and tries again
/// on a fresh worker. Budget is bounded by <see cref="CopilotAgentSwapOptions.CliSwapBudgetPerStep"/>;
/// non-recoverable errors and cancellation propagate immediately.
///
/// These tests use an in-memory pool whose <c>AcquireAsync</c> returns scripted clients
/// from a queue, mirroring the real <c>CopilotClientPool</c>'s "skip latched worker"
/// behaviour without requiring a real Copilot CLI process.
/// </summary>
public class CopilotAgentSwapTests
{
	private const string Model = "claude-opus-4.6";
	private const string Prompt = "hello world";

	[Fact]
	public async Task TransportLoss_OnFirstAttempt_SwapsAndSucceedsOnFreshClient()
	{
		var failingSession = new ScriptedCopilotSession("session-doomed", sendThrows: new InvalidOperationException("The JSON-RPC connection with the remote party was lost before the request could complete."));
		var failingClient = new ScriptedCopilotClient(failingSession);
		var failingBroker = new ProbeLatchingFaultBroker();
		var goodSession = new ScriptedCopilotSession("session-recovered", completeImmediately: true);
		var goodClient = new ScriptedCopilotClient(goodSession);

		var pool = new ScriptedPool(
			new ScriptedLease(failingClient, failingBroker),
			new ScriptedLease(goodClient, faultBroker: null));
		var reporter = Substitute.For<IOrchestrationReporter>();
		var agent = CreateAgent(pool, reporter, ResumeEnabled: false);

		var task = agent.SendAsync(Prompt);
		var events = await DrainEventsAsync(task);
		var result = await task.GetResultAsync();

		result.Should().NotBeNull();
		pool.AcquireCount.Should().Be(2, "we acquired once, the swap acquired again");
		pool.SwapsRecorded.Should().Be(1);

		// The swap event must be observable on the channel so operators see the recovery.
		var swap = events.Should().ContainSingle(e => e.Type == AgentEventType.CliInstanceSwapped).Subject;
		swap.SwapAttempt.Should().Be(1);
		swap.SwapBudget.Should().Be(3);
		swap.SwapReason.Should().Be("transport_lost");
		swap.SwapMode.Should().Be("cold_restart", "resume is disabled in this test");

		// The reporter must also see the swap so SSE/UI can surface it.
		reporter.Received(1).ReportCliSwapTriggered(
			Arg.Any<string>(),
			Arg.Any<string?>(),
			swapAttempt: 1,
			swapBudget: 3,
			reason: "transport_lost",
			mode: "cold_restart");
	}

	[Fact]
	public async Task ResumeOnSwap_WhenEnabled_AndSessionIdAvailable_ResumesOnFreshClient()
	{
		var failingSession = new ScriptedCopilotSession("session-first", sendThrows: new InvalidOperationException("connection lost"));
		var failingClient = new ScriptedCopilotClient(failingSession);
		var failingBroker = new ProbeLatchingFaultBroker();
		var resumedSession = new ScriptedCopilotSession("session-first", completeImmediately: true);
		var resumeClient = new ScriptedCopilotClient(resumedSession);

		var pool = new ScriptedPool(
			new ScriptedLease(failingClient, failingBroker),
			new ScriptedLease(resumeClient, faultBroker: null));
		var agent = CreateAgent(pool, Substitute.For<IOrchestrationReporter>(), ResumeEnabled: true);

		var task = agent.SendAsync(Prompt);
		var events = await DrainEventsAsync(task);
		await task.GetResultAsync();

		resumeClient.ResumeCalls.Should().HaveCount(1, "the swap should have called ResumeSessionAsync on the new client");
		resumeClient.ResumeCalls[0].sessionId.Should().Be("session-first");
		resumeClient.CreateCalls.Should().BeEmpty("a resume swap must not call CreateSessionAsync on the new client");

		events.Should().ContainSingle(e => e.Type == AgentEventType.CliInstanceSwapped)
			.Which.SwapMode.Should().Be("resume");
	}

	[Fact]
	public async Task SwapBudget_Exhausted_FailsTheStepWithTheLastError()
	{
		// Three failing clients, budget=3 → 1 original + 3 swaps = 4 attempts, all fail.
		var pool = new ScriptedPool(
			MakeFailingLease("c1"),
			MakeFailingLease("c2"),
			MakeFailingLease("c3"),
			MakeFailingLease("c4"));
		var agent = CreateAgent(pool, Substitute.For<IOrchestrationReporter>(), ResumeEnabled: false);

		var task = agent.SendAsync(Prompt);
		await DrainEventsAsync(task);

		var act = () => task.GetResultAsync();
		await act.Should().ThrowAsync<Exception>();
		pool.AcquireCount.Should().Be(4);
		pool.SwapsRecorded.Should().Be(3, "budget=3 swaps");

		static ScriptedLease MakeFailingLease(string sessionId) =>
			new(new ScriptedCopilotClient(new ScriptedCopilotSession(sessionId, sendThrows: new InvalidOperationException("CLI dead"))),
				new ProbeLatchingFaultBroker());
	}

	[Fact]
	public async Task NonRecoverableError_DoesNotConsumeSwapBudget()
	{
		// CreateSessionAsync throws something the classifier doesn't recognise as transport.
		// The agent should rethrow without invoking the swap loop.
		var bad = new ScriptedCopilotClient(createSessionThrows: new ArgumentException("invalid model"));
		var pool = new ScriptedPool(bad);
		var agent = CreateAgent(pool, Substitute.For<IOrchestrationReporter>(), ResumeEnabled: false);

		var task = agent.SendAsync(Prompt);
		var events = await DrainEventsAsync(task);

		var act = () => task.GetResultAsync();
		await act.Should().ThrowAsync<Exception>();
		pool.AcquireCount.Should().Be(1);
		pool.SwapsRecorded.Should().Be(0);
		events.Should().NotContain(e => e.Type == AgentEventType.CliInstanceSwapped);
	}

	[Fact]
	public async Task ClientUnhealthyOnAcquire_TriggersSwap_AndSwapModeRespectsPriorSessionId()
	{
		// Simulate a worker whose fault broker is already latched (because a sibling died).
		// CopilotAgent's fast-fail path throws CopilotClientUnhealthyException directly.
		var brokenBroker = new LatchedFaultBroker(
			triggeringSessionId: "sibling-1",
			triggeringFailureReason: "sibling died",
			probeDetails: "ping timed out");
		var deadClient = new ScriptedCopilotClient(new ScriptedCopilotSession("never-runs", completeImmediately: true));
		var recoverySession = new ScriptedCopilotSession("session-fresh", completeImmediately: true);
		var recoveryClient = new ScriptedCopilotClient(recoverySession);

		var pool = new ScriptedPool(
			new ScriptedLease(deadClient, brokenBroker),
			new ScriptedLease(recoveryClient, faultBroker: null));
		var agent = CreateAgent(pool, Substitute.For<IOrchestrationReporter>(), ResumeEnabled: true);

		var task = agent.SendAsync(Prompt);
		var events = await DrainEventsAsync(task);
		await task.GetResultAsync();

		var swap = events.Should().ContainSingle(e => e.Type == AgentEventType.CliInstanceSwapped).Subject;
		// No session id was ever issued (we fast-failed before CreateSessionAsync), so the
		// only sensible mode is cold restart.
		swap.SwapMode.Should().Be("cold_restart");
		swap.SwapReason.Should().Be("transport_lost");
	}

	[Fact]
	public async Task SwapBudgetZero_DoesNotSwap_AndPropagatesFirstFailure()
	{
		var pool = new ScriptedPool(MakeFailingLease("c1"));
		var agent = CreateAgent(
			pool,
			Substitute.For<IOrchestrationReporter>(),
			ResumeEnabled: false,
			swapBudgetOverride: 0);

		var task = agent.SendAsync(Prompt);
		await DrainEventsAsync(task);
		var act = () => task.GetResultAsync();
		await act.Should().ThrowAsync<Exception>();
		pool.AcquireCount.Should().Be(1);
		pool.SwapsRecorded.Should().Be(0);

		static ScriptedLease MakeFailingLease(string sessionId) =>
			new(new ScriptedCopilotClient(new ScriptedCopilotSession(sessionId, sendThrows: new InvalidOperationException("CLI dead"))),
				new ProbeLatchingFaultBroker());
	}

	[Fact]
	public async Task SwapBudgetExceeded_LogsSwapBudgetExhausted_AndThrowsBudgetExhausted()
	{
		var pool = new ScriptedPool(
			new ScriptedLease(new ScriptedCopilotClient(new ScriptedCopilotSession("a", sendThrows: new InvalidOperationException("transport gone"))), new ProbeLatchingFaultBroker()),
			new ScriptedLease(new ScriptedCopilotClient(new ScriptedCopilotSession("b", sendThrows: new InvalidOperationException("transport gone"))), new ProbeLatchingFaultBroker()));
		var agent = CreateAgent(pool, Substitute.For<IOrchestrationReporter>(), ResumeEnabled: false, swapBudgetOverride: 1);

		var task = agent.SendAsync(Prompt);
		await DrainEventsAsync(task);

		var act = () => task.GetResultAsync();
		// On budget exhaustion the loop surfaces a terminal AgentSwapBudgetExhaustedException
		// whose message makes the give-up explicit. The structured CopilotClientUnhealthyException
		// is preserved as InnerException so the engine still categorizes the step as
		// ClientUnhealthy; the original SDK InvalidOperationException remains in Data["InnerSdkException"].
		var ex = (await act.Should().ThrowAsync<AgentSwapBudgetExhaustedException>()).Which;
		ex.InnerException.Should().BeOfType<CopilotClientUnhealthyException>();
		ex.Message.Should().NotContain("falling back to cold restart");
		pool.AcquireCount.Should().Be(2);
		pool.SwapsRecorded.Should().Be(1);
	}

	[Fact]
	public async Task CliExhaustedRetries_RoutedAsSwapTrigger_ViaPhase3Recognizer()
	{
		// We can't easily simulate a real SessionErrorEvent here without reflecting into the
		// SDK event types, but the recognizer is unit-testable directly. This test asserts
		// the surface a real CLI session would produce.
		CopilotSessionHandler.LooksLikeCliExhaustedRetries(
			"Failed to get response from the AI model; retried 5 times (total retry wait time: 5.62 seconds)")
			.Should().BeTrue();
		CopilotSessionHandler.LooksLikeCliExhaustedRetries("Failed to get response from the AI model").Should().BeTrue();
		CopilotSessionHandler.LooksLikeCliExhaustedRetries(null).Should().BeFalse();
		CopilotSessionHandler.LooksLikeCliExhaustedRetries("403 Forbidden").Should().BeFalse();
		CopilotSessionHandler.LooksLikeCliExhaustedRetries("the operation was retried 7 times before timing out").Should().BeTrue();

		await Task.CompletedTask;
	}

	[Fact]
	public async Task ResumeSessionMissing_FallsBackToColdRestartOnNextAttempt_AndSucceeds()
	{
		// First attempt: a normal transport failure produces a session id "session-first"
		// and triggers a swap with resume enabled.
		var failingSession = new ScriptedCopilotSession("session-first", sendThrows: new InvalidOperationException("connection lost"));
		var failingClient = new ScriptedCopilotClient(failingSession);
		var failingBroker = new ProbeLatchingFaultBroker();

		// Second attempt: swap loop calls ResumeSessionAsync, the CLI replies "Session not
		// found". This is the regression: previously the orchestration would fail; now we
		// expect another swap (cold restart) instead.
		var resumeMissingClient = new ScriptedCopilotClient(
			resumeSessionThrows: new Exception("Communication error with Copilot CLI: Request session.resume failed with message: Session not found: session-first"));

		// Third attempt: cold restart on a fresh worker — succeeds.
		var recoverySession = new ScriptedCopilotSession("session-recovered", completeImmediately: true);
		var recoveryClient = new ScriptedCopilotClient(recoverySession);

		var pool = new ScriptedPool(
			new ScriptedLease(failingClient, failingBroker),
			new ScriptedLease(resumeMissingClient, faultBroker: null),
			new ScriptedLease(recoveryClient, faultBroker: null));
		var reporter = Substitute.For<IOrchestrationReporter>();
		var agent = CreateAgent(pool, reporter, ResumeEnabled: true);

		var task = agent.SendAsync(Prompt);
		var events = await DrainEventsAsync(task);
		var result = await task.GetResultAsync();

		result.Should().NotBeNull("the step must succeed via cold restart after the resume failed with Session not found");
		pool.AcquireCount.Should().Be(3);
		pool.SwapsRecorded.Should().Be(2);

		// First swap is resume (transport_lost), second is cold restart triggered by the
		// missing prior session id.
		var swaps = events.Where(e => e.Type == AgentEventType.CliInstanceSwapped).ToList();
		swaps.Should().HaveCount(2);
		swaps[0].SwapReason.Should().Be("transport_lost");
		swaps[0].SwapMode.Should().Be("resume");
		swaps[1].SwapReason.Should().Be("resume_session_missing");
		swaps[1].SwapMode.Should().Be("cold_restart", "a missing prior session id must force a cold restart, not another resume");

		// The recovery client must have been used via CreateSessionAsync (not Resume).
		recoveryClient.CreateCalls.Should().HaveCount(1);
		recoveryClient.ResumeCalls.Should().BeEmpty();
		// And we did attempt resume against the second client with the original session id.
		resumeMissingClient.ResumeCalls.Should().ContainSingle()
			.Which.sessionId.Should().Be("session-first");
	}

	[Fact]
	public async Task ResumeSessionMissing_RepeatedlyFailing_RespectsSwapBudget()
	{
		// Three workers all fail with the same transport error so we keep producing a
		// prior session id and re-attempting resume. Each resume hits "Session not found"
		// → cold restart → next worker → fails transport → resume again → ... The budget
		// must cap the loop so it can't run forever.
		ScriptedLease MakeLease(string sessionId) => new(
			new ScriptedCopilotClient(
				session: new ScriptedCopilotSession(sessionId, sendThrows: new InvalidOperationException("connection lost")),
				resumeSessionThrows: new Exception("Communication error with Copilot CLI: Request session.resume failed with message: Session not found: " + sessionId)),
			new ProbeLatchingFaultBroker());

		var pool = new ScriptedPool(
			MakeLease("s1"),
			MakeLease("s2"),
			MakeLease("s3"),
			MakeLease("s4"));
		var agent = CreateAgent(pool, Substitute.For<IOrchestrationReporter>(), ResumeEnabled: true, swapBudgetOverride: 3);

		var task = agent.SendAsync(Prompt);
		await DrainEventsAsync(task);

		var act = () => task.GetResultAsync();
		await act.Should().ThrowAsync<Exception>();
		// 1 original + at most 3 swaps. We don't pin an exact count because the cold-restart
		// branch could short-circuit on a different recoverable failure first; what matters
		// is the loop is bounded by the configured budget.
		pool.AcquireCount.Should().BeLessThanOrEqualTo(4, "the swap loop must not exceed the configured budget");
		pool.SwapsRecorded.Should().BeLessThanOrEqualTo(3);
	}

	[Fact]
	public async Task SessionCreateTransientUpstream_SwapsAndRecreatesInstance_ThenSucceeds()
	{
		// Reproduces the observed production failure: the CLI process is ALIVE but session.create
		// failed because GitHub's OAuth user-fetch returned a transient 503. The health probe
		// stays healthy (transport up), so previously this rethrew raw and failed the whole step.
		// Now it must force-recreate this worker (latch it unhealthy so the pool replaces it) and
		// swap to a fresh CLI that re-authenticates and succeeds.
		const string transient =
			"Communication error with Copilot CLI: Request session.create failed with message: " +
			"Authentication failed: Failed to fetch OAuth user login (503): GitHub returned: " +
			"No server is currently available to service your request.";

		var failingClient = new ScriptedCopilotClient(createSessionThrows: new Exception(transient));
		var failingBroker = new HealthyForceableFaultBroker();

		var recoverySession = new ScriptedCopilotSession("session-recovered", completeImmediately: true);
		var recoveryClient = new ScriptedCopilotClient(recoverySession);

		var pool = new ScriptedPool(
			new ScriptedLease(failingClient, failingBroker),
			new ScriptedLease(recoveryClient, faultBroker: null));
		var reporter = Substitute.For<IOrchestrationReporter>();
		var agent = CreateAgent(pool, reporter, ResumeEnabled: true);

		var task = agent.SendAsync(Prompt);
		var events = await DrainEventsAsync(task);
		var result = await task.GetResultAsync();

		result.Should().NotBeNull("a transient session-create auth failure must recover on a fresh CLI worker");
		pool.AcquireCount.Should().Be(2);
		pool.SwapsRecorded.Should().Be(1);

		// The failing worker was force-latched unhealthy so the pool recreates it (rather than
		// re-selecting the same still-alive-but-transiently-failing CLI process).
		failingBroker.ForceUnhealthyCalls.Should().Be(1);
		failingBroker.IsClientUnhealthy.Should().BeTrue();
		failingBroker.UnhealthyTriggeringFailureReason.Should().Be("session_setup_transient");

		var swaps = events.Where(e => e.Type == AgentEventType.CliInstanceSwapped).ToList();
		swaps.Should().ContainSingle();
		swaps[0].SwapReason.Should().Be("transient_upstream");
		swaps[0].SwapMode.Should().Be("cold_restart", "the session was never created, so there is no id to resume");

		recoveryClient.CreateCalls.Should().HaveCount(1);
		reporter.Received().ReportCliSwapTriggered(
			Arg.Any<string>(), Arg.Any<string?>(), 1, Arg.Any<int>(), "transient_upstream", "cold_restart");
	}

	[Fact]
	public async Task SessionCreatePermanentAuthFailure_DoesNotSwap_AndFailsFast()
	{
		// A PERMANENT auth failure (401 / bad credentials) must NOT be treated as transient —
		// retrying can't fix bad creds, so we fail fast without burning swap budget or churning
		// CLI workers. Guards the conservative classification.
		var failingClient = new ScriptedCopilotClient(createSessionThrows: new Exception(
			"Communication error with Copilot CLI: Request session.create failed with message: " +
			"Authentication failed: 401 Unauthorized: Bad credentials"));
		var failingBroker = new HealthyForceableFaultBroker();

		var pool = new ScriptedPool(new ScriptedLease(failingClient, failingBroker));
		var agent = CreateAgent(pool, Substitute.For<IOrchestrationReporter>(), ResumeEnabled: true);

		var task = agent.SendAsync(Prompt);
		await DrainEventsAsync(task);
		var act = () => task.GetResultAsync();

		await act.Should().ThrowAsync<Exception>();
		pool.AcquireCount.Should().Be(1, "a permanent auth failure must not trigger a swap");
		pool.SwapsRecorded.Should().Be(0);
		failingBroker.ForceUnhealthyCalls.Should().Be(0, "the worker must not be recreated for an unrecoverable auth error");
	}

	[Theory]
	[InlineData("Authentication failed: Failed to fetch OAuth user login (503): GitHub returned: No server is currently available to service your request.")]
	[InlineData("Authentication failed: Failed to fetch OAuth user login: network fetch failed: request failed: error sending request for url (https://api.github.com/copilot_internal/user)")]
	public void LooksLikeTransientUpstream_MatchesTransientSessionCreateAuthFailures(string message)
	{
		CopilotSessionHandler.LooksLikeTransientUpstreamFailure(message, statusCode: null)
			.Should().BeTrue("transient GitHub OAuth/network failures at session.create must be swap-eligible");
	}

	[Theory]
	[InlineData("Authentication failed: 401 Unauthorized: Bad credentials")]
	[InlineData("Authentication failed: token has expired")]
	[InlineData("invalid model")]
	[InlineData(null)]
	public void LooksLikeTransientUpstream_DoesNotMatchPermanentOrUnrelatedFailures(string? message)
	{
		CopilotSessionHandler.LooksLikeTransientUpstreamFailure(message, statusCode: null)
			.Should().BeFalse("permanent auth / unrelated errors must not be retried as transient");
	}

	#region Helpers

	private static CopilotAgent CreateAgent(
		ScriptedPool pool,
		IOrchestrationReporter reporter,
		bool ResumeEnabled,
		int? swapBudgetOverride = null)
	{
		var swap = new CopilotAgentSwapOptions(
			CliSwapBudgetPerStep: swapBudgetOverride ?? 3,
			ResumeOnSwapEnabled: ResumeEnabled,
			ResumeAlreadyInUseWait: TimeSpan.FromMilliseconds(100),
			ResumeAlreadyInUsePollInterval: TimeSpan.FromMilliseconds(50),
			McpStartupTimeout: TimeSpan.Zero);

		return new CopilotAgent(
			clientPool: pool,
			model: Model,
			systemPrompt: null,
			mcps: [],
			subagents: [],
			reasoningLevel: null,
			systemPromptMode: null,
			systemPromptSections: null,
			reporter: reporter,
			engineTools: [],
			engineToolContext: null,
			skillDirectories: [],
			infiniteSessionConfig: null,
			attachments: [],
			swapOptions: swap,
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>(),
			loggerFactory: NullLoggerFactory.Instance);
	}

	private static async Task<List<AgentEvent>> DrainEventsAsync(AgentTask task)
	{
		var events = new List<AgentEvent>();
		await foreach (var evt in task)
		{
			events.Add(evt);
		}
		return events;
	}

	#endregion

	#region Test doubles

	/// <summary>
	/// In-memory <see cref="ICopilotClientPool"/> that hands out a scripted sequence of
	/// clients (one per <c>AcquireAsync</c> call). Each acquire counts toward the
	/// <see cref="AcquireCount"/> and <see cref="SwapsRecorded"/> counters, which the
	/// tests assert against.
	/// </summary>
	private sealed class ScriptedPool : ICopilotClientPool
	{
		private readonly Queue<ScriptedLease> _leases;
		private int _swaps;
		private int _acquires;

		public ScriptedPool(params ScriptedCopilotClient[] clients)
			: this(clients.Select(c => new ScriptedLease(c, faultBroker: null)).ToArray())
		{
		}

		public ScriptedPool(params ScriptedLease[] leases)
		{
			_leases = new Queue<ScriptedLease>(leases);
		}

		public int AcquireCount => Volatile.Read(ref _acquires);
		public int SwapsRecorded => Volatile.Read(ref _swaps);

		public ValueTask<ICopilotClientLease> AcquireAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _acquires);
			if (_leases.Count == 0)
				throw new InvalidOperationException("ScriptedPool exhausted; no more leases configured for this test.");
			return ValueTask.FromResult<ICopilotClientLease>(_leases.Dequeue());
		}

		public void RecordSwapTriggered() => Interlocked.Increment(ref _swaps);
	}

	private sealed class ScriptedLease : ICopilotClientLease
	{
		public ScriptedLease(ICopilotClient client, ISessionFaultBroker? faultBroker)
		{
			Client = client;
			FaultBroker = faultBroker;
		}

		public ICopilotClient Client { get; }
		public ISessionFaultBroker? FaultBroker { get; }
		public IReadOnlyList<AvailableModelInfo>? CachedAvailableModels { get; private set; }
		public void SetCachedAvailableModels(IReadOnlyList<AvailableModelInfo> models) => CachedAvailableModels = models;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class ScriptedCopilotClient : ICopilotClient
	{
		private readonly ScriptedCopilotSession? _session;
		private readonly Exception? _createSessionThrows;
		private readonly Exception? _resumeSessionThrows;

		public ScriptedCopilotClient(ScriptedCopilotSession session)
		{
			_session = session;
		}

		public ScriptedCopilotClient(Exception createSessionThrows)
		{
			_createSessionThrows = createSessionThrows;
		}

		public ScriptedCopilotClient(ScriptedCopilotSession? session = null, Exception? createSessionThrows = null, Exception? resumeSessionThrows = null)
		{
			_session = session;
			_createSessionThrows = createSessionThrows;
			_resumeSessionThrows = resumeSessionThrows;
		}

		public int DiagnosticHash => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

		public List<SessionConfig> CreateCalls { get; } = [];
		public List<(string sessionId, ResumeSessionConfig config)> ResumeCalls { get; } = [];

		public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public Task StopAsync() => Task.CompletedTask;
		public Task PingAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
		{
			CreateCalls.Add(config);
			if (_createSessionThrows is not null)
				return Task.FromException<ICopilotSession>(_createSessionThrows);
			if (_session is null)
				throw new InvalidOperationException("ScriptedCopilotClient has no session configured.");
			// SDK 1.0.0: CopilotAgent now wires the event handler via SessionConfig.OnEvent
			// instead of session.On(...) — capture the config-supplied handler onto the
			// fake so the synthetic SessionIdleEvent fired from SendAsync still drives the
			// session's TaskCompletionSource. Without this the test hangs waiting for
			// completion that never arrives.
			if (config.OnEvent is { } onEvent)
			{
				_session.WireConfigHandler(onEvent);
			}
			return Task.FromResult<ICopilotSession>(_session);
		}

		public Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
		{
			ResumeCalls.Add((sessionId, config));
			if (_resumeSessionThrows is not null)
				return Task.FromException<ICopilotSession>(_resumeSessionThrows);
			if (_session is null)
				throw new InvalidOperationException("ScriptedCopilotClient has no session configured for resume.");
			// Mirror of the CreateSessionAsync path — see comment above. Both paths route
			// the handler through SessionConfig.OnEvent on the SDK 1.0.0 wire.
			if (config.OnEvent is { } onEvent)
			{
				_session.WireConfigHandler(onEvent);
			}
			return Task.FromResult<ICopilotSession>(_session);
		}

		public Task<string?> GetLastSessionIdAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
		public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<ModelInfo>>([]);
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class ScriptedCopilotSession : ICopilotSession
	{
		private readonly Exception? _sendThrows;
		private readonly bool _completeImmediately;
		// SDK 1.0.0 dropped the SessionEventHandler delegate; sessions now register
		// plain Action<SessionEvent> callbacks. CopilotAgent registers the handler at
		// config time via SessionConfig.OnEvent, so the fake ScriptedCopilotClient
		// calls WireConfigHandler on construction; legacy code that calls session.On()
		// after creation also lands here.
		private Action<SessionEvent>? _handler;

		public ScriptedCopilotSession(string sessionId, Exception? sendThrows = null, bool completeImmediately = false)
		{
			SessionId = sessionId;
			_sendThrows = sendThrows;
			_completeImmediately = completeImmediately;
		}

		public string SessionId { get; }

		public IDisposable On(Action<SessionEvent> handler)
		{
			_handler = handler;
			return new NoopDisposable();
		}

		/// <summary>
		/// Test seam: lets the fake client install the SessionConfig.OnEvent handler
		/// onto this session. Mirrors what the SDK 1.0.0 runtime does internally when
		/// the config-supplied OnEvent fires for events the runtime emits.
		/// </summary>
		internal void WireConfigHandler(Action<SessionEvent> handler) => _handler = handler;

		public Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken)
		{
			if (_sendThrows is not null)
				return Task.FromException<string>(_sendThrows);
			if (_completeImmediately)
			{
				// Drive the handler to TrySetResult by simulating a SessionIdleEvent.
				_handler?.Invoke(new SessionIdleEvent { Data = new SessionIdleData() });
			}
			return Task.FromResult("message-id");
		}

		public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class LatchedFaultBroker : ISessionFaultBroker
	{
		public LatchedFaultBroker(string triggeringSessionId, string triggeringFailureReason, string probeDetails)
		{
			UnhealthyTriggeringSessionId = triggeringSessionId;
			UnhealthyTriggeringFailureReason = triggeringFailureReason;
			UnhealthyReason = probeDetails;
		}

		public bool IsClientUnhealthy => true;
		public string? UnhealthyReason { get; }
		public string? UnhealthyTriggeringSessionId { get; }
		public string? UnhealthyTriggeringFailureReason { get; }

		public IDisposable RegisterSession(string sessionId, Action<Exception> onFault) => new NoopDisposable();
		public Task<bool> ProbeAndMaybeFaultSiblingsAsync(string failedSessionId, string failureReason, CancellationToken cancellationToken)
			=> Task.FromResult(false);
		public void ForceUnhealthy(string triggeringSessionId, string triggeringFailureReason, string? details) { }
	}

	/// <summary>
	/// Simulates a fault broker whose first probe latches the client unhealthy — the
	/// realistic outcome when the CLI process dies. The agent's swap loop then transforms
	/// the SDK's transport exception into a <see cref="CopilotClientUnhealthyException"/>,
	/// which is the trigger the swap loop's classifier listens for.
	/// </summary>
	private sealed class ProbeLatchingFaultBroker : ISessionFaultBroker
	{
		private bool _latched;
		public bool IsClientUnhealthy => _latched;
		public string? UnhealthyReason { get; private set; }
		public string? UnhealthyTriggeringSessionId { get; private set; }
		public string? UnhealthyTriggeringFailureReason { get; private set; }

		public IDisposable RegisterSession(string sessionId, Action<Exception> onFault) => new NoopDisposable();

		public Task<bool> ProbeAndMaybeFaultSiblingsAsync(string failedSessionId, string failureReason, CancellationToken cancellationToken)
		{
			_latched = true;
			UnhealthyTriggeringSessionId = failedSessionId;
			UnhealthyTriggeringFailureReason = failureReason;
			UnhealthyReason = $"ping failed; probe latched after '{failedSessionId}' failed with: {failureReason}";
			return Task.FromResult(false); // unhealthy → false return
		}

		public void ForceUnhealthy(string triggeringSessionId, string triggeringFailureReason, string? details)
		{
			_latched = true;
			UnhealthyTriggeringSessionId = triggeringSessionId;
			UnhealthyTriggeringFailureReason = triggeringFailureReason;
			UnhealthyReason = details;
		}
	}

	/// <summary>
	/// Fault broker whose health probe always reports HEALTHY (the CLI transport is alive) —
	/// reproducing the transient-upstream reality where session.create failed only because an
	/// upstream dependency (GitHub OAuth) was briefly down. It latches unhealthy ONLY when
	/// <see cref="ForceUnhealthy"/> is called, so a test can assert the agent force-recreated
	/// the worker for this error class instead of reusing the still-alive process.
	/// </summary>
	private sealed class HealthyForceableFaultBroker : ISessionFaultBroker
	{
		public bool IsClientUnhealthy { get; private set; }
		public string? UnhealthyReason { get; private set; }
		public string? UnhealthyTriggeringSessionId { get; private set; }
		public string? UnhealthyTriggeringFailureReason { get; private set; }
		public int ForceUnhealthyCalls { get; private set; }
		public int ProbeCalls { get; private set; }

		public IDisposable RegisterSession(string sessionId, Action<Exception> onFault) => new NoopDisposable();

		public Task<bool> ProbeAndMaybeFaultSiblingsAsync(string failedSessionId, string failureReason, CancellationToken cancellationToken)
		{
			ProbeCalls++;
			return Task.FromResult(true); // transport healthy → do NOT latch
		}

		public void ForceUnhealthy(string triggeringSessionId, string triggeringFailureReason, string? details)
		{
			ForceUnhealthyCalls++;
			if (IsClientUnhealthy)
				return;
			IsClientUnhealthy = true;
			UnhealthyTriggeringSessionId = triggeringSessionId;
			UnhealthyTriggeringFailureReason = triggeringFailureReason;
			UnhealthyReason = details;
		}
	}

	private sealed class NoopDisposable : IDisposable
	{
		public void Dispose() { }
	}

	#endregion
}
