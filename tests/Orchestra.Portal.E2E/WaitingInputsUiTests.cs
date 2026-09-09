using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace Orchestra.Portal.E2E;

/// <summary>
/// End-to-end tests for the human-in-the-loop "Waiting for Input" UI flow.
///
/// Verifies the full user journey:
///   1. An orchestration with an Approval step is registered + started
///   2. The Portal sidebar surfaces a count badge on the Waiting Inputs button
///   3. Opening the modal lists the pending wait
///   4. Clicking a choice + Submit posts to <c>POST /respond</c>
///   5. The wait disappears from the modal and the run completes
/// </summary>
[Trait("Category", "E2E")]
public class WaitingInputsUiTests : PlaywrightTestBase
{
	public WaitingInputsUiTests(PortalServerFixture server) : base(server)
	{
	}

	[Fact]
	public async Task ApprovalStep_AppearsInWaitingInputs_AndResolvesOnSubmit()
	{
		// ── Arrange: register an approval-only orchestration via the API ─────────
		var (orchestrationId, orchestrationName) = await RegisterApprovalOrchestrationAsync();

		// ── Act 1: kick off a run. We don't need to read the SSE stream — the
		// dashboard SSE will deliver awaiting-input to the Portal.
		using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
		// Fire-and-forget the SSE run endpoint. We don't care about the response —
		// the Approval step persists a PendingInputRecord which is what we test for.
		_ = Task.Run(async () =>
		{
			try
			{
				await httpClient.GetAsync(
					$"{Server.BaseUrl}/api/orchestrations/{Uri.EscapeDataString(orchestrationId)}/run",
					HttpCompletionOption.ResponseHeadersRead);
			}
			catch
			{
				// Connection drop is expected when we cancel.
			}
		});

		// Wait for the Approval step to register a pending wait.
		using var apiClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
		string? runId = null;
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (DateTime.UtcNow < deadline && runId is null)
		{
			await Task.Delay(500);
			var pendingResponse = await apiClient.GetAsync($"{Server.BaseUrl}/api/runs/pending");
			if (!pendingResponse.IsSuccessStatusCode) continue;
			var pendingJson = JsonSerializer.Deserialize<JsonElement>(
				await pendingResponse.Content.ReadAsStringAsync());
			foreach (var record in pendingJson.EnumerateArray())
			{
				var name = record.GetProperty("orchestrationName").GetString();
				if (name == orchestrationName)
				{
					runId = record.GetProperty("runId").GetString();
					break;
				}
			}
		}
		runId.Should().NotBeNull("the Approval step should persist a pending input record within 30s");

		// ── Act 2: navigate to the Portal and open the Waiting Inputs modal ─────
		await NavigateToPortalAsync();
		// Wait for the dashboard SSE to deliver awaiting-input → button shows count badge.
		await Page.WaitForFunctionAsync(
			"() => document.querySelector('.waiting-inputs-badge')?.textContent === '1'",
			null,
			new() { Timeout = 15_000 });

		var sidebarButton = Page.Locator("button", new() { HasTextString = "Waiting for Input" }).First;
		await sidebarButton.ClickAsync();

		// Modal opened: at least one row visible
		await Page.WaitForSelectorAsync(".waiting-inputs-row", new() { Timeout = 5_000 });
		var rows = await Page.Locator(".waiting-inputs-row").CountAsync();
		rows.Should().BeGreaterThan(0);

		// ── Act 3: click "approve" + Submit ────────────────────────────────────
		var approveRadio = Page.GetByLabel("approve");
		await approveRadio.CheckAsync();

		var submitButton = Page.Locator("button", new() { HasTextString = "Submit response" }).First;
		await submitButton.ClickAsync();

		// ── Assert: the wait disappears from the list (badge gone) ─────────────
		await Page.WaitForFunctionAsync(
			"() => !document.querySelector('.waiting-inputs-badge')",
			null,
			new() { Timeout = 10_000 });

		// And the server agrees: GET /api/runs/pending no longer lists the run.
		var afterResponse = await apiClient.GetAsync($"{Server.BaseUrl}/api/runs/pending");
		afterResponse.IsSuccessStatusCode.Should().BeTrue();
		var afterJson = JsonSerializer.Deserialize<JsonElement>(
			await afterResponse.Content.ReadAsStringAsync());
		var stillWaiting = afterJson.EnumerateArray()
			.Any(r => r.GetProperty("runId").GetString() == runId);
		stillWaiting.Should().BeFalse("submitting a response should resolve the wait on the server");
	}

	private async Task<(string Id, string Name)> RegisterApprovalOrchestrationAsync()
	{
		var unique = Guid.NewGuid().ToString("N")[..8];
		var orchestrationName = $"E2E Approval {unique}";
		var orchestrationJson = $$"""
			{
				"name": "{{orchestrationName}}",
				"description": "Approval-only orchestration for HITL E2E test",
				"version": "1.0.0",
				"steps": [
					{
						"name": "review-deploy",
						"type": "Approval",
						"dependsOn": [],
						"prompt": "Approve E2E test deploy?",
						"choices": ["approve", "reject"]
					}
				]
			}
			""";

		using var httpClient = new HttpClient();
		var registerBody = new { Json = orchestrationJson, McpJson = (string?)null };
		var registerResponse = await httpClient.PostAsync(
			$"{Server.BaseUrl}/api/orchestrations/json",
			new StringContent(
				JsonSerializer.Serialize(registerBody),
				System.Text.Encoding.UTF8,
				"application/json"));
		registerResponse.EnsureSuccessStatusCode();
		var result = JsonSerializer.Deserialize<JsonElement>(
			await registerResponse.Content.ReadAsStringAsync());
		var id = result.GetProperty("id").GetString();
		id.Should().NotBeNullOrEmpty();
		return (id!, orchestrationName);
	}
}
