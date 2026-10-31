using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class RunScopeLifecycleTests
{
	private static PromptOrchestrationStep Step(string name, string provider, bool enabled = true) => new()
	{
		Name = name, Type = OrchestrationStepType.Prompt, SystemPrompt = "", UserPrompt = name,
		Model = "test", Provider = provider, Enabled = enabled,
	};

	private static Orchestration Definition(params OrchestrationStep[] steps) => new()
	{
		Name = "scope-lifecycle", Description = "Provider lifetime and isolation", TimeoutSeconds = null, Steps = steps,
	};

	private static OrchestrationExecutor Executor(params (string Name, AgentBuilder Builder)[] builders)
		=> new(new OrchestrationScheduler(), new AgentProviderRegistry(builders.ToDictionary(b => b.Name, b => b.Builder), builders[0].Name),
			NullOrchestrationReporter.Instance, NullLoggerFactory.Instance);

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StartupFailure_DisposesPreviouslyOpenedScopes(bool synchronousFailure)
	{
		var scope = new ProbeScope();
		var good = new StartingBuilder(_ => Task.FromResult<IAsyncDisposable>(scope));
		var failure = new StartingBuilder(_ => synchronousFailure
			? throw new InvalidOperationException("startup failed")
			: Task.FromException<IAsyncDisposable>(new InvalidOperationException("startup failed")));
		var executor = Executor(("good", good), ("failure", failure));

		var execute = () => executor.ExecuteAsync(Definition(Step("a", "good"), Step("b", "failure")));
		await execute.Should().ThrowAsync<InvalidOperationException>().WithMessage("startup failed");

		scope.Disposed.Should().BeTrue("another provider's startup failure must not leak a successfully opened process pool");
	}

	[Fact]
	public async Task StartupFailure_CancelsOtherPendingProviderStartups()
	{
		var pending = new StartingBuilder(async token =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return new ProbeScope();
		});
		var failure = new StartingBuilder(_ => Task.FromException<IAsyncDisposable>(new InvalidOperationException("startup failed")));
		var executor = Executor(("pending", pending), ("failure", failure));
		using var cancellation = new CancellationTokenSource();
		var run = executor.ExecuteAsync(Definition(Step("a", "pending"), Step("b", "failure")), cancellationToken: cancellation.Token);
		try
		{
			var execute = () => run.WaitAsync(TimeSpan.FromSeconds(3));
			await execute.Should().ThrowAsync<InvalidOperationException>().WithMessage("startup failed");
		}
		finally
		{
			await cancellation.CancelAsync();
			try { await run; } catch (InvalidOperationException) { }
		}
	}

	[Fact]
	public async Task DisabledPrompt_DoesNotStartItsProvider()
	{
		var unavailable = new StartingBuilder(_ => throw new InvalidOperationException("unavailable provider"));
		var result = await Executor(("unavailable", unavailable)).ExecuteAsync(Definition(Step("disabled", "unavailable", enabled: false)));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		unavailable.Starts.Should().Be(0);
	}

	[Fact]
	public async Task RestoredPrompts_DoNotReopenUnusedProviders()
	{
		var unavailable = new StartingBuilder(_ => throw new InvalidOperationException("unavailable provider"));
		var definition = Definition(Step("restored", "unavailable"));
		var checkpoint = new CheckpointData
		{
			RunId = "restored-run", OrchestrationName = definition.Name, StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
			CheckpointedAt = DateTimeOffset.UtcNow, CompletedSteps = new()
			{
				["restored"] = CheckpointStepResult.FromExecutionResult(ExecutionResult.Succeeded("saved")),
			},
		};

		var result = await Executor(("unavailable", unavailable)).ResumeAsync(definition, checkpoint);

		result.StepResults["restored"].Content.Should().Be("saved");
		unavailable.Starts.Should().Be(0);
	}

	[Fact]
	public async Task PendingChecker_StillOpensTheProviderOfItsRestoredLoopTarget()
	{
		var targetScope = new ProbeScope();
		var checkerScope = new ProbeScope();
		var target = new StartingBuilder(_ => Task.FromResult<IAsyncDisposable>(targetScope));
		var checker = new StartingBuilder(_ => Task.FromResult<IAsyncDisposable>(checkerScope));
		var providers = new AgentProviderRegistry(new Dictionary<string, AgentBuilder> { ["target"] = target, ["checker"] = checker }, "checker");
		var checks = 0;
		var drafts = 0;
		var registry = new StepExecutorRegistry().Register(new DelegateStepExecutor(OrchestrationStepType.Prompt, (step, _, _) =>
		{
			if (step.Name == "draft") drafts++;
			return Task.FromResult(ExecutionResult.Succeeded(step.Name == "check" && ++checks == 1 ? "refine" : "PASS"));
		}));
		var definition = Definition(Step("draft", "target"), new PromptOrchestrationStep
		{
			Name = "check", Type = OrchestrationStepType.Prompt, SystemPrompt = "", UserPrompt = "check", Model = "test", Provider = "checker",
			DependsOn = ["draft"], Loop = new() { Target = "draft", ExitPattern = "PASS", MaxIterations = 1 },
		});
		var checkpoint = new CheckpointData
		{
			RunId = "loop-resume", OrchestrationName = definition.Name, StartedAt = DateTimeOffset.UtcNow,
			CheckpointedAt = DateTimeOffset.UtcNow,
			CompletedSteps = new() { ["draft"] = CheckpointStepResult.FromExecutionResult(ExecutionResult.Succeeded("original")) },
		};
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), providers, NullOrchestrationReporter.Instance,
			NullLoggerFactory.Instance, stepExecutorRegistry: registry);

		(await executor.ResumeAsync(definition, checkpoint)).Status.Should().Be(ExecutionStatus.Succeeded);

		target.Starts.Should().Be(1);
		checker.Starts.Should().Be(1);
		drafts.Should().Be(1, "only the refinement, not the restored initial target, runs");
		targetScope.Disposed.Should().BeTrue();
		checkerScope.Disposed.Should().BeTrue();
	}

	[Fact]
	public async Task InvalidConcurrency_IsRejectedBeforeStartingProviderResources()
	{
		var builder = new StartingBuilder(_ => Task.FromResult<IAsyncDisposable>(new ProbeScope()));
		var definition = new Orchestration
		{
			Name = "invalid-cap", Description = "Validate before resource acquisition", MaxConcurrency = 0, Steps = [Step("a", "provider")],
		};
		var run = () => Executor(("provider", builder)).ExecuteAsync(definition);

		await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maxConcurrency*");
		builder.Starts.Should().Be(0);
	}

	[Fact]
	public async Task ConcurrentRuns_FlowTheirOwnAsyncLocalScopeThroughAsyncStartupAndStepWorkers()
	{
		var builder = new ScopeFlowBuilder();
		var executor = Executor(("scoped", builder));
		var definition = Definition(Step("a", "scoped"), Step("b", "scoped"));

		var runs = await Task.WhenAll(executor.ExecuteAsync(definition), executor.ExecuteAsync(definition));

		runs.Should().OnlyContain(run => run.Status == ExecutionStatus.Succeeded);
		foreach (var run in runs)
			run.StepResults["a"].Content.Should().Be(run.StepResults["b"].Content, "both workers share their run's pool");
		runs[0].StepResults["a"].Content.Should().NotBe(runs[1].StepResults["a"].Content, "parallel runs have independent pools");
		builder.Scopes.Should().HaveCount(2).And.OnlyContain(scope => scope.Disposed);
	}

	private sealed class ProbeScope : IAsyncDisposable
	{
		public string Id { get; } = Guid.NewGuid().ToString("N");
		public bool Disposed { get; private set; }
		public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
	}

	private sealed class StartingBuilder(Func<CancellationToken, Task<IAsyncDisposable>> start) : AgentBuilder
	{
		public int Starts { get; private set; }
		public override Task<IAsyncDisposable> CreateRunScopeAsync(AgentPoolConfig? agentPool = null, CancellationToken cancellationToken = default)
		{
			Starts++;
			return start(cancellationToken);
		}
		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("test");
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class ScopeFlowBuilder : AgentBuilder
	{
		private readonly AsyncLocal<ProbeScope?> _scope = new();
		public List<ProbeScope> Scopes { get; } = [];
		public override Task<IAsyncDisposable> CreateRunScopeAsync(AgentPoolConfig? agentPool = null, CancellationToken cancellationToken = default)
		{
			var scope = new ProbeScope();
			_scope.Value = scope;
			Scopes.Add(scope);
			return InitializeAsync(scope);
		}
		private static async Task<IAsyncDisposable> InitializeAsync(ProbeScope scope) { await Task.Yield(); return scope; }
		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("scoped");
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public override async Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
		{
			await Task.Yield();
			var scope = _scope.Value ?? throw new InvalidOperationException("Lost run scope");
			if (scope.Disposed) throw new InvalidOperationException("Scope disposed before its workers finished");
			return new ScopeAgent(scope);
		}
		private sealed class ScopeAgent(ProbeScope scope) : IAgent
		{
			public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
			{
				var channel = Channel.CreateUnbounded<AgentEvent>();
				channel.Writer.TryComplete();
				return new(channel.Reader, Task.FromResult(new AgentResult { Content = scope.Id }));
			}
		}
	}
}
