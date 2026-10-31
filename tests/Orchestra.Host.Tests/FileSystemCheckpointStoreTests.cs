using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for FileSystemCheckpointStore.
/// </summary>
public class FileSystemCheckpointStoreTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemCheckpointStore _store;

	public FileSystemCheckpointStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-checkpoint-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemCheckpointStore(_tempDir, NullLogger<FileSystemCheckpointStore>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private static CheckpointData CreateTestCheckpoint(
		string? runId = null,
		string? orchestrationName = null,
		Dictionary<string, CheckpointStepResult>? completedSteps = null,
		Dictionary<string, string>? parameters = null,
		string? triggerId = null)
	{
		return new CheckpointData
		{
			RunId = runId ?? Guid.NewGuid().ToString("N")[..12],
			OrchestrationName = orchestrationName ?? "test-orchestration",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CheckpointedAt = DateTimeOffset.UtcNow,
			Parameters = parameters ?? [],
			TriggerId = triggerId,
			CompletedSteps = completedSteps ?? new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new()
				{
					Status = ExecutionStatus.Succeeded,
					Content = "Step A output",
				}
			},
		};
	}

	[Fact]
	public void Constructor_CreatesCheckpointsDirectory()
	{
		// Assert
		var checkpointsDir = Path.Combine(_tempDir, "checkpoints");
		Directory.Exists(checkpointsDir).Should().BeTrue();
	}

	[Fact]
	public void RootPath_ReturnsCheckpointsSubdirectory()
	{
		// Assert
		_store.RootPath.Should().Be(Path.Combine(_tempDir, "checkpoints"));
	}

	[Fact]
	public async Task SaveCheckpointAsync_CreatesCheckpointFile()
	{
		// Arrange
		var checkpoint = CreateTestCheckpoint(runId: "test-run-001");

		// Act
		await _store.SaveCheckpointAsync(checkpoint);

		// Assert
		var filePath = Path.Combine(_tempDir, "checkpoints", "test-orchestration", "test-run-001", "checkpoint.json");
		File.Exists(filePath).Should().BeTrue();
	}

	[Fact]
	public async Task SaveCheckpointAsync_OverwritesPreviousCheckpoint()
	{
		// Arrange
		var checkpoint1 = CreateTestCheckpoint(
			runId: "test-run-002",
			completedSteps: new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new() { Status = ExecutionStatus.Succeeded, Content = "First save" },
			});

		var checkpoint2 = CreateTestCheckpoint(
			runId: "test-run-002",
			completedSteps: new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new() { Status = ExecutionStatus.Succeeded, Content = "First save" },
				["step-b"] = new() { Status = ExecutionStatus.Succeeded, Content = "Second save" },
			});

		// Act
		await _store.SaveCheckpointAsync(checkpoint1);
		await _store.SaveCheckpointAsync(checkpoint2);

		// Assert — load should return the latest version
		var loaded = await _store.LoadCheckpointAsync("test-orchestration", "test-run-002");
		loaded.Should().NotBeNull();
		loaded!.CompletedSteps.Should().HaveCount(2);
		loaded.CompletedSteps.Should().ContainKey("step-b");
	}

	[Fact]
	public async Task LoadCheckpointAsync_ReturnsNullForMissingCheckpoint()
	{
		// Act
		var result = await _store.LoadCheckpointAsync("nonexistent", "nonexistent-run");

		// Assert
		result.Should().BeNull();
	}

	[Fact]
	public async Task LoadCheckpointAsync_RoundTripsAllFields()
	{
		// Arrange
		var checkpoint = CreateTestCheckpoint(
			runId: "roundtrip-test",
			orchestrationName: "my-orchestration",
			parameters: new Dictionary<string, string> { ["param1"] = "value1" },
			triggerId: "trigger-123",
			completedSteps: new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new()
				{
					Status = ExecutionStatus.Succeeded,
					Content = "Content A",
					RawContent = "Raw A",
					PromptSent = "Prompt A",
					ActualModel = "claude-opus-4.5",
					RawDependencyOutputs = new Dictionary<string, string> { ["dep1"] = "dep1-out" },
				},
			});

		// Act
		await _store.SaveCheckpointAsync(checkpoint);
		var loaded = await _store.LoadCheckpointAsync("my-orchestration", "roundtrip-test");

		// Assert
		loaded.Should().NotBeNull();
		loaded!.RunId.Should().Be("roundtrip-test");
		loaded.OrchestrationName.Should().Be("my-orchestration");
		loaded.Parameters.Should().ContainKey("param1");
		loaded.Parameters["param1"].Should().Be("value1");
		loaded.TriggerId.Should().Be("trigger-123");
		loaded.CompletedSteps.Should().HaveCount(1);

		var step = loaded.CompletedSteps["step-a"];
		step.Status.Should().Be(ExecutionStatus.Succeeded);
		step.Content.Should().Be("Content A");
		step.RawContent.Should().Be("Raw A");
		step.PromptSent.Should().Be("Prompt A");
		step.ActualModel.Should().Be("claude-opus-4.5");
		step.RawDependencyOutputs.Should().ContainKey("dep1");
	}

	[Fact]
	public async Task DeleteCheckpointAsync_RemovesCheckpointDirectory()
	{
		// Arrange
		var checkpoint = CreateTestCheckpoint(runId: "delete-test");
		await _store.SaveCheckpointAsync(checkpoint);

		// Verify it exists first
		var loaded = await _store.LoadCheckpointAsync("test-orchestration", "delete-test");
		loaded.Should().NotBeNull();

		// Act
		await _store.DeleteCheckpointAsync("test-orchestration", "delete-test");

		// Assert
		var loadedAfter = await _store.LoadCheckpointAsync("test-orchestration", "delete-test");
		loadedAfter.Should().BeNull();
	}

	[Fact]
	public async Task DeleteCheckpointAsync_NonexistentCheckpoint_DoesNotThrow()
	{
		// Act & Assert — should not throw
		await _store.DeleteCheckpointAsync("nonexistent", "nonexistent-run");
	}

	[Fact]
	public async Task ListCheckpointsAsync_ReturnsAllCheckpoints()
	{
		// Arrange
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "run-1", orchestrationName: "orch-a"));
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "run-2", orchestrationName: "orch-a"));
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "run-3", orchestrationName: "orch-b"));

		// Act
		var all = await _store.ListCheckpointsAsync();

		// Assert
		all.Should().HaveCount(3);
	}

	[Fact]
	public async Task ListCheckpointsAsync_FilterByOrchestrationName()
	{
		// Arrange
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "run-1", orchestrationName: "orch-a"));
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "run-2", orchestrationName: "orch-a"));
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "run-3", orchestrationName: "orch-b"));

		// Act
		var filtered = await _store.ListCheckpointsAsync("orch-a");

		// Assert
		filtered.Should().HaveCount(2);
		filtered.Should().AllSatisfy(c => c.OrchestrationName.Should().Be("orch-a"));
	}

	[Fact]
	public async Task ListCheckpointsAsync_EmptyStore_ReturnsEmpty()
	{
		// Act
		var result = await _store.ListCheckpointsAsync();

		// Assert
		result.Should().BeEmpty();
	}

	[Fact]
	public async Task ListCheckpointsAsync_NonexistentOrchestration_ReturnsEmpty()
	{
		// Arrange
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(orchestrationName: "orch-a"));

		// Act
		var result = await _store.ListCheckpointsAsync("nonexistent");

		// Assert
		result.Should().BeEmpty();
	}

	[Fact]
	public async Task SaveCheckpointAsync_MultipleSteps_AllPersisted()
	{
		// Arrange
		var checkpoint = CreateTestCheckpoint(
			runId: "multi-step",
			completedSteps: new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new() { Status = ExecutionStatus.Succeeded, Content = "A" },
				["step-b"] = new() { Status = ExecutionStatus.Succeeded, Content = "B" },
				["step-c"] = new() { Status = ExecutionStatus.Succeeded, Content = "C" },
			});

		// Act
		await _store.SaveCheckpointAsync(checkpoint);
		var loaded = await _store.LoadCheckpointAsync("test-orchestration", "multi-step");

		// Assert
		loaded.Should().NotBeNull();
		loaded!.CompletedSteps.Should().HaveCount(3);
		loaded.CompletedSteps["step-a"].Content.Should().Be("A");
		loaded.CompletedSteps["step-b"].Content.Should().Be("B");
		loaded.CompletedSteps["step-c"].Content.Should().Be("C");
	}

	[Fact]
	public async Task ConcurrentSavesAndReads_AlwaysPublishCompleteDocumentsAndLeaveNoTemporaryFiles()
	{
		var values = Enumerable.Range(0, 24).Select(i => $"revision-{i}:" + new string((char)('a' + i), 128 * 1024)).ToArray();
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "parallel", completedSteps: new()
		{
			["step"] = new() { Status = ExecutionStatus.Succeeded, Content = values[0] },
		}));
		var writes = values.Select(value => _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "parallel", completedSteps: new()
		{
			["step"] = new() { Status = ExecutionStatus.Succeeded, Content = value },
		}))).ToArray();
		var reads = Task.Run(async () =>
		{
			for (var i = 0; i < 24; i++)
			{
				var loaded = await _store.LoadCheckpointAsync("test-orchestration", "parallel");
				loaded.Should().NotBeNull("atomic replacement never removes the committed file");
				values.Should().Contain(loaded!.CompletedSteps["step"].Content, "readers see a complete revision");
			}
		});

		await Task.WhenAll(writes.Append(reads));
		Directory.GetFiles(_store.RootPath, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
	}

	[Fact]
	public async Task FailedReplacement_PreservesDestinationAndCleansTemporaryFile()
	{
		var directory = Path.Combine(_store.RootPath, "test-orchestration", "blocked", "checkpoint.json");
		Directory.CreateDirectory(directory);
		var sentinel = Path.Combine(directory, "keep.txt");
		await File.WriteAllTextAsync(sentinel, "original");
		var save = () => _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "blocked"));

		await save.Should().ThrowAsync<Exception>();

		(await File.ReadAllTextAsync(sentinel)).Should().Be("original");
		Directory.GetFiles(_store.RootPath, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
	}

	[Fact]
	public async Task ReadAndList_PropagateCallerCancellation()
	{
		await _store.SaveCheckpointAsync(CreateTestCheckpoint(runId: "cancel"));
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		var read = () => _store.LoadCheckpointAsync("test-orchestration", "cancel", cancellation.Token);
		var list = () => _store.ListCheckpointsAsync(cancellationToken: cancellation.Token);
		await read.Should().ThrowAsync<OperationCanceledException>();
		await list.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task ParallelRun_HostShutdownAndResume_PreserveEveryDurableResult()
	{
		const int count = 48;
		var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var work = new CheckpointWork(parked);
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new UnusedAgentBuilder(),
			NullOrchestrationReporter.Instance, NullLoggerFactory.Instance, checkpointStore: _store,
			stepExecutorRegistry: new StepExecutorRegistry().Register(work));
		var names = Enumerable.Range(0, count).Select(i => $"s{i}").ToArray();
		var definition = new Orchestration
		{
			Name = "parallel-resume", Description = "Real checkpoint storage under parallel completion", TimeoutSeconds = null,
			Steps = names.Select(name => (OrchestrationStep)new TransformOrchestrationStep
			{
				Name = name, Type = OrchestrationStepType.Transform, Template = name,
			}).Append(new TransformOrchestrationStep
			{
				Name = "park", Type = OrchestrationStepType.Transform, Template = "done", DependsOn = names,
			}).ToArray(),
		};
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var running = executor.ExecuteAsync(definition, executionIdOverride: "resume-run", cancellationToken: cancellation.Token,
			resolveExternalCancellationCause: () => CancellationDetails.HostShutdown());
		await parked.Task.WaitAsync(cancellation.Token);
		await cancellation.CancelAsync();
		(await running.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(ExecutionStatus.Cancelled);

		var checkpoint = await _store.LoadCheckpointAsync(definition.Name, "resume-run");
		checkpoint!.CompletedSteps.Keys.Should().BeEquivalentTo(names);
		checkpoint.CompletedSteps.Should().AllSatisfy(entry => entry.Value.Content.Should().Be(entry.Key));
		var resumed = await executor.ResumeAsync(definition, checkpoint).WaitAsync(TimeSpan.FromSeconds(10));

		resumed.Status.Should().Be(ExecutionStatus.Succeeded);
		work.Calls["park"].Should().Be(2);
		foreach (var name in names)
			work.Calls[name].Should().Be(1, "checkpointed steps must not execute again");
		(await _store.LoadCheckpointAsync(definition.Name, "resume-run")).Should().BeNull();
	}

	private sealed class CheckpointWork(TaskCompletionSource parked) : IStepExecutor
	{
		public OrchestrationStepType StepType => OrchestrationStepType.Transform;
		public System.Collections.Concurrent.ConcurrentDictionary<string, int> Calls { get; } = new();
		public async Task<ExecutionResult> ExecuteAsync(OrchestrationStep step, OrchestrationExecutionContext context, CancellationToken cancellationToken = default)
		{
			if (Calls.AddOrUpdate(step.Name, 1, (_, count) => count + 1) == 1 && step.Name == "park")
			{
				parked.TrySetResult();
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}
			await Task.Yield();
			return ExecutionResult.Succeeded(step.Name);
		}
	}

	private sealed class UnusedAgentBuilder : AgentBuilder
	{
		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("unused");
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
