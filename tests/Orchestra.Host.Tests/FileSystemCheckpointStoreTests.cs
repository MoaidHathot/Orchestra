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
}
