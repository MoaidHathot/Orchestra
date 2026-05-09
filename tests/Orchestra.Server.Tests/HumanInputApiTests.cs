using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Integration tests for the HITL HumanInput API (POST /api/orchestrations/{name}/runs/{runId}/respond,
/// GET /api/runs/pending, GET /api/orchestrations/{name}/runs/{runId}/pending/{step}).
/// </summary>
public class HumanInputApiTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly ServerWebApplicationFactory _factory;
	private readonly HttpClient _client;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	public HumanInputApiTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose() => _client.Dispose();

	[Fact]
	public async Task GetPending_Empty_ReturnsEmptyArray()
	{
		var response = await _client.GetAsync("/api/runs/pending");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var pending = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		pending.ValueKind.Should().Be(JsonValueKind.Array);
	}

	[Fact]
	public async Task GetPendingForStep_Missing_Returns404()
	{
		var response = await _client.GetAsync("/api/orchestrations/none/runs/none/pending/none");

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Respond_NoPendingRecord_Returns404()
	{
		var response = await _client.PostAsJsonAsync(
			"/api/orchestrations/none/runs/none/respond?step=none",
			new { reply = "ok" },
			_jsonOptions);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Respond_MissingStepQueryParam_Returns400()
	{
		var response = await _client.PostAsJsonAsync(
			"/api/orchestrations/orch/runs/run/respond",
			new { reply = "ok" },
			_jsonOptions);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Respond_MissingChoiceAndReply_Returns400()
	{
		// First seed a pending record so we get past the existence check.
		// We can't easily reach into the host's IPendingInputStore here, so we test the
		// validation-only path: missing step query parameter triggers BadRequest before
		// the body is checked. Use the no-body case for the missing-fields path:
		var pendingStore = (Orchestra.Engine.IPendingInputStore)_factory.Services.GetRequiredService<Orchestra.Engine.IPendingInputStore>();
		await pendingStore.SaveAsync(new Orchestra.Engine.PendingInputRecord
		{
			OrchestrationName = "orch-empty",
			RunId = "run-empty",
			StepName = "step-empty",
			Kind = Orchestra.Engine.PendingInputKind.Approval,
			Prompt = "?",
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var response = await _client.PostAsJsonAsync(
			"/api/orchestrations/orch-empty/runs/run-empty/respond?step=step-empty",
			new { },
			_jsonOptions);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Respond_InvalidChoice_Returns400()
	{
		var pendingStore = _factory.Services.GetRequiredService<Orchestra.Engine.IPendingInputStore>();
		await pendingStore.SaveAsync(new Orchestra.Engine.PendingInputRecord
		{
			OrchestrationName = "orch-c",
			RunId = "run-c",
			StepName = "step-c",
			Kind = Orchestra.Engine.PendingInputKind.Approval,
			Prompt = "OK?",
			Choices = ["approve", "reject"],
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var response = await _client.PostAsJsonAsync(
			"/api/orchestrations/orch-c/runs/run-c/respond?step=step-c",
			new { choice = "explode" },
			_jsonOptions);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Respond_NoActiveWait_Returns404()
	{
		// Pending record exists but no in-process wait is registered (host hasn't started
		// the step's executor or restarted in between). Should fail-fast with 404.
		var pendingStore = _factory.Services.GetRequiredService<Orchestra.Engine.IPendingInputStore>();
		await pendingStore.SaveAsync(new Orchestra.Engine.PendingInputRecord
		{
			OrchestrationName = "orch-nw",
			RunId = "run-nw",
			StepName = "step-nw",
			Kind = Orchestra.Engine.PendingInputKind.Approval,
			Prompt = "?",
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var response = await _client.PostAsJsonAsync(
			"/api/orchestrations/orch-nw/runs/run-nw/respond?step=step-nw",
			new { reply = "ok" },
			_jsonOptions);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Respond_WaiterRegistered_CompletesWait()
	{
		var pendingStore = _factory.Services.GetRequiredService<Orchestra.Engine.IPendingInputStore>();
		var waiter = _factory.Services.GetRequiredService<Orchestra.Engine.IHumanInputWaiter>();

		await pendingStore.SaveAsync(new Orchestra.Engine.PendingInputRecord
		{
			OrchestrationName = "orch-ok",
			RunId = "run-ok",
			StepName = "step-ok",
			Kind = Orchestra.Engine.PendingInputKind.Approval,
			Prompt = "?",
			Choices = ["approve", "reject"],
			CreatedAt = DateTimeOffset.UtcNow,
		});

		// Register a wait, then post the response — the wait should complete.
		var waitTask = waiter.WaitAsync("orch-ok", "run-ok", "step-ok", CancellationToken.None);

		var response = await _client.PostAsJsonAsync(
			"/api/orchestrations/orch-ok/runs/run-ok/respond?step=step-ok",
			new { choice = "approve", respondedBy = "alice" },
			_jsonOptions);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		responseBody.GetProperty("accepted").GetBoolean().Should().BeTrue();

		var resolved = await waitTask;
		resolved.Choice.Should().Be("approve");
		resolved.RespondedBy.Should().Be("alice");
	}

	[Fact]
	public async Task GetPending_ReturnsRecordsAfterSeeding()
	{
		var pendingStore = _factory.Services.GetRequiredService<Orchestra.Engine.IPendingInputStore>();
		await pendingStore.SaveAsync(new Orchestra.Engine.PendingInputRecord
		{
			OrchestrationName = "orch-list",
			RunId = "run-list",
			StepName = "step-list",
			Kind = Orchestra.Engine.PendingInputKind.Approval,
			Prompt = "Approve?",
			Choices = ["approve", "reject"],
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var response = await _client.GetAsync("/api/runs/pending");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var pending = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
		var asArray = pending.EnumerateArray().ToList();
		asArray.Should().Contain(r =>
			r.GetProperty("orchestrationName").GetString() == "orch-list"
			&& r.GetProperty("stepName").GetString() == "step-list");
	}
}
