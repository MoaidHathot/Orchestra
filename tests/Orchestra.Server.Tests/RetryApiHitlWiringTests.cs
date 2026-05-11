using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Regression coverage for the retry endpoint's HITL wiring.
///
/// The retry endpoint (<c>GET /api/history/{name}/{runId}/retry</c>) used to construct its
/// <see cref="OrchestrationExecutor"/> without passing <see cref="IPendingInputStore"/> or
/// <see cref="IHumanInputWaiter"/>, so the executor fell back to
/// <see cref="NullPendingInputStore"/> (silently dropped saves) and
/// <see cref="NullHumanInputWaiter"/> (blocked forever, never resolved on
/// <c>POST /respond</c>). A retried run that hit an <c>Approval</c> step was therefore
/// unrescuable — the Portal "Waiting for Input" list never showed the new runId, and any
/// response sent to the new runId returned 404.
///
/// These tests exercise the live retry endpoint with an Approval orchestration and assert:
///   1. The new run's <see cref="PendingInputRecord"/> is persisted under the NEW runId
///      (proves the real <see cref="IPendingInputStore"/> is wired).
///   2. <c>POST /respond</c> against that new runId returns 200 and the run completes
///      (proves the real <see cref="IHumanInputWaiter"/> is wired).
/// </summary>
public class RetryApiHitlWiringTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly ServerWebApplicationFactory _factory;
	private readonly HttpClient _client;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	public RetryApiHitlWiringTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
		// AllowAutoRedirect doesn't matter; we just need a working HttpClient.
		_client = factory.CreateClient();
	}

	public void Dispose() => _client.Dispose();

	[Fact]
	public async Task RetryApprovalOrchestration_PersistsPendingRecord_AndCompletesOnRespond()
	{
		// ── Arrange ────────────────────────────────────────────────────────────
		// Register an Approval-only orchestration so the retry executor reaches a HITL pause
		// in seconds (no agent invocation, no command process startup).
		var unique = Guid.NewGuid().ToString("N")[..8];
		var orchestrationName = $"retry-hitl-{unique}";
		var orchestrationJson = $$"""
			{
				"name": "{{orchestrationName}}",
				"description": "Approval-only orchestration for retry HITL wiring regression test.",
				"version": "1.0.0",
				"steps": [
					{
						"name": "review",
						"type": "Approval",
						"dependsOn": [],
						"prompt": "Approve?",
						"choices": ["approve", "reject"]
					}
				]
			}
			""";

		var register = await _client.PostAsJsonAsync(
			"/api/orchestrations/json",
			new { Json = orchestrationJson, McpJson = (string?)null },
			_jsonOptions);
		register.StatusCode.Should().Be(HttpStatusCode.OK);

		// Seed a "failed" source run so the retry endpoint has something to retry. mode=all
		// re-runs from scratch with the original parameters, which is what we want — it
		// will go straight into the Approval step on the NEW runId.
		var runStore = _factory.Services.GetRequiredService<FileSystemRunStore>();
		var sourceRunId = $"src-{unique}";
		var sourceRecord = new OrchestrationRunRecord
		{
			RunId = sourceRunId,
			OrchestrationName = orchestrationName,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
			Status = ExecutionStatus.Failed,
			IsIncomplete = false,
			FinalContent = string.Empty,
			SavedFiles = [],
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>(),
			Parameters = new Dictionary<string, string>(),
		};
		await runStore.SaveRunAsync(sourceRecord, default(CancellationToken));

		// ── Act 1: kick off retry ─────────────────────────────────────────────
		// Open the SSE retry endpoint and dispose without reading the body — the server
		// runs the orchestration in a background task that is not bound to the connection
		// lifetime, so the Approval step will pause regardless of whether we keep reading.
		using (var retryReq = new HttpRequestMessage(HttpMethod.Get,
			$"/api/history/{Uri.EscapeDataString(orchestrationName)}/{Uri.EscapeDataString(sourceRunId)}/retry?mode=all"))
		using (var retryResp = await _client.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead))
		{
			retryResp.StatusCode.Should().Be(HttpStatusCode.OK);
		}

		// ── Assert 1: a pending record lands for the NEW runId ────────────────
		// Poll up to 30s — the Approval step normally pauses within ~1s.
		var pendingStore = _factory.Services.GetRequiredService<IPendingInputStore>();
		PendingInputRecord? pending = null;
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (DateTime.UtcNow < deadline && pending is null)
		{
			await Task.Delay(200);
			var list = await pendingStore.ListAsync(orchestrationName);
			pending = list.FirstOrDefault(r => r.RunId != sourceRunId && r.StepName == "review");
		}

		pending.Should().NotBeNull(
			"the retried run should persist its own PendingInputRecord — when the executor "
			+ "is wired with NullPendingInputStore this never happens and the Portal cannot "
			+ "see or respond to the retried run's HITL wait.");
		var newRunId = pending!.RunId;
		newRunId.Should().NotBe(sourceRunId, "the retry must use a fresh runId");

		// ── Act 2: respond to the NEW runId via the HTTP API ──────────────────
		var respond = await _client.PostAsJsonAsync(
			$"/api/orchestrations/{Uri.EscapeDataString(orchestrationName)}/runs/{Uri.EscapeDataString(newRunId)}/respond?step=review",
			new { choice = "approve", respondedBy = "regression-test" },
			_jsonOptions);

		// ── Assert 2: respond succeeds — proves the real in-memory waiter is wired
		respond.StatusCode.Should().Be(HttpStatusCode.OK,
			"POST /respond must complete the wait — when the executor is wired with "
			+ "NullHumanInputWaiter the call returns 404 because TryComplete always returns false.");

		var body = await respond.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		body.GetProperty("accepted").GetBoolean().Should().BeTrue();
		body.GetProperty("runId").GetString().Should().Be(newRunId);

		// ── Assert 3: pending record is removed once the wait resolves ────────
		// Give the executor a moment to clean up its persisted record.
		var cleanupDeadline = DateTime.UtcNow.AddSeconds(15);
		while (DateTime.UtcNow < cleanupDeadline)
		{
			var afterList = await pendingStore.ListAsync(orchestrationName);
			if (afterList.All(r => r.RunId != newRunId))
				return; // record was deleted as expected
			await Task.Delay(200);
		}

		var stillThere = await pendingStore.ListAsync(orchestrationName);
		stillThere.Should().NotContain(r => r.RunId == newRunId,
			"ApprovalStepExecutor should DeleteAsync the record once the wait is satisfied");
	}
}
