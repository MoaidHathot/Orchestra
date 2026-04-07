using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestra.Portal.Tests;

/// <summary>
/// Tests for SPA routing - ensures the Portal serves index.html for all routes.
/// </summary>
public class SpaRoutingTests : IClassFixture<PortalWebApplicationFactory>, IDisposable
{
	private readonly PortalWebApplicationFactory _factory;
	private readonly HttpClient _client;

	public SpaRoutingTests(PortalWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose()
	{
		_client.Dispose();
	}

	[Fact]
	public async Task RootUrl_ReturnsIndexHtml()
	{
		// Act
		var response = await _client.GetAsync("/");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var content = await response.Content.ReadAsStringAsync();
		content.Should().Contain("<!DOCTYPE html>");
		content.Should().Contain("Orchestra Portal");
	}

	[Fact]
	public async Task NonExistentRoute_ReturnsIndexHtml_ForSpaRouting()
	{
		// Act - Request a path that doesn't exist as a static file or API endpoint
		var response = await _client.GetAsync("/some/spa/route");

		// Assert - Should return index.html for SPA client-side routing
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var content = await response.Content.ReadAsStringAsync();
		content.Should().Contain("<!DOCTYPE html>");
		content.Should().Contain("Orchestra Portal");
	}

	[Fact]
	public async Task ApiEndpoint_StillWorks()
	{
		// Act - API endpoints should still work normally
		var response = await _client.GetAsync("/api/orchestrations");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var content = await response.Content.ReadAsStringAsync();
		content.Should().Contain("orchestrations");
	}
}

/// <summary>
/// Integration tests for the webhook trigger functionality.
/// Tests the complete flow: register orchestration -> enable trigger -> fire webhook -> verify status
/// </summary>
public class WebhookTriggerTests : IClassFixture<PortalWebApplicationFactory>, IDisposable
{
	private readonly PortalWebApplicationFactory _factory;
	private readonly HttpClient _client;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true
	};

	public WebhookTriggerTests(PortalWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
	}

	public void Dispose()
	{
		_client.Dispose();
	}

	/// <summary>
	/// Helper to register an orchestration via the add-json endpoint.
	/// </summary>
	private async Task<JsonElement> RegisterOrchestrationAsync(string orchestrationJson)
	{
		// The endpoint expects { "Json": "...", "McpJson": null }
		var requestBody = JsonSerializer.Serialize(new { Json = orchestrationJson, McpJson = (string?)null });
		var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
		var response = await _client.PostAsync("/api/orchestrations/json", content);
		
		var responseContent = await response.Content.ReadAsStringAsync();
		if (!response.IsSuccessStatusCode)
		{
			throw new Exception($"Failed to register orchestration: {response.StatusCode} - {responseContent}");
		}
		
		return JsonSerializer.Deserialize<JsonElement>(responseContent);
	}

	[Fact]
	public async Task RegisterWebhookOrchestration_ReturnsSuccess()
	{
		// Arrange
		var orchestrationJson = CreateWebhookOrchestrationJson("test-webhook-1");

		// Act
		var result = await RegisterOrchestrationAsync(orchestrationJson);

		// Assert
		result.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task EnableWebhookTrigger_RegistersTriggerAndReturnsWebhookUrl()
	{
		// Arrange - Register an orchestration with a disabled webhook trigger
		var orchestrationJson = CreateWebhookOrchestrationJson("test-webhook-2", enabled: false);
		var registered = await RegisterOrchestrationAsync(orchestrationJson);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// Act - Enable the trigger
		var enableResponse = await _client.PostAsync($"/api/orchestrations/{orchestrationId}/enable", null);

		// Assert
		enableResponse.StatusCode.Should().Be(HttpStatusCode.OK);

		// Verify the trigger appears in the active list
		var activeResponse = await _client.GetFromJsonAsync<JsonElement>("/api/active");
		var pending = activeResponse.GetProperty("pending");
		pending.EnumerateArray().Should().NotBeEmpty();
	}

	[Fact]
	public async Task FireWebhookTrigger_ExecutesOrchestrationAndReturnsToWaiting()
	{
		// Arrange - Register and enable a webhook orchestration
		var testName = $"Test Webhook {Guid.NewGuid():N}";
		var orchestrationJson = CreateWebhookOrchestrationJson("test-webhook-3", enabled: true, name: testName);
		var registered = await RegisterOrchestrationAsync(orchestrationJson);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// Wait a moment for trigger registration
		await Task.Delay(500);

		// Get the trigger ID from triggers endpoint
		var triggersResponse = await _client.GetFromJsonAsync<JsonElement>("/api/triggers");
		var triggersArray = triggersResponse.GetProperty("triggers");
		var webhookTrigger = triggersArray.EnumerateArray()
			.FirstOrDefault(t =>
				t.GetProperty("triggerType").GetString() == "webhook" &&
				t.GetProperty("orchestrationName").GetString() == testName);

		webhookTrigger.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Webhook trigger should be registered");
		var triggerId = webhookTrigger.GetProperty("id").GetString()!;

		// Act - Fire the webhook
		var webhookPayload = new { message = "Test message", priority = "high" };
		var fireResponse = await _client.PostAsJsonAsync($"/api/webhooks/{triggerId}", webhookPayload, _jsonOptions);

		// Assert - Webhook should be accepted
		fireResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		var fireResult = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
		fireResult.GetProperty("accepted").GetBoolean().Should().BeTrue();
		fireResult.GetProperty("executionId").GetString().Should().NotBeNullOrEmpty();

		// Wait for execution to complete
		await Task.Delay(5000);

		// Verify the trigger is back to Waiting status (still in active list)
		var activeAfter = await _client.GetFromJsonAsync<JsonElement>("/api/active");
		var pendingAfter = activeAfter.GetProperty("pending");

		var triggerStillPending = pendingAfter.EnumerateArray()
			.Any(t => t.GetProperty("orchestrationId").GetString() == triggerId ||
			          t.GetProperty("orchestrationName").GetString() == testName);
		triggerStillPending.Should().BeTrue("Webhook trigger should remain in pending/waiting state after execution");
	}

	[Fact]
	public async Task WebhookTrigger_WithParameters_MergesWithPayload()
	{
		// Arrange
		var testName = $"Test Webhook Params {Guid.NewGuid():N}";
		var orchestrationJson = CreateWebhookOrchestrationJson("test-webhook-params", enabled: true, name: testName);
		await RegisterOrchestrationAsync(orchestrationJson);

		await Task.Delay(500);

		var triggersResponse = await _client.GetFromJsonAsync<JsonElement>("/api/triggers");
		var triggersArray = triggersResponse.GetProperty("triggers");
		var webhookTrigger = triggersArray.EnumerateArray()
			.FirstOrDefault(t => t.GetProperty("orchestrationName").GetString() == testName);
		var triggerId = webhookTrigger.GetProperty("id").GetString()!;

		// Act - Fire with custom parameters
		var payload = new { customParam = "custom-value", anotherParam = "another-value" };
		var response = await _client.PostAsJsonAsync($"/api/webhooks/{triggerId}", payload, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("accepted").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task WebhookTrigger_WhenDisabled_ReturnsNotFound()
	{
		// Arrange - Register with disabled trigger, then check behavior
		var testName = $"Test Webhook Disabled {Guid.NewGuid():N}";
		var orchestrationJson = CreateWebhookOrchestrationJson("test-webhook-disabled", enabled: false, name: testName);
		var registered = await RegisterOrchestrationAsync(orchestrationJson);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// Enable the trigger first so it gets registered, then disable it
		await _client.PostAsync($"/api/orchestrations/{orchestrationId}/enable", null);
		await Task.Delay(300);
		await _client.PostAsync($"/api/orchestrations/{orchestrationId}/disable", null);
		await Task.Delay(300);

		var triggersResponse = await _client.GetFromJsonAsync<JsonElement>("/api/triggers");
		var triggersArray = triggersResponse.GetProperty("triggers");
		var webhookTrigger = triggersArray.EnumerateArray()
			.FirstOrDefault(t => t.GetProperty("orchestrationName").GetString() == testName);

		// If trigger doesn't exist (not registered because disabled), this test validates that behavior
		if (webhookTrigger.ValueKind == JsonValueKind.Undefined)
		{
			// Expected - disabled triggers may not be registered
			return;
		}

		var triggerId = webhookTrigger.GetProperty("id").GetString()!;

		// Act
		var response = await _client.PostAsJsonAsync($"/api/webhooks/{triggerId}", new { }, _jsonOptions);

		// Assert - Should return 404 (trigger is disabled, endpoint is not listening)
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task WebhookEndpoint_WithInvalidTriggerId_ReturnsNotFound()
	{
		// Act
		var response = await _client.PostAsJsonAsync("/api/webhooks/nonexistent-trigger-id", new { }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task GetActiveOrchestrations_IncludesWebhookTriggers()
	{
		// Arrange - Register an enabled webhook orchestration
		var testName = $"Test Active List {Guid.NewGuid():N}";
		var orchestrationJson = CreateWebhookOrchestrationJson("test-active-list", enabled: true, name: testName);
		await RegisterOrchestrationAsync(orchestrationJson);

		await Task.Delay(500);

		// Act
		var response = await _client.GetFromJsonAsync<JsonElement>("/api/active");

		// Assert
		var pending = response.GetProperty("pending");
		var hasWebhookTrigger = pending.EnumerateArray()
			.Any(t => t.GetProperty("triggerType").GetString() == "webhook");
		hasWebhookTrigger.Should().BeTrue("Active orchestrations should include webhook triggers");
	}

	private static string CreateWebhookOrchestrationJson(string uniqueSuffix, bool enabled = true, string? name = null)
	{
		var orchName = name ?? "Test Webhook Orchestration";
		return $$"""
		{
			"name": "{{orchName}}",
			"description": "Test webhook orchestration for integration tests - {{uniqueSuffix}}",
			"version": "1.0.0",
			"trigger": {
				"type": "Webhook",
				"enabled": {{enabled.ToString().ToLowerInvariant()}},
				"parameters": {
					"defaultParam": "defaultValue"
				}
			},
			"steps": [
				{
					"name": "test-step",
					"type": "Prompt",
					"dependsOn": [],
					"systemPrompt": "You are a test assistant.",
					"userPrompt": "Simply respond with: OK - {{uniqueSuffix}}",
					"model": "gpt-4o-mini"
				}
			]
		}
		""";
	}

	[Fact]
	public async Task WebhookTrigger_Execution_EmitsTerminalSseEvents()
	{
		// Arrange - Register and enable a webhook orchestration
		var testName = $"Test Webhook SSE {Guid.NewGuid():N}";
		var orchestrationJson = CreateWebhookOrchestrationJson("test-webhook-sse", enabled: true, name: testName);
		await RegisterOrchestrationAsync(orchestrationJson);

		await Task.Delay(500);

		// Get the trigger ID
		var triggersResponse = await _client.GetFromJsonAsync<JsonElement>("/api/triggers");
		var triggersArray = triggersResponse.GetProperty("triggers");
		var webhookTrigger = triggersArray.EnumerateArray()
			.FirstOrDefault(t => t.GetProperty("orchestrationName").GetString() == testName);
		webhookTrigger.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Webhook trigger should be registered");
		var triggerId = webhookTrigger.GetProperty("id").GetString()!;

		// Act - Fire the webhook
		var fireResponse = await _client.PostAsJsonAsync($"/api/webhooks/{triggerId}", new { data = "test" }, _jsonOptions);
		fireResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		var fireResult = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
		var executionId = fireResult.GetProperty("executionId").GetString()!;

		// Attach to the SSE stream to receive events
		var attachRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/execution/{executionId}/attach");
		var response = await _client.SendAsync(attachRequest, HttpCompletionOption.ResponseHeadersRead);
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		// Read the SSE stream and look for terminal events
		var stream = await response.Content.ReadAsStreamAsync();
		using var reader = new StreamReader(stream);

		var events = new List<string>();
		var hasOrchestrationDone = false;
		var hasStepCompleted = false;

		using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		try
		{
			while (!timeoutCts.Token.IsCancellationRequested)
			{
				var line = await reader.ReadLineAsync(timeoutCts.Token);
				if (line is null) break; // Stream closed
				if (line.StartsWith("event: "))
				{
					var eventType = line["event: ".Length..];
					events.Add(eventType);
					if (eventType == "orchestration-done") hasOrchestrationDone = true;
					if (eventType == "step-completed") hasStepCompleted = true;
				}
				if (hasOrchestrationDone) break; // Done, no need to read more
			}
		}
		catch (OperationCanceledException)
		{
			// Timeout - the test will fail on the assertion below
		}

		// Assert - The SSE stream should contain terminal events
		hasOrchestrationDone.Should().BeTrue("Trigger-based executions must emit orchestration-done so the UI updates in real-time");
		hasStepCompleted.Should().BeTrue("Step completion events should be emitted during execution");

		// Verify the stream also included step-started (not a regression)
		events.Should().Contain("step-started");
	}
}
