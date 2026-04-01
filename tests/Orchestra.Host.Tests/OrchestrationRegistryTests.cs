using FluentAssertions;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Orchestra.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for OrchestrationRegistry.
/// </summary>
public class OrchestrationRegistryTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _persistPath;
	private readonly string _orchestrationsDir;

	public OrchestrationRegistryTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-registry-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_persistPath = Path.Combine(_tempDir, "registered-orchestrations.json");
		_orchestrationsDir = Path.Combine(_tempDir, "orchestrations");
		Directory.CreateDirectory(_orchestrationsDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private string CreateTestOrchestrationFile(string name, string? version = null)
	{
		var orchestration = new
		{
			name,
			description = $"Test orchestration: {name}",
			version = version ?? "1.0.0",
			model = "claude-opus-4.5",
			steps = new[]
			{
				new
				{
					name = "step1",
					type = "prompt",
					systemPrompt = "You are a test assistant.",
					userPrompt = "Test prompt",
					model = "claude-opus-4.5"
				}
			}
		};

		var path = Path.Combine(_orchestrationsDir, $"{name}.json");
		var json = System.Text.Json.JsonSerializer.Serialize(orchestration, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(path, json);
		return path;
	}

	[Fact]
	public void Constructor_WithDefaultPath_UsesLocalAppData()
	{
		// Act
		var registry = new OrchestrationRegistry();

		// Assert
		registry.Count.Should().Be(0);
	}

	[Fact]
	public void Constructor_WithCustomPath_UsesProvidedPath()
	{
		// Act
		var registry = new OrchestrationRegistry(_persistPath);

		// Assert
		registry.Count.Should().Be(0);
	}

	[Fact]
	public void Register_ValidOrchestration_AddsToRegistry()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);
		var orchPath = CreateTestOrchestrationFile("test-orch-1");

		// Act
		var entry = registry.Register(orchPath, mcpPath: null, persist: false);

		// Assert
		registry.Count.Should().Be(1);
		entry.Id.Should().NotBeNullOrWhiteSpace();
		entry.Path.Should().Be(orchPath);
		entry.Orchestration.Name.Should().Be("test-orch-1");
	}

	[Fact]
	public void Register_WithPersist_SavesToDisk()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);
		var orchPath = CreateTestOrchestrationFile("persist-test");

		// Act
		registry.Register(orchPath, mcpPath: null, persist: true);

		// Assert
		File.Exists(_persistPath).Should().BeTrue();
		var content = File.ReadAllText(_persistPath);
		content.Should().Contain("persist-test");
	}

	[Fact]
	public void Get_ExistingId_ReturnsEntry()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);
		var orchPath = CreateTestOrchestrationFile("get-test");
		var entry = registry.Register(orchPath, mcpPath: null, persist: false);

		// Act
		var retrieved = registry.Get(entry.Id);

		// Assert
		retrieved.Should().NotBeNull();
		retrieved!.Id.Should().Be(entry.Id);
		retrieved.Orchestration.Name.Should().Be("get-test");
	}

	[Fact]
	public void Get_NonExistentId_ReturnsNull()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);

		// Act
		var result = registry.Get("non-existent-id");

		// Assert
		result.Should().BeNull();
	}

	[Fact]
	public void GetAll_ReturnsAllEntries()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);
		var path1 = CreateTestOrchestrationFile("orch-1");
		var path2 = CreateTestOrchestrationFile("orch-2");
		var path3 = CreateTestOrchestrationFile("orch-3");

		registry.Register(path1, mcpPath: null, persist: false);
		registry.Register(path2, mcpPath: null, persist: false);
		registry.Register(path3, mcpPath: null, persist: false);

		// Act
		var all = registry.GetAll().ToList();

		// Assert
		all.Should().HaveCount(3);
		all.Select(e => e.Orchestration.Name).Should().Contain(["orch-1", "orch-2", "orch-3"]);
	}

	[Fact]
	public void Remove_ExistingId_RemovesAndReturnsTrue()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);
		var orchPath = CreateTestOrchestrationFile("remove-test");
		var entry = registry.Register(orchPath, mcpPath: null, persist: false);

		// Act
		var result = registry.Remove(entry.Id);

		// Assert
		result.Should().BeTrue();
		registry.Count.Should().Be(0);
		registry.Get(entry.Id).Should().BeNull();
	}

	[Fact]
	public void Remove_NonExistentId_ReturnsFalse()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);

		// Act
		var result = registry.Remove("non-existent-id");

		// Assert
		result.Should().BeFalse();
	}

	[Fact]
	public void Clear_RemovesAllEntries()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);
		var path1 = CreateTestOrchestrationFile("clear-1");
		var path2 = CreateTestOrchestrationFile("clear-2");

		registry.Register(path1, mcpPath: null, persist: false);
		registry.Register(path2, mcpPath: null, persist: false);

		// Act
		registry.Clear();

		// Assert
		registry.Count.Should().Be(0);
	}

	[Fact]
	public void LoadFromDisk_WithValidFile_LoadsOrchestrations()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("load-test");

		// First registry - save
		var registry1 = new OrchestrationRegistry(_persistPath);
		registry1.Register(orchPath, mcpPath: null, persist: true);

		// Second registry - load
		var registry2 = new OrchestrationRegistry(_persistPath);

		// Act
		var loaded = registry2.LoadFromDisk();

		// Assert
		loaded.Should().Be(1);
		registry2.Count.Should().Be(1);
		registry2.GetAll().First().Orchestration.Name.Should().Be("load-test");
	}

	[Fact]
	public void LoadFromDisk_NoFile_ReturnsZero()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);

		// Act
		var loaded = registry.LoadFromDisk();

		// Assert
		loaded.Should().Be(0);
		registry.Count.Should().Be(0);
	}

	[Fact]
	public void LoadFromDisk_MissingOrchestrationFile_SkipsEntry()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("will-delete");
		var registry1 = new OrchestrationRegistry(_persistPath);
		registry1.Register(orchPath, mcpPath: null, persist: true);

		// Delete the orchestration file
		File.Delete(orchPath);

		var registry2 = new OrchestrationRegistry(_persistPath);

		// Act
		var loaded = registry2.LoadFromDisk();

		// Assert
		loaded.Should().Be(0);
	}

	[Fact]
	public void ScanDirectory_ValidDirectory_RegistersOrchestrations()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);
		CreateTestOrchestrationFile("scan-1");
		CreateTestOrchestrationFile("scan-2");
		CreateTestOrchestrationFile("scan-3");

		// Act
		var loaded = registry.ScanDirectory(_orchestrationsDir);

		// Assert
		loaded.Should().Be(3);
		registry.Count.Should().Be(3);
	}

	[Fact]
	public void ScanDirectory_NonExistentDirectory_ReturnsZero()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath);

		// Act
		var loaded = registry.ScanDirectory(Path.Combine(_tempDir, "non-existent"));

		// Assert
		loaded.Should().Be(0);
	}

	[Fact]
	public void GenerateId_SamePath_ProducesSameId()
	{
		// Arrange
		var path = "/path/to/orchestration.json";
		var name = "my-orchestration";

		// Act
		var id1 = OrchestrationRegistry.GenerateId(name, path);
		var id2 = OrchestrationRegistry.GenerateId(name, path);

		// Assert
		id1.Should().Be(id2);
	}

	[Fact]
	public void GenerateId_DifferentPaths_ProducesDifferentIds()
	{
		// Arrange
		var name = "my-orchestration";

		// Act
		var id1 = OrchestrationRegistry.GenerateId(name, "/path/one/orchestration.json");
		var id2 = OrchestrationRegistry.GenerateId(name, "/path/two/orchestration.json");

		// Assert
		id1.Should().NotBe(id2);
	}

	[Fact]
	public void GenerateId_SanitizesSpecialCharacters()
	{
		// Arrange
		var name = "My Orchestration! With Special@Chars#123";
		var path = "/some/path.json";

		// Act
		var id = OrchestrationRegistry.GenerateId(name, path);

		// Assert
		id.Should().NotContain(" ");
		id.Should().NotContain("!");
		id.Should().NotContain("@");
		id.Should().NotContain("#");
		id.Should().MatchRegex(@"^[a-z0-9\-]+$");
	}

	[Fact]
	public void GenerateId_IsDeterministicAcrossProcessRestarts()
	{
		// This test verifies the ID is based on a deterministic hash (SHA-256),
		// not string.GetHashCode() which is randomized per-process in .NET 6+.
		// The expected value is pre-computed and must remain stable across runs.
		var name = "my-orchestration";
		var path = "/path/to/orchestration.json";

		var id = OrchestrationRegistry.GenerateId(name, path);

		// SHA-256 of the path produces a fixed hash; first 4 hex chars = "1510"
		id.Should().Be("my-orchestration-1510");
	}

	[Fact]
	public void GenerateId_MatchesGenerateTriggerId()
	{
		// OrchestrationRegistry.GenerateId and TriggerManager.GenerateTriggerId
		// must produce the same ID for the same inputs.
		var name = "my-orchestration";
		var path = "/path/to/orchestration.json";

		var registryId = OrchestrationRegistry.GenerateId(name, path);
		var triggerId = Triggers.TriggerManager.GenerateTriggerId(path, name);

		registryId.Should().Be(triggerId);
	}
}
