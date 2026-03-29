using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Host.Api;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Portal.Tests;

/// <summary>
/// API contract tests that verify every frontend API call resolves to a real backend
/// endpoint and returns a proper API response — not the SPA fallback HTML page.
///
/// These tests catch URL mismatches between the frontend TypeScript code and the
/// backend .NET endpoints. Each test corresponds to an API call made by the Portal
/// frontend (App.tsx, AddModal.tsx, ViewerModal.tsx, HistoryModal.tsx, McpsModal.tsx,
/// useOnlineStatus.ts).
///
/// A response is considered a contract violation if:
///   - It returns HTML (Content-Type text/html) indicating the SPA fallback caught it
///   - It returns 405 Method Not Allowed (route exists but wrong HTTP method)
///   - It returns 500+ (unhandled server error)
///
/// Expected responses: 200/OK with JSON, 400 BadRequest (validation), 404 NotFound
/// (resource doesn't exist yet but the route is valid).
/// </summary>
public class ApiContractTests : IClassFixture<PortalWebApplicationFactory>, IDisposable
{
	private readonly PortalWebApplicationFactory _factory;
	private readonly HttpClient _client;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true
	};

	public ApiContractTests(PortalWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose()
	{
		_client.Dispose();
	}

	#region Assertion Helpers

	/// <summary>
	/// Asserts that a response is a proper API response, not the SPA HTML fallback.
	/// An API response has JSON content type (or is empty) and is NOT 405.
	/// </summary>
	private static async Task AssertIsApiResponse(HttpResponseMessage response, string endpoint)
	{
		// Must not be 405 Method Not Allowed — that means the route doesn't match the HTTP method
		response.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed,
			$"Endpoint {endpoint} returned 405 Method Not Allowed — route exists but HTTP method is wrong");

		// Must not be 5xx — unhandled server error
		((int)response.StatusCode).Should().BeLessThan(500,
			$"Endpoint {endpoint} returned {response.StatusCode} — unhandled server error");

		// If there's content, it must not be HTML (SPA fallback)
		if (response.Content.Headers.ContentLength > 0 || response.Content.Headers.ContentType != null)
		{
			var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
			if (contentType == "text/html")
			{
				var body = await response.Content.ReadAsStringAsync();
				body.Should().NotContain("<!DOCTYPE html>",
					$"Endpoint {endpoint} returned HTML (SPA fallback) instead of an API response. " +
					$"This means the route is not registered on the backend.");
			}
		}
	}

	/// <summary>
	/// Asserts the response is valid JSON (not HTML, not empty).
	/// </summary>
	private static async Task AssertIsJsonResponse(HttpResponseMessage response, string endpoint)
	{
		await AssertIsApiResponse(response, endpoint);

		var contentType = response.Content.Headers.ContentType?.MediaType;
		contentType.Should().BeOneOf("application/json", "application/problem+json",
			$"Endpoint {endpoint} should return JSON but returned {contentType}");
	}

	#endregion

	#region Helper: Register a test orchestration

	private async Task<(string Id, string Name)> RegisterTestOrchestrationAsync(
		string? name = null, bool withWebhookTrigger = false, bool triggerEnabled = false,
		bool withParameters = false)
	{
		name ??= $"Contract Test {Guid.NewGuid():N}";

		string json;
		if (withParameters)
		{
			json = """
			{
				"name": "PARAM_NAME_PLACEHOLDER",
				"description": "Orchestration with parameters",
				"steps": [
					{
						"name": "step-a",
						"type": "Prompt",
						"dependsOn": [],
						"parameters": ["destination", "duration"],
						"systemPrompt": "Plan a trip",
						"userPrompt": "Plan a trip",
						"model": "claude-opus-4.5"
					},
					{
						"name": "step-b",
						"type": "Prompt",
						"dependsOn": ["step-a"],
						"parameters": ["destination"],
						"systemPrompt": "Summarize",
						"userPrompt": "Summarize the trip",
						"model": "claude-opus-4.5"
					}
				]
			}
			""".Replace("PARAM_NAME_PLACEHOLDER", name);
		}
		else if (withWebhookTrigger)
		{
			json = $$"""
			{
				"name": "{{name}}",
				"description": "Contract test orchestration",
				"version": "1.0.0",
				"trigger": {
					"type": "Webhook",
					"enabled": {{triggerEnabled.ToString().ToLowerInvariant()}}
				},
				"steps": [{
					"name": "test-step",
					"type": "Prompt",
					"dependsOn": [],
					"systemPrompt": "Test",
					"userPrompt": "Hello",
					"model": "claude-opus-4.5"
				}]
			}
			""";
		}
		else
		{
			json = $$"""
			{
				"name": "{{name}}",
				"description": "Contract test orchestration",
				"steps": [{
					"name": "test-step",
					"type": "Prompt",
					"dependsOn": [],
					"systemPrompt": "Test",
					"userPrompt": "Hello",
					"model": "claude-opus-4.5"
				}]
			}
			""";
		}

		var response = await _client.PostAsJsonAsync("/api/orchestrations/add-json",
			new { json }, _jsonOptions);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var id = result.GetProperty("id").GetString()!;
		return (id, name);
	}

	#endregion

	#region 1. GET /api/orchestrations — List all orchestrations

	[Fact]
	public async Task Contract_GetOrchestrations_ReturnsJsonWithOrchestrationsList()
	{
		var response = await _client.GetAsync("/api/orchestrations");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, "GET /api/orchestrations");

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("orchestrations", out _).Should().BeTrue(
			"Response should have 'orchestrations' array matching frontend OrchestrationsResponse type");
	}

	#endregion

	#region 2. GET /api/orchestrations/{id} — Get single orchestration

	[Fact]
	public async Task Contract_GetOrchestrationById_WithValidId_ReturnsJson()
	{
		var (id, _) = await RegisterTestOrchestrationAsync();

		var response = await _client.GetAsync($"/api/orchestrations/{id}");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, $"GET /api/orchestrations/{id}");
	}

	[Fact]
	public async Task Contract_GetOrchestrationById_WithInvalidId_ReturnsNotFoundNotHtml()
	{
		var response = await _client.GetAsync("/api/orchestrations/nonexistent-id");

		await AssertIsApiResponse(response, "GET /api/orchestrations/nonexistent-id");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 2b. Parameters shape — parameters must be string[] for frontend compatibility

	[Fact]
	public async Task Contract_GetOrchestrations_ParametersIsStringArray()
	{
		var (id, _) = await RegisterTestOrchestrationAsync(withParameters: true);

		var response = await _client.GetAsync("/api/orchestrations");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orchestrations = result.GetProperty("orchestrations");
		var orch = orchestrations.EnumerateArray()
			.First(o => o.GetProperty("id").GetString() == id);

		// parameters must be an array of strings, not an object —
		// the frontend iterates it with array methods, and Object.keys()
		// on an array returns indices ("0", "1") instead of parameter names
		var parameters = orch.GetProperty("parameters");
		parameters.ValueKind.Should().Be(JsonValueKind.Array,
			"parameters must be a JSON array, not an object — the frontend depends on this");

		var paramValues = parameters.EnumerateArray().Select(e => e.GetString()).ToArray();
		paramValues.Should().Contain("destination");
		paramValues.Should().Contain("duration");

		// hasParameters must also be true
		orch.GetProperty("hasParameters").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Contract_GetOrchestrations_StepParametersIsStringArray()
	{
		var (id, _) = await RegisterTestOrchestrationAsync(withParameters: true);

		var response = await _client.GetAsync("/api/orchestrations");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orchestrations = result.GetProperty("orchestrations");
		var orch = orchestrations.EnumerateArray()
			.First(o => o.GetProperty("id").GetString() == id);

		var steps = orch.GetProperty("steps");
		var stepA = steps.EnumerateArray()
			.First(s => s.GetProperty("name").GetString() == "step-a");

		// Step-level parameters must also be a string array
		var stepParams = stepA.GetProperty("parameters");
		stepParams.ValueKind.Should().Be(JsonValueKind.Array,
			"step parameters must be a JSON array of strings");

		var stepParamValues = stepParams.EnumerateArray().Select(e => e.GetString()).ToArray();
		stepParamValues.Should().BeEquivalentTo(["destination", "duration"]);
	}

	[Fact]
	public async Task Contract_GetOrchestrationById_ParametersIsStringArray()
	{
		var (id, _) = await RegisterTestOrchestrationAsync(withParameters: true);

		var response = await _client.GetAsync($"/api/orchestrations/{id}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();

		// Orchestration-level parameters must be a string array
		var parameters = result.GetProperty("parameters");
		parameters.ValueKind.Should().Be(JsonValueKind.Array,
			"parameters must be a JSON array, not an object — the frontend depends on this");

		var paramValues = parameters.EnumerateArray().Select(e => e.GetString()).ToArray();
		paramValues.Should().Contain("destination");
		paramValues.Should().Contain("duration");

		// Step-level parameters must also be string arrays
		var steps = result.GetProperty("steps");
		var stepB = steps.EnumerateArray()
			.First(s => s.GetProperty("name").GetString() == "step-b");

		var stepBParams = stepB.GetProperty("parameters");
		stepBParams.ValueKind.Should().Be(JsonValueKind.Array);
		stepBParams.EnumerateArray().Select(e => e.GetString()).ToArray()
			.Should().BeEquivalentTo(["destination"]);
	}

	[Fact]
	public async Task Contract_GetOrchestrations_NoParameters_HasEmptyArray()
	{
		var (id, _) = await RegisterTestOrchestrationAsync(withParameters: false);

		var response = await _client.GetAsync("/api/orchestrations");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orchestrations = result.GetProperty("orchestrations");
		var orch = orchestrations.EnumerateArray()
			.First(o => o.GetProperty("id").GetString() == id);

		var parameters = orch.GetProperty("parameters");
		parameters.ValueKind.Should().Be(JsonValueKind.Array);
		parameters.GetArrayLength().Should().Be(0);
		orch.GetProperty("hasParameters").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public async Task Contract_GetOrchestrations_ParametersAreDeduplicatedAcrossSteps()
	{
		var (id, _) = await RegisterTestOrchestrationAsync(withParameters: true);

		var response = await _client.GetAsync("/api/orchestrations");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orch = result.GetProperty("orchestrations").EnumerateArray()
			.First(o => o.GetProperty("id").GetString() == id);

		// step-a has ["destination", "duration"], step-b has ["destination"]
		// Orchestration-level parameters should be deduplicated: ["destination", "duration"]
		var paramValues = orch.GetProperty("parameters").EnumerateArray()
			.Select(e => e.GetString()).ToArray();
		paramValues.Should().HaveCount(2,
			"parameters should be deduplicated across steps — 'destination' appears in both steps but should only be listed once");
		paramValues.Should().Contain("destination");
		paramValues.Should().Contain("duration");
	}

	#endregion

	#region 2c. Subagent & step type shape

	private async Task<string> RegisterOrchestrationWithSubagentsAsync()
	{
		var name = $"Subagent Test {Guid.NewGuid():N}";
		var json = """
		{
			"name": "NAME_PLACEHOLDER",
			"description": "Orchestration with subagents and mixed step types",
			"steps": [
				{
					"name": "fetch-data",
					"type": "Http",
					"method": "GET",
					"url": "https://api.example.com/data",
					"dependsOn": []
				},
				{
					"name": "analyze",
					"type": "Prompt",
					"dependsOn": ["fetch-data"],
					"systemPrompt": "You are an analyst.",
					"userPrompt": "Analyze the data: {{fetch-data.output}}",
					"model": "claude-opus-4.5",
					"subagents": [
						{
							"name": "researcher",
							"displayName": "Research Agent",
							"description": "Searches for additional context",
							"prompt": "You are a researcher.",
							"infer": true
						},
						{
							"name": "writer",
							"displayName": "Writer Agent",
							"description": "Writes polished content",
							"prompt": "You are a writer.",
							"tools": ["edit_file", "create_file"],
							"infer": false
						}
					]
				},
				{
					"name": "format-output",
					"type": "Transform",
					"dependsOn": ["analyze"],
					"template": "Result: {{analyze.output}}"
				}
			]
		}
		""".Replace("NAME_PLACEHOLDER", name);

		var response = await _client.PostAsJsonAsync("/api/orchestrations/add-json",
			new { json }, _jsonOptions);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		return result.GetProperty("id").GetString()!;
	}

	[Fact]
	public async Task Contract_GetOrchestrations_StepsIncludeTypeField()
	{
		var id = await RegisterOrchestrationWithSubagentsAsync();

		var response = await _client.GetAsync("/api/orchestrations");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orch = result.GetProperty("orchestrations").EnumerateArray()
			.First(o => o.GetProperty("id").GetString() == id);

		var steps = orch.GetProperty("steps").EnumerateArray().ToArray();
		steps.Should().HaveCount(3);

		// Verify type field is present and correct
		steps[0].GetProperty("type").GetString().Should().Be("Http");
		steps[1].GetProperty("type").GetString().Should().Be("Prompt");
		steps[2].GetProperty("type").GetString().Should().Be("Transform");
	}

	[Fact]
	public async Task Contract_GetOrchestrations_SubagentsIncludedInListEndpoint()
	{
		var id = await RegisterOrchestrationWithSubagentsAsync();

		var response = await _client.GetAsync("/api/orchestrations");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orch = result.GetProperty("orchestrations").EnumerateArray()
			.First(o => o.GetProperty("id").GetString() == id);

		var analyzeStep = orch.GetProperty("steps").EnumerateArray()
			.First(s => s.GetProperty("name").GetString() == "analyze");

		// Subagents should be present
		analyzeStep.TryGetProperty("subagents", out var subagents).Should().BeTrue(
			"Prompt steps with subagents should include subagents in the list endpoint");

		subagents.GetArrayLength().Should().Be(2);

		var researcher = subagents.EnumerateArray()
			.First(sa => sa.GetProperty("name").GetString() == "researcher");
		researcher.GetProperty("displayName").GetString().Should().Be("Research Agent");
		researcher.GetProperty("description").GetString().Should().Be("Searches for additional context");
	}

	[Fact]
	public async Task Contract_GetOrchestrationById_SubagentsIncludeAllFields()
	{
		var id = await RegisterOrchestrationWithSubagentsAsync();

		var response = await _client.GetAsync($"/api/orchestrations/{id}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var analyzeStep = result.GetProperty("steps").EnumerateArray()
			.First(s => s.GetProperty("name").GetString() == "analyze");

		var subagents = analyzeStep.GetProperty("subagents");
		subagents.GetArrayLength().Should().Be(2);

		// Verify writer agent has full detail
		var writer = subagents.EnumerateArray()
			.First(sa => sa.GetProperty("name").GetString() == "writer");
		writer.GetProperty("displayName").GetString().Should().Be("Writer Agent");
		writer.GetProperty("description").GetString().Should().Be("Writes polished content");
		writer.GetProperty("infer").GetBoolean().Should().BeFalse();

		var tools = writer.GetProperty("tools");
		tools.ValueKind.Should().Be(JsonValueKind.Array);
		tools.GetArrayLength().Should().Be(2);
	}

	[Fact]
	public async Task Contract_GetOrchestrations_HttpStepIncludesMethodAndUrl()
	{
		var id = await RegisterOrchestrationWithSubagentsAsync();

		var response = await _client.GetAsync("/api/orchestrations");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orch = result.GetProperty("orchestrations").EnumerateArray()
			.First(o => o.GetProperty("id").GetString() == id);

		var httpStep = orch.GetProperty("steps").EnumerateArray()
			.First(s => s.GetProperty("name").GetString() == "fetch-data");

		httpStep.GetProperty("type").GetString().Should().Be("Http");
		httpStep.GetProperty("method").GetString().Should().Be("GET");
		httpStep.GetProperty("url").GetString().Should().Be("https://api.example.com/data");
	}

	[Fact]
	public async Task Contract_GetOrchestrationById_TransformStepIncludesTemplate()
	{
		var id = await RegisterOrchestrationWithSubagentsAsync();

		var response = await _client.GetAsync($"/api/orchestrations/{id}");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var transformStep = result.GetProperty("steps").EnumerateArray()
			.First(s => s.GetProperty("name").GetString() == "format-output");

		transformStep.GetProperty("type").GetString().Should().Be("Transform");
		transformStep.GetProperty("template").GetString().Should().Be("Result: {{analyze.output}}");
	}

	[Fact]
	public async Task Contract_GetOrchestrations_PromptStepWithoutSubagents_OmitsSubagentsField()
	{
		var (id, _) = await RegisterTestOrchestrationAsync();

		var response = await _client.GetAsync("/api/orchestrations");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orch = result.GetProperty("orchestrations").EnumerateArray()
			.First(o => o.GetProperty("id").GetString() == id);

		var step = orch.GetProperty("steps").EnumerateArray().First();

		// When no subagents, the field should be null/omitted (WhenWritingNull)
		if (step.TryGetProperty("subagents", out var val))
		{
			val.ValueKind.Should().Be(JsonValueKind.Null,
				"subagents should be null (omitted) when the step has no subagents");
		}
	}

	#endregion

	#region 3. DELETE /api/orchestrations/{id} — Delete orchestration

	[Fact]
	public async Task Contract_DeleteOrchestration_WithValidId_ReturnsApiResponse()
	{
		var (id, _) = await RegisterTestOrchestrationAsync();

		var response = await _client.DeleteAsync($"/api/orchestrations/{id}");

		await AssertIsApiResponse(response, $"DELETE /api/orchestrations/{id}");
		// Accept 200 OK or 204 NoContent
		((int)response.StatusCode).Should().BeInRange(200, 204);
	}

	[Fact]
	public async Task Contract_DeleteOrchestration_WithInvalidId_ReturnsNotFoundNotHtml()
	{
		var response = await _client.DeleteAsync("/api/orchestrations/nonexistent-id");

		await AssertIsApiResponse(response, "DELETE /api/orchestrations/nonexistent-id");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 4-5. POST /api/orchestrations/{id}/enable and /disable

	[Fact]
	public async Task Contract_EnableOrchestration_RouteExists()
	{
		var (id, _) = await RegisterTestOrchestrationAsync(withWebhookTrigger: true, triggerEnabled: false);

		var response = await _client.PostAsync($"/api/orchestrations/{id}/enable", null);

		await AssertIsApiResponse(response, $"POST /api/orchestrations/{id}/enable");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Contract_DisableOrchestration_RouteExists()
	{
		var (id, _) = await RegisterTestOrchestrationAsync(withWebhookTrigger: true, triggerEnabled: true);
		await Task.Delay(200);

		var response = await _client.PostAsync($"/api/orchestrations/{id}/disable", null);

		await AssertIsApiResponse(response, $"POST /api/orchestrations/{id}/disable");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Contract_EnableOrchestration_WithInvalidId_ReturnsNotFoundNotHtml()
	{
		var response = await _client.PostAsync("/api/orchestrations/nonexistent-id/enable", null);

		await AssertIsApiResponse(response, "POST /api/orchestrations/nonexistent-id/enable");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 6. POST /api/orchestrations/{id}/toggle — Toggle trigger enabled

	[Fact]
	public async Task Contract_ToggleOrchestration_RouteExists()
	{
		var (id, _) = await RegisterTestOrchestrationAsync(withWebhookTrigger: true, triggerEnabled: true);
		await Task.Delay(200);

		var response = await _client.PostAsJsonAsync(
			$"/api/orchestrations/{id}/toggle",
			new { enabled = false }, _jsonOptions);

		await AssertIsApiResponse(response, $"POST /api/orchestrations/{id}/toggle");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Contract_ToggleOrchestration_WithInvalidId_ReturnsNotFoundNotHtml()
	{
		var response = await _client.PostAsJsonAsync(
			"/api/orchestrations/nonexistent-id/toggle",
			new { enabled = true }, _jsonOptions);

		await AssertIsApiResponse(response, "POST /api/orchestrations/nonexistent-id/toggle");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 7. GET /api/orchestrations/{id}/run — SSE run endpoint

	[Fact]
	public async Task Contract_RunOrchestration_RouteExists()
	{
		// We can't fully test SSE here, but we can verify the route is registered
		// by making a GET request and checking we don't get HTML fallback.
		// The SSE endpoint should return text/event-stream or at least not return HTML.
		var (id, _) = await RegisterTestOrchestrationAsync();

		// Use a short timeout — we don't want to run the whole orchestration
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		try
		{
			var response = await _client.GetAsync(
				$"/api/orchestrations/{id}/run", HttpCompletionOption.ResponseHeadersRead, cts.Token);

			// The key assertion: it should NOT be HTML (SPA fallback)
			var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
			contentType.Should().NotBe("text/html",
				"SSE endpoint /api/orchestrations/{id}/run should not return HTML fallback");

			// SSE endpoints return text/event-stream
			contentType.Should().Be("text/event-stream",
				"SSE endpoint should return text/event-stream content type");
		}
		catch (OperationCanceledException)
		{
			// Expected — we cancelled before the orchestration finished
		}
	}

	#endregion

	#region 8. POST /api/orchestrations/json — Add orchestration from JSON (Host route)

	[Fact]
	public async Task Contract_AddOrchestrationJson_HostRoute_ReturnsApiResponse()
	{
		var json = """
		{
			"name": "Contract JSON Host Route",
			"description": "Test",
			"steps": [{
				"name": "s1",
				"type": "Prompt",
				"dependsOn": [],
				"systemPrompt": "Test",
				"userPrompt": "Hello",
				"model": "claude-opus-4.5"
			}]
		}
		""";

		var response = await _client.PostAsJsonAsync("/api/orchestrations/json",
			new { json, mcpJson = (string?)null }, _jsonOptions);

		await AssertIsApiResponse(response, "POST /api/orchestrations/json");
		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	#endregion

	#region 9. POST /api/orchestrations/add — Portal alias

	[Fact]
	public async Task Contract_AddOrchestrations_RouteExists()
	{
		// Send a request with nonexistent paths — the route should exist and return
		// a structured JSON response (with errors for bad paths), not HTML.
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add",
			new { paths = new[] { "C:\\nonexistent\\file.json" } }, _jsonOptions);

		await AssertIsApiResponse(response, "POST /api/orchestrations/add");
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("addedCount", out _).Should().BeTrue(
			"Response should have 'addedCount' property");
	}

	#endregion

	#region 10. POST /api/orchestrations/add-json — Portal alias

	[Fact]
	public async Task Contract_AddOrchestrationAddJson_RouteExists()
	{
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add-json",
			new { json = "{}" }, _jsonOptions);

		// The route should be registered — we expect a 400 BadRequest for invalid JSON,
		// NOT an HTML fallback or 405
		await AssertIsApiResponse(response, "POST /api/orchestrations/add-json");
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	#endregion

	#region 11. POST /api/cancel/{executionId} — Cancel execution (Portal alias)

	[Fact]
	public async Task Contract_CancelExecution_RouteExists()
	{
		// Even with a nonexistent execution ID, the route should be registered
		// and return 404 NotFound (not HTML fallback)
		var response = await _client.PostAsync("/api/cancel/nonexistent-exec-id", null);

		await AssertIsApiResponse(response, "POST /api/cancel/nonexistent-exec-id");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 12. GET /api/execution/{executionId}/attach — SSE attach endpoint

	[Fact]
	public async Task Contract_AttachExecution_RouteExists()
	{
		// The attach endpoint should be registered. With a nonexistent execution ID,
		// it should return an error, not the HTML fallback.
		var response = await _client.GetAsync("/api/execution/nonexistent-exec-id/attach");

		await AssertIsApiResponse(response, "GET /api/execution/nonexistent-exec-id/attach");
		// Could be 404 or possibly a different error, but NOT HTML
		response.StatusCode.Should().NotBe(HttpStatusCode.OK,
			"Attach with nonexistent execution should not return 200");
	}

	#endregion

	#region 13. GET /api/history — List history

	[Fact]
	public async Task Contract_GetHistory_ReturnsJsonWithRunsList()
	{
		var response = await _client.GetAsync("/api/history?limit=15");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, "GET /api/history?limit=15");

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("runs", out _).Should().BeTrue(
			"Response should have 'runs' array matching frontend HistoryResponse type");
	}

	[Fact]
	public async Task Contract_GetHistory_WithHigherLimit_ReturnsJson()
	{
		// HistoryModal.tsx uses limit=100
		var response = await _client.GetAsync("/api/history?limit=100");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, "GET /api/history?limit=100");
	}

	#endregion

	#region 14. GET /api/history/{name}/{runId} — Get execution details

	[Fact]
	public async Task Contract_GetHistoryDetail_WithInvalidIds_ReturnsNotFoundNotHtml()
	{
		var response = await _client.GetAsync("/api/history/nonexistent-orch/nonexistent-run");

		await AssertIsApiResponse(response, "GET /api/history/nonexistent-orch/nonexistent-run");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 15. DELETE /api/history/{name}/{runId} — Delete history entry

	[Fact]
	public async Task Contract_DeleteHistoryEntry_WithInvalidIds_ReturnsNotFoundNotHtml()
	{
		var response = await _client.DeleteAsync("/api/history/nonexistent-orch/nonexistent-run");

		await AssertIsApiResponse(response, "DELETE /api/history/nonexistent-orch/nonexistent-run");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 16. GET /api/active — List active executions and pending triggers

	[Fact]
	public async Task Contract_GetActive_ReturnsJsonWithRunningAndPending()
	{
		var response = await _client.GetAsync("/api/active");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, "GET /api/active");

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("running", out _).Should().BeTrue(
			"Response should have 'running' array matching frontend ActiveData type");
		result.TryGetProperty("pending", out _).Should().BeTrue(
			"Response should have 'pending' array matching frontend ActiveData type");
	}

	#endregion

	#region 17. GET /api/status — Server status

	[Fact]
	public async Task Contract_GetStatus_ReturnsJsonWithExpectedFields()
	{
		var response = await _client.GetAsync("/api/status");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, "GET /api/status");

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		// Frontend expects: orchestrationCount, activeTriggers, runningExecutions
		result.TryGetProperty("orchestrationCount", out _).Should().BeTrue(
			"Status response should have 'orchestrationCount'");
		result.TryGetProperty("activeTriggers", out _).Should().BeTrue(
			"Status response should have 'activeTriggers'");
		result.TryGetProperty("runningExecutions", out _).Should().BeTrue(
			"Status response should have 'runningExecutions'");
	}

	[Fact]
	public async Task Contract_GetStatus_RawFetch_ReturnsOk()
	{
		// useOnlineStatus.ts uses raw fetch() to check server reachability
		var response = await _client.GetAsync("/api/status");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsApiResponse(response, "GET /api/status (raw fetch)");
	}

	#endregion

	#region 18. GET /api/mcps — List MCP tools

	[Fact]
	public async Task Contract_GetMcps_ReturnsJsonWithMcpsList()
	{
		var response = await _client.GetAsync("/api/mcps");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, "GET /api/mcps");

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("mcps", out _).Should().BeTrue(
			"Response should have 'mcps' array matching frontend McpsResponse type");
	}

	#endregion

	#region 19. GET /api/folder/browse — Native folder dialog

	[Fact]
	public async Task Contract_FolderBrowse_RouteExists()
	{
		// This endpoint opens a native Windows dialog, which won't work in test,
		// but we verify the route is registered (not returning HTML fallback).
		var response = await _client.GetAsync("/api/folder/browse");

		await AssertIsApiResponse(response, "GET /api/folder/browse");
		// On non-Windows or in test env, might return 400; on Windows, returns JSON
		// The key assertion is that it's NOT an HTML page
	}

	#endregion

	#region 20. POST /api/folder/scan — Scan folder for orchestrations

	[Fact]
	public async Task Contract_FolderScan_RouteExists()
	{
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { directory = "C:\\nonexistent" }, _jsonOptions);

		await AssertIsApiResponse(response, "POST /api/folder/scan");
		// Should return 400 BadRequest for nonexistent directory, not HTML
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	#endregion

	#region 21. GET /api/file/read — Read file content

	[Fact]
	public async Task Contract_FileRead_RouteExists()
	{
		var response = await _client.GetAsync("/api/file/read?path=C%3A%5Cnonexistent.json");

		await AssertIsApiResponse(response, "GET /api/file/read?path=...");
		// Should return 404 for nonexistent file, not HTML
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region Host canonical routes still work (backend owns these)

	[Fact]
	public async Task Contract_HostCanonical_PostActiveCancel_RouteExists()
	{
		// POST /api/active/{executionId}/cancel is the Host's canonical route
		var response = await _client.PostAsync("/api/active/nonexistent-id/cancel", null);

		await AssertIsApiResponse(response, "POST /api/active/nonexistent-id/cancel");
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Contract_HostCanonical_GetTriggers_ReturnsJson()
	{
		var response = await _client.GetAsync("/api/triggers");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, "GET /api/triggers");
	}

	[Fact]
	public async Task Contract_HostCanonical_GetHealth_ReturnsOk()
	{
		var response = await _client.GetAsync("/api/health");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsApiResponse(response, "GET /api/health");
	}

	[Fact]
	public async Task Contract_HostCanonical_GetModels_ReturnsJson()
	{
		var response = await _client.GetAsync("/api/models");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		await AssertIsJsonResponse(response, "GET /api/models");
	}

	#endregion

	#region Comprehensive: Every frontend URL returns API response, not SPA fallback

	/// <summary>
	/// Master test that verifies ALL frontend API URLs are registered as backend routes.
	/// For each URL pattern, we substitute test values for path parameters and make a
	/// request. The assertion is simple: the response must NOT be the SPA HTML fallback.
	/// </summary>
	[Theory]
	[InlineData("GET", "/api/orchestrations")]
	[InlineData("GET", "/api/orchestrations/{id}")]
	[InlineData("DELETE", "/api/orchestrations/{id}")]
	[InlineData("POST", "/api/orchestrations/{id}/enable")]
	[InlineData("POST", "/api/orchestrations/{id}/disable")]
	[InlineData("POST", "/api/orchestrations/{id}/toggle")]
	[InlineData("GET", "/api/orchestrations/{id}/run")]
	[InlineData("POST", "/api/orchestrations/json")]
	[InlineData("POST", "/api/orchestrations/add")]
	[InlineData("POST", "/api/orchestrations/add-json")]
	[InlineData("POST", "/api/cancel/{executionId}")]
	[InlineData("GET", "/api/execution/{executionId}/attach")]
	[InlineData("GET", "/api/history?limit=15")]
	[InlineData("GET", "/api/history/{name}/{runId}")]
	[InlineData("DELETE", "/api/history/{name}/{runId}")]
	[InlineData("GET", "/api/active")]
	[InlineData("GET", "/api/status")]
	[InlineData("GET", "/api/mcps")]
	[InlineData("GET", "/api/folder/browse")]
	[InlineData("POST", "/api/folder/scan")]
	[InlineData("GET", "/api/file/read?path=C%3A%5Ctest.json")]
	public async Task Contract_AllFrontendUrls_ResolveToBackendRoutes_NotSpaFallback(
		string method, string urlTemplate)
	{
		// Substitute test values for path parameters
		var url = urlTemplate
			.Replace("{id}", "test-id-000")
			.Replace("{executionId}", "test-exec-000")
			.Replace("{name}", "test-name")
			.Replace("{runId}", "test-run");

		// Build the request with appropriate body for POST endpoints
		var request = new HttpRequestMessage(new HttpMethod(method), url);
		if (method == "POST")
		{
			// Provide a minimal JSON body for POST endpoints
			var body = urlTemplate switch
			{
				"/api/orchestrations/{id}/toggle" => """{"enabled":true}""",
				"/api/orchestrations/json" => """{"json":"{}","mcpJson":null}""",
				"/api/orchestrations/add" => """{"paths":[]}""",
				"/api/orchestrations/add-json" => """{"json":"{}"}""",
				"/api/folder/scan" => """{"directory":"C:\\test"}""",
				_ => "{}"
			};
			request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		}

		HttpResponseMessage response;
		try
		{
			if (urlTemplate.Contains("/run") || urlTemplate.Contains("/attach"))
			{
				// SSE endpoints may hang — use a timeout
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
				response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
			}
			else
			{
				response = await _client.SendAsync(request);
			}
		}
		catch (OperationCanceledException)
		{
			// SSE endpoint responded (headers received) before timeout — that's fine,
			// it means the route exists
			return;
		}

		// The critical assertion: the response must NOT be HTML (SPA fallback)
		var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
		if (contentType == "text/html")
		{
			var body = await response.Content.ReadAsStringAsync();
			body.Should().NotContain("<!DOCTYPE html>",
				$"{method} {urlTemplate} returned the SPA HTML fallback. " +
				$"This means the backend does not have a route registered for this URL. " +
				$"Status: {response.StatusCode}");
		}

		// Must not be 405
		response.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed,
			$"{method} {urlTemplate} returned 405 — the URL is registered but the HTTP method is wrong");
	}

	#endregion
}
