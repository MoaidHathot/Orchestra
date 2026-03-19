using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class CheckpointTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	private static PromptOrchestrationStep CreateStep(
		string name,
		string[]? dependsOn = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Prompt,
		DependsOn = dependsOn ?? [],
		Parameters = [],
		SystemPrompt = "Test system prompt",
		UserPrompt = "Test user prompt",
		Model = "claude-opus-4.5",
	};

	private Orchestration CreateOrchestration(
		OrchestrationStep[] steps,
		int? timeoutSeconds = 3600) => new()
	{
		Name = "checkpoint-test",
		Description = "Test checkpoint behavior",
		Steps = steps,
		TimeoutSeconds = timeoutSeconds,
	};

	#region Checkpoint Saving

	[Fact]
	public async Task ExecuteAsync_WithCheckpointStore_SavesCheckpointAfterEachStep()
	{
		// Arrange
		var checkpointStore = new InMemoryCheckpointStore();
		var agentBuilder = new MockAgentBuilder().WithResponse("Step output");

		var orchestration = CreateOrchestration(
		[
			CreateStep("step-a"),
			CreateStep("step-b", dependsOn: ["step-a"]),
		]);

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory,
			checkpointStore: checkpointStore);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);

		// Checkpoints should have been saved after each step, but deleted on completion
		checkpointStore.SaveCount.Should().Be(2, "one checkpoint per successful step");
		checkpointStore.DeleteCount.Should().Be(1, "checkpoint cleaned up after completion");
	}

	[Fact]
	public async Task ExecuteAsync_CheckpointContainsOnlySucceededSteps()
	{
		// Arrange
		CheckpointData? lastCheckpoint = null;
		var checkpointStore = new InMemoryCheckpointStore
		{
			OnSave = cp => lastCheckpoint = cp,
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");

		var orchestration = CreateOrchestration(
		[
			CreateStep("step-a"),
			CreateStep("step-b", dependsOn: ["step-a"]),
		]);

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory,
			checkpointStore: checkpointStore);

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — the last checkpoint should contain both steps
		lastCheckpoint.Should().NotBeNull();
		lastCheckpoint!.CompletedSteps.Should().HaveCount(2);
		lastCheckpoint.CompletedSteps.Should().ContainKey("step-a");
		lastCheckpoint.CompletedSteps.Should().ContainKey("step-b");
		lastCheckpoint.CompletedSteps["step-a"].Status.Should().Be(ExecutionStatus.Succeeded);
		lastCheckpoint.CompletedSteps["step-b"].Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_WithNullCheckpointStore_DoesNotThrow()
	{
		// Arrange — default NullCheckpointStore
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var orchestration = CreateOrchestration([CreateStep("step-a")]);

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory);

		// Act & Assert — should work fine with no checkpoint store
		var result = await executor.ExecuteAsync(orchestration);
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_CheckpointSaveFailure_DoesNotFailOrchestration()
	{
		// Arrange — checkpoint store that throws on save
		var failingStore = new InMemoryCheckpointStore
		{
			ThrowOnSave = true,
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var orchestration = CreateOrchestration([CreateStep("step-a")]);

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory,
			checkpointStore: failingStore);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — orchestration should still succeed despite checkpoint save failure
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_CheckpointDeletedOnCompletion()
	{
		// Arrange
		var checkpointStore = new InMemoryCheckpointStore();
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var orchestration = CreateOrchestration([CreateStep("step-a")]);

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory,
			checkpointStore: checkpointStore);

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert — checkpoint should be deleted after completion
		checkpointStore.DeleteCount.Should().Be(1);
		var remaining = await checkpointStore.ListCheckpointsAsync();
		remaining.Should().BeEmpty("checkpoint should be deleted after successful completion");
	}

	[Fact]
	public async Task ExecuteAsync_ParallelSteps_SavesCheckpointForEach()
	{
		// Arrange
		var checkpointStore = new InMemoryCheckpointStore();
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");

		// Three parallel steps (no dependencies between them)
		var orchestration = CreateOrchestration(
		[
			CreateStep("step-a"),
			CreateStep("step-b"),
			CreateStep("step-c"),
		]);

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory,
			checkpointStore: checkpointStore);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		checkpointStore.SaveCount.Should().Be(3, "one checkpoint save per step");
	}

	[Fact]
	public async Task ExecuteAsync_CheckpointPreservesParameters()
	{
		// Arrange
		CheckpointData? lastCheckpoint = null;
		var checkpointStore = new InMemoryCheckpointStore
		{
			OnSave = cp => lastCheckpoint = cp,
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Output");
		var orchestration = CreateOrchestration([CreateStep("step-a")]);

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory,
			checkpointStore: checkpointStore);

		var parameters = new Dictionary<string, string> { ["key1"] = "value1", ["key2"] = "value2" };

		// Act
		await executor.ExecuteAsync(orchestration, parameters);

		// Assert
		lastCheckpoint.Should().NotBeNull();
		lastCheckpoint!.Parameters.Should().ContainKey("key1");
		lastCheckpoint.Parameters.Should().ContainKey("key2");
		lastCheckpoint.Parameters["key1"].Should().Be("value1");
		lastCheckpoint.OrchestrationName.Should().Be("checkpoint-test");
	}

	#endregion

	#region Resume Behavior

	[Fact]
	public async Task ResumeAsync_SkipsCompletedSteps()
	{
		// Arrange
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.MessageDelta,
					Content = "Resumed output"
				}, ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "Resumed output" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var orchestration = CreateOrchestration(
		[
			CreateStep("step-a"),
			CreateStep("step-b", dependsOn: ["step-a"]),
			CreateStep("step-c", dependsOn: ["step-b"]),
		]);

		// Checkpoint: step-a completed, step-b and step-c remaining
		var checkpoint = new CheckpointData
		{
			RunId = "resume-test-001",
			OrchestrationName = "checkpoint-test",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CheckpointedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new()
				{
					Status = ExecutionStatus.Succeeded,
					Content = "Step A output",
				}
			},
		};

		var checkpointStore = new InMemoryCheckpointStore();
		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory,
			checkpointStore: checkpointStore);

		// Act
		var result = await executor.ResumeAsync(orchestration, checkpoint);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		callCount.Should().Be(2, "step-a was restored from checkpoint; only step-b and step-c should execute");
		result.StepResults.Should().ContainKey("step-a");
		result.StepResults.Should().ContainKey("step-b");
		result.StepResults.Should().ContainKey("step-c");
		result.StepResults["step-a"].Content.Should().Be("Step A output", "restored from checkpoint");
		result.StepResults["step-b"].Content.Should().Be("Resumed output");
		result.StepResults["step-c"].Content.Should().Be("Resumed output");
	}

	[Fact]
	public async Task ResumeAsync_WithMultipleCompletedSteps_SkipsAll()
	{
		// Arrange
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.MessageDelta,
					Content = "Final output"
				}, ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "Final output" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var orchestration = CreateOrchestration(
		[
			CreateStep("step-a"),
			CreateStep("step-b"),
			CreateStep("step-c", dependsOn: ["step-a", "step-b"]),
		]);

		// Both step-a and step-b completed
		var checkpoint = new CheckpointData
		{
			RunId = "resume-test-002",
			OrchestrationName = "checkpoint-test",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CheckpointedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new()
				{
					Status = ExecutionStatus.Succeeded,
					Content = "A output",
				},
				["step-b"] = new()
				{
					Status = ExecutionStatus.Succeeded,
					Content = "B output",
				}
			},
		};

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory);

		// Act
		var result = await executor.ResumeAsync(orchestration, checkpoint);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		callCount.Should().Be(1, "only step-c should execute, step-a and step-b restored from checkpoint");
	}

	[Fact]
	public async Task ResumeAsync_RestoredStepResultsAvailableAsDependencies()
	{
		// Arrange — step-b depends on step-a, which was checkpointed with specific content
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.MessageDelta,
					Content = "B uses A"
				}, ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "B uses A" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var orchestration = CreateOrchestration(
		[
			CreateStep("step-a"),
			new PromptOrchestrationStep
			{
				Name = "step-b",
				Type = OrchestrationStepType.Prompt,
				DependsOn = ["step-a"],
				Parameters = [],
				SystemPrompt = "System",
				UserPrompt = "Use this: {{step-a.output}}",
				Model = "claude-opus-4.5",
			},
		]);

		var checkpoint = new CheckpointData
		{
			RunId = "resume-dep-test",
			OrchestrationName = "checkpoint-test",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CheckpointedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new()
				{
					Status = ExecutionStatus.Succeeded,
					Content = "Checkpointed A content",
				}
			},
		};

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory);

		// Act
		var result = await executor.ResumeAsync(orchestration, checkpoint);

		// Assert — step-b should have received step-a's checkpointed output in its prompt
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		capturedPrompt.Should().NotBeNull();
		capturedPrompt.Should().Contain("Checkpointed A content",
			"the restored step-a output should be available as dependency");
	}

	[Fact]
	public async Task ResumeAsync_AllStepsAlreadyComplete_ReturnsImmediately()
	{
		// Arrange — all steps already in checkpoint
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromResult(new AgentResult { Content = "X" }));
		});

		var orchestration = CreateOrchestration(
		[
			CreateStep("step-a"),
			CreateStep("step-b", dependsOn: ["step-a"]),
		]);

		var checkpoint = new CheckpointData
		{
			RunId = "all-complete-test",
			OrchestrationName = "checkpoint-test",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CheckpointedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new() { Status = ExecutionStatus.Succeeded, Content = "A done" },
				["step-b"] = new() { Status = ExecutionStatus.Succeeded, Content = "B done" },
			},
		};

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory);

		// Act
		var result = await executor.ResumeAsync(orchestration, checkpoint);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		callCount.Should().Be(0, "no steps should be executed since all were restored from checkpoint");
		result.StepResults["step-a"].Content.Should().Be("A done");
		result.StepResults["step-b"].Content.Should().Be("B done");
	}

	[Fact]
	public async Task ResumeAsync_PreservesRunId()
	{
		// Arrange
		CheckpointData? savedCheckpoint = null;
		var checkpointStore = new InMemoryCheckpointStore
		{
			OnSave = cp => savedCheckpoint = cp,
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Output");

		var orchestration = CreateOrchestration(
		[
			CreateStep("step-a"),
			CreateStep("step-b", dependsOn: ["step-a"]),
		]);

		var originalRunId = "original-run-123";
		var checkpoint = new CheckpointData
		{
			RunId = originalRunId,
			OrchestrationName = "checkpoint-test",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CheckpointedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new() { Status = ExecutionStatus.Succeeded, Content = "A" },
			},
		};

		var executor = new OrchestrationExecutor(
			_scheduler, agentBuilder, NullOrchestrationReporter.Instance, _loggerFactory,
			checkpointStore: checkpointStore);

		// Act
		await executor.ResumeAsync(orchestration, checkpoint);

		// Assert — the run ID in the new checkpoint should match the original
		savedCheckpoint.Should().NotBeNull();
		savedCheckpoint!.RunId.Should().Be(originalRunId, "resume should preserve the original run ID");
	}

	#endregion

	#region CheckpointStepResult Round-Trip

	[Fact]
	public void CheckpointStepResult_RoundTrip_PreservesAllFields()
	{
		// Arrange
		var original = ExecutionResult.Succeeded(
			content: "Test content",
			rawContent: "Raw content",
			rawDependencyOutputs: new Dictionary<string, string> { ["dep1"] = "dep1-output" },
			promptSent: "The prompt",
			actualModel: "claude-opus-4.5");

		// Act
		var checkpointResult = CheckpointStepResult.FromExecutionResult(original);
		var restored = checkpointResult.ToExecutionResult();

		// Assert
		restored.Status.Should().Be(ExecutionStatus.Succeeded);
		restored.Content.Should().Be("Test content");
		restored.RawContent.Should().Be("Raw content");
		restored.RawDependencyOutputs.Should().ContainKey("dep1");
		restored.RawDependencyOutputs["dep1"].Should().Be("dep1-output");
		restored.PromptSent.Should().Be("The prompt");
		restored.ActualModel.Should().Be("claude-opus-4.5");
		restored.ErrorMessage.Should().BeNull();
	}

	[Fact]
	public void CheckpointStepResult_RoundTrip_PreservesFailedResult()
	{
		// Arrange
		var original = ExecutionResult.Failed("Something went wrong");

		// Act
		var checkpointResult = CheckpointStepResult.FromExecutionResult(original);
		var restored = checkpointResult.ToExecutionResult();

		// Assert
		restored.Status.Should().Be(ExecutionStatus.Failed);
		restored.Content.Should().BeEmpty();
		restored.ErrorMessage.Should().Be("Something went wrong");
	}

	#endregion

	#region NullCheckpointStore

	[Fact]
	public async Task NullCheckpointStore_SaveDoesNothing()
	{
		var store = NullCheckpointStore.Instance;
		var checkpoint = new CheckpointData
		{
			RunId = "test",
			OrchestrationName = "test",
			StartedAt = DateTimeOffset.UtcNow,
			CheckpointedAt = DateTimeOffset.UtcNow,
			CompletedSteps = [],
		};

		// Should not throw
		await store.SaveCheckpointAsync(checkpoint);
	}

	[Fact]
	public async Task NullCheckpointStore_LoadReturnsNull()
	{
		var store = NullCheckpointStore.Instance;
		var result = await store.LoadCheckpointAsync("test", "test-run");
		result.Should().BeNull();
	}

	[Fact]
	public async Task NullCheckpointStore_ListReturnsEmpty()
	{
		var store = NullCheckpointStore.Instance;
		var result = await store.ListCheckpointsAsync();
		result.Should().BeEmpty();
	}

	#endregion

	/// <summary>
	/// In-memory implementation of ICheckpointStore for testing.
	/// </summary>
	private class InMemoryCheckpointStore : ICheckpointStore
	{
		private readonly ConcurrentDictionary<string, CheckpointData> _checkpoints = new();
		public int SaveCount;
		public int DeleteCount;
		public bool ThrowOnSave;
		public Action<CheckpointData>? OnSave;

		private static string Key(string orchestrationName, string runId) => $"{orchestrationName}:{runId}";

		public Task SaveCheckpointAsync(CheckpointData checkpoint, CancellationToken cancellationToken = default)
		{
			if (ThrowOnSave)
				throw new IOException("Simulated save failure");

			Interlocked.Increment(ref SaveCount);
			_checkpoints[Key(checkpoint.OrchestrationName, checkpoint.RunId)] = checkpoint;
			OnSave?.Invoke(checkpoint);
			return Task.CompletedTask;
		}

		public Task<CheckpointData?> LoadCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
		{
			_checkpoints.TryGetValue(Key(orchestrationName, runId), out var checkpoint);
			return Task.FromResult(checkpoint);
		}

		public Task DeleteCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref DeleteCount);
			_checkpoints.TryRemove(Key(orchestrationName, runId), out _);
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<CheckpointData>> ListCheckpointsAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
		{
			IReadOnlyList<CheckpointData> result = orchestrationName is null
				? _checkpoints.Values.ToList()
				: _checkpoints.Values.Where(c => c.OrchestrationName == orchestrationName).ToList();
			return Task.FromResult(result);
		}
	}
}
