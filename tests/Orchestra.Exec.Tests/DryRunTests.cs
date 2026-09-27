using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestra.Exec.Tests;

/// <summary>
/// <c>orchestra run --run-file ... --dry-run</c>: the executor's pre-flight plus the layered plan,
/// rendered without booting a host or registering anything.
/// </summary>
[Collection("exec-serial")]
public sealed class DryRunTests : IDisposable
{
	private readonly string _tempDir;

	public DryRunTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-dry-run-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best-effort */ }
	}

	private string Write(string name, string yaml)
	{
		var path = Path.Combine(_tempDir, name);
		File.WriteAllText(path, yaml);
		return path;
	}

	private const string Diamond = """
		name: diamond
		description: a -> b, c -> d
		inputs:
		  topic:
		    type: string
		    required: false
		    default: things
		steps:
		  - name: a
		    type: Transform
		    template: "start {{param.topic}}"
		  - name: b
		    type: Transform
		    dependsOn: [a]
		    template: "{{a.output}} b"
		  - name: c
		    type: Transform
		    dependsOn: [a]
		    when: "{{a.status}} == 'succeeded'"
		    template: "{{a.output}} c"
		  - name: d
		    type: Transform
		    dependsOn: [b, c]
		    template: "{{b.output}} {{c.output}}"
		""";

	/// <summary>Runs the dry run with stdout captured (the JSON report goes to Console.Out).</summary>
	private static async Task<(int Exit, string Stdout)> RunAsync(ExecOptions options)
	{
		var original = Console.Out;
		using var writer = new StringWriter();
		Console.SetOut(writer);
		try
		{
			var exit = await ExecProgram.RunCoreAsync(options);
			return (exit, writer.ToString());
		}
		finally
		{
			Console.SetOut(original);
		}
	}

	[Fact]
	public async Task DryRun_ValidFile_ExitsZero_AndPrintsTheLayeredPlanAsJson()
	{
		var path = Write("diamond.yaml", Diamond);

		var (exit, stdout) = await RunAsync(new ExecOptions { RunFile = path, DryRun = true, Report = ReportFormat.Json });

		exit.Should().Be(0, stdout);
		using var doc = JsonDocument.Parse(stdout);
		var root = doc.RootElement;
		root.GetProperty("dryRun").GetBoolean().Should().BeTrue();
		root.GetProperty("valid").GetBoolean().Should().BeTrue();
		root.GetProperty("name").GetString().Should().Be("diamond");
		root.GetProperty("stepCount").GetInt32().Should().Be(4);
		var layers = root.GetProperty("layers").EnumerateArray()
			.Select(l => l.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()).OrderBy(n => n).ToArray())
			.ToArray();
		layers.Should().BeEquivalentTo(new[] { new[] { "a" }, new[] { "b", "c" }, new[] { "d" } }, o => o.WithStrictOrdering());
		root.GetProperty("layers")[1].GetProperty("steps").EnumerateArray().First(s => s.GetProperty("name").GetString() == "c")
			.GetProperty("when").GetString().Should().Be("{{a.status}} == 'succeeded'");
		root.GetProperty("parameters").GetProperty("topic").GetString().Should().Be("things", "input defaults are applied");
		root.GetProperty("errors").GetArrayLength().Should().Be(0);
	}

	[Fact]
	public async Task DryRun_UndeclaredParameter_IsAWarning_NotAFailure()
	{
		var path = Write("diamond.yaml", Diamond);

		var (exit, stdout) = await RunAsync(new ExecOptions
		{
			RunFile = path,
			DryRun = true,
			Report = ReportFormat.Json,
			Parameters = new Dictionary<string, string> { ["topci"] = "x" },
		});

		exit.Should().Be(0);
		using var doc = JsonDocument.Parse(stdout);
		doc.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().ContainSingle(w => w!.Contains("'topci'"));
	}

	[Fact]
	public async Task DryRun_BrokenTemplateReference_ExitsOne_WithTheError()
	{
		var path = Write("broken.yaml", """
			name: broken
			description: refers to a step that does not exist
			steps:
			  - name: a
			    type: Transform
			    template: "{{nope.output}}"
			""");

		var (exit, stdout) = await RunAsync(new ExecOptions { RunFile = path, DryRun = true, Report = ReportFormat.Json });

		exit.Should().Be(ExecProgram.DryRunInvalidExitCode);
		using var doc = JsonDocument.Parse(stdout);
		doc.RootElement.GetProperty("valid").GetBoolean().Should().BeFalse();
		doc.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).Should().Contain(e => e!.Contains("nope"));
	}

	[Fact]
	public async Task DryRun_MissingRequiredInput_ExitsOne()
	{
		var path = Write("needs-owner.yaml", """
			name: needs-owner
			description: d
			inputs:
			  owner:
			    type: string
			    required: true
			steps:
			  - name: a
			    type: Transform
			    template: "{{param.owner}}"
			""");

		var (exit, stdout) = await RunAsync(new ExecOptions { RunFile = path, DryRun = true, Report = ReportFormat.Json });

		exit.Should().Be(ExecProgram.DryRunInvalidExitCode);
		stdout.Should().Contain("owner");
	}

	[Fact]
	public async Task DryRun_MissingFile_IsALaunchError()
	{
		var (exit, _) = await RunAsync(new ExecOptions { RunFile = Path.Combine(_tempDir, "missing.yaml"), DryRun = true });

		exit.Should().Be(ExecProgram.LaunchErrorExitCode);
	}

	[Fact]
	public async Task DryRun_TextOutput_ListsLayersAndConditions()
	{
		var path = Write("diamond.yaml", Diamond);
		var console = new Spectre.Console.Testing.TestConsole();
		var previous = Spectre.Console.AnsiConsole.Console;
		Spectre.Console.AnsiConsole.Console = console;
		try
		{
			var exit = await ExecProgram.RunCoreAsync(new ExecOptions { RunFile = path, DryRun = true });
			exit.Should().Be(0);
		}
		finally
		{
			Spectre.Console.AnsiConsole.Console = previous;
		}

		var output = console.Output;
		output.Should().Contain("plan").And.Contain("diamond (4 steps, 3 layers)");
		output.Should().Contain("layer 1:").And.Contain("layer 3:");
		output.Should().Contain("c (Transform)").And.Contain("when {{a.status}} == 'succeeded'");
		output.Should().Contain("topic = things");
	}
}
