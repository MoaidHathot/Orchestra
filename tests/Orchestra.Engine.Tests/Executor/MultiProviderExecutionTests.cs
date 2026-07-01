using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Verifies per-step / per-orchestration agent-provider selection: the executor opens one
/// run scope per distinct provider the run uses and routes each Prompt step to the builder
/// resolved from <c>step.provider → orchestration.defaultProvider → host default</c>.
/// </summary>
public class MultiProviderExecutionTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();

	private static PromptOrchestrationStep Step(string name, string? provider, string[]? dependsOn = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Prompt,
		DependsOn = dependsOn ?? [],
		SystemPrompt = "sys",
		UserPrompt = "user",
		Model = "claude-opus-4.8",
		Provider = provider,
	};

	[Fact]
	public async Task PerStepProvider_RoutesEachStepToItsProvider_AndOpensScopePerProvider()
	{
		var copilot = new RecordingAgentBuilder("copilot");
		var opencode = new RecordingAgentBuilder("opencode");
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = copilot, ["opencode"] = opencode },
			defaultProviderName: "copilot");
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, NullLoggerFactory.Instance);

		var orchestration = new Orchestration
		{
			Name = "mixed",
			Description = "mixed providers",
			DefaultProvider = "copilot",
			Steps =
			[
				Step("a", provider: null),          // → default (copilot)
				Step("b", provider: "opencode"),    // → opencode
			],
		};

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["a"].Content.Should().Be("copilot");
		result.StepResults["b"].Content.Should().Be("opencode");

		// One run scope opened (and disposed) per distinct provider used by the run.
		copilot.RunScopeOpenCount.Should().Be(1);
		opencode.RunScopeOpenCount.Should().Be(1);
		copilot.RunScopeDisposeCount.Should().Be(1);
		opencode.RunScopeDisposeCount.Should().Be(1);
	}

	[Fact]
	public async Task OrchestrationDefaultProvider_AppliesToStepsWithoutProvider()
	{
		var copilot = new RecordingAgentBuilder("copilot");
		var opencode = new RecordingAgentBuilder("opencode");
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = copilot, ["opencode"] = opencode },
			defaultProviderName: "copilot");
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, NullLoggerFactory.Instance);

		var orchestration = new Orchestration
		{
			Name = "all-opencode",
			Description = "default provider opencode",
			DefaultProvider = "opencode",
			Steps = [Step("a", provider: null), Step("b", provider: null, dependsOn: ["a"])],
		};

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["a"].Content.Should().Be("opencode");
		result.StepResults["b"].Content.Should().Be("opencode");

		// The unused Copilot pool must NOT be opened for an all-OpenCode run.
		opencode.RunScopeOpenCount.Should().Be(1);
		copilot.RunScopeOpenCount.Should().Be(0);
	}

	[Fact]
	public async Task StepProvider_OverridesOrchestrationDefault()
	{
		var copilot = new RecordingAgentBuilder("copilot");
		var opencode = new RecordingAgentBuilder("opencode");
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = copilot, ["opencode"] = opencode },
			defaultProviderName: "opencode");
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, NullLoggerFactory.Instance);

		var orchestration = new Orchestration
		{
			Name = "override",
			Description = "step overrides default",
			DefaultProvider = "opencode",
			Steps = [Step("a", provider: "copilot")],
		};

		var result = await executor.ExecuteAsync(orchestration);

		result.StepResults["a"].Content.Should().Be("copilot");
		copilot.RunScopeOpenCount.Should().Be(1);
		opencode.RunScopeOpenCount.Should().Be(0);
	}

	[Fact]
	public async Task UnknownProvider_FailsFast()
	{
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = new RecordingAgentBuilder("copilot") },
			defaultProviderName: "copilot");
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, NullLoggerFactory.Instance);

		var orchestration = new Orchestration
		{
			Name = "bad-provider",
			Description = "typo provider",
			Steps = [Step("a", provider: "coppilot")],
		};

		var act = () => executor.ExecuteAsync(orchestration);

		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Unknown agent provider 'coppilot'*");
	}

	[Fact]
	public async Task ProviderSubstitution_FailsFast_WhenSingleProviderHostIgnoresRequestedProvider()
	{
		// Reproduces the Portal misconfiguration: the host registered a single (Copilot) builder
		// behind a SingleAgentProviderRegistry, which ignores per-step `provider` and returns the
		// one builder for ANY name. A step asking for `opencode` must NOT silently run on Copilot —
		// the engine fails it fast.
		var copilot = new RecordingAgentBuilder("copilot");
		var registry = new SingleAgentProviderRegistry(copilot, providerName: "copilot");
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, NullLoggerFactory.Instance);

		var orchestration = new Orchestration
		{
			Name = "single-provider-host",
			Description = "opencode step on a copilot-only host",
			Steps = [Step("research-opencode", provider: "opencode")],
		};

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Failed);
		var step = result.StepResults["research-opencode"];
		step.Status.Should().Be(ExecutionStatus.Failed);
		step.ErrorCategory.Should().Be(StepErrorCategory.ValidationError);
		step.ErrorMessage.Should().Contain("requested provider 'opencode'");
		step.ErrorMessage.Should().Contain("resolved it to provider 'copilot'");
		// The step must never reach the agent: Copilot's echo content would be "copilot".
		step.Content.Should().NotBe("copilot");
	}

	[Fact]
	public async Task StepRecord_CapturesConfiguredAndActualProvider_OnSuccess()
	{
		var copilot = new RecordingAgentBuilder("copilot");
		var opencode = new RecordingAgentBuilder("opencode");
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = copilot, ["opencode"] = opencode },
			defaultProviderName: "copilot");
		var executor = new OrchestrationExecutor(_scheduler, registry, _reporter, NullLoggerFactory.Instance);

		var orchestration = new Orchestration
		{
			Name = "labeled",
			Description = "provider labels on the trace",
			DefaultProvider = "copilot",
			Steps =
			[
				Step("a", provider: null),        // configured = default (copilot), actual = copilot
				Step("b", provider: "opencode"),  // configured = opencode, actual = opencode
			],
		};

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded);

		result.StepResults["a"].Trace!.ConfiguredProvider.Should().Be("copilot");
		result.StepResults["a"].Trace!.ActualProvider.Should().Be("copilot");

		result.StepResults["b"].Trace!.ConfiguredProvider.Should().Be("opencode");
		result.StepResults["b"].Trace!.ActualProvider.Should().Be("opencode");
	}

	/// <summary>
	/// Minimal <see cref="AgentBuilder"/> that records run-scope open/dispose counts and the
	/// models it built, and whose agent echoes the builder's provider name so a test can assert
	/// which provider executed each step.
	/// </summary>
	private sealed class RecordingAgentBuilder(string name) : AgentBuilder
	{
		private int _runScopeOpenCount;
		private int _runScopeDisposeCount;

		public int RunScopeOpenCount => _runScopeOpenCount;
		public int RunScopeDisposeCount => _runScopeDisposeCount;

		public override Task<IAsyncDisposable> CreateRunScopeAsync(AgentPoolConfig? agentPool = null, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _runScopeOpenCount);
			return Task.FromResult<IAsyncDisposable>(new Scope(this));
		}

		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
			=> BuildAgentAsync(new AgentBuildConfig { Model = "unset" }, cancellationToken);

		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
			=> Task.FromResult<IAgent>(new EchoAgent(name));

		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All(name);

		public override AgentRuntimeStatus? GetRuntimeStatus() => new(name, _runScopeOpenCount, 0, 0);

		private sealed class Scope(RecordingAgentBuilder owner) : IAsyncDisposable
		{
			public ValueTask DisposeAsync()
			{
				Interlocked.Increment(ref owner._runScopeDisposeCount);
				return ValueTask.CompletedTask;
			}
		}

		private sealed class EchoAgent(string providerName) : IAgent
		{
			public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
			{
				var channel = Channel.CreateUnbounded<AgentEvent>();
				var resultTask = Task.Run(async () =>
				{
					await channel.Writer.WriteAsync(new AgentEvent { Type = AgentEventType.MessageDelta, Content = providerName }, cancellationToken);
					channel.Writer.Complete();
					return new AgentResult { Content = providerName, ActualModel = providerName };
				}, cancellationToken);
				return new AgentTask(channel.Reader, resultTask);
			}
		}
	}
}
