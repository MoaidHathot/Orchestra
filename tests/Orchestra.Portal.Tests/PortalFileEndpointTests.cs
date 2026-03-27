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
/// Integration tests for Portal-specific endpoints:
/// - GET /api/browse - Browse directories
/// - POST /api/folder/scan - Scan a folder for orchestration JSON files
/// - GET /api/file/read - Read file content for preview
/// - POST /api/orchestrations/add - Register orchestrations from file paths
/// - POST /api/orchestrations/add-json - Register orchestration from pasted JSON
/// - POST /api/cancel/{executionId} - Cancel an active execution
/// - POST /api/orchestrations/{id}/toggle - Enable/disable an orchestration trigger
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
		result.GetProperty("mcpPath").ValueKind.Should().Be(JsonValueKind.Null);

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
	public async Task FolderScan_WithMcpJson_DetectsMcpPath()
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

		var expectedMcpPath = Path.Combine(_tempDir, "mcp.json");
		result.GetProperty("mcpPath").GetString().Should().Be(expectedMcpPath);

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

	[Fact]
	public async Task FolderScan_DetectsPerFileMcpJson()
	{
		// Arrange - Create orchestration with a matching per-file mcp.json
		WriteFile("my-workflow.json", CreateValidOrchestrationJson("My Workflow"));
		var perFileMcpContent = """
		{
			"mcps": [
				{
					"name": "test-mcp",
					"type": "local",
					"command": "echo",
					"arguments": ["hello"]
				}
			]
		}
		""";
		WriteFile("my-workflow.mcp.json", perFileMcpContent);

		// Act
		var response = await _client.PostAsJsonAsync("/api/folder/scan",
			new { Directory = _tempDir }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();

		var orchestrations = result.GetProperty("orchestrations").EnumerateArray().ToList();
		// my-workflow.mcp.json will also be parsed (and likely fail/be invalid as an orchestration),
		// but the important thing is that my-workflow.json gets the per-file mcpPath
		var workflow = orchestrations.First(o => o.GetProperty("fileName").GetString() == "my-workflow.json");
		var mcpPath = workflow.GetProperty("mcpPath").GetString();
		mcpPath.Should().NotBeNull();
		mcpPath.Should().Contain("my-workflow.mcp.json");
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

	#region POST /api/orchestrations/add

	[Fact]
	public async Task OrchestrationsAdd_WithValidFilePaths_RegistersOrchestrations()
	{
		// Arrange
		var json = CreateValidOrchestrationJson("Add Test Workflow", "Test adding via file path");
		var filePath = Path.Combine(_tempDir, "add-test.json");
		File.WriteAllText(filePath, json);

		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add",
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
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add",
			new { paths = new[] { fakePath } }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("addedCount").GetInt32().Should().Be(0);
		result.GetProperty("errors").EnumerateArray().Should().NotBeEmpty();
	}

	[Fact]
	public async Task OrchestrationsAdd_AutoDetectsMcpJson()
	{
		// Arrange - Create orchestration and mcp.json in same directory
		var json = CreateValidOrchestrationJson("MCP Auto Detect Test");
		var filePath = Path.Combine(_tempDir, "mcp-auto.json");
		File.WriteAllText(filePath, json);
		File.WriteAllText(Path.Combine(_tempDir, "mcp.json"), """
		{
			"mcps": [{ "name": "test", "type": "local", "command": "echo", "arguments": ["hi"] }]
		}
		""");

		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add",
			new { paths = new[] { filePath } }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("addedCount").GetInt32().Should().Be(1);
	}

	#endregion

	#region Helper: Register orchestration via add-json

	/// <summary>
	/// Registers an orchestration via the /api/orchestrations/add-json endpoint and returns the response.
	/// </summary>
	private async Task<JsonElement> RegisterOrchestrationViaJsonAsync(string json)
	{
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add-json",
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

	#region POST /api/orchestrations/add-json

	[Fact]
	public async Task OrchestrationsAddJson_WithValidJson_RegistersOrchestration()
	{
		// Arrange
		var json = CreateValidOrchestrationJson("Add JSON Test", "Test adding via pasted JSON");

		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add-json",
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
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add-json",
			new { json = "" }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task OrchestrationsAddJson_WithInvalidJson_ReturnsBadRequest()
	{
		// Act
		var response = await _client.PostAsJsonAsync("/api/orchestrations/add-json",
			new { json = "{ invalid json }" }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	#endregion

	#region POST /api/cancel/{executionId}

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
			Status = "Running"
		};
		activeInfos[executionId] = info;

		try
		{
			// Act
			var response = await _client.PostAsync($"/api/cancel/{executionId}", null);

			// Assert
			response.StatusCode.Should().Be(HttpStatusCode.OK);
			var result = await response.Content.ReadFromJsonAsync<JsonElement>();
			result.GetProperty("cancelled").GetBoolean().Should().BeTrue();
			result.GetProperty("executionId").GetString().Should().Be(executionId);
			result.GetProperty("status").GetString().Should().Be("Cancelling");

			// Verify the CancellationToken was actually cancelled
			cts.Token.IsCancellationRequested.Should().BeTrue();

			// Verify the status was updated on the info object
			info.Status.Should().Be("Cancelling");
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
		var response = await _client.PostAsync("/api/cancel/nonexistent-execution-id", null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("error").GetString().Should().Contain("No active execution");
	}

	[Fact]
	public async Task Cancel_AlsoAvailableViaHostRoute()
	{
		// Verify the Host's canonical cancel route still works
		// POST /api/active/{executionId}/cancel
		var response = await _client.PostAsync("/api/active/nonexistent-id/cancel", null);

		// Should return NotFound (not 404 from SPA fallback which would return HTML)
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var content = await response.Content.ReadAsStringAsync();
		content.Should().NotContain("<!DOCTYPE html>", "Cancel endpoint should return JSON, not SPA fallback");
	}

	#endregion

	#region POST /api/orchestrations/{id}/toggle

	[Fact]
	public async Task Toggle_Enable_WithWebhookTrigger_EnablesTrigger()
	{
		// Arrange - Register an orchestration with a disabled webhook trigger
		var orchName = $"Toggle Enable Test {Guid.NewGuid():N}";
		var json = CreateOrchestrationWithTriggerJson(orchName, "Webhook", enabled: false);
		var registered = await RegisterOrchestrationViaJsonAsync(json);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// First, enable via the Host's /enable endpoint to register the trigger
		await _client.PostAsync($"/api/orchestrations/{orchestrationId}/enable", null);
		await Task.Delay(200);

		// Now disable it so we can test toggle
		await _client.PostAsync($"/api/orchestrations/{orchestrationId}/disable", null);
		await Task.Delay(200);

		// Act - Toggle to enabled
		var response = await _client.PostAsJsonAsync(
			$"/api/orchestrations/{orchestrationId}/toggle",
			new { enabled = true }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("id").GetString().Should().Be(orchestrationId);
		result.GetProperty("enabled").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Toggle_Disable_WithWebhookTrigger_DisablesTrigger()
	{
		// Arrange - Register an orchestration with an enabled webhook trigger
		var orchName = $"Toggle Disable Test {Guid.NewGuid():N}";
		var json = CreateOrchestrationWithTriggerJson(orchName, "Webhook", enabled: true);
		var registered = await RegisterOrchestrationViaJsonAsync(json);
		var orchestrationId = registered.GetProperty("id").GetString()!;
		await Task.Delay(200);

		// Act - Toggle to disabled
		var response = await _client.PostAsJsonAsync(
			$"/api/orchestrations/{orchestrationId}/toggle",
			new { enabled = false }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("id").GetString().Should().Be(orchestrationId);
		result.GetProperty("enabled").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public async Task Toggle_WithNonExistentOrchestration_ReturnsNotFound()
	{
		// Act
		var response = await _client.PostAsJsonAsync(
			"/api/orchestrations/nonexistent-id/toggle",
			new { enabled = true }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("error").GetString().Should().Contain("not found");
	}

	[Fact]
	public async Task Toggle_Enable_WithNoTriggerDefined_ReturnsBadRequest()
	{
		// Arrange - Register an orchestration WITHOUT a trigger
		var orchName = $"No Trigger Test {Guid.NewGuid():N}";
		var json = CreateValidOrchestrationJson(orchName, "No trigger orchestration");
		var registered = await RegisterOrchestrationViaJsonAsync(json);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// Act - Try to enable toggle on an orchestration with no trigger
		var response = await _client.PostAsJsonAsync(
			$"/api/orchestrations/{orchestrationId}/toggle",
			new { enabled = true }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("error").GetString().Should().Contain("no trigger");
	}

	[Fact]
	public async Task Toggle_Disable_WithNoTriggerDefined_Succeeds()
	{
		// Arrange - Register an orchestration WITHOUT a trigger
		var orchName = $"No Trigger Disable Test {Guid.NewGuid():N}";
		var json = CreateValidOrchestrationJson(orchName, "No trigger orchestration");
		var registered = await RegisterOrchestrationViaJsonAsync(json);
		var orchestrationId = registered.GetProperty("id").GetString()!;

		// Act - Disabling should succeed (no-op) even without a trigger
		var response = await _client.PostAsJsonAsync(
			$"/api/orchestrations/{orchestrationId}/toggle",
			new { enabled = false }, _jsonOptions);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var result = await response.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("id").GetString().Should().Be(orchestrationId);
		result.GetProperty("enabled").GetBoolean().Should().BeFalse();
	}

	#endregion
}
