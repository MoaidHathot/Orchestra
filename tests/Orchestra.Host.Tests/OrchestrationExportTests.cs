using FluentAssertions;
using Orchestra.Host.Registry;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for orchestration export functionality. Tests the registry-based
/// export behavior of copying managed orchestration files to a target directory.
/// </summary>
public class OrchestrationExportTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _orchestrationsDir;
	private readonly OrchestrationRegistry _registry;

	public OrchestrationExportTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-orchexport-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_orchestrationsDir = Path.Combine(_tempDir, "source-orchestrations");
		Directory.CreateDirectory(_orchestrationsDir);

		var registryPersistPath = Path.Combine(_tempDir, "registered-orchestrations.json");
		_registry = new OrchestrationRegistry(persistPath: registryPersistPath, dataPath: _tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private string CreateOrchestrationFile(string name, string? description = null)
	{
		var json = $$"""
		{
			"name": "{{name}}",
			"description": "{{description ?? $"Test orchestration: {name}"}}",
			"version": "1.0.0",
			"model": "claude-opus-4.5",
			"steps": [
				{
					"name": "step1",
					"type": "prompt",
					"systemPrompt": "Test.",
					"userPrompt": "Test.",
					"model": "claude-opus-4.5"
				}
			]
		}
		""";

		var path = Path.Combine(_orchestrationsDir, $"{name}.json");
		File.WriteAllText(path, json);
		return path;
	}

	private string RegisterOrchestration(string name, string? description = null)
	{
		var path = CreateOrchestrationFile(name, description);
		var entry = _registry.Register(path, null);
		return entry.Id;
	}

	/// <summary>
	/// Replicates the export logic from OrchestrationsApi.cs to test export behavior.
	/// </summary>
	private (List<(string Id, string Name, string Path)> Exported,
			 List<(string Id, string Name, string Reason)> Skipped,
			 List<(string Id, string Name, string Error)> Errors)
		ExportOrchestrations(string directory, string[]? orchestrationIds = null, bool overwriteExisting = false)
	{
		Directory.CreateDirectory(directory);

		var entries = orchestrationIds is { Length: > 0 }
			? _registry.GetAll().Where(e => orchestrationIds.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).ToArray()
			: _registry.GetAll().ToArray();

		var exported = new List<(string Id, string Name, string Path)>();
		var skipped = new List<(string Id, string Name, string Reason)>();
		var errors = new List<(string Id, string Name, string Error)>();

		foreach (var entry in entries)
		{
			var sanitizedName = new string(entry.Orchestration.Name
				.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c)
				.ToArray()).Trim('-');
			var fileName = $"{sanitizedName}.json";
			var filePath = Path.Combine(directory, fileName);

			if (File.Exists(filePath) && !overwriteExisting)
			{
				skipped.Add((entry.Id, entry.Orchestration.Name, "File already exists"));
				continue;
			}

			if (!File.Exists(entry.Path))
			{
				errors.Add((entry.Id, entry.Orchestration.Name, "Orchestration source file not found"));
				continue;
			}

			var json = File.ReadAllText(entry.Path);
			File.WriteAllText(filePath, json);
			exported.Add((entry.Id, entry.Orchestration.Name, filePath));
		}

		return (exported, skipped, errors);
	}

	// ── Export all ──

	[Fact]
	public void ExportAll_ExportsAllRegistered()
	{
		RegisterOrchestration("Orch-Alpha");
		RegisterOrchestration("Orch-Beta");

		var exportDir = Path.Combine(_tempDir, "export-all");
		var (exported, skipped, errors) = ExportOrchestrations(exportDir);

		exported.Should().HaveCount(2);
		skipped.Should().BeEmpty();
		errors.Should().BeEmpty();
		File.Exists(Path.Combine(exportDir, "Orch-Alpha.json")).Should().BeTrue();
		File.Exists(Path.Combine(exportDir, "Orch-Beta.json")).Should().BeTrue();
	}

	// ── Export specific IDs ──

	[Fact]
	public void ExportSpecificIds_ExportsOnlyMatching()
	{
		var id1 = RegisterOrchestration("Export-One");
		RegisterOrchestration("Export-Two");

		var exportDir = Path.Combine(_tempDir, "export-specific");
		var (exported, skipped, errors) = ExportOrchestrations(exportDir, orchestrationIds: [id1]);

		exported.Should().HaveCount(1);
		exported[0].Name.Should().Be("Export-One");
		skipped.Should().BeEmpty();
		errors.Should().BeEmpty();
	}

	// ── Skip existing without overwrite ──

	[Fact]
	public void ExportWithoutOverwrite_SkipsExistingFiles()
	{
		RegisterOrchestration("Already-Exists");

		var exportDir = Path.Combine(_tempDir, "export-skip");
		Directory.CreateDirectory(exportDir);
		File.WriteAllText(Path.Combine(exportDir, "Already-Exists.json"), "original content");

		var (exported, skipped, errors) = ExportOrchestrations(exportDir, overwriteExisting: false);

		exported.Should().BeEmpty();
		skipped.Should().HaveCount(1);
		skipped[0].Reason.Should().Contain("already exists");
		errors.Should().BeEmpty();
		File.ReadAllText(Path.Combine(exportDir, "Already-Exists.json")).Should().Be("original content");
	}

	// ── Overwrite existing ──

	[Fact]
	public void ExportWithOverwrite_OverwritesExistingFiles()
	{
		RegisterOrchestration("Overwrite-Me");

		var exportDir = Path.Combine(_tempDir, "export-overwrite");
		Directory.CreateDirectory(exportDir);
		File.WriteAllText(Path.Combine(exportDir, "Overwrite-Me.json"), "old");

		var (exported, skipped, errors) = ExportOrchestrations(exportDir, overwriteExisting: true);

		exported.Should().HaveCount(1);
		skipped.Should().BeEmpty();
		errors.Should().BeEmpty();
		var content = File.ReadAllText(Path.Combine(exportDir, "Overwrite-Me.json"));
		content.Should().NotBe("old");
		content.Should().Contain("Overwrite-Me");
	}

	// ── Name sanitization ──

	[Fact]
	public void Export_SanitizesFileNames()
	{
		RegisterOrchestration("My Cool Orchestration");

		var exportDir = Path.Combine(_tempDir, "export-sanitize");
		var (exported, _, _) = ExportOrchestrations(exportDir);

		exported.Should().HaveCount(1);
		// Spaces are replaced with dashes
		File.Exists(Path.Combine(exportDir, "My-Cool-Orchestration.json")).Should().BeTrue();
	}

	// ── Creates directory ──

	[Fact]
	public void Export_CreatesDirectoryIfNotExists()
	{
		RegisterOrchestration("Dir-Create");

		var exportDir = Path.Combine(_tempDir, "new-export-dir", "sub");
		Directory.Exists(exportDir).Should().BeFalse();

		ExportOrchestrations(exportDir);

		Directory.Exists(exportDir).Should().BeTrue();
		File.Exists(Path.Combine(exportDir, "Dir-Create.json")).Should().BeTrue();
	}

	// ── Empty registry ──

	[Fact]
	public void Export_EmptyRegistry_ReturnsEmpty()
	{
		var exportDir = Path.Combine(_tempDir, "export-empty");
		var (exported, skipped, errors) = ExportOrchestrations(exportDir);

		exported.Should().BeEmpty();
		skipped.Should().BeEmpty();
		errors.Should().BeEmpty();
	}

	// ── Exported file content matches source ──

	[Fact]
	public void Export_ContentMatchesManagedCopy()
	{
		var id = RegisterOrchestration("Content-Check");

		var exportDir = Path.Combine(_tempDir, "export-content");
		ExportOrchestrations(exportDir);

		var entry = _registry.Get(id)!;
		var sourceContent = File.ReadAllText(entry.Path);
		var exportedContent = File.ReadAllText(Path.Combine(exportDir, "Content-Check.json"));
		exportedContent.Should().Be(sourceContent);
	}
}
