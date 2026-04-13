using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Integration tests for the orchestration auto-sync feature.
/// Verifies that writing orchestration files to the configured scan directory
/// results in them being registered by the server on startup.
/// </summary>
public class OrchestrationSyncIntegrationTests : IDisposable
{
	private readonly string _testDir;
	private readonly string _dataPath;
	private readonly string _scanDir;

	public OrchestrationSyncIntegrationTests()
	{
		_testDir = Path.Combine(Path.GetTempPath(), "Orchestra.SyncTests", Guid.NewGuid().ToString("N"));
		_dataPath = Path.Combine(_testDir, "data");
		_scanDir = Path.Combine(_testDir, "orchestrations");
		Directory.CreateDirectory(_dataPath);
		Directory.CreateDirectory(_scanDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_testDir))
		{
			try { Directory.Delete(_testDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	[Fact]
	public async Task Server_WithScanDirectory_AutoRegistersOrchestrations()
	{
		// Arrange — write orchestration files to the scan directory before starting the server
		File.WriteAllText(Path.Combine(_scanDir, "sync-test-1.json"), """
		{
			"name": "Sync Test Alpha",
			"description": "Auto-synced orchestration",
			"steps": [{
				"name": "step1",
				"type": "prompt",
				"systemPrompt": "Test",
				"userPrompt": "Hello",
				"model": "claude-opus-4.5"
			}]
		}
		""");

		File.WriteAllText(Path.Combine(_scanDir, "sync-test-2.json"), """
		{
			"name": "Sync Test Beta",
			"description": "Another auto-synced orchestration",
			"steps": [{
				"name": "step1",
				"type": "prompt",
				"systemPrompt": "Test",
				"userPrompt": "Hello",
				"model": "claude-opus-4.5"
			}]
		}
		""");

		// Act — start the server with the scan directory configured
		await using var factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(builder =>
			{
				builder.UseEnvironment("Testing");
				builder.ConfigureAppConfiguration((_, config) =>
				{
					config.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["data-path"] = _dataPath,
						["orchestrations-path"] = _scanDir,
					});
				});
			});

		var client = factory.CreateClient();

		// Get all registered orchestrations
		var response = await client.GetAsync("/api/orchestrations");
		response.EnsureSuccessStatusCode();

		var result = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
		var orchestrations = result.GetProperty("orchestrations");

		// Assert
		orchestrations.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);

		var names = orchestrations.EnumerateArray()
			.Select(o => o.GetProperty("name").GetString())
			.ToList();

		names.Should().Contain("Sync Test Alpha");
		names.Should().Contain("Sync Test Beta");
	}

	[Fact]
	public async Task Server_WithScanDirectory_DetectsContentChanges()
	{
		// Arrange — write an orchestration file
		var filePath = Path.Combine(_scanDir, "change-detection.json");
		File.WriteAllText(filePath, """
		{
			"name": "Change Detection Test",
			"description": "Original description",
			"steps": [{
				"name": "step1",
				"type": "prompt",
				"systemPrompt": "Test",
				"userPrompt": "Hello",
				"model": "claude-opus-4.5"
			}]
		}
		""");

		// First server startup — registers the orchestration
		await using var factory1 = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(builder =>
			{
				builder.UseEnvironment("Testing");
				builder.ConfigureAppConfiguration((_, config) =>
				{
					config.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["data-path"] = _dataPath,
						["orchestrations-path"] = _scanDir,
					});
				});
			});

		var client1 = factory1.CreateClient();
		var response1 = await client1.GetAsync("/api/orchestrations");
		var result1 = await response1.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
		var orchestrations1 = result1.GetProperty("orchestrations");
		var entry = orchestrations1.EnumerateArray().First(
			o => o.GetProperty("name").GetString() == "Change Detection Test");
		entry.GetProperty("description").GetString().Should().Be("Original description");

		// Dispose first factory
		await factory1.DisposeAsync();

		// Modify the file
		File.WriteAllText(filePath, """
		{
			"name": "Change Detection Test",
			"description": "Updated description",
			"steps": [{
				"name": "step1",
				"type": "prompt",
				"systemPrompt": "Test",
				"userPrompt": "Hello",
				"model": "claude-opus-4.5"
			}]
		}
		""");

		// Second server startup — should detect the content change
		await using var factory2 = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(builder =>
			{
				builder.UseEnvironment("Testing");
				builder.ConfigureAppConfiguration((_, config) =>
				{
					config.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["data-path"] = _dataPath,
						["orchestrations-path"] = _scanDir,
					});
				});
			});

		var client2 = factory2.CreateClient();
		var response2 = await client2.GetAsync("/api/orchestrations");
		var result2 = await response2.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
		var orchestrations2 = result2.GetProperty("orchestrations");

		// Assert — the description should be updated
		var updatedEntry = orchestrations2.EnumerateArray().First(
			o => o.GetProperty("name").GetString() == "Change Detection Test");
		updatedEntry.GetProperty("description").GetString().Should().Be("Updated description");
	}
}
