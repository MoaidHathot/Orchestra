using FluentAssertions;

namespace Orchestra.Engine.Tests.Storage;

public class NullRunStoreTests
{
	private readonly NullRunStore _store = NullRunStore.Instance;

	#region Helper Methods

	private static OrchestrationRunRecord CreateTestRecord(string runId = "run-1") => new()
	{
		RunId = runId,
		OrchestrationName = "test",
		StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
		CompletedAt = DateTimeOffset.UtcNow,
		Status = ExecutionStatus.Succeeded,
		StepRecords = new Dictionary<string, StepRunRecord>(),
		AllStepRecords = new Dictionary<string, StepRunRecord>(),
		FinalContent = "Test output"
	};

	#endregion

	#region Singleton Instance

	[Fact]
	public void Instance_ReturnsSameInstance()
	{
		// Act
		var instance1 = NullRunStore.Instance;
		var instance2 = NullRunStore.Instance;

		// Assert
		instance1.Should().BeSameAs(instance2);
	}

	#endregion

	#region SaveRunAsync

	[Fact]
	public async Task SaveRunAsync_CompletesSuccessfully()
	{
		// Arrange
		var record = CreateTestRecord();

		// Act
		var act = () => _store.SaveRunAsync(record);

		// Assert - No exception thrown
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task SaveRunAsync_ReturnsCompletedTask()
	{
		// Arrange
		var record = CreateTestRecord();

		// Act
		var task = _store.SaveRunAsync(record);

		// Assert
		task.IsCompleted.Should().BeTrue();
		await task;
	}

	#endregion

	#region ListRunsAsync

	[Fact]
	public async Task ListRunsAsync_ReturnsEmptyList()
	{
		// Act
		var result = await _store.ListRunsAsync("any-orchestration");

		// Assert
		result.Should().BeEmpty();
	}

	[Fact]
	public async Task ListRunsAsync_WithLimit_ReturnsEmptyList()
	{
		// Act
		var result = await _store.ListRunsAsync("any-orchestration", limit: 10);

		// Assert
		result.Should().BeEmpty();
	}

	#endregion

	#region ListAllRunsAsync

	[Fact]
	public async Task ListAllRunsAsync_ReturnsEmptyList()
	{
		// Act
		var result = await _store.ListAllRunsAsync();

		// Assert
		result.Should().BeEmpty();
	}

	[Fact]
	public async Task ListAllRunsAsync_WithLimit_ReturnsEmptyList()
	{
		// Act
		var result = await _store.ListAllRunsAsync(limit: 100);

		// Assert
		result.Should().BeEmpty();
	}

	#endregion

	#region ListRunsByTriggerAsync

	[Fact]
	public async Task ListRunsByTriggerAsync_ReturnsEmptyList()
	{
		// Act
		var result = await _store.ListRunsByTriggerAsync("any-trigger");

		// Assert
		result.Should().BeEmpty();
	}

	[Fact]
	public async Task ListRunsByTriggerAsync_WithLimit_ReturnsEmptyList()
	{
		// Act
		var result = await _store.ListRunsByTriggerAsync("any-trigger", limit: 5);

		// Assert
		result.Should().BeEmpty();
	}

	#endregion

	#region GetRunAsync

	[Fact]
	public async Task GetRunAsync_ReturnsNull()
	{
		// Act
		var result = await _store.GetRunAsync("any-orchestration", "any-run");

		// Assert
		result.Should().BeNull();
	}

	#endregion

	#region DeleteRunAsync

	[Fact]
	public async Task DeleteRunAsync_ReturnsFalse()
	{
		// Act
		var result = await _store.DeleteRunAsync("any-orchestration", "any-run");

		// Assert - Always returns false (nothing to delete)
		result.Should().BeFalse();
	}

	#endregion

	#region IRunStore Interface

	[Fact]
	public void NullRunStore_ImplementsIRunStore()
	{
		// Assert
		_store.Should().BeAssignableTo<IRunStore>();
	}

	#endregion
}
