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

	/// <summary>
	/// Regression for the file-locking race that flaked
	/// <c>RetryApiHitlWiringTests.RetryApprovalOrchestration_PersistsPendingRecord_AndCompletesOnRespond</c>.
	/// <para>
	/// On Windows, <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> opens the file
	/// with <see cref="FileShare.Read"/>, which denies a concurrent <see cref="File.Delete(string)"/>.
	/// When ApprovalStepExecutor's cleanup ran while the test was polling
	/// <see cref="FileSystemPendingInputStore.ListAsync(string?, CancellationToken)"/>, the delete
	/// silently failed and the test saw the record still present after 15s.
	/// </para>
	/// <para>
	/// This test re-creates the race by hammering <c>ListAsync</c> from one task while another
	/// task saves+deletes records. Without the fix, the delete loses the race intermittently and
	/// leaves the file behind. With the fix (share-aware readers + retry loop), <see cref="GetAsync"/>
	/// must return <c>null</c> after every <c>DeleteAsync</c> completes.
	/// </para>
	/// </summary>
	[Fact]
	public async Task DeleteAsync_SucceedsUnderConcurrentListReads()
	{
		const int iterations = 30;
		const string orchestrationName = "race-orch";

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		// Pre-seed a few "noise" records so ListAsync always has work to do and is
		// likely to be mid-read when we delete the target.
		for (var i = 0; i < 3; i++)
		{
			await _store.SaveAsync(BuildRecord(orchestrationName, $"noise-{i}", "review"));
		}

		var hammerTask = Task.Run(async () =>
		{
			while (!cts.IsCancellationRequested)
			{
				try
				{
					_ = await _store.ListAsync(orchestrationName, cts.Token);
				}
				catch (OperationCanceledException) { return; }
			}
		});

		try
		{
			for (var i = 0; i < iterations; i++)
			{
				var runId = $"race-run-{i}";
				var record = BuildRecord(orchestrationName, runId, "review");

				await _store.SaveAsync(record);
				await _store.DeleteAsync(record.OrchestrationName, record.RunId, record.StepName);

				var loaded = await _store.GetAsync(record.OrchestrationName, record.RunId, record.StepName);
				loaded.Should().BeNull(
					$"iteration {i}: DeleteAsync must remove the record even when ListAsync " +
					"is concurrently reading sibling files in the same directory");
			}
		}
		finally
		{
			cts.Cancel();
			try { await hammerTask; } catch (OperationCanceledException) { }
		}
	}

	/// <summary>
	/// Validates the retry loop in <c>TryDeleteWithRetryAsync</c>: even when an external
	/// reader holds the file open with the exclusive-delete-denying
	/// <see cref="FileShare.Read"/> share mode, <see cref="DeleteAsync"/> eventually succeeds
	/// once the handle is released.
	/// </summary>
	[Fact]
	public async Task DeleteAsync_RetriesWhenFileTransientlyLocked()
	{
		var record = BuildRecord();
		await _store.SaveAsync(record);

		var filePath = Path.Combine(
			_store.RootPath,
			record.OrchestrationName,
			record.RunId,
			record.StepName + ".json");
		File.Exists(filePath).Should().BeTrue("the save should have produced a file on disk");

		// Hold an external delete-denying read handle for ~150ms, well below the store's
		// ~620ms retry budget, then release it. The retry loop must observe the handle
		// being released and successfully delete the file.
		using var lockHandle = new FileStream(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read); // intentionally excludes FileShare.Delete

		var releaseAfter = Task.Run(async () =>
		{
			await Task.Delay(150);
			lockHandle.Dispose();
		});

		await _store.DeleteAsync(record.OrchestrationName, record.RunId, record.StepName);
		await releaseAfter;

		File.Exists(filePath).Should().BeFalse(
			"DeleteAsync's retry loop should have succeeded once the external read handle was released");
	}
}
