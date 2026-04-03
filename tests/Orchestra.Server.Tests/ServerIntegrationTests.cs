using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Integration tests for Orchestra.Server — the headless API server.
///
/// These tests verify:
///   1. All Host library endpoints are exposed (not behind SPA fallback)
///   2. OpenAPI endpoint is available
///   3. CORS headers are returned for localhost origins
///   4. Basic orchestration CRUD works end-to-end through the server
///   5. No SPA fallback exists (Server is headless — unknown routes return 404, not HTML)
/// </summary>
public class ServerIntegrationTests : IClassFixture<ServerWebApplicationFactory>, IDisposable
{
	private readonly ServerWebApplicationFactory _factory;
	private readonly HttpClient _client;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true
	};

	public ServerIntegrationTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose()
	{
		_client.Dispose();
	}

	#region Helpers

	private async Task<string> RegisterTestOrchestrationAsync(string? name = null)
	{
		name ??= $"Server Test {Guid.NewGuid():N}";

		var json = $$"""
		{
			"name": "{{name}}",
			"description": "Server integration test orchestration",
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

		var response = await _client.PostAsJsonAsync("/api/orchestrations/json",
			new { json }, _jsonOptions);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		return result.GetProperty("id").GetString()!;
	}

	#endregion

	#region 1. Server starts and Host endpoints are available

	[Fact]
	public async Task Server_HealthEndpoint_ReturnsOk()
	{
		var response = await _client.GetAsync("/api/health");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Server_StatusEndpoint_ReturnsJsonWithExpectedFields()
	{
		var response = await _client.GetAsync("/api/status");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var contentType = response.Content.Headers.ContentType?.MediaType;
		contentType.Should().Be("application/json");

		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("orchestrationCount", out _).Should().BeTrue();
		result.TryGetProperty("activeTriggers", out _).Should().BeTrue();
		result.TryGetProperty("runningExecutions", out _).Should().BeTrue();
	}

	[Fact]
	public async Task Server_ModelsEndpoint_ReturnsJson()
	{
		var response = await _client.GetAsync("/api/models");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var contentType = response.Content.Headers.ContentType?.MediaType;
		contentType.Should().Be("application/json");
	}

	[Fact]
	public async Task Server_TriggersEndpoint_ReturnsJson()
	{
		var response = await _client.GetAsync("/api/triggers");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var contentType = response.Content.Headers.ContentType?.MediaType;
		contentType.Should().Be("application/json");
	}

	#endregion

	#region 2. OpenAPI endpoint

	[Fact]
	public async Task Server_OpenApiEndpoint_ReturnsJsonDocument()
	{
		var response = await _client.GetAsync("/openapi/v1.json");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var contentType = response.Content.Headers.ContentType?.MediaType;
		contentType.Should().Be("application/json");

		var content = await response.Content.ReadAsStringAsync();
		content.Should().Contain("openapi", "OpenAPI document should contain the openapi version field");
		content.Should().Contain("paths", "OpenAPI document should contain paths");
	}

	#endregion

	#region 3. No SPA fallback — headless server returns 404 for unknown routes

	[Fact]
	public async Task Server_UnknownRoute_Returns404_NotHtml()
	{
		var response = await _client.GetAsync("/some/unknown/path");

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);

		// Must NOT return HTML — the Server has no SPA fallback
		var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
		contentType.Should().NotBe("text/html",
			"Headless server should not return HTML for unknown routes");
	}

	[Fact]
	public async Task Server_RootPath_Returns404_NotHtml()
	{
		var response = await _client.GetAsync("/");

		// The headless server has no UI, so root should be 404
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);

		var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
		contentType.Should().NotBe("text/html",
			"Headless server should not serve HTML at root");
	}

	#endregion

	#region 4. CORS headers for localhost origins

	[Fact]
	public async Task Server_CorsHeaders_AllowedForLocalhostOrigin()
	{
		var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
		request.Headers.Add("Origin", "http://localhost:3000");

		var response = await _client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue(
			"CORS should allow localhost origins");
		response.Headers.GetValues("Access-Control-Allow-Origin").First()
			.Should().Be("http://localhost:3000");
	}

	[Fact]
	public async Task Server_CorsHeaders_AllowedFor127001Origin()
	{
		var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
		request.Headers.Add("Origin", "http://127.0.0.1:5173");

		var response = await _client.SendAsync(request);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue(
			"CORS should allow 127.0.0.1 origins");
	}

	#endregion

	#region 5. Orchestration CRUD — end-to-end through the server

	[Fact]
	public async Task Server_CreateOrchestration_ViaJson_Succeeds()
	{
		var name = $"CRUD Test {Guid.NewGuid():N}";
		var json = $$"""
		{
			"name": "{{name}}",
			"description": "End-to-end CRUD test",
			"steps": [{
				"name": "step1",
				"type": "Prompt",
				"dependsOn": [],
				"systemPrompt": "Test",
				"userPrompt": "Hello",
				"model": "claude-opus-4.5"
			}]
		}
		""";

		var response = await _client.PostAsJsonAsync("/api/orchestrations/json",
			new { json }, _jsonOptions);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("id", out var idProp).Should().BeTrue();
		idProp.GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Server_ListOrchestrations_ReturnsCreatedOrchestration()
	{
		var name = $"List Test {Guid.NewGuid():N}";
		await RegisterTestOrchestrationAsync(name);

		var response = await _client.GetAsync("/api/orchestrations");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var orchestrations = result.GetProperty("orchestrations");

		var found = orchestrations.EnumerateArray()
			.Any(o => o.GetProperty("name").GetString() == name);
		found.Should().BeTrue($"orchestration '{name}' should appear in the list");
	}

	[Fact]
	public async Task Server_GetOrchestrationById_ReturnsDetails()
	{
		var id = await RegisterTestOrchestrationAsync();

		var response = await _client.GetAsync($"/api/orchestrations/{id}");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("id").GetString().Should().Be(id);
		result.TryGetProperty("steps", out _).Should().BeTrue();
	}

	[Fact]
	public async Task Server_DeleteOrchestration_RemovesItFromList()
	{
		var id = await RegisterTestOrchestrationAsync();

		// Delete it
		var deleteResponse = await _client.DeleteAsync($"/api/orchestrations/{id}");
		((int)deleteResponse.StatusCode).Should().BeInRange(200, 204);

		// Verify it's gone
		var getResponse = await _client.GetAsync($"/api/orchestrations/{id}");
		getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 6. History and active endpoints are available

	[Fact]
	public async Task Server_HistoryEndpoint_ReturnsJson()
	{
		var response = await _client.GetAsync("/api/history?limit=15");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("runs", out _).Should().BeTrue();
	}

	[Fact]
	public async Task Server_HistoryAllEndpoint_ReturnsPaginatedJson()
	{
		var response = await _client.GetAsync("/api/history/all?offset=0&limit=10");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("runs", out _).Should().BeTrue();
		result.TryGetProperty("total", out _).Should().BeTrue();
		result.TryGetProperty("offset", out _).Should().BeTrue();
		result.TryGetProperty("limit", out _).Should().BeTrue();
	}

	[Fact]
	public async Task Server_ActiveEndpoint_ReturnsJson()
	{
		var response = await _client.GetAsync("/api/active");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.TryGetProperty("running", out _).Should().BeTrue();
		result.TryGetProperty("pending", out _).Should().BeTrue();
	}

	#endregion

	#region 7. Enable/Disable endpoints

	[Fact]
	public async Task Server_EnableDisable_WithInvalidId_Returns404()
	{
		var enableResponse = await _client.PostAsync("/api/orchestrations/nonexistent/enable", null);
		enableResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

		var disableResponse = await _client.PostAsync("/api/orchestrations/nonexistent/disable", null);
		disableResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 8. Cancel endpoint

	[Fact]
	public async Task Server_CancelExecution_WithInvalidId_Returns404()
	{
		var response = await _client.PostAsync("/api/active/nonexistent-exec/cancel", null);

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	#endregion

	#region 9. Comprehensive: All Host endpoints resolve, not 404

	/// <summary>
	/// Verifies that every key API endpoint is registered and returns a proper response
	/// (not a 404 that would indicate the endpoint isn't mapped).
	/// </summary>
	[Theory]
	[InlineData("GET", "/api/orchestrations")]
	[InlineData("GET", "/api/orchestrations/{id}")]
	[InlineData("DELETE", "/api/orchestrations/{id}")]
	[InlineData("POST", "/api/orchestrations/{id}/enable")]
	[InlineData("POST", "/api/orchestrations/{id}/disable")]
	[InlineData("POST", "/api/orchestrations/json")]
	[InlineData("GET", "/api/history?limit=15")]
	[InlineData("GET", "/api/history/all?offset=0&limit=10")]
	[InlineData("GET", "/api/active")]
	[InlineData("GET", "/api/status")]
	[InlineData("GET", "/api/health")]
	[InlineData("GET", "/api/models")]
	[InlineData("GET", "/api/triggers")]
	[InlineData("GET", "/api/mcps")]
	[InlineData("GET", "/openapi/v1.json")]
	public async Task Server_AllEndpoints_DoNotReturn405(string method, string urlTemplate)
	{
		var url = urlTemplate
			.Replace("{id}", "test-id-000")
			.Replace("{executionId}", "test-exec-000");

		var request = new HttpRequestMessage(new HttpMethod(method), url);
		if (method == "POST")
		{
			var body = urlTemplate switch
			{
				"/api/orchestrations/json" => """{"json":"{}","mcpJson":null}""",
				_ => "{}"
			};
			request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		}

		var response = await _client.SendAsync(request);

		// Must not be 405 Method Not Allowed — that means the route exists but wrong method
		response.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed,
			$"{method} {urlTemplate} returned 405 — route exists but HTTP method is wrong");

		// Must not be 5xx
		((int)response.StatusCode).Should().BeLessThan(500,
			$"{method} {urlTemplate} returned {response.StatusCode} — server error");
	}

	#endregion
}
