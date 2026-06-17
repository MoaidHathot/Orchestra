using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeWorkspaceBuilderTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "oc-ws-" + Guid.NewGuid().ToString("N")[..8]);

	public OpenCodeWorkspaceBuilderTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public void Prepare_WritesConfigFileInArtifactDirectory()
	{
		var artifact = Path.Combine(_root, "artifact");
		Directory.CreateDirectory(artifact);
		var config = new Dictionary<string, object> { ["agent"] = new Dictionary<string, object> { ["x"] = "y" } };

		var ws = OpenCodeWorkspaceBuilder.Prepare(config, stepWorkingDirectory: null, skillDirectories: [], artifactDirectory: artifact);

		ws.ConfigFilePath.Should().NotBeNull();
		Path.GetDirectoryName(ws.ConfigFilePath).Should().Be(artifact);
		var json = JsonDocument.Parse(File.ReadAllText(ws.ConfigFilePath!));
		json.RootElement.GetProperty("agent").GetProperty("x").GetString().Should().Be("y");

		ws.Cleanup(NullLogger.Instance);
		File.Exists(ws.ConfigFilePath).Should().BeFalse();
	}

	[Fact]
	public void Prepare_NoConfig_NoFile()
	{
		var ws = OpenCodeWorkspaceBuilder.Prepare(config: null, stepWorkingDirectory: null, skillDirectories: [], artifactDirectory: _root);
		ws.ConfigFilePath.Should().BeNull();
		ws.WorkingDirectory.Should().BeNull();
	}

	[Fact]
	public void Prepare_SkillDirectoryWithSkillMd_StagesUnderOpencodeSkills()
	{
		// A skill directory that directly contains SKILL.md is staged as one skill folder.
		var skill = Path.Combine(_root, "my-skill");
		Directory.CreateDirectory(skill);
		File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: my-skill\ndescription: test\n---\nbody");
		File.WriteAllText(Path.Combine(skill, "helper.py"), "print('x')");

		var ws = OpenCodeWorkspaceBuilder.Prepare(config: null, stepWorkingDirectory: null, skillDirectories: [skill], artifactDirectory: _root);

		ws.WorkingDirectory.Should().NotBeNull();
		ws.CreatedWorkingDirectory.Should().Be(ws.WorkingDirectory, "no step working dir was given, so a staging cwd was created");
		var staged = Path.Combine(ws.WorkingDirectory!, ".opencode", "skills", "my-skill");
		File.Exists(Path.Combine(staged, "SKILL.md")).Should().BeTrue();
		File.Exists(Path.Combine(staged, "helper.py")).Should().BeTrue("supporting files are copied too");

		ws.Cleanup(NullLogger.Instance);
		Directory.Exists(ws.CreatedWorkingDirectory).Should().BeFalse();
	}

	[Fact]
	public void Prepare_SkillDirectoryOfSubfolders_StagesEachSkill()
	{
		// A directory holding multiple skill subfolders stages each one.
		var skillsDir = Path.Combine(_root, "skills");
		foreach (var name in new[] { "alpha", "beta" })
		{
			var d = Path.Combine(skillsDir, name);
			Directory.CreateDirectory(d);
			File.WriteAllText(Path.Combine(d, "SKILL.md"), $"---\nname: {name}\ndescription: d\n---");
		}
		var workdir = Path.Combine(_root, "project");
		Directory.CreateDirectory(workdir);

		var ws = OpenCodeWorkspaceBuilder.Prepare(config: null, stepWorkingDirectory: workdir, skillDirectories: [skillsDir], artifactDirectory: _root);

		ws.WorkingDirectory.Should().Be(workdir);
		ws.CreatedWorkingDirectory.Should().BeNull("the step provided its own working directory");
		File.Exists(Path.Combine(workdir, ".opencode", "skills", "alpha", "SKILL.md")).Should().BeTrue();
		File.Exists(Path.Combine(workdir, ".opencode", "skills", "beta", "SKILL.md")).Should().BeTrue();

		ws.Cleanup(NullLogger.Instance);
		Directory.Exists(Path.Combine(workdir, ".opencode", "skills", "alpha")).Should().BeFalse("staged skills are cleaned up");
	}
}
