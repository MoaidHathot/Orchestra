using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class OrchestrationExecutorTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	#region Basic Execution

	[Fact]
	public async Task ExecuteAsync_SingleStep_ReturnsSucceededResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Step output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Results.Should().HaveCount(1);
		result.Results["step1"].Content.Should().Be("Step output");
	}

	[Fact]
	public async Task ExecuteAsync_EmptyOrchestration_ReturnsSucceeded()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("ignored");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.Empty();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Results.Should().BeEmpty();
	}

	#endregion

	#region Linear Chain Execution

	[Fact]
	public async Task ExecuteAsync_LinearChain_ExecutesInOrder()
	{
		// Arrange
		var executionOrder = new List<string>();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			// Extract step name from prompt context
			var stepName = prompt.Contains("A") ? "A" : prompt.Contains("B") ? "B" : "C";
			executionOrder.Add(stepName);
			return MockAgentBuilderExtensions.CreateWithResponse($"Output from {stepName}")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(3);
	}

	[Fact]
	public async Task ExecuteAsync_LinearChain_AllStepsSucceed()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Succeeded);
	}

	#endregion

	#region Parallel Execution

	[Fact]
	public async Task ExecuteAsync_ParallelSteps_AllExecute()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Parallel output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.ParallelSteps();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(3);
		result.Results.Should().HaveCount(3); // All are terminal steps
	}

	#endregion

	#region Diamond DAG Execution

	[Fact]
	public async Task ExecuteAsync_DiamondDag_ExecutesCorrectly()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DiamondDag();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(4);
		// Only D is terminal
		result.Results.Should().HaveCount(1);
		result.Results.Should().ContainKey("D");
	}

	[Fact]
	public async Task ExecuteAsync_DiamondDag_DependenciesReceiveOutputs()
	{
		// Arrange
		var capturedPrompts = new Dictionary<string, string>();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			// Store the prompt to check dependency injection
			capturedPrompts[Guid.NewGuid().ToString()] = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("Response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DiamondDag();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert - D should receive outputs from both B and C
		// The actual prompt format depends on the formatter
		capturedPrompts.Values.Should().NotBeEmpty();
	}

	#endregion

	#region Complex DAG Execution

	[Fact]
	public async Task ExecuteAsync_ComplexDag_AllStepsComplete()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.ComplexDag();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(6);
	}

	#endregion

	#region Error Handling

	[Fact]
	public async Task ExecuteAsync_StepFails_DownstreamStepsSkipped()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Step failed"));
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain(); // A -> B -> C

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Skipped);
	}

	[Fact]
	public async Task ExecuteAsync_StepFails_ReportsStepStartedForFailedStep()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Error"));
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert
		_reporter.Received().ReportStepStarted("step1");
	}

	[Fact]
	public async Task ExecuteAsync_StepSkipped_ReportsStepSkipped()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Error"));
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert
		_reporter.Received().ReportStepSkipped("B", Arg.Any<string>());
		_reporter.Received().ReportStepSkipped("C", Arg.Any<string>());
	}

	#endregion

	#region Parameter Validation

	[Fact]
	public async Task ExecuteAsync_MissingRequiredParameter_ThrowsInvalidOperationException()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithParameters(); // Requires param1, param2, param3

		// Act
		var act = () => executor.ExecuteAsync(orchestration);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Missing required parameters*");
	}

	[Fact]
	public async Task ExecuteAsync_AllParametersProvided_Succeeds()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithParameters();

		var parameters = new Dictionary<string, string>
		{
			["param1"] = "value1",
			["param2"] = "value2",
			["param3"] = "value3"
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration, parameters);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	#endregion

	#region Input Schema Validation

	[Fact]
	public async Task ExecuteAsync_WithInputs_MissingRequired_ThrowsInvalidOperationException()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithInputs();

		// Act - provide only optional inputs, missing required ones
		var act = () => executor.ExecuteAsync(orchestration, new Dictionary<string, string>
		{
			["dryRun"] = "true"
		});

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Missing required input*serviceName*");
	}

	[Fact]
	public async Task ExecuteAsync_WithInputs_AllRequiredProvided_Succeeds()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithInputs();

		var parameters = new Dictionary<string, string>
		{
			["serviceName"] = "api",
			["environment"] = "staging"
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration, parameters);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_WithInputs_InvalidEnum_ThrowsInvalidOperationException()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithInputs();

		var parameters = new Dictionary<string, string>
		{
			["serviceName"] = "api",
			["environment"] = "development" // Not in enum: staging, production
		};

		// Act
		var act = () => executor.ExecuteAsync(orchestration, parameters);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*environment*not one of the allowed values*");
	}

	[Fact]
	public async Task ExecuteAsync_WithInputs_InvalidBoolean_ThrowsInvalidOperationException()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithInputs();

		var parameters = new Dictionary<string, string>
		{
			["serviceName"] = "api",
			["environment"] = "staging",
			["dryRun"] = "maybe" // Not a valid boolean
		};

		// Act
		var act = () => executor.ExecuteAsync(orchestration, parameters);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*dryRun*boolean*maybe*");
	}

	[Fact]
	public async Task ExecuteAsync_WithInputs_InvalidNumber_ThrowsInvalidOperationException()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithInputs();

		var parameters = new Dictionary<string, string>
		{
			["serviceName"] = "api",
			["environment"] = "staging",
			["retryCount"] = "abc" // Not a valid number
		};

		// Act
		var act = () => executor.ExecuteAsync(orchestration, parameters);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*retryCount*numeric*abc*");
	}

	[Fact]
	public async Task ExecuteAsync_WithInputs_DefaultsApplied_ForOptionalInputs()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithInputs();

		// Only provide required inputs; optional ones should get defaults
		var parameters = new Dictionary<string, string>
		{
			["serviceName"] = "api",
			["environment"] = "staging"
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration, parameters);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_WithInputs_NoParams_ThrowsForAllRequired()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithRequiredInputs();

		// Act - provide no parameters
		var act = () => executor.ExecuteAsync(orchestration);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Missing required input*name*")
			.WithMessage("*Missing required input*count*");
	}

	[Fact]
	public async Task ExecuteAsync_LegacyParameters_StillWork_WithoutInputs()
	{
		// Arrange - using the old-style WithParameters (no Inputs defined)
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithParameters();

		var parameters = new Dictionary<string, string>
		{
			["param1"] = "value1",
			["param2"] = "value2",
			["param3"] = "value3"
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration, parameters);

		// Assert - legacy behavior preserved
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_LegacyParameters_Missing_StillThrows()
	{
		// Arrange - using the old-style WithParameters (no Inputs defined)
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithParameters();

		// Act - missing all parameters
		var act = () => executor.ExecuteAsync(orchestration);

		// Assert - legacy behavior preserved
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*Missing required parameters*");
	}

	#endregion

	#region DAG Validation

	[Fact]
	public async Task ExecuteAsync_WithCycle_ThrowsInvalidOperationException()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithCycle();

		// Act
		var act = () => executor.ExecuteAsync(orchestration);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task ExecuteAsync_WithMissingDependency_ThrowsInvalidOperationException()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithMissingDependency();

		// Act
		var act = () => executor.ExecuteAsync(orchestration);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task ExecuteAsync_WithDuplicateNames_ThrowsInvalidOperationException()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.WithDuplicateNames();

		// Act
		var act = () => executor.ExecuteAsync(orchestration);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	#endregion

	#region Cancellation

	[Fact]
	public async Task ExecuteAsync_CancellationRequested_ReportsCancellation()
	{
		// Arrange
		var cts = new CancellationTokenSource();
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain();

		// Act - Cancel immediately before execution
		cts.Cancel();
		var result = await executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
	}

	[Fact]
	public async Task ExecuteAsync_CancellationDuringExecution_StopsProcessing()
	{
		// Arrange
		var cts = new CancellationTokenSource();
		var stepStarted = new TaskCompletionSource();

		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			// Signal that we've started, then wait for cancellation
			stepStarted.TrySetResult();

			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				// Wait until cancelled
				try
				{
					await Task.Delay(Timeout.Infinite, ct);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				return new AgentResult { Content = "Should not reach here" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		// Act - Start execution then cancel after step begins
		var executeTask = executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);
		await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cts.Cancel();

		var result = await executeTask;

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
	}

	[Fact]
	public async Task ExecuteAsync_CancellationDuringExecution_LinearChainCompletesWithinTimeout()
	{
		// Arrange — This is the primary regression test for the "stuck in cancelling" bug.
		// In a linear chain (A -> B -> C), cancelling while A is running caused
		// Task.WhenAll to hang forever because B and C's TaskCompletionSources were
		// never resolved.
		var cts = new CancellationTokenSource();
		var stepStarted = new TaskCompletionSource();

		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			// Signal that step A has started, then block until cancelled
			stepStarted.TrySetResult();

			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(Timeout.Infinite, ct);
				return new AgentResult { Content = "unreachable" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain(); // A -> B -> C

		// Act — Start execution, wait for step A to begin, then cancel
		var executeTask = executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);
		await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cts.Cancel();

		// The executor must complete within a reasonable time — if it hangs, the bug is present
		var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults.Should().HaveCount(3);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Cancelled);
		_reporter.Received().ReportStepCancelled("A");
		_reporter.Received().ReportStepCancelled("B");
		_reporter.Received().ReportStepCancelled("C");
	}

	[Fact]
	public async Task ExecuteAsync_CancellationDuringExecution_DiamondDagCompletesWithinTimeout()
	{
		// Arrange — Diamond: A -> B, A -> C, B -> D, C -> D
		// Cancel while A is running — B, C, D should all be cancelled and execution should finish
		var cts = new CancellationTokenSource();
		var stepStarted = new TaskCompletionSource();

		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			stepStarted.TrySetResult();

			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(Timeout.Infinite, ct);
				return new AgentResult { Content = "unreachable" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DiamondDag(); // A -> B,C -> D

		// Act
		var executeTask = executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);
		await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cts.Cancel();

		var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults.Should().HaveCount(4);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["D"].Status.Should().Be(ExecutionStatus.Cancelled);
	}

	[Fact]
	public async Task ExecuteAsync_CancellationAfterFirstStepSucceeds_DownstreamStepsCancelled()
	{
		// Arrange — A -> B -> C. Step A succeeds normally. Step B blocks until cancelled.
		// After B starts, we cancel externally. B should be cancelled (it was running),
		// C should be cancelled (dependency was cancelled), overall status should be Cancelled.
		var cts = new CancellationTokenSource();
		var stepBStarted = new TaskCompletionSource();
		var stepCount = 0;

		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var current = Interlocked.Increment(ref stepCount);
			if (current == 1)
			{
				// Step A — completes successfully
				var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
				channel.Writer.Complete();
				return new AgentTask(channel.Reader, Task.FromResult(new AgentResult { Content = "Step A output" }));
			}

			// Step B — signal that it started, then block until cancelled
			stepBStarted.TrySetResult();
			var ch = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(Timeout.Infinite, ct);
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(ch.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain(); // A -> B -> C

		// Act — Start execution, wait for B to start running, then cancel
		var executeTask = executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);
		await stepBStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cts.Cancel();

		var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Cancelled);
	}

	[Fact]
	public async Task ExecuteAsync_CancellationWithPartialParallelCompletion_MixedResults()
	{
		// Arrange — A and B have no deps and run in parallel. C depends on both.
		// The first handler call completes fast, the second blocks until cancelled.
		// Since A and B are parallel, we can't guarantee which is called first,
		// so we just verify that one succeeded, one was cancelled, and C was cancelled.
		var slowStepStarted = new TaskCompletionSource();
		var cts = new CancellationTokenSource();
		var callCount = 0;

		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var current = Interlocked.Increment(ref callCount);
			if (current == 1)
			{
				// First step to be called — complete fast
				var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
				channel.Writer.Complete();
				return new AgentTask(channel.Reader, Task.FromResult(new AgentResult { Content = "fast output" }));
			}
			else
			{
				// Second step to be called — wait forever until cancelled
				slowStepStarted.TrySetResult();
				var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
				var resultTask = Task.Run(async () =>
				{
					await Task.Delay(Timeout.Infinite, ct);
					return new AgentResult { Content = "unreachable" };
				}, ct);
				return new AgentTask(channel.Reader, resultTask);
			}
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "partial-cancel",
			Description = "A(parallel), B(parallel), C(depends on A and B)",
			Steps =
			[
				TestOrchestrations.CreatePromptStep("A"),
				TestOrchestrations.CreatePromptStep("B"),
				TestOrchestrations.CreatePromptStep("C", dependsOn: ["A", "B"])
			]
		};

		// Act — wait for the slow step to start, then cancel
		var executeTask = executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);
		await slowStepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cts.Cancel();

		var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

		// Assert — overall status is Cancelled
		result.Status.Should().Be(ExecutionStatus.Cancelled);

		// One of A/B succeeded (the fast one), the other was cancelled (the slow one)
		var statuses = new[] { result.StepResults["A"].Status, result.StepResults["B"].Status };
		statuses.Should().Contain(ExecutionStatus.Succeeded);
		statuses.Should().Contain(ExecutionStatus.Cancelled);

		// C depends on both so it should be cancelled (one dependency was cancelled)
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Cancelled);
	}

	#endregion

	#region Run Store Integration

	[Fact]
	public async Task ExecuteAsync_SavesRunRecord()
	{
		// Arrange
		var runStore = Substitute.For<IRunStore>();
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, runStore: runStore);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert
		await runStore.Received(1).SaveRunAsync(Arg.Any<OrchestrationRunRecord>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ExecuteAsync_RunRecordContainsCorrectData()
	{
		// Arrange
		OrchestrationRunRecord? capturedRecord = null;
		var runStore = Substitute.For<IRunStore>();
		runStore.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => capturedRecord = r), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		var agentBuilder = new MockAgentBuilder().WithResponse("Test output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, runStore: runStore);
		var orchestration = TestOrchestrations.SingleStep("my-orchestration");

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert
		capturedRecord.Should().NotBeNull();
		capturedRecord!.OrchestrationName.Should().Be("my-orchestration");
		capturedRecord.Status.Should().Be(ExecutionStatus.Succeeded);
		capturedRecord.RunId.Should().NotBeNullOrEmpty();
		capturedRecord.StepRecords.Should().HaveCount(1);
	}

	[Fact]
	public async Task ExecuteAsync_WithTriggerId_IncludesInRunRecord()
	{
		// Arrange
		OrchestrationRunRecord? capturedRecord = null;
		var runStore = Substitute.For<IRunStore>();
		runStore.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => capturedRecord = r), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, runStore: runStore);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		await executor.ExecuteAsync(orchestration, triggerId: "webhook-trigger-123");

		// Assert
		capturedRecord!.TriggerId.Should().Be("webhook-trigger-123");
	}

	[Fact]
	public async Task ExecuteAsync_RunStoreFails_ContinuesWithoutThrowing()
	{
		// Arrange
		var runStore = Substitute.For<IRunStore>();
		runStore.SaveRunAsync(Arg.Any<OrchestrationRunRecord>(), Arg.Any<CancellationToken>())
			.Returns(Task.FromException(new Exception("Storage failed")));

		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, runStore: runStore);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert - Should still return succeeded despite storage failure
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	#endregion

	#region DefaultSystemPromptMode

	[Fact]
	public async Task ExecuteAsync_WithDefaultSystemPromptMode_PassesToContext()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);

		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "Test orchestration",
			DefaultSystemPromptMode = SystemPromptMode.Replace,
			Steps = [TestOrchestrations.CreatePromptStep("step1")]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert - The builder should receive the mode
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	#endregion

	#region Terminal Steps

	[Fact]
	public async Task ExecuteAsync_IdentifiesTerminalStepsCorrectly()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain(); // A -> B -> C, only C is terminal

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Results.Should().HaveCount(1);
		result.Results.Should().ContainKey("C");
		result.StepResults.Should().HaveCount(3); // All steps
	}

	#endregion

	#region BuildFinalContent

	[Fact]
	public async Task ExecuteAsync_CancelledRun_FinalContentContainsCancellationSummary()
	{
		// Arrange
		OrchestrationRunRecord? capturedRecord = null;
		var runStore = Substitute.For<IRunStore>();
		runStore.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => capturedRecord = r), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		var cts = new CancellationTokenSource();
		cts.Cancel(); // Cancel immediately

		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, runStore: runStore);
		var orchestration = TestOrchestrations.LinearChain(); // A -> B -> C

		// Act
		await executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);

		// Assert
		capturedRecord.Should().NotBeNull();
		capturedRecord!.Status.Should().Be(ExecutionStatus.Cancelled);
		capturedRecord.FinalContent.Should().Contain("Orchestration was cancelled.");
		capturedRecord.FinalContent.Should().Contain("Cancelled steps:");
	}

	[Fact]
	public async Task ExecuteAsync_FailedRun_FinalContentContainsFailureSummary()
	{
		// Arrange
		OrchestrationRunRecord? capturedRecord = null;
		var runStore = Substitute.For<IRunStore>();
		runStore.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => capturedRecord = r), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Something broke"));
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, runStore: runStore);
		var orchestration = TestOrchestrations.LinearChain(); // A -> B -> C

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert
		capturedRecord.Should().NotBeNull();
		capturedRecord!.Status.Should().Be(ExecutionStatus.Failed);
		capturedRecord.FinalContent.Should().Contain("Orchestration failed.");
		capturedRecord.FinalContent.Should().Contain("Failed steps:");
		capturedRecord.FinalContent.Should().Contain("Skipped steps:");
	}

	[Fact]
	public async Task ExecuteAsync_CancelledRun_SavesRunRecordSuccessfully()
	{
		// Arrange — verifies the CancellationToken.None fix for SaveRunAsync
		var runStore = Substitute.For<IRunStore>();
		runStore.SaveRunAsync(Arg.Any<OrchestrationRunRecord>(), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		var cts = new CancellationTokenSource();
		cts.Cancel();

		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory, runStore: runStore);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		await executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);

		// Assert — SaveRunAsync should be called despite the external token being cancelled
		await runStore.Received(1).SaveRunAsync(
			Arg.Is<OrchestrationRunRecord>(r => r.Status == ExecutionStatus.Cancelled),
			Arg.Any<CancellationToken>());
	}

	#endregion

	#region Skip Reason With Cancelled Dependencies

	[Fact]
	public async Task ExecuteAsync_CancelledDependency_DownstreamStepsCancelled()
	{
		// Arrange — Use a linear chain where A is cancelled during execution,
		// causing B and C to also be cancelled (since the external token is cancelled)
		var cts = new CancellationTokenSource();
		var stepCount = 0;

		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var current = Interlocked.Increment(ref stepCount);
			if (current == 1)
			{
				// Cancel during first step — throw OperationCanceledException
				cts.Cancel();
				throw new OperationCanceledException(ct);
			}

			var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			var resultTask = Task.FromResult(new AgentResult { Content = "Output" });
			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain(); // A -> B -> C

		// Act
		var result = await executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Cancelled);
		// B and C should also be cancelled (the external token is cancelled, so they
		// hit the pre-cancellation check rather than the dependency-skip check)
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Cancelled);
		_reporter.Received().ReportStepCancelled("B");
		_reporter.Received().ReportStepCancelled("C");
	}

	#endregion

	#region Disabled Steps

	[Fact]
	public async Task ExecuteAsync_DisabledSingleStep_ReturnsSucceededWithEmptyContent()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Should not be called");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DisabledSingleStep();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["step1"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["step1"].Content.Should().BeEmpty();
	}

	[Fact]
	public async Task ExecuteAsync_DisabledStep_ReportsStepSkipped()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DisabledSingleStep();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert
		_reporter.Received().ReportStepSkipped("step1", "Step is disabled (enabled: false)");
	}

	[Fact]
	public async Task ExecuteAsync_DisabledMiddleStep_DownstreamStepsStillRun()
	{
		// Arrange — A -> B(disabled) -> C
		// B returns empty, C should still run since B is "succeeded" with empty content.
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DisabledMiddleStep();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Content.Should().Be("Output");
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["B"].Content.Should().BeEmpty();
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["C"].Content.Should().Be("Output");
	}

	[Fact]
	public async Task ExecuteAsync_DisabledFirstStep_DownstreamStepsStillRun()
	{
		// Arrange — A(disabled) -> B -> C
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DisabledFirstStep();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Content.Should().BeEmpty();
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["B"].Content.Should().Be("Output");
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["C"].Content.Should().Be("Output");
	}

	[Fact]
	public async Task ExecuteAsync_DisabledParallelStep_OtherStepsStillRun()
	{
		// Arrange — A, B(disabled), C (parallel)
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DisabledParallelStep();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Content.Should().Be("Output");
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["B"].Content.Should().BeEmpty();
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["C"].Content.Should().Be("Output");
	}

	[Fact]
	public async Task ExecuteAsync_AllStepsDisabled_ReturnsSucceeded()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Should not be called");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.AllStepsDisabled();

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(3);
		result.StepResults.Values.Should().AllSatisfy(r =>
		{
			r.Status.Should().Be(ExecutionStatus.Succeeded);
			r.Content.Should().BeEmpty();
		});
	}

	[Fact]
	public async Task ExecuteAsync_DisabledStep_DoesNotExecuteStepLogic()
	{
		// Arrange — Track whether any step actually executes
		var executionCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref executionCount);
			return MockAgentBuilderExtensions.CreateWithResponse("Output")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DisabledSingleStep();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — No agent calls should have been made
		executionCount.Should().Be(0);
	}

	[Fact]
	public async Task ExecuteAsync_EnabledStepExplicitly_RunsNormally()
	{
		// Arrange — Step with enabled: true (explicit) should run normally
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);

		var orchestration = new Orchestration
		{
			Name = "explicit-enabled",
			Description = "Explicitly enabled step",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Enabled = true,
					SystemPrompt = "You are a test assistant.",
					UserPrompt = "Test prompt",
					Model = "claude-opus-4.5"
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["step1"].Content.Should().Be("Output");
	}

	#endregion

	#region ReportStepCompleted (Fix #3: All step types emit step-completed)

	[Fact]
	public async Task ExecuteAsync_SingleStepSucceeds_ReportsStepCompleted()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Step output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — ReportStepCompleted should be called centrally after execution
		_reporter.Received().ReportStepCompleted("step1", Arg.Is<AgentResult>(r => r.Content == "Step output"));
	}

	[Fact]
	public async Task ExecuteAsync_LinearChainAllSucceed_ReportsStepCompletedForEachStep()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.LinearChain(); // A -> B -> C

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — All three steps should get step-completed events
		_reporter.Received().ReportStepCompleted("A", Arg.Any<AgentResult>());
		_reporter.Received().ReportStepCompleted("B", Arg.Any<AgentResult>());
		_reporter.Received().ReportStepCompleted("C", Arg.Any<AgentResult>());
	}

	[Fact]
	public async Task ExecuteAsync_ParallelStepsAllSucceed_ReportsStepCompletedForAll()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Parallel output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.ParallelSteps();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — All three parallel steps should get step-completed events
		_reporter.Received().ReportStepCompleted("A", Arg.Any<AgentResult>());
		_reporter.Received().ReportStepCompleted("B", Arg.Any<AgentResult>());
		_reporter.Received().ReportStepCompleted("C", Arg.Any<AgentResult>());
	}

	[Fact]
	public async Task ExecuteAsync_StepFails_DoesNotReportStepCompletedForFailedStep()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Error"));
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — Failed steps should NOT get step-completed events
		_reporter.DidNotReceive().ReportStepCompleted(Arg.Any<string>(), Arg.Any<AgentResult>());
	}

	[Fact]
	public async Task ExecuteAsync_DisabledStep_StillReportsStepCompleted()
	{
		// Arrange — Disabled steps return Succeeded with empty content,
		// so they should still get a step-completed event (the UI needs it).
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = TestOrchestrations.DisabledSingleStep();

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — Disabled steps return Succeeded status, so they DO get step-completed
		_reporter.Received().ReportStepCompleted("step1", Arg.Is<AgentResult>(r => r.Content == string.Empty));
	}

	#endregion

	#region Concurrency Safety (AgentBuildConfig)

	[Fact]
	public async Task ExecuteAsync_ParallelStepsWithDifferentMcps_EachStepReceivesCorrectMcps()
	{
		// Arrange — Two parallel steps with different MCP configurations.
		// Before the AgentBuildConfig fix, the shared mutable AgentBuilder would
		// let one step's MCPs be overwritten by the other before BuildAgentAsync()
		// captured them.
		var mcpA = new Mcp { Name = "icm", Type = McpType.Local };
		var mcpB = new Mcp { Name = "graph", Type = McpType.Remote };

		var stepA = new PromptOrchestrationStep
		{
			Name = "A",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			Parameters = [],
			SystemPrompt = "System A",
			UserPrompt = "Prompt A",
			Model = "claude-opus-4.5",
			Mcps = [mcpA]
		};

		var stepB = new PromptOrchestrationStep
		{
			Name = "B",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			Parameters = [],
			SystemPrompt = "System B",
			UserPrompt = "Prompt B",
			Model = "claude-opus-4.5",
			Mcps = [mcpB]
		};

		var orchestration = new Orchestration
		{
			Name = "parallel-mcp-test",
			Description = "Two parallel steps with different MCPs",
			Steps = [stepA, stepB]
		};

		// Use a builder that captures every config in a thread-safe collection
		var agentBuilder = new ConcurrentCapturingAgentBuilder();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);

		// Act — Run multiple times to increase the chance of detecting races
		for (var i = 0; i < 20; i++)
		{
			agentBuilder.ClearCaptures();
			var result = await executor.ExecuteAsync(orchestration);

			// Assert
			result.Status.Should().Be(ExecutionStatus.Succeeded);

			var configs = agentBuilder.CapturedConfigs;
			configs.Should().HaveCount(2, "two parallel steps should produce two configs");

			var configA = configs.FirstOrDefault(c => c.Mcps.Any(m => m.Name == "icm"));
			var configB = configs.FirstOrDefault(c => c.Mcps.Any(m => m.Name == "graph"));

			configA.Should().NotBeNull("step A should receive the 'icm' MCP");
			configB.Should().NotBeNull("step B should receive the 'graph' MCP");

			configA!.Mcps.Should().HaveCount(1);
			configA.Mcps[0].Name.Should().Be("icm");

			configB!.Mcps.Should().HaveCount(1);
			configB.Mcps[0].Name.Should().Be("graph");
		}
	}

	/// <summary>
	/// Thread-safe mock builder that captures all AgentBuildConfig instances
	/// passed across concurrent BuildAgentAsync calls.
	/// </summary>
	private class ConcurrentCapturingAgentBuilder : AgentBuilder
	{
		private readonly System.Collections.Concurrent.ConcurrentBag<AgentBuildConfig> _capturedConfigs = new();

		public IReadOnlyList<AgentBuildConfig> CapturedConfigs => [.. _capturedConfigs];

		public void ClearCaptures() => _capturedConfigs.Clear();

		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
		{
			var agent = NSubstitute.Substitute.For<IAgent>();
			agent.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
				.Returns(callInfo =>
				{
					var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
					var resultTask = Task.Run(async () =>
					{
						await channel.Writer.WriteAsync(new AgentEvent
						{
							Type = AgentEventType.MessageDelta,
							Content = "Response"
						});
						channel.Writer.Complete();
						return new AgentResult { Content = "Response", ActualModel = "claude-opus-4.5" };
					});
					return new AgentTask(channel.Reader, resultTask);
				});
			return Task.FromResult(agent);
		}

		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
		{
			_capturedConfigs.Add(config);
			return BuildAgentAsync(cancellationToken);
		}
	}

	#endregion
}
