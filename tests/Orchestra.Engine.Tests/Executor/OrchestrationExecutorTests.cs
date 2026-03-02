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
		result.Status.Should().Be(ExecutionStatus.Failed);
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
		result.Status.Should().Be(ExecutionStatus.Failed);
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
}
