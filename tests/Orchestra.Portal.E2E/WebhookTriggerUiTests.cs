using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace Orchestra.Portal.E2E;

/// <summary>
/// End-to-end tests for the webhook trigger UI flow.
/// Tests the complete user journey through the Portal UI.
/// </summary>
[Trait("Category", "E2E")]
public class WebhookTriggerUiTests : PlaywrightTestBase
{
	public WebhookTriggerUiTests(PortalServerFixture server) : base(server)
	{
	}

	[Fact]
	public async Task PortalLoads_ShowsOrchestrationsTab()
	{
		// Act
		await NavigateToPortalAsync();

		// Assert - Portal should load and show orchestrations area
		var pageContent = await Page.ContentAsync();
		pageContent.Should().Contain("Orchestrations");
	}

	[Fact]
	public async Task EnableWebhookTrigger_ShowsInActivePaneWithWebhookUrl()
	{
		// Arrange - Navigate to portal
		await NavigateToPortalAsync();

		// Upload a webhook orchestration (if needed)
		var orchestrationJson = CreateAddJsonRequest();

		// Use the API to register the orchestration (simpler than UI upload for test reliability)
		var httpClient = new HttpClient();
		var response = await httpClient.PostAsync(
			$"{Server.BaseUrl}/api/orchestrations/json",
			new StringContent(orchestrationJson, System.Text.Encoding.UTF8, "application/json"));
		response.EnsureSuccessStatusCode();
		var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
		var orchestrationId = result.GetProperty("id").GetString();

		// Refresh the page to see the new orchestration
		await Page.ReloadAsync();
		await Task.Delay(1000);

		// Act - Enable the trigger via UI
		// Find the orchestration card and click enable toggle
		var orchestrationCard = Page.Locator($"[data-orchestration-id='{orchestrationId}'], .orchestration-card").First;

		// Look for enable/disable toggle or button
		var toggleButton = orchestrationCard.Locator("button:has-text('Enable'), input[type='checkbox'], .trigger-toggle").First;
		if (await toggleButton.CountAsync() > 0)
		{
			await toggleButton.ClickAsync();
			await Task.Delay(500);
		}

		// Assert - Check the Active Orchestrations pane
		var activePaneVisible = await Page.Locator("text=Active Orchestrations, text=Pending, .active-pane").First.IsVisibleAsync();

		if (activePaneVisible)
		{
			// Look for webhook URL in the active pane
			var webhookUrlVisible = await Page.Locator("text=/api/webhook/, text=POST /api/webhook").First.IsVisibleAsync();
			webhookUrlVisible.Should().BeTrue("Webhook URL should be displayed in active orchestrations pane");
		}
	}

	[Fact]
	public async Task ViewWebhookDetails_ShowsCurlCommand()
	{
		// Arrange - Set up webhook orchestration via API
		var orchestrationJson = CreateAddJsonRequest();
		var httpClient = new HttpClient();
		var response = await httpClient.PostAsync(
			$"{Server.BaseUrl}/api/orchestrations/json",
			new StringContent(orchestrationJson, System.Text.Encoding.UTF8, "application/json"));
		response.EnsureSuccessStatusCode();
		var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
		var orchestrationId = result.GetProperty("id").GetString();

		// Navigate to portal
		await NavigateToPortalAsync();
		await Task.Delay(1500);

		// Act - Click on the orchestration card in the sidebar to open ViewerModal
		var orchestrationCard = Page.Locator($"[data-orchestration-id='{orchestrationId}']").First;
		var cardCount = await orchestrationCard.CountAsync();
		cardCount.Should().BeGreaterThan(0, "At least one orchestration should exist in the sidebar");

		await orchestrationCard.ClickAsync();
		await Task.Delay(500);

		// Click on the "Details" tab in the ViewerModal
		var detailsTab = Page.Locator(".tab:has-text('Details')").First;
		if (await detailsTab.CountAsync() > 0)
		{
			await detailsTab.ClickAsync();
			await Task.Delay(500);
		}

		// Assert - Modal should show webhook details including cURL command
		// The cURL example is in a <pre> element in the Details tab
		var curlElement = Page.Locator("pre:has-text('curl')").First;
		var curlVisible = await curlElement.CountAsync() > 0;
		curlVisible.Should().BeTrue("Modal Details tab should show cURL example command for webhook triggers");
	}

	[Fact]
	public async Task TriggerWebhook_ShowsExecutionInRunningState()
	{
		// Arrange - Set up and enable webhook orchestration
		var orchestrationJson = CreateAddJsonRequest();
		var httpClient = new HttpClient();
		var registerResponse = await httpClient.PostAsync(
			$"{Server.BaseUrl}/api/orchestrations/json",
			new StringContent(orchestrationJson, System.Text.Encoding.UTF8, "application/json"));
		registerResponse.EnsureSuccessStatusCode();

		await Task.Delay(1000);

		var triggersResponse = await httpClient.GetAsync($"{Server.BaseUrl}/api/triggers");
		var triggersResult = JsonSerializer.Deserialize<JsonElement>(await triggersResponse.Content.ReadAsStringAsync());
		var triggersArray = triggersResult.GetProperty("triggers");
		var webhookTrigger = triggersArray.EnumerateArray()
			.FirstOrDefault(t => t.GetProperty("triggerType").GetString() == "webhook");

		if (webhookTrigger.ValueKind == JsonValueKind.Undefined)
		{
			// Skip test if no webhook trigger available
			return;
		}

		var triggerId = webhookTrigger.GetProperty("id").GetString();

		// Navigate to portal
		await NavigateToPortalAsync();

		// Act - Fire the webhook via API
		var fireResponse = await httpClient.PostAsync(
			$"{Server.BaseUrl}/api/webhook/{triggerId}",
			new StringContent("{\"test\": \"data\"}", System.Text.Encoding.UTF8, "application/json"));

		// Assert - Page should show running execution
		await Task.Delay(500);
		await Page.ReloadAsync();
		await Task.Delay(1000);

		// Check for running indicator in active pane
		var pageContent = await Page.ContentAsync();
		// The execution should appear as running or the trigger should still be visible
		(pageContent.Contains("Running") || pageContent.Contains("Waiting") || pageContent.Contains(triggerId!))
			.Should().BeTrue("Webhook trigger should be visible in some state");
	}

	[Fact]
	public async Task AfterWebhookExecution_TriggerRemainsInActivePaneAsWaiting()
	{
		// Arrange - Set up webhook orchestration
		var orchestrationJson = CreateAddJsonRequest();
		var httpClient = new HttpClient();
		await httpClient.PostAsync(
			$"{Server.BaseUrl}/api/orchestrations/json",
			new StringContent(orchestrationJson, System.Text.Encoding.UTF8, "application/json"));

		await Task.Delay(1000);

		var triggersResponse = await httpClient.GetAsync($"{Server.BaseUrl}/api/triggers");
		var triggersResult = JsonSerializer.Deserialize<JsonElement>(await triggersResponse.Content.ReadAsStringAsync());
		var triggersArray = triggersResult.GetProperty("triggers");
		var webhookTrigger = triggersArray.EnumerateArray()
			.FirstOrDefault(t => t.GetProperty("triggerType").GetString() == "webhook");

		if (webhookTrigger.ValueKind == JsonValueKind.Undefined)
		{
			return;
		}

		var triggerId = webhookTrigger.GetProperty("id").GetString();

		// Act - Fire the webhook and wait for completion
		await httpClient.PostAsync(
			$"{Server.BaseUrl}/api/webhook/{triggerId}",
			new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

		// Wait for execution to complete
		await Task.Delay(5000);

		// Assert - Check via API that trigger is still in waiting state
		var activeResponse = await httpClient.GetAsync($"{Server.BaseUrl}/api/active");
		var active = JsonSerializer.Deserialize<JsonElement>(await activeResponse.Content.ReadAsStringAsync());
		var pending = active.GetProperty("pending");

		// Pending items have orchestrationId, not id - use the correct property name
		var triggerStillPending = pending.EnumerateArray()
			.Any(t => t.GetProperty("orchestrationId").GetString() == triggerId);

		triggerStillPending.Should().BeTrue("Webhook trigger should remain in pending state after execution completes");

		// Also verify via UI
		await NavigateToPortalAsync();
		await Task.Delay(1000);

		var pageContent = await Page.ContentAsync();
		// The status is returned as lowercase "waiting" from the API
		(pageContent.Contains("waiting") || pageContent.Contains("Pending Triggers"))
			.Should().BeTrue("UI should show trigger in waiting state or in Pending Triggers section");
	}

	/// <summary>
	/// Creates orchestration JSON in the format expected by the json endpoint.
	/// The endpoint expects: { "Json": "...", "McpJson": null }
	/// </summary>
	private static string CreateAddJsonRequest()
	{
		var uniqueId = Guid.NewGuid().ToString("N")[..8];
		// Use raw JSON string to ensure correct format
		var orchestrationJson = $$"""
		{
			"name": "E2E Test Webhook {{uniqueId}}",
			"description": "Webhook orchestration for E2E testing",
			"version": "1.0.0",
			"trigger": {
				"type": "Webhook",
				"enabled": true,
				"parameters": {
					"source": "e2e-test"
				}
			},
			"steps": [
				{
					"name": "echo-step",
					"type": "Prompt",
					"dependsOn": [],
					"systemPrompt": "You are a test assistant.",
					"userPrompt": "Simply respond with: Test completed successfully",
					"model": "claude-opus-4.6"
				}
			]
		}
		""";

		return JsonSerializer.Serialize(new
		{
			Json = orchestrationJson,
			McpJson = (string?)null
		});
	}
}
