using FluentAssertions;
using Orchestra.Host.Persistence;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for FileSystemRunStore.
/// </summary>
public class FileSystemRunStoreTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;

	public FileSystemRunStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-runstore-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemRunStore(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private OrchestrationRunRecord CreateTestRecord(
		string? runId = null,
		string? orchestrationName = null,
		string? triggerId = null,
		ExecutionStatus status = ExecutionStatus.Succeeded,
		DateTimeOffset? startedAt = null)
	{
		var now = DateTimeOffset.UtcNow;
		var started = startedAt ?? now.AddMinutes(-5);
		return new OrchestrationRunRecord
		{
			RunId = runId ?? Guid.NewGuid().ToString("N")[..12],
			OrchestrationName = orchestrationName ?? "test-orchestration",
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			TriggerId = triggerId,
			StartedAt = started,
			CompletedAt = now,
			Status = status,
			FinalContent = "Test result content",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>()
		};
	}

	[Fact]
	public async Task SaveRunAsync_PersistsHookExecutions()
	{
		var record = CreateTestRecord(runId: "hook-record");
		record = new OrchestrationRunRecord
		{
			RunId = record.RunId,
			OrchestrationName = record.OrchestrationName,
			OrchestrationVersion = record.OrchestrationVersion,
			TriggeredBy = record.TriggeredBy,
			TriggerId = record.TriggerId,
			StartedAt = record.StartedAt,
			CompletedAt = record.CompletedAt,
			Status = record.Status,
			FinalContent = record.FinalContent,
			StepRecords = record.StepRecords,
			AllStepRecords = record.AllStepRecords,
			HookExecutions =
			[
				new HookExecutionRecord
				{
					HookName = "notify",
					EventType = HookEventType.OrchestrationAfter,
					Source = HookSource.Global,
					Status = ExecutionStatus.Succeeded,
					StartedAt = record.StartedAt,
					CompletedAt = record.CompletedAt,
					FailurePolicy = HookFailurePolicy.Warn,
					ActionType = HookActionType.Script,
				}
			]
		};

		await _store.SaveRunAsync(record, cancellationToken: default);

		var loaded = await _store.GetRunAsync(record.OrchestrationName, record.RunId, default);
		loaded.Should().NotBeNull();
		loaded!.HookExecutions.Should().ContainSingle();
		loaded.HookExecutions[0].HookName.Should().Be("notify");
		loaded.HookExecutions[0].Source.Should().Be(HookSource.Global);
	}

	[Fact]
	public void Constructor_CreatesExecutionsDirectory()
	{
		// Assert
		var executionsDir = Path.Combine(_tempDir, "executions");
		Directory.Exists(executionsDir).Should().BeTrue();
	}

	[Fact]
	public void RootPath_ReturnsExecutionsSubdirectory()
	{
		// Assert
		_store.RootPath.Should().Be(Path.Combine(_tempDir, "executions"));
	}

	[Fact]
	public async Task SaveRunAsync_CreatesRunDirectory()
	{
		// Arrange
		var record = CreateTestRecord();

		// Act
		await _store.SaveRunAsync(record, cancellationToken: default);

		// Assert
		var orchestrationDirs = Directory.GetDirectories(_store.RootPath);
		orchestrationDirs.Should().HaveCount(1);

		var runDirs = Directory.GetDirectories(orchestrationDirs[0]);
		runDirs.Should().HaveCount(1);
	}

	[Fact]
	public async Task SaveRunAsync_CreatesRunJsonFile()
	{
		// Arrange
		var record = CreateTestRecord(runId: "abc123xyz789");

		// Act
		await _store.SaveRunAsync(record, cancellationToken: default);

		// Assert
		var orchestrationDir = Directory.GetDirectories(_store.RootPath).First();
		var runDir = Directory.GetDirectories(orchestrationDir).First();
		var runJsonPath = Path.Combine(runDir, "run.json");

		File.Exists(runJsonPath).Should().BeTrue();
		var content = await File.ReadAllTextAsync(runJsonPath);
		content.Should().Contain("abc123xyz789");
		content.Should().Contain("test-orchestration");
	}

	[Fact]
	public async Task SaveRunAsync_CreatesResultMdFile()
	{
		// Arrange
		var record = CreateTestRecord();

		// Act
		await _store.SaveRunAsync(record, cancellationToken: default);

		// Assert
		var orchestrationDir = Directory.GetDirectories(_store.RootPath).First();
		var runDir = Directory.GetDirectories(orchestrationDir).First();
		var resultPath = Path.Combine(runDir, "result.md");

		File.Exists(resultPath).Should().BeTrue();
		var content = await File.ReadAllTextAsync(resultPath);
		content.Should().Contain("Test result content");
	}

	[Fact]
	public async Task ListAllRunsAsync_ReturnsAllSavedRuns()
	{
		// Arrange
		var record1 = CreateTestRecord(orchestrationName: "orch-1");
		var record2 = CreateTestRecord(orchestrationName: "orch-2");
		var record3 = CreateTestRecord(orchestrationName: "orch-1");

		await _store.SaveRunAsync(record1, cancellationToken: default);
		await _store.SaveRunAsync(record2, cancellationToken: default);
		await _store.SaveRunAsync(record3, cancellationToken: default);

		// Act
		var runs = await _store.ListAllRunsAsync();

		// Assert
		runs.Should().HaveCount(3);
	}

	[Fact]
	public async Task ListAllRunsAsync_WithLimit_ReturnsLimitedRuns()
	{
		// Arrange
		for (int i = 0; i < 5; i++)
		{
			await _store.SaveRunAsync(CreateTestRecord(), cancellationToken: default);
		}

		// Act
		var runs = await _store.ListAllRunsAsync(limit: 3);

		// Assert
		runs.Should().HaveCount(3);
	}

	[Fact]
	public async Task ListRunsAsync_FiltersByOrchestrationName()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "target-orch"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "other-orch"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "target-orch"), cancellationToken: default);

		// Act
		var runs = await _store.ListRunsAsync("target-orch");

		// Assert
		runs.Should().HaveCount(2);
		runs.Should().AllSatisfy(r => r.OrchestrationName.Should().Be("target-orch"));
	}

	[Fact]
	public async Task ListRunsAsync_NonExistentOrchestration_ReturnsEmpty()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "existing"), cancellationToken: default);

		// Act
		var runs = await _store.ListRunsAsync("non-existent");

		// Assert
		runs.Should().BeEmpty();
	}

	[Fact]
	public async Task ListRunsByTriggerAsync_FiltersByTriggerId()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(triggerId: "trigger-a"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(triggerId: "trigger-b"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(triggerId: "trigger-a"), cancellationToken: default);

		// Act
		var runs = await _store.ListRunsByTriggerAsync("trigger-a");

		// Assert
		runs.Should().HaveCount(2);
		runs.Should().AllSatisfy(r => r.TriggerId.Should().Be("trigger-a"));
	}

	[Fact]
	public async Task GetRunAsync_ExistingRun_ReturnsRecord()
	{
		// Arrange
		var record = CreateTestRecord(runId: "specific-id", orchestrationName: "my-orch");
		await _store.SaveRunAsync(record, cancellationToken: default);

		// Act
		var retrieved = await _store.GetRunAsync("my-orch", "specific-id");

		// Assert
		retrieved.Should().NotBeNull();
		retrieved!.RunId.Should().Be("specific-id");
		retrieved.OrchestrationName.Should().Be("my-orch");
	}

	[Fact]
	public async Task GetRunAsync_NonExistentRun_ReturnsNull()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "my-orch"), cancellationToken: default);

		// Act
		var retrieved = await _store.GetRunAsync("my-orch", "non-existent-id");

		// Assert
		retrieved.Should().BeNull();
	}

	[Fact]
	public async Task GetRunAsync_WrongOrchestration_ReturnsNull()
	{
		// Arrange
		var record = CreateTestRecord(runId: "my-run", orchestrationName: "orch-a");
		await _store.SaveRunAsync(record, cancellationToken: default);

		// Act
		var retrieved = await _store.GetRunAsync("orch-b", "my-run");

		// Assert
		retrieved.Should().BeNull();
	}

	[Fact]
	public async Task DeleteRunAsync_ExistingRun_DeletesAndReturnsTrue()
	{
		// Arrange
		var record = CreateTestRecord(runId: "delete-me", orchestrationName: "my-orch");
		await _store.SaveRunAsync(record, cancellationToken: default);

		// Verify it exists first
		var before = await _store.GetRunAsync("my-orch", "delete-me");
		before.Should().NotBeNull();

		// Act
		var result = await _store.DeleteRunAsync("my-orch", "delete-me");

		// Assert
		result.Should().BeTrue();
		var after = await _store.GetRunAsync("my-orch", "delete-me");
		after.Should().BeNull();
	}

	[Fact]
	public async Task DeleteRunAsync_NonExistentRun_ReturnsFalse()
	{
		// Act
		var result = await _store.DeleteRunAsync("any-orch", "non-existent");

		// Assert
		result.Should().BeFalse();
	}

	[Fact]
	public async Task GetRunSummariesAsync_ReturnsLightweightIndices()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "orch-1"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "orch-2"), cancellationToken: default);

		// Act
		var summaries = await _store.GetRunSummariesAsync();

		// Assert
		summaries.Should().HaveCount(2);
		summaries.Should().AllSatisfy(s =>
		{
			s.RunId.Should().NotBeNullOrWhiteSpace();
			s.OrchestrationName.Should().NotBeNullOrWhiteSpace();
			s.FolderPath.Should().NotBeNullOrWhiteSpace();
		});
	}

	[Fact]
	public async Task GetRunSummariesAsync_WithOrchestrationName_FiltersBySummary()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "target"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "other"), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(orchestrationName: "target"), cancellationToken: default);

		// Act
		var summaries = await _store.GetRunSummariesAsync("target");

		// Assert
		summaries.Should().HaveCount(2);
		summaries.Should().AllSatisfy(s => s.OrchestrationName.Should().Be("target"));
	}

	[Fact]
	public async Task ListAllRunsAsync_OrdersByStartedAtDescending()
	{
		// Arrange - create records with different start times
		var now = DateTimeOffset.UtcNow;
		var oldRecord = CreateTestRecord(startedAt: now.AddHours(-2));
		var newRecord = CreateTestRecord(startedAt: now);
		var middleRecord = CreateTestRecord(startedAt: now.AddHours(-1));

		await _store.SaveRunAsync(oldRecord, cancellationToken: default);
		await _store.SaveRunAsync(middleRecord, cancellationToken: default);
		await _store.SaveRunAsync(newRecord, cancellationToken: default);

		// Act
		var runs = await _store.ListAllRunsAsync();

		// Assert
		runs.Should().HaveCount(3);
		runs[0].StartedAt.Should().BeOnOrAfter(runs[1].StartedAt);
		runs[1].StartedAt.Should().BeOnOrAfter(runs[2].StartedAt);
	}

	[Fact]
	public async Task SaveRunAsync_WithStepRecords_CreatesStepFiles()
	{
		// Arrange
		var stepRecord = new StepRunRecord
		{
			StepName = "my-step",
			Status = ExecutionStatus.Succeeded,
			Content = "Step output",
			RawContent = "Raw step output",
			PromptSent = "The prompt",
			StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
			CompletedAt = DateTimeOffset.UtcNow
		};

		var allStepRecords = new Dictionary<string, StepRunRecord>
		{
			["my-step"] = stepRecord
		};

		var record = new OrchestrationRunRecord
		{
			RunId = Guid.NewGuid().ToString("N")[..12],
			OrchestrationName = "test-orchestration",
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			CompletedAt = DateTimeOffset.UtcNow,
			Status = ExecutionStatus.Succeeded,
			FinalContent = "Test result",
			StepRecords = allStepRecords,
			AllStepRecords = allStepRecords
		};

		// Act
		await _store.SaveRunAsync(record, cancellationToken: default);

		// Assert
		var orchestrationDir = Directory.GetDirectories(_store.RootPath).First();
		var runDir = Directory.GetDirectories(orchestrationDir).First();

		File.Exists(Path.Combine(runDir, "my-step-inputs.json")).Should().BeTrue();
		File.Exists(Path.Combine(runDir, "my-step-outputs.json")).Should().BeTrue();
		File.Exists(Path.Combine(runDir, "my-step-result.json")).Should().BeTrue();
	}
}
