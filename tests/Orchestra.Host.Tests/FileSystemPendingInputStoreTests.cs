using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for <see cref="FileSystemPendingInputStore"/>.
/// </summary>
public class FileSystemPendingInputStoreTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemPendingInputStore _store;

	public FileSystemPendingInputStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-pending-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemPendingInputStore(_tempDir, NullLogger<FileSystemPendingInputStore>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort */ }
		}
	}

	private static PendingInputRecord BuildRecord(
		string orchestrationName = "test-orch",
		string runId = "run-1",
		string stepName = "review",
		PendingInputKind kind = PendingInputKind.Approval,
		string prompt = "Approve?",
		string[]? choices = null)
		=> new()
		{
			OrchestrationName = orchestrationName,
			RunId = runId,
			StepName = stepName,
			Kind = kind,
			Prompt = prompt,
			Choices = choices ?? [],
			CreatedAt = DateTimeOffset.UtcNow,
		};

	[Fact]
	public void RootPath_ReturnsPendingSubdirectory()
	{
		_store.RootPath.Should().Be(Path.Combine(_tempDir, "pending"));
	}

	[Fact]
	public async Task SaveAsync_PersistsRecord()
	{
		var record = BuildRecord();

		await _store.SaveAsync(record);

		var loaded = await _store.GetAsync(record.OrchestrationName, record.RunId, record.StepName);
		loaded.Should().NotBeNull();
		loaded!.Prompt.Should().Be("Approve?");
		loaded.Kind.Should().Be(PendingInputKind.Approval);
	}

	[Fact]
	public async Task GetAsync_ReturnsNull_WhenRecordMissing()
	{
		var loaded = await _store.GetAsync("none", "none", "none");

		loaded.Should().BeNull();
	}

	[Fact]
	public async Task SaveAsync_OverwritesExistingRecord()
	{
		var first = BuildRecord(prompt: "First");
		var second = BuildRecord(prompt: "Second");

		await _store.SaveAsync(first);
		await _store.SaveAsync(second);

		var loaded = await _store.GetAsync(second.OrchestrationName, second.RunId, second.StepName);
		loaded!.Prompt.Should().Be("Second");
	}

	[Fact]
	public async Task ListAsync_ReturnsAllRecords_WhenNoFilter()
	{
		await _store.SaveAsync(BuildRecord(orchestrationName: "a", runId: "r1", stepName: "s1"));
		await _store.SaveAsync(BuildRecord(orchestrationName: "a", runId: "r1", stepName: "s2"));
		await _store.SaveAsync(BuildRecord(orchestrationName: "b", runId: "r1", stepName: "s1"));

		var all = await _store.ListAsync();

		all.Should().HaveCount(3);
	}

	[Fact]
	public async Task ListAsync_FiltersByOrchestrationName()
	{
		await _store.SaveAsync(BuildRecord(orchestrationName: "a", runId: "r1", stepName: "s1"));
		await _store.SaveAsync(BuildRecord(orchestrationName: "b", runId: "r1", stepName: "s1"));

		var filtered = await _store.ListAsync("a");

		filtered.Should().ContainSingle()
			.Which.OrchestrationName.Should().Be("a");
	}

	[Fact]
	public async Task DeleteAsync_RemovesRecord()
	{
		var record = BuildRecord();
		await _store.SaveAsync(record);

		await _store.DeleteAsync(record.OrchestrationName, record.RunId, record.StepName);

		var loaded = await _store.GetAsync(record.OrchestrationName, record.RunId, record.StepName);
		loaded.Should().BeNull();
	}

	[Fact]
	public async Task DeleteAsync_Idempotent_DoesNotThrowForMissing()
	{
		var act = async () => await _store.DeleteAsync("none", "none", "none");

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task SaveAsync_PreservesChoicesArray()
	{
		var record = BuildRecord(choices: ["approve", "reject", "review"]);

		await _store.SaveAsync(record);

		var loaded = await _store.GetAsync(record.OrchestrationName, record.RunId, record.StepName);
		loaded!.Choices.Should().Equal("approve", "reject", "review");
	}

	[Fact]
	public async Task SaveAsync_PreservesEngineToolKind()
	{
		var record = BuildRecord(kind: PendingInputKind.EngineTool);

		await _store.SaveAsync(record);

		var loaded = await _store.GetAsync(record.OrchestrationName, record.RunId, record.StepName);
		loaded!.Kind.Should().Be(PendingInputKind.EngineTool);
	}

	[Fact]
	public async Task SaveAsync_SanitizesNamesWithInvalidChars()
	{
		var record = new PendingInputRecord
		{
			OrchestrationName = "test/with:invalid|chars",
			RunId = "run-1",
			StepName = "step-1",
			Kind = PendingInputKind.Approval,
			Prompt = "?",
			CreatedAt = DateTimeOffset.UtcNow,
		};

		var act = async () => await _store.SaveAsync(record);

		await act.Should().NotThrowAsync();
	}
}
