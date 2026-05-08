using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace Orchestra.Tool.Tests;

/// <summary>
/// Verifies that the bundled schemas physically ship in the build output of
/// Orchestra.Tool, which is what gets packed into the NuGet tool package.
/// </summary>
public class BundledSchemasTests
{
	private static readonly string[] s_expectedSchemas =
	[
		"orchestration.schema.json",
		"orchestra.mcp.schema.json",
		"orchestra.services.schema.json",
	];

	[Fact]
	public void OrchestraTool_BuildOutput_IncludesAllSchemas()
	{
		var toolBaseDir = LocateOrchestraToolOutputDirectory();
		var schemasDir = Path.Combine(toolBaseDir, "schemas");

		Directory.Exists(schemasDir)
			.Should().BeTrue($"Orchestra.Tool build output must contain a 'schemas' directory at '{schemasDir}'.");

		foreach (var name in s_expectedSchemas)
		{
			var path = Path.Combine(schemasDir, name);
			File.Exists(path).Should().BeTrue($"schema '{name}' must be copied to '{path}'.");
			new FileInfo(path).Length.Should().BeGreaterThan(0);
		}
	}

	[Fact]
	public void OrchestrationSchema_AllowsMultilineInputHint()
	{
		var toolBaseDir = LocateOrchestraToolOutputDirectory();
		var schemaPath = Path.Combine(toolBaseDir, "schemas", "orchestration.schema.json");
		using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));

		var multiline = doc.RootElement
			.GetProperty("$defs")
			.GetProperty("inputDefinition")
			.GetProperty("properties")
			.GetProperty("multiline");

		multiline.GetProperty("type").GetString().Should().Be("boolean");
	}

	[Fact]
	public void OrchestrationSchema_AllowsOrchestrationStepInputHandler()
	{
		var toolBaseDir = LocateOrchestraToolOutputDirectory();
		var schemaPath = Path.Combine(toolBaseDir, "schemas", "orchestration.schema.json");
		using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));

		var stepTypeEnum = doc.RootElement
			.GetProperty("$defs")
			.GetProperty("step")
			.GetProperty("properties")
			.GetProperty("type")
			.GetProperty("enum")
			.EnumerateArray()
			.Select(value => value.GetString())
			.ToArray();

		stepTypeEnum.Should().Contain("Orchestration");

		var orchestrationStepProperties = doc.RootElement
			.GetProperty("$defs")
			.GetProperty("orchestrationStepProperties")
			.GetProperty("properties");

		orchestrationStepProperties.TryGetProperty("inputHandlerPrompt", out _).Should().BeTrue();
		orchestrationStepProperties.TryGetProperty("inputHandlerModel", out _).Should().BeTrue();
	}

	private static string LocateOrchestraToolOutputDirectory()
	{
		// Walk up from the test assembly location to the repo root, then
		// look in src/Orchestra.Tool/bin/<config>/<tfm>/.
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		DirectoryInfo? repoRoot = null;
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "OrchestrationEngine.slnx")))
			{
				repoRoot = current;
				break;
			}
			current = current.Parent;
		}

		repoRoot.Should().NotBeNull("test must be running inside the Orchestra repository");

		var toolBin = Path.Combine(repoRoot!.FullName, "src", "Orchestra.Tool", "bin");
		Directory.Exists(toolBin).Should().BeTrue($"Orchestra.Tool bin directory missing: {toolBin}");

		// Find the most-recently-built TFM directory under any configuration.
		var tfmDirs = Directory.GetDirectories(toolBin, "*", SearchOption.AllDirectories)
			.Where(d => Path.GetFileName(d).StartsWith("net", StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(d => new DirectoryInfo(d).LastWriteTimeUtc)
			.ToList();

		tfmDirs.Should().NotBeEmpty("Orchestra.Tool must be built before this test runs");

		return tfmDirs[0];
	}
}
