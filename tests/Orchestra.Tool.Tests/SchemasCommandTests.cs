using FluentAssertions;
using Orchestra.Tool;
using Xunit;

namespace Orchestra.Tool.Tests;

public class SchemasCommandTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _bundledSchemasDir;
	private readonly string _workingDir;

	private static readonly string[] s_expectedSchemas =
	[
		"orchestration.schema.json",
		"orchestra.mcp.schema.json",
		"orchestra.services.schema.json",
	];

	public SchemasCommandTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), "orchestra-schemas-tests-" + Guid.NewGuid().ToString("N"));
		_bundledSchemasDir = Path.Combine(_tempRoot, "bundled");
		_workingDir = Path.Combine(_tempRoot, "work");

		Directory.CreateDirectory(_bundledSchemasDir);
		Directory.CreateDirectory(_workingDir);

		foreach (var name in s_expectedSchemas)
			File.WriteAllText(Path.Combine(_bundledSchemasDir, name), $"{{ \"name\": \"{name}\" }}");
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempRoot, recursive: true); }
		catch { /* best-effort cleanup */ }
	}

	[Fact]
	public void Execute_WithDefaults_WritesAllSchemasToDefaultDirectory()
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();

		var exitCode = SchemasCommand.Execute(
			Array.Empty<string>(),
			stdout,
			stderr,
			_bundledSchemasDir,
			_workingDir);

		exitCode.Should().Be(0, stderr.ToString());

		var defaultDir = Path.Combine(_workingDir, ".orchestra", "schemas");
		Directory.Exists(defaultDir).Should().BeTrue();
		foreach (var name in s_expectedSchemas)
			File.Exists(Path.Combine(defaultDir, name)).Should().BeTrue($"because {name} should be copied");

		stdout.ToString().Should().Contain("Done. 3 written, 0 skipped.");
	}

	[Fact]
	public void Execute_WithOutputFlag_WritesToCustomDirectory()
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var customRelative = "custom/path";

		var exitCode = SchemasCommand.Execute(
			["--output", customRelative],
			stdout,
			stderr,
			_bundledSchemasDir,
			_workingDir);

		exitCode.Should().Be(0, stderr.ToString());

		var resolved = Path.Combine(_workingDir, "custom", "path");
		foreach (var name in s_expectedSchemas)
			File.Exists(Path.Combine(resolved, name)).Should().BeTrue();
	}

	[Fact]
	public void Execute_WithExistingFile_SkipsByDefault()
	{
		var defaultDir = Path.Combine(_workingDir, ".orchestra", "schemas");
		Directory.CreateDirectory(defaultDir);
		var existingPath = Path.Combine(defaultDir, "orchestration.schema.json");
		File.WriteAllText(existingPath, "EXISTING_CONTENT");

		var stdout = new StringWriter();
		var stderr = new StringWriter();

		var exitCode = SchemasCommand.Execute(
			Array.Empty<string>(),
			stdout,
			stderr,
			_bundledSchemasDir,
			_workingDir);

		exitCode.Should().Be(0, stderr.ToString());
		File.ReadAllText(existingPath).Should().Be("EXISTING_CONTENT");
		stdout.ToString().Should().Contain("Skipped (already exists)");
		stdout.ToString().Should().Contain("Done. 2 written, 1 skipped.");
	}

	[Fact]
	public void Execute_WithForceFlag_OverwritesExistingFiles()
	{
		var defaultDir = Path.Combine(_workingDir, ".orchestra", "schemas");
		Directory.CreateDirectory(defaultDir);
		var existingPath = Path.Combine(defaultDir, "orchestration.schema.json");
		File.WriteAllText(existingPath, "EXISTING_CONTENT");

		var stdout = new StringWriter();
		var stderr = new StringWriter();

		var exitCode = SchemasCommand.Execute(
			["--force"],
			stdout,
			stderr,
			_bundledSchemasDir,
			_workingDir);

		exitCode.Should().Be(0, stderr.ToString());
		File.ReadAllText(existingPath).Should().NotBe("EXISTING_CONTENT");
		File.ReadAllText(existingPath).Should().Contain("orchestration.schema.json");
		stdout.ToString().Should().Contain("Done. 3 written, 0 skipped.");
	}

	[Fact]
	public void Execute_WithMissingBundledDirectory_ReturnsError()
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var nonExistent = Path.Combine(_tempRoot, "does-not-exist");

		var exitCode = SchemasCommand.Execute(
			Array.Empty<string>(),
			stdout,
			stderr,
			nonExistent,
			_workingDir);

		exitCode.Should().Be(1);
		stderr.ToString().Should().Contain("bundled schemas directory not found");
	}

	[Fact]
	public void Execute_WithUnknownArgument_ReturnsUsageError()
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();

		var exitCode = SchemasCommand.Execute(
			["--bogus"],
			stdout,
			stderr,
			_bundledSchemasDir,
			_workingDir);

		exitCode.Should().Be(2);
		stderr.ToString().Should().Contain("unknown argument");
	}

	[Fact]
	public void Execute_WithOutputMissingValue_ReturnsUsageError()
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();

		var exitCode = SchemasCommand.Execute(
			["--output"],
			stdout,
			stderr,
			_bundledSchemasDir,
			_workingDir);

		exitCode.Should().Be(2);
		stderr.ToString().Should().Contain("--output requires");
	}

	[Fact]
	public void Execute_WithHelpFlag_PrintsUsageAndExitsZero()
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();

		var exitCode = SchemasCommand.Execute(
			["--help"],
			stdout,
			stderr,
			_bundledSchemasDir,
			_workingDir);

		exitCode.Should().Be(0);
		stdout.ToString().Should().Contain("Usage: orchestra schemas");
	}

	[Fact]
	public void Execute_WithAbsoluteOutputPath_WritesToThatPath()
	{
		var absoluteTarget = Path.Combine(_tempRoot, "absolute-target");

		var stdout = new StringWriter();
		var stderr = new StringWriter();

		var exitCode = SchemasCommand.Execute(
			["--output", absoluteTarget],
			stdout,
			stderr,
			_bundledSchemasDir,
			_workingDir);

		exitCode.Should().Be(0, stderr.ToString());
		foreach (var name in s_expectedSchemas)
			File.Exists(Path.Combine(absoluteTarget, name)).Should().BeTrue();
	}
}
