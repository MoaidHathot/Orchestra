using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.McpServer;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for the HITL data-plane MCP tools (<c>list_pending_inputs</c>,
/// <c>respond_to_input</c>) added to <see cref="DataPlaneTools"/>.
/// </summary>
public class DataPlaneHitlToolsTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemPendingInputStore _pendingStore;
	private readonly InMemoryHumanInputWaiter _waiter;

	public DataPlaneHitlToolsTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-hitl-mcp-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_pendingStore = new FileSystemPendingInputStore(_tempDir, NullLogger<FileSystemPendingInputStore>.Instance);
		_waiter = new InMemoryHumanInputWaiter(NullLogger<InMemoryHumanInputWaiter>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
		}
	}

	[Fact]
	public async Task ListPendingInputs_ReturnsEmptyWhenNoneExist()
	{
		var result = await DataPlaneTools.ListPendingInputs(_pendingStore);
		var doc = JsonDocument.Parse(result);

		doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
		doc.RootElement.GetProperty("pending").GetArrayLength().Should().Be(0);
	}

	[Fact]
	public async Task ListPendingInputs_ReturnsRecordsAfterSeeding()
	{
		await _pendingStore.SaveAsync(new PendingInputRecord
		{
			OrchestrationName = "orch-a",
			RunId = "r1",
			StepName = "review",
			Kind = PendingInputKind.Approval,
			Prompt = "Approve?",
			Choices = ["approve", "reject"],
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var result = await DataPlaneTools.ListPendingInputs(_pendingStore);
		var doc = JsonDocument.Parse(result);

		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		var first = doc.RootElement.GetProperty("pending")[0];
		first.GetProperty("orchestrationName").GetString().Should().Be("orch-a");
		first.GetProperty("runId").GetString().Should().Be("r1");
		first.GetProperty("stepName").GetString().Should().Be("review");
		first.GetProperty("kind").GetString().Should().Be("Approval");
	}

	[Fact]
	public async Task ListPendingInputs_FiltersByOrchestrationName()
	{
		await _pendingStore.SaveAsync(BuildRecord("orch-a", "r1", "s1"));
		await _pendingStore.SaveAsync(BuildRecord("orch-b", "r1", "s1"));

		var result = await DataPlaneTools.ListPendingInputs(_pendingStore, orchestrationName: "orch-a");
		var doc = JsonDocument.Parse(result);

		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
	}

	[Fact]
	public void RespondToInput_RequiresChoiceOrReply()
	{
		var result = DataPlaneTools.RespondToInput(_pendingStore, _waiter, "orch", "run", "step");
		var doc = JsonDocument.Parse(result);

		doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
		err.GetString().Should().Contain("'choice' or 'reply'");
	}

	[Fact]
	public async Task RespondToInput_NoPendingRecord_ReturnsError()
	{
		var result = DataPlaneTools.RespondToInput(_pendingStore, _waiter, "orch", "run", "step", reply: "ok");
		var doc = JsonDocument.Parse(result);

		doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
		err.GetString().Should().Contain("No pending input record");

		await Task.CompletedTask;
	}

	[Fact]
	public async Task RespondToInput_NoActiveWait_ReturnsError()
	{
		await _pendingStore.SaveAsync(BuildRecord("orch", "run", "step"));

		var result = DataPlaneTools.RespondToInput(_pendingStore, _waiter, "orch", "run", "step", reply: "ok");
		var doc = JsonDocument.Parse(result);

		doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
		err.GetString().Should().Contain("No active wait found");
	}

	[Fact]
	public async Task RespondToInput_ChoiceNotInDeclaredList_ReturnsError()
	{
		await _pendingStore.SaveAsync(BuildRecord("orch", "run", "step", choices: ["approve", "reject"]));
		var waitTask = _waiter.WaitAsync("orch", "run", "step", CancellationToken.None);

		var result = DataPlaneTools.RespondToInput(_pendingStore, _waiter, "orch", "run", "step", choice: "explode");
		var doc = JsonDocument.Parse(result);

		doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
		err.GetString().Should().Contain("not one of the allowed values");
		// Wait should still be active.
		waitTask.IsCompleted.Should().BeFalse();
	}

	[Fact]
	public async Task RespondToInput_ValidChoice_CompletesWaiter()
	{
		await _pendingStore.SaveAsync(BuildRecord("orch", "run", "step", choices: ["approve", "reject"]));
		var waitTask = _waiter.WaitAsync("orch", "run", "step", CancellationToken.None);

		var result = DataPlaneTools.RespondToInput(_pendingStore, _waiter, "orch", "run", "step", choice: "approve", respondedBy: "alice");
		var doc = JsonDocument.Parse(result);

		doc.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
		var resolved = await waitTask;
		resolved.Choice.Should().Be("approve");
		resolved.RespondedBy.Should().Be("alice");
	}

	[Fact]
	public async Task RespondToInput_ReplyOnly_AcceptedForFreeFormPending()
	{
		await _pendingStore.SaveAsync(BuildRecord("orch", "run", "step")); // no choices
		var waitTask = _waiter.WaitAsync("orch", "run", "step", CancellationToken.None);

		var result = DataPlaneTools.RespondToInput(_pendingStore, _waiter, "orch", "run", "step", reply: "ship it");
		var doc = JsonDocument.Parse(result);

		doc.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
		var resolved = await waitTask;
		resolved.Reply.Should().Be("ship it");
	}

	private static PendingInputRecord BuildRecord(string orch, string runId, string step, string[]? choices = null)
		=> new()
		{
			OrchestrationName = orch,
			RunId = runId,
			StepName = step,
			Kind = PendingInputKind.Approval,
			Prompt = "?",
			Choices = choices ?? [],
			CreatedAt = DateTimeOffset.UtcNow,
		};
}
