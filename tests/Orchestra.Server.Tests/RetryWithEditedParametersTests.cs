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
/// Coverage for the "re-run with edits" path on the retry endpoint:
/// <c>GET /api/history/{name}/{runId}/retry?mode=all&amp;params=&lt;URL-encoded JSON&gt;</c>.
///
/// The Portal's "Re-run with edits..." button opens the parameter modal pre-filled
/// with the source run's stored parameters; on submit it POSTs through this endpoint
/// with the user-edited values. The server contract under test:
/// <list type="number">
///   <item><description>The override fully replaces the source run's parameters.</description></item>
///   <item><description>The new run is tagged <c>retryMode = "all-edited"</c> so historical
///     browsing can distinguish "verbatim re-run" from "re-run with edits".</description></item>
///   <item><description>Retry lineage (<c>RetriedFromRunId</c>) is preserved either way.</description></item>
///   <item><description>Override is rejected (HTTP 400) for <c>failed</c> / <c>from-step</c>
///     modes because those replay checkpointed outputs derived from the original parameters.</description></item>
/// </list>
/// </summary>
public class RetryWithEditedParametersTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly ServerWebApplicationFactory _factory;
	private readonly HttpClient _client;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	public RetryWithEditedParametersTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose() => _client.Dispose();

	[Fact]
	public async Task RetryWithEditedParameters_OverridesSourceRunParameters_AndTagsRunRecord()
	{
		// ── Arrange ────────────────────────────────────────────────────────────
		// Use a Transform-only orchestration so the retry completes in milliseconds
		// without any LLM / process / HITL machinery getting in the way. The
		// `template` references `{{param.message}}` so we can verify the override
		// actually drove execution (not just the parameter dictionary).
		var unique = Guid.NewGuid().ToString("N")[..8];
		var orchestrationName = $"retry-edits-{unique}";
		// Use a non-interpolated raw string + concatenate the dynamic name so the
		// literal `{{param.message}}` template expression inside the orchestration's
		// `template` field survives as-is. (The interpolated raw-string form $$"""..."""
		// treats `{{...}}` as a C# expression placeholder and would steal that token.)
		var orchestrationJson = """
			{
				"name": "__NAME__",
				"description": "Transform-only orchestration for retry-with-edits parameter override test.",
				"version": "1.0.0",
				"inputs": {
					"message": { "type": "string", "required": true, "description": "Echo target." }
				},
				"steps": [
					{
						"name": "echo",
						"type": "Transform",
						"template": "echoed: {{param.message}}"
					}
				]
			}
			""".Replace("__NAME__", orchestrationName);

		var register = await _client.PostAsJsonAsync(
			"/api/orchestrations/json",
			new { Json = orchestrationJson, McpJson = (string?)null },
			_jsonOptions);
		register.StatusCode.Should().Be(HttpStatusCode.OK);

		// Seed a source run with parameter message="original". This is what the
		// override must REPLACE -- if the override doesn't take, the retry's
		// finalContent will read "echoed: original" instead of "echoed: edited".
		var runStore = _factory.Services.GetRequiredService<FileSystemRunStore>();
		var sourceRunId = $"src-{unique}";
		await runStore.SaveRunAsync(
			new OrchestrationRunRecord
			{
				RunId = sourceRunId,
				OrchestrationName = orchestrationName,
				OrchestrationVersion = "1.0.0",
				TriggeredBy = "manual",
				StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
				CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
				Status = ExecutionStatus.Succeeded,
				IsIncomplete = false,
				FinalContent = "echoed: original",
				SavedFiles = [],
				HookExecutions = [],
				StepRecords = new Dictionary<string, StepRunRecord>(),
				AllStepRecords = new Dictionary<string, StepRunRecord>(),
				Parameters = new Dictionary<string, string> { ["message"] = "original" },
			},
			default(CancellationToken));

		// ── Act: retry mode=all with an edited parameter override ─────────────
		var paramsJson = JsonSerializer.Serialize(new { message = "edited" }, _jsonOptions);
		var retryUrl =
			$"/api/history/{Uri.EscapeDataString(orchestrationName)}/{Uri.EscapeDataString(sourceRunId)}/retry"
			+ $"?mode=all&params={Uri.EscapeDataString(paramsJson)}";

		using (var retryReq = new HttpRequestMessage(HttpMethod.Get, retryUrl))
		using (var retryResp = await _client.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead))
		{
			retryResp.StatusCode.Should().Be(HttpStatusCode.OK);
		}

		// ── Assert: locate the new run by lineage and verify the override took ─
		// Poll the run store until a child run linked to sourceRunId shows up. A
		// 30s deadline easily covers the ~100ms typical Transform-step execution
		// plus a generous slack for startup contention in CI.
		OrchestrationRunRecord? newRun = null;
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (DateTime.UtcNow < deadline && newRun is null)
		{
			await Task.Delay(200);
			var summaries = await runStore.GetRunSummariesAsync(orchestrationName);
			var childSummary = summaries.FirstOrDefault(s =>
				s.RetriedFromRunId == sourceRunId && s.Status == ExecutionStatus.Succeeded);
			if (childSummary is not null)
			{
				newRun = await runStore.GetRunAsync(orchestrationName, childSummary.RunId);
			}
		}

		newRun.Should().NotBeNull("the retried run should be persisted with retry lineage");
		newRun!.RetriedFromRunId.Should().Be(sourceRunId,
			"the retry lineage must survive the parameter override");
		newRun.RetryMode.Should().Be("all-edited",
			"runs created via the override path must be distinguishable from verbatim re-runs in history");
		newRun.Parameters.Should().ContainKey("message")
			.WhoseValue.Should().Be("edited",
				"the override should fully replace the source run's parameters -- if it didn't, the value here would still be 'original'");
		newRun.FinalContent.Should().Contain("echoed: edited",
			"the executor should have rendered the Transform template against the OVERRIDDEN parameters, not the source run's");
	}

	[Fact]
	public async Task RetryWithEditedParameters_RejectsOverride_ForFailedMode()
	{
		// Override is meaningless for mode=failed because that mode replays
		// checkpointed step outputs derived from the original parameter set;
		// swapping parameters would silently corrupt the per-step inputs visible
		// to dependent steps that aren't being replayed. The server must reject
		// this rather than silently ignore -- a silent ignore would be the worst
		// of both worlds (the user thinks they edited, the server uses the old values).
		var unique = Guid.NewGuid().ToString("N")[..8];
		var orchestrationName = $"retry-edits-reject-{unique}";

		// We don't even need a real orchestration / source run for this test --
		// validation happens before either is fetched. We use bogus identifiers
		// purely so the request reaches the handler.
		var paramsJson = JsonSerializer.Serialize(new { foo = "bar" }, _jsonOptions);
		var url =
			$"/api/history/{Uri.EscapeDataString(orchestrationName)}/bogus-run/retry"
			+ $"?mode=failed&params={Uri.EscapeDataString(paramsJson)}";

		var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var body = await response.Content.ReadAsStringAsync();
		body.Should().Contain("only valid for retry mode 'all'",
			"the 400 detail should explain WHY the override was rejected so the caller can pick the right mode");
	}

	[Fact]
	public async Task RetryWithEditedParameters_RejectsOverride_ForFromStepMode()
	{
		// Same reasoning as the failed-mode test; from-step is the other
		// checkpoint-restoring mode.
		var unique = Guid.NewGuid().ToString("N")[..8];
		var orchestrationName = $"retry-edits-reject-fs-{unique}";

		var paramsJson = JsonSerializer.Serialize(new { foo = "bar" }, _jsonOptions);
		var url =
			$"/api/history/{Uri.EscapeDataString(orchestrationName)}/bogus-run/retry"
			+ $"?mode=from-step&step=some-step&params={Uri.EscapeDataString(paramsJson)}";

		var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await response.Content.ReadAsStringAsync()).Should().Contain("only valid for retry mode 'all'");
	}

	[Fact]
	public async Task RetryWithEditedParameters_MalformedJson_Returns400()
	{
		// The server parses the query value with the same JsonSerializerOptions
		// used by the rest of the API; malformed JSON should produce a clean 400,
		// not crash the SSE stream or leak an exception page.
		var unique = Guid.NewGuid().ToString("N")[..8];
		var orchestrationName = $"retry-edits-bad-json-{unique}";

		var url =
			$"/api/history/{Uri.EscapeDataString(orchestrationName)}/bogus-run/retry"
			+ $"?mode=all&params={Uri.EscapeDataString("{not-valid-json")}";

		var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await response.Content.ReadAsStringAsync()).Should().Contain("Invalid JSON");
	}
}
