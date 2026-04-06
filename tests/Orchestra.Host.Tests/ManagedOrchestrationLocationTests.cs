using FluentAssertions;
using Orchestra.Host.Registry;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for managed orchestration file location (Work Item 5).
/// Verifies that orchestration files are copied to the managed directory
/// on registration, and that RegisterFromJson works correctly.
/// </summary>
public class ManagedOrchestrationLocationTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _persistPath;
	private readonly string _orchestrationsDir;
	private readonly string _dataPath;

	public ManagedOrchestrationLocationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-managed-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_persistPath = Path.Combine(_tempDir, "registered-orchestrations.json");
		_orchestrationsDir = Path.Combine(_tempDir, "source-orchestrations");
		_dataPath = Path.Combine(_tempDir, "data");
		Directory.CreateDirectory(_orchestrationsDir);
		Directory.CreateDirectory(_dataPath);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private OrchestrationRegistry CreateRegistry(string? dataPath = null)
	{
		return new OrchestrationRegistry(
			persistPath: _persistPath,
			dataPath: dataPath ?? _dataPath);
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
		var json = System.Text.Json.JsonSerializer.Serialize(orchestration,
			new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(path, json);
		return path;
	}

	private static string GetTestOrchestrationJson(string name, string? version = null)
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

		return System.Text.Json.JsonSerializer.Serialize(orchestration,
			new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
	}

	// ── Managed copy on registration ──

	[Fact]
	public void Register_WithDataPath_CopiesFileToManagedLocation()
	{
		// Arrange
		var registry = CreateRegistry();
		var orchPath = CreateTestOrchestrationFile("copy-test");

		// Act
		var entry = registry.Register(orchPath, persist: false);

		// Assert — entry path should point to managed location
		var managedDir = Path.Combine(_dataPath, "orchestrations");
		entry.Path.Should().StartWith(managedDir);
		File.Exists(entry.Path).Should().BeTrue();
		entry.Orchestration.Name.Should().Be("copy-test");
	}

	[Fact]
	public void Register_WithDataPath_ManagedFileContainsSameContent()
	{
		// Arrange
		var registry = CreateRegistry();
		var orchPath = CreateTestOrchestrationFile("content-check");
		var originalContent = File.ReadAllText(orchPath);

		// Act
		var entry = registry.Register(orchPath, persist: false);

		// Assert
		var managedContent = File.ReadAllText(entry.Path);
		managedContent.Should().Be(originalContent);
	}

	[Fact]
	public void Register_WithoutDataPath_StoresOriginalPath()
	{
		// Arrange — no dataPath means no managed directory
		var registry = new OrchestrationRegistry(persistPath: _persistPath);
		var orchPath = CreateTestOrchestrationFile("no-managed");

		// Act
		var entry = registry.Register(orchPath, persist: false);

		// Assert — should keep the original path since there's no managed dir
		entry.Path.Should().Be(orchPath);
	}

	[Fact]
	public void Register_CreatesOrchestrationsSubdirectory()
	{
		// Arrange
		var freshDataPath = Path.Combine(_tempDir, "fresh-data");
		Directory.CreateDirectory(freshDataPath);

		// Act
		var registry = CreateRegistry(freshDataPath);

		// Assert
		Directory.Exists(Path.Combine(freshDataPath, "orchestrations")).Should().BeTrue();
	}

	[Fact]
	public void Register_SurvivesSourceFileDeletion()
	{
		// Arrange
		var registry = CreateRegistry();
		var orchPath = CreateTestOrchestrationFile("delete-source");

		var entry = registry.Register(orchPath, persist: false);

		// Act — delete the original source file
		File.Delete(orchPath);

		// Assert — managed copy should still exist
		File.Exists(entry.Path).Should().BeTrue();
		File.Exists(orchPath).Should().BeFalse();
	}

	// ── RegisterFromJson tests ──

	[Fact]
	public void RegisterFromJson_WithDataPath_WritesToManagedLocation()
	{
		// Arrange
		var registry = CreateRegistry();
		var json = GetTestOrchestrationJson("json-import");

		// Act
		var entry = registry.RegisterFromJson(json, persist: false);

		// Assert
		var managedDir = Path.Combine(_dataPath, "orchestrations");
		entry.Path.Should().StartWith(managedDir);
		File.Exists(entry.Path).Should().BeTrue();
		entry.Orchestration.Name.Should().Be("json-import");
	}

	[Fact]
	public void RegisterFromJson_WithoutDataPath_WritesToTempDir()
	{
		// Arrange — no dataPath
		var registry = new OrchestrationRegistry(persistPath: _persistPath);
		var json = GetTestOrchestrationJson("json-temp");

		// Act
		var entry = registry.RegisterFromJson(json, persist: false);

		// Assert — should use temp directory as fallback
		entry.Path.Should().Contain("orchestra-host");
		File.Exists(entry.Path).Should().BeTrue();
		entry.Orchestration.Name.Should().Be("json-temp");
	}

	[Fact]
	public void RegisterFromJson_PreservesContent()
	{
		// Arrange
		var registry = CreateRegistry();
		var json = GetTestOrchestrationJson("preserve-content");

		// Act
		var entry = registry.RegisterFromJson(json, persist: false);

		// Assert
		var savedContent = File.ReadAllText(entry.Path);
		// The content may have been re-serialized, but the original JSON was written
		savedContent.Should().Contain("preserve-content");
	}

	// ── Persistence round-trip with managed files ──

	[Fact]
	public void RegisterAndReload_WithManagedFiles_Succeeds()
	{
		// Arrange — register an orchestration
		var registry1 = CreateRegistry();
		var orchPath = CreateTestOrchestrationFile("reload-test");
		registry1.Register(orchPath, persist: true);

		// Act — create a new registry and reload from disk
		var registry2 = CreateRegistry();
		var loaded = registry2.LoadFromDisk();

		// Assert
		loaded.Should().Be(1);
		var entry = registry2.GetAll().First();
		entry.Orchestration.Name.Should().Be("reload-test");
		File.Exists(entry.Path).Should().BeTrue();
	}

	[Fact]
	public void RegisterFromJson_AndReload_Succeeds()
	{
		// Arrange
		var registry1 = CreateRegistry();
		var json = GetTestOrchestrationJson("json-reload");
		registry1.RegisterFromJson(json, persist: true);

		// Act
		var registry2 = CreateRegistry();
		var loaded = registry2.LoadFromDisk();

		// Assert
		loaded.Should().Be(1);
		var entry = registry2.GetAll().First();
		entry.Orchestration.Name.Should().Be("json-reload");
	}

	[Fact]
	public void Register_MultipleOrchestrations_EachGetsManagedCopy()
	{
		// Arrange
		var registry = CreateRegistry();
		var path1 = CreateTestOrchestrationFile("orch-alpha");
		var path2 = CreateTestOrchestrationFile("orch-beta");
		var path3 = CreateTestOrchestrationFile("orch-gamma");

		// Act
		var entry1 = registry.Register(path1, persist: false);
		var entry2 = registry.Register(path2, persist: false);
		var entry3 = registry.Register(path3, persist: false);

		// Assert — all should be in managed dir, all different paths
		var managedDir = Path.Combine(_dataPath, "orchestrations");
		entry1.Path.Should().StartWith(managedDir);
		entry2.Path.Should().StartWith(managedDir);
		entry3.Path.Should().StartWith(managedDir);
		entry1.Path.Should().NotBe(entry2.Path);
		entry2.Path.Should().NotBe(entry3.Path);
	}
}
