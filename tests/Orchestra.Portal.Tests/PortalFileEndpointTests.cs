using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Portal.Tests;

/// <summary>
/// Integration tests for Portal-specific and Host endpoints:
/// - GET /api/browse - Browse directories
/// - POST /api/folder/scan - Scan a folder for orchestration JSON files
/// - GET /api/file/read - Read file content for preview
/// - POST /api/orchestrations - Register orchestrations from file paths (Host canonical)
/// - POST /api/orchestrations/json - Register orchestration from pasted JSON (Host canonical)
/// - POST /api/active/{executionId}/cancel - Cancel an active execution (Host canonical)
/// - POST /api/orchestrations/{id}/enable - Enable an orchestration trigger (Host canonical)
/// - POST /api/orchestrations/{id}/disable - Disable an orchestration trigger (Host canonical)
/// Note: GET /api/folder/browse is not tested here because it opens a native Windows dialog.
/// </summary>
public class PortalFileEndpointTests : IClassFixture<PortalWebApplicationFactory>, IDisposable
{
	private readonly PortalWebApplicationFactory _factory;
	private readonly HttpClient _client;
	private readonly string _tempDir;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true
	};

	public PortalFileEndpointTests(PortalWebApplicationFactory factory)
	{
		_factory = factory;
		_client = factory.CreateClient();
		_tempDir = Path.Combine(Path.GetTempPath(), "Orchestra.Portal.FileTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		_client.Dispose();
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* ignore cleanup errors */ }
		}
	}

	#region Helper methods

	private static string CreateValidOrchestrationJson(string name, string description = "Test orchestration")
	{
		return $$"""
		{
			"name": "{{name}}",
			"description": "{{description}}",
			"steps": [
				{
					"name": "test-step",
					"type": "Prompt",
					"dependsOn": [],
					"systemPrompt": "You are a test assistant.",
					"userPrompt": "Say hello",
					"model": "claude-opus-4.5"
				}
			]
		}
		""";
	}

	private static string CreateInvalidJson()
	{
		return "{ this is not valid json }}}";
	}

	private void WriteFile(string relativePath, string content)
	{
		var fullPath = Path.Combine(_tempDir, relativePath);
		var dir = Path.GetDirectoryName(fullPath)!;
		Directory.CreateDirectory(dir);
		File.WriteAllText(fullPath, content);
	}

	#endregion

	#region GET /api/browse

	[Fact]
	public async Task Browse_WithValidDirectory_ReturnsDirectoryEntries()
	{
		// Arrange - Create subdirectories and JSON files
		Directory.CreateDirectory(Path.Combine(_tempDir, "subdir1"));
		Directory.CreateDirectory(Path.Combine(_tempDir, "subdir2"));
		WriteFile("test.json", "{}");
		WriteFile("other.json", "{}");

		// Act
		var response = await _client.GetAsync($"/api/browse?Directory={Uri.EscapeDataString(_tempDir)}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();

		result.GetProperty("currentDirectory").GetString().Should().Be(_tempDir);

		var entries = result.GetProperty("entries").EnumerateArray().ToList();
		// Should include parent (..), 2 subdirs, and 2 JSON files
		entries.Should().HaveCountGreaterThanOrEqualTo(4);

		// Verify parent entry
		var parentEntry = entries.First(e => e.GetProperty("name").GetString() == "..");
		parentEntry.GetProperty("isDirectory").GetBoolean().Should().BeTrue();
		parentEntry.GetProperty("isParent").GetBoolean().Should().BeTrue();

		// Verify subdirectories
		var subdirs = entries.Where(e =>
			e.GetProperty("isDirectory").GetBoolean() &&
			!e.GetProperty("isParent").GetBoolean()).ToList();
		subdirs.Should().HaveCount(2);
		subdirs.Select(e => e.GetProperty("name").GetString()).Should().Contain("subdir1").And.Contain("subdir2");

		// Verify JSON files
		var files = entries.Where(e => !e.GetProperty("isDirectory").GetBoolean()).ToList();
		files.Should().HaveCount(2);
		files.Select(e => e.GetProperty("name").GetString()).Should().Contain("test.json").And.Contain("other.json");
	}

	[Fact]
	public async Task Browse_WithNonExistentDirectory_ReturnsBadRequest()
	{
		// Act
		var nonExistent = Path.Combine(_tempDir, "does-not-exist");
		var response = await _client.GetAsync($"/api/browse?Directory={Uri.EscapeDataString(nonExistent)}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("error").GetString().Should().Contain("not found");
	}

	[Fact]
	public async Task Browse_WithNoDirectory_DefaultsToUserProfile()
	{
		// Act
		var response = await _client.GetAsync("/api/browse");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var currentDir = result.GetProperty("currentDirectory").GetString();
		currentDir.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
	}

	[Fact]
	public async Task Browse_OnlyShowsJsonFiles_NotOtherFiles()
	{
		// Arrange - Create both JSON and non-JSON files
		WriteFile("orchestration.json", "{}");
		WriteFile("readme.txt", "hello");
		WriteFile("script.py", "print('hi')");

		// Act
		var response = await _client.GetAsync($"/api/browse?Directory={Uri.EscapeDataString(_tempDir)}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		var entries = result.GetProperty("entries").EnumerateArray().ToList();

		var files = entries.Where(e => !e.GetProperty("isDirectory").GetBoolean()).ToList();
		files.Should().HaveCount(1);
		files[0].GetProperty("name").GetString().Should().Be("orchestration.json");
	}

	#endregion

	#region POST /api/folder/scan

	[Fact]
	public async Task FolderScan_WithValidOrchestrations_ReturnsOrchestrationMetadata()
	{
		// Arrange
		WriteFile("workflow1.json", CreateValidOrchestrationJson("Workflow One", "First workflow"));
		WriteFile("workflow2.json", CreateValidOrchestrationJson("Workflow Two", "Second workflow"));

		// Act
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { Directory = _tempDir }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();

		result.GetProperty("directory").GetString().Should().Be(_tempDir);
		result.GetProperty("count").GetInt32().Should().Be(2);

		var orchestrations = result.GetProperty("orchestrations").EnumerateArray().ToList();
		orchestrations.Should().HaveCount(2);

		// Verify first orchestration metadata
		var orch1 = orchestrations.First(o => o.GetProperty("name").GetString() == "Workflow One");
		orch1.GetProperty("valid").GetBoolean().Should().BeTrue();
		orch1.GetProperty("description").GetString().Should().Be("First workflow");
		orch1.GetProperty("stepCount").GetInt32().Should().Be(1);
		orch1.GetProperty("steps").EnumerateArray().First().GetString().Should().Be("test-step");
		orch1.GetProperty("fileName").GetString().Should().Be("workflow1.json");
		orch1.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
	}

	[Fact]
	public async Task FolderScan_WithMcpJson_ExcludesMcpJsonFromOrchestrations()
	{
		// Arrange
		WriteFile("workflow.json", CreateValidOrchestrationJson("Test Workflow"));
		WriteFile("mcp.json", """
		{
			"mcps": [
				{
					"name": "filesystem",
					"type": "local",
					"command": "npx",
					"arguments": ["-y", "@anthropic/mcp-server-filesystem"]
				}
			]
		}
		""");

		// Act
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { Directory = _tempDir }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();

		// mcp.json should NOT appear in the orchestrations list
		var orchestrations = result.GetProperty("orchestrations").EnumerateArray().ToList();
		orchestrations.Should().HaveCount(1);
		orchestrations[0].GetProperty("fileName").GetString().Should().Be("workflow.json");
	}

	[Fact]
	public async Task FolderScan_WithInvalidJson_ReturnsErrorForThatFile()
	{
		// Arrange
		WriteFile("valid.json", CreateValidOrchestrationJson("Valid Orchestration"));
		WriteFile("invalid.json", CreateInvalidJson());

		// Act
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { Directory = _tempDir }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("count").GetInt32().Should().Be(2);

		var orchestrations = result.GetProperty("orchestrations").EnumerateArray().ToList();

		var valid = orchestrations.First(o => o.GetProperty("fileName").GetString() == "valid.json");
		valid.GetProperty("valid").GetBoolean().Should().BeTrue();
		valid.GetProperty("name").GetString().Should().Be("Valid Orchestration");

		var invalid = orchestrations.First(o => o.GetProperty("fileName").GetString() == "invalid.json");
		invalid.GetProperty("valid").GetBoolean().Should().BeFalse();
		invalid.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task FolderScan_WithEmptyDirectory_ReturnsEmptyList()
	{
		// Act
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { Directory = _tempDir }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("count").GetInt32().Should().Be(0);
		result.GetProperty("orchestrations").EnumerateArray().Should().BeEmpty();
	}

	[Fact]
	public async Task FolderScan_WithNonExistentDirectory_ReturnsBadRequest()
	{
		// Act
		var nonExistent = Path.Combine(_tempDir, "does-not-exist");
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { Directory = nonExistent }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("error").GetString().Should().Contain("not found");
	}

	[Fact]
	public async Task FolderScan_WithNullDirectory_ReturnsBadRequest()
	{
		// Act
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { Directory = (string?)null }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("error").GetString().Should().Contain("required");
	}

	[Fact]
	public async Task FolderScan_IgnoresSubdirectoryFiles()
	{
		// Arrange - Only top-level JSON files should be scanned
		WriteFile("top-level.json", CreateValidOrchestrationJson("Top Level"));
		WriteFile("subdir/nested.json", CreateValidOrchestrationJson("Nested"));

		// Act
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { Directory = _tempDir }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("count").GetInt32().Should().Be(1);
		var orchestrations = result.GetProperty("orchestrations").EnumerateArray().ToList();
		orchestrations[0].GetProperty("fileName").GetString().Should().Be("top-level.json");
	}

	#endregion

	#region GET /api/file/read

	[Fact]
	public async Task FileRead_WithValidJsonFile_ReturnsFileContent()
	{
		// Arrange
		var content = CreateValidOrchestrationJson("Read Test");
		var filePath = Path.Combine(_tempDir, "readable.json");
		File.WriteAllText(filePath, content);

		// Act
		var response = await _client.GetAsync($"/api/file/read?path={Uri.EscapeDataString(filePath)}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		var responseContent = await response.Content.ReadAsStringAsync();
		responseContent.Should().Contain("Read Test");
		responseContent.Should().Contain("test-step");
	}

	[Fact]
	public async Task FileRead_WithNonExistentFile_ReturnsNotFound()
	{
		// Act
		var nonExistent = Path.Combine(_tempDir, "does-not-exist.json");
		var response = await _client.GetAsync($"/api/file/read?path={Uri.EscapeDataString(nonExistent)}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("error").GetString().Should().Contain("not found");
	}

	[Fact]
	public async Task FileRead_WithEmptyPath_ReturnsBadRequest()
	{
		// Act
		var response = await _client.GetAsync("/api/file/read?path=");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("error").GetString().Should().Contain("required");
	}

	[Fact]
	public async Task FileRead_PreservesExactFileContent()
	{
		// Arrange - Write a file with specific formatting
		var content = """
		{
		    "name": "Formatted Test",
		    "description": "Tests that formatting is preserved",
		    "steps": []
		}
		""";
		var filePath = Path.Combine(_tempDir, "formatted.json");
		File.WriteAllText(filePath, content);

		// Act
		var response = await _client.GetAsync($"/api/file/read?path={Uri.EscapeDataString(filePath)}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var responseContent = await response.Content.ReadAsStringAsync();
		responseContent.Should().Be(content);
	}

	#endregion

	#region POST /api/orchestrations (add from file paths)

	[Fact]
	public async Task OrchestrationsAdd_WithValidFilePaths_RegistersOrchestrations()
	{
		// Arrange
		var json = CreateValidOrchestrationJson("Add Test Workflow", "Test adding via file path");
		var filePath = Path.Combine(_tempDir, "add-test.json");
		File.WriteAllText(filePath, json);

		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations",
			new { paths = new[] { filePath } }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("addedCount").GetInt32().Should().Be(1);

		var added = result.GetProperty("added").EnumerateArray().ToList();
		added.Should().HaveCount(1);
		added[0].GetProperty("name").GetString().Should().Be("Add Test Workflow");
		added[0].GetProperty("id").GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task OrchestrationsAdd_WithNonExistentFile_ReturnsError()
	{
		// Arrange
		var fakePath = Path.Combine(_tempDir, "does-not-exist.json");

		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations",
			new { paths = new[] { fakePath } }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("addedCount").GetInt32().Should().Be(0);
		result.GetProperty("errors").EnumerateArray().Should().NotBeEmpty();
	}

	[Fact]
	public async Task OrchestrationsAdd_RegistersOrchestrationIgnoringMcpJson()
	{
		// Arrange - Create orchestration and mcp.json in same directory
		// mcp.json should be ignored (global MCPs are managed by McpManager)
		var json = CreateValidOrchestrationJson("MCP Auto Detect Test");
		var filePath = Path.Combine(_tempDir, "mcp-auto.json");
		File.WriteAllText(filePath, json);
		File.WriteAllText(Path.Combine(_tempDir, "mcp.json"), """
		{
			"mcps": [{ "name": "test", "type": "local", "command": "echo", "arguments": ["hi"] }]
		}
		""");

		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations",
			new { paths = new[] { filePath } }, _jsonOptions);

		// Assert - orchestration should still be registered successfully
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("addedCount").GetInt32().Should().Be(1);
	}

	#endregion

	#region Helper: Register orchestration via JSON endpoint

	/// <summary>
	/// Registers an orchestration via the /api/orchestrations/json endpoint and returns the response.
	/// </summary>
	private async Task<JsonElement> RegisterOrchestrationViaJsonAsync(string json)
	{
		var response = await _client.PostAsJsonAsync("/api/orchestrations/json",
			new { json }, _jsonOptions);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static string CreateOrchestrationWithTriggerJson(string name, string triggerType, bool enabled)
	{
		return $$"""
		{
			"name": "{{name}}",
			"description": "Trigger test orchestration",
			"version": "1.0.0",
			"trigger": {
				"type": "{{triggerType}}",
				"enabled": {{enabled.ToString().ToLowerInvariant()}}
			},
			"steps": [
				{
					"name": "test-step",
					"type": "Prompt",
					"dependsOn": [],
					"systemPrompt": "You are a test assistant.",
					"userPrompt": "Say hello",
					"model": "claude-opus-4.5"
				}
			]
		}
		""";
	}

	#endregion

	#region POST /api/orchestrations/json

	[Fact]
	public async Task OrchestrationsAddJson_WithValidJson_RegistersOrchestration()
	{
		// Arrange
		var json = CreateValidOrchestrationJson("Add JSON Test", "Test adding via pasted JSON");

		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations/json",
			new { json }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("name").GetString().Should().Be("Add JSON Test");
		result.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
		result.GetProperty("path").GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task OrchestrationsAddJson_WithEmptyJson_ReturnsBadRequest()
	{
		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations/json",
			new { json = "" }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task OrchestrationsAddJson_WithInvalidJson_ReturnsBadRequest()
	{
		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations/json",
			new { json = "{ invalid json }" }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	#endregion

	#region POST /api/active/{executionId}/cancel

	[Fact]
	public async Task Cancel_WithActiveExecution_CancelsAndReturnsSuccess()
	{
		// Arrange - Insert a fake active execution into the DI-registered dictionary
		var executionId = $"test-exec-{Guid.NewGuid():N}";
		var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var activeInfos = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();

		var info = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "test-orch-id",
			OrchestrationName = "Test Cancel Orchestration",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Running
		};
		activeInfos[executionId] = info;

		try
		{
			// Act
			var response = await _client.PostAsync($"/api/active/{executionId}/cancel", null);

			// Assert
			response.StatusCode.Should().Be(HttpStatusCode.OK);
			var result = await response.Content.ReadFromJsonAsync<JsonElement>();
			result.GetProperty("cancelled").GetBoolean().Should().BeTrue();
			result.GetProperty("executionId").GetString().Should().Be(executionId);
			result.GetProperty("status").GetString().Should().Be("Cancelling");

			// Verify the CancellationToken was actually cancelled
			cts.Token.IsCancellationRequested.Should().BeTrue();

			// Verify the status was updated on the info object
			info.Status.Should().Be(HostExecutionStatus.Cancelling);
		}
		finally
		{
			// Cleanup
			activeInfos.TryRemove(executionId, out _);
			reporter.Dispose();
			cts.Dispose();
		}
	}

	[Fact]
	public async Task Cancel_WithNonExistentExecution_ReturnsNotFound()
	{
		// Act
		var response = await _client.PostAsync("/api/active/nonexistent-execution-id/cancel", null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("detail").GetString().Should().Contain("No active execution with ID");
	}

	[Fact]
	public async Task Cancel_DoubleCancelSameExecution_SecondCallStillReturnsOk()
	{
		// Arrange — calling cancel twice on the same execution should work both times
		var executionId = $"test-double-cancel-{Guid.NewGuid():N}";
		var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var activeInfos = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();

		var info = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "test-orch-id",
			OrchestrationName = "Double Cancel Test",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Running
		};
		activeInfos[executionId] = info;

		try
		{
			// Act — cancel twice
			var response1 = await _client.PostAsync($"/api/active/{executionId}/cancel", null);
			var response2 = await _client.PostAsync($"/api/active/{executionId}/cancel", null);

			// Assert — both calls succeed
			response1.StatusCode.Should().Be(HttpStatusCode.OK);
			response2.StatusCode.Should().Be(HttpStatusCode.OK);

			var result1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
			result1.GetProperty("cancelled").GetBoolean().Should().BeTrue();

			var result2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
			result2.GetProperty("cancelled").GetBoolean().Should().BeTrue();

			// Token should be cancelled
			cts.Token.IsCancellationRequested.Should().BeTrue();
			info.Status.Should().Be(HostExecutionStatus.Cancelling);
		}
		finally
		{
			activeInfos.TryRemove(executionId, out _);
			reporter.Dispose();
			cts.Dispose();
		}
	}

	[Fact]
	public async Task Cancel_WithSseReporter_EmitsStatusChangedSseEvent()
	{
		// Arrange — verify that cancellation via the API emits an SSE status-changed event
		var executionId = $"test-sse-cancel-{Guid.NewGuid():N}";
		var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var activeInfos = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();

		// Subscribe to SSE events before cancel
		var (replay, future) = reporter.Subscribe();
		replay.Should().BeEmpty();
		future.Should().NotBeNull();

		var info = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "test-orch-id",
			OrchestrationName = "SSE Cancel Test",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Running
		};
		activeInfos[executionId] = info;

		try
		{
			// Act — cancel via the canonical /api/active/{executionId}/cancel endpoint
			var response = await _client.PostAsync($"/api/active/{executionId}/cancel", null);
			response.StatusCode.Should().Be(HttpStatusCode.OK);

			// Assert — SSE event was emitted
			var evt = await future!.ReadAsync();
			evt.Type.Should().Be("status-changed");
			evt.Data.Should().Contain("Cancelling");
		}
		finally
		{
			activeInfos.TryRemove(executionId, out _);
			reporter.Dispose();
			cts.Dispose();
		}
	}

	#endregion

	#region POST /api/orchestrations/{id}/enable and /api/orchestrations/{id}/disable

	[Fact]
	public async Task Enable_WithWebhookTrigger_EnablesTrigger()
	{
		// Arrange - Register an orchestration with a disabled webhook trigger
		var orchName = $"Enable Test {Guid.NewGuid():N}";
		var json = CreateOrchestrationWithTriggerJson(orchName, "Webhook", enabled: false);
		var registered = await RegisterOrchestrationViaJsonAsync(json);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// Act - Enable the trigger
		var response = await _client.PostAsync(
			$"/api/orchestrations/{orchestrationId}/enable", null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("id").GetString().Should().Be(orchestrationId);
		result.GetProperty("enabled").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Disable_WithWebhookTrigger_DisablesTrigger()
	{
		// Arrange - Register an orchestration with an enabled webhook trigger
		var orchName = $"Disable Test {Guid.NewGuid():N}";
		var json = CreateOrchestrationWithTriggerJson(orchName, "Webhook", enabled: true);
		var registered = await RegisterOrchestrationViaJsonAsync(json);
		var orchestrationId = registered.GetProperty("id").GetString()!;
		await Task.Delay(200);

		// Act - Disable the trigger
		var response = await _client.PostAsync(
			$"/api/orchestrations/{orchestrationId}/disable", null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("id").GetString().Should().Be(orchestrationId);
		result.GetProperty("enabled").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public async Task Enable_WithNonExistentOrchestration_ReturnsNotFound()
	{
		// Act
		var response = await _client.PostAsync(
			"/api/orchestrations/nonexistent-id/enable", null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("detail").GetString().Should().Contain("not found");
	}

	[Fact]
	public async Task Disable_WithNonExistentOrchestration_ReturnsNotFound()
	{
		// Act
		var response = await _client.PostAsync(
			"/api/orchestrations/nonexistent-id/disable", null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("detail").GetString().Should().Contain("not found");
	}

	[Fact]
	public async Task Enable_WithNoTriggerDefined_Succeeds()
	{
		// Arrange - Register an orchestration WITHOUT an explicit trigger (gets ManualTriggerConfig)
		var orchName = $"No Trigger Test {Guid.NewGuid():N}";
		var json = CreateValidOrchestrationJson(orchName, "No trigger orchestration");
		var registered = await RegisterOrchestrationViaJsonAsync(json);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// Act - Enabling should succeed (ManualTriggerConfig is always present)
		var response = await _client.PostAsync(
			$"/api/orchestrations/{orchestrationId}/enable", null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("id").GetString().Should().Be(orchestrationId);
		result.GetProperty("enabled").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Disable_WithNoTriggerDefined_Succeeds()
	{
		// Arrange - Register an orchestration WITHOUT a trigger
		var orchName = $"No Trigger Disable Test {Guid.NewGuid():N}";
		var json = CreateValidOrchestrationJson(orchName, "No trigger orchestration");
		var registered = await RegisterOrchestrationViaJsonAsync(json);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// Act - Disabling should succeed (no-op) even without a trigger
		var response = await _client.PostAsync(
			$"/api/orchestrations/{orchestrationId}/disable", null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("id").GetString().Should().Be(orchestrationId);
		result.GetProperty("enabled").GetBoolean().Should().BeFalse();
	}

	#endregion

	#region GET /api/active - Progress and filtering

	[Fact]
	public async Task Active_RunningExecution_ReturnsProgressFields()
	{
		// Arrange - Insert a fake running execution with progress data
		var executionId = $"test-progress-{Guid.NewGuid():N}";
		var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var activeInfos = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();

		var info = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-progress-test",
			OrchestrationName = "Progress Test Orchestration",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = reporter,
			TotalSteps = 9,
			CompletedSteps = 4,
			CurrentStep = "Analyze Data"
		};
		activeInfos[executionId] = info;

		try
		{
			// Act
			var response = await _client.GetAsync("/api/active");
			response.StatusCode.Should().Be(HttpStatusCode.OK);
			var result = await response.Content.ReadFromJsonAsync<JsonElement>();

			// Assert - Find our execution in the running list
			var running = result.GetProperty("running");
			var found = false;
			foreach (var exec in running.EnumerateArray())
			{
				if (exec.GetProperty("executionId").GetString() == executionId)
				{
					found = true;
					exec.GetProperty("totalSteps").GetInt32().Should().Be(9);
					exec.GetProperty("completedSteps").GetInt32().Should().Be(4);
					exec.GetProperty("currentStep").GetString().Should().Be("Analyze Data");
					break;
				}
			}
			found.Should().BeTrue("the running execution should appear in /api/active with progress fields");
		}
		finally
		{
			activeInfos.TryRemove(executionId, out _);
			reporter.Dispose();
			cts.Dispose();
		}
	}

	[Theory]
	[InlineData(HostExecutionStatus.Completed)]
	[InlineData(HostExecutionStatus.Cancelled)]
	[InlineData(HostExecutionStatus.Failed)]
	public async Task Active_CompletedExecution_IsFilteredOutOfRunningList(HostExecutionStatus terminalStatus)
	{
		// Arrange - Insert a fake execution that has already finished
		var executionId = $"test-filtered-{Guid.NewGuid():N}";
		var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var activeInfos = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();

		var info = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-filter-test",
			OrchestrationName = "Filter Test Orchestration",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "scheduler",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = terminalStatus,
			TotalSteps = 5,
			CompletedSteps = 5
		};
		activeInfos[executionId] = info;

		try
		{
			// Act
			var response = await _client.GetAsync("/api/active");
			response.StatusCode.Should().Be(HttpStatusCode.OK);
			var result = await response.Content.ReadFromJsonAsync<JsonElement>();

			// Assert - The execution should NOT appear in the running list
			var running = result.GetProperty("running");
			foreach (var exec in running.EnumerateArray())
			{
				exec.GetProperty("executionId").GetString().Should()
					.NotBe(executionId, $"execution with status '{terminalStatus}' should be filtered out");
			}
		}
		finally
		{
			activeInfos.TryRemove(executionId, out _);
			reporter.Dispose();
			cts.Dispose();
		}
	}

	[Fact]
	public async Task Active_RunningExecution_AppearsInRunningList()
	{
		// Arrange
		var executionId = $"test-running-{Guid.NewGuid():N}";
		var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var activeInfos = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();

		var info = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-running-test",
			OrchestrationName = "Running Test Orchestration",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "loop",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Running,
			TotalSteps = 3
		};
		activeInfos[executionId] = info;

		try
		{
			// Act
			var response = await _client.GetAsync("/api/active");
			response.StatusCode.Should().Be(HttpStatusCode.OK);
			var result = await response.Content.ReadFromJsonAsync<JsonElement>();

			// Assert - The execution SHOULD appear in the running list
			var running = result.GetProperty("running");
			var found = running.EnumerateArray().Any(exec =>
				exec.GetProperty("executionId").GetString() == executionId);
			found.Should().BeTrue("a running execution should appear in /api/active running list");
		}
		finally
		{
			activeInfos.TryRemove(executionId, out _);
			reporter.Dispose();
			cts.Dispose();
		}
	}

	[Fact]
	public async Task Active_CancellingExecution_StillAppearsInRunningList()
	{
		// Arrange - "Cancelling" is a transient state and should still appear
		var executionId = $"test-cancelling-{Guid.NewGuid():N}";
		var cts = new CancellationTokenSource();
		var reporter = new SseReporter();
		var activeInfos = _factory.Services.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();

		var info = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = "orch-cancelling-test",
			OrchestrationName = "Cancelling Test Orchestration",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Status = HostExecutionStatus.Cancelling,
			TotalSteps = 5,
			CompletedSteps = 2
		};
		activeInfos[executionId] = info;

		try
		{
			// Act
			var response = await _client.GetAsync("/api/active");
			response.StatusCode.Should().Be(HttpStatusCode.OK);
			var result = await response.Content.ReadFromJsonAsync<JsonElement>();

			// Assert - "Cancelling" is still in-flight, should appear
			var running = result.GetProperty("running");
			var found = running.EnumerateArray().Any(exec =>
				exec.GetProperty("executionId").GetString() == executionId);
			found.Should().BeTrue("a cancelling execution should still appear in the running list");
		}
		finally
		{
			activeInfos.TryRemove(executionId, out _);
			reporter.Dispose();
			cts.Dispose();
		}
	}

	#endregion

	#region GET /api/active - Registry fallback for orchestration name

	[Fact]
	public async Task Active_PendingTriggerWithNullOrchestrationName_FallsBackToRegistryName()
	{
		// Arrange - Register an orchestration via the JSON API so it's in the registry
		var orchName = $"Registry Fallback Test {Guid.NewGuid():N}";
		var orchJson = CreateValidOrchestrationJson(orchName);

		var addResponse = await _client.PostAsJsonAsync("/api/orchestrations/json",
			new { json = orchJson, mcpJson = (string?)null }, _jsonOptions);
		addResponse.StatusCode.Should().Be(HttpStatusCode.OK);
		var addResult = await addResponse.Content.ReadFromJsonAsync<JsonElement>();
		var orchId = addResult.GetProperty("id").GetString()!;

		// Get the TriggerManager and register a trigger with the registry ID,
		// then clear OrchestrationName to simulate the bug
		var triggerManager = _factory.Services.GetRequiredService<TriggerManager>();

		triggerManager.RegisterTrigger(
			Path.Combine(_tempDir, "fallback-test.json"),
			new SchedulerTriggerConfig { Type = TriggerType.Scheduler, Enabled = true, IntervalSeconds = 9999 },
			null,
			TriggerSource.Json,
			orchId);
		var reg = triggerManager.GetTrigger(orchId);
		reg.Should().NotBeNull();
		reg!.OrchestrationName = null;

		try
		{
			// Act
			var response = await _client.GetAsync("/api/active");
			response.StatusCode.Should().Be(HttpStatusCode.OK);
			var result = await response.Content.ReadFromJsonAsync<JsonElement>();

			// Assert - Find the pending trigger and verify name comes from registry
			var pending = result.GetProperty("pending");
			var found = false;
			foreach (var item in pending.EnumerateArray())
			{
				if (item.GetProperty("orchestrationId").GetString() == orchId)
				{
					found = true;
					var name = item.GetProperty("orchestrationName").GetString();
					name.Should().Be(orchName,
						"when OrchestrationName is null, it should fall back to registry name");
					break;
				}
			}
			found.Should().BeTrue("the trigger should appear in the pending list");
		}
		finally
		{
			triggerManager.RemoveTrigger(orchId);
		}
	}

	[Fact]
	public async Task Active_PendingTriggerWithNullNameAndMissingRegistry_ReturnsUnknown()
	{
		// Arrange - Register a trigger that is NOT in the orchestration registry
		var triggerManager = _factory.Services.GetRequiredService<TriggerManager>();
		var fakeId = $"nonexistent-{Guid.NewGuid():N}";
		var fakePath = Path.Combine(_tempDir, "does-not-exist.json");

		triggerManager.RegisterTrigger(
			fakePath,
			new SchedulerTriggerConfig { Type = TriggerType.Scheduler, Enabled = true, IntervalSeconds = 9999 },
			null,
			TriggerSource.Json,
			fakeId);

		var trigger = triggerManager.GetTrigger(fakeId);
		trigger.Should().NotBeNull();
		trigger!.OrchestrationName = null;

		try
		{
			// Act
			var response = await _client.GetAsync("/api/active");
			response.StatusCode.Should().Be(HttpStatusCode.OK);
			var result = await response.Content.ReadFromJsonAsync<JsonElement>();

			// Assert - The name should be "Unknown" since both trigger name and registry are null
			var pending = result.GetProperty("pending");
			var found = false;
			foreach (var item in pending.EnumerateArray())
			{
				if (item.GetProperty("orchestrationId").GetString() == fakeId)
				{
					found = true;
					var name = item.GetProperty("orchestrationName").GetString();
					name.Should().Be("Unknown",
						"when both trigger name and registry lookup fail, name should be 'Unknown'");
					break;
				}
			}
			found.Should().BeTrue("the trigger should appear in the pending list");
		}
		finally
		{
			triggerManager.RemoveTrigger(fakeId);
		}
	}

	#endregion
}
