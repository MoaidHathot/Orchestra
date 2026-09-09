using FluentAssertions;
using Orchestra.Cli.Commands;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Tests for <c>orchestra validate</c>.
/// </summary>
/// <remarks>
/// The exit codes are the contract that matters: a Script step inside a generator
/// orchestration branches on them to decide whether a generated file may be written, so
/// 0/1/2 must stay stable.
/// </remarks>
public sealed class ValidateCommandTests : IDisposable
{
	private readonly string _tempDir;

	public ValidateCommandTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-validate-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best-effort cleanup */ }
	}

	private static CommandAppTester NewTester()
	{
		var tester = new CommandAppTester();
		tester.Configure(Program.Configure);
		return tester;
	}

	private string WriteFile(string name, string content)
	{
		var path = Path.Combine(_tempDir, name);
		File.WriteAllText(path, content);
		return path;
	}

	private const string ValidOrchestration = """
		name: sample
		description: A valid orchestration.
		defaultModel: claude-opus-4.8
		inputs:
		  topic:
		    type: string
		    required: false
		    default: things
		steps:
		  - name: first
		    type: Transform
		    template: "topic is {{param.topic}}"
		  - name: second
		    type: Transform
		    dependsOn: [first]
		    template: "{{first.output}}"
		""";

	[Fact]
	public void Validate_ValidFile_ExitsZero()
	{
		var path = WriteFile("valid.yaml", ValidOrchestration);

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("valid");
	}

	[Fact]
	public void Validate_MissingFile_ExitsTwo()
	{
		// Distinct from "invalid" so a caller can tell a typo'd path from a bad orchestration.
		var result = NewTester().Run("validate", Path.Combine(_tempDir, "nope.yaml"));

		result.ExitCode.Should().Be(ValidateCommand.FileErrorExitCode);
	}

	[Fact]
	public void Validate_UnparseableFile_ExitsOne()
	{
		var path = WriteFile("broken.yaml", "name: x\nthis is not: [valid");

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(1);
	}

	[Fact]
	public void Validate_MissingRequiredFields_ExitsOne()
	{
		var path = WriteFile("incomplete.yaml", "name: only-a-name\n");

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(1);
	}

	[Fact]
	public void Validate_UnknownStepReference_ExitsOneAndNamesTheStep()
	{
		var path = WriteFile("bad-ref.yaml", """
			name: bad-ref
			description: References a step that does not exist.
			steps:
			  - name: only
			    type: Transform
			    template: "{{ghost.output}}"
			""");

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(1);
		result.Output.Should().Contain("only");
	}

	[Fact]
	public void Validate_BareParameterExpression_IsRejected()
	{
		// {{topic}} without the param. prefix is legacy syntax the resolver never supported;
		// it would reach the model as a literal. This is the check that found ten broken
		// example orchestrations in the repo.
		var path = WriteFile("bare.yaml", """
			name: bare
			description: Uses a bare parameter expression.
			steps:
			  - name: only
			    type: Transform
			    parameters: [topic]
			    template: "{{topic}}"
			""");

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(1);
	}

	[Fact]
	public void Validate_EscapedLiteralExpression_IsAccepted()
	{
		// Documentation shown to an LLM must be escapable, otherwise a generator template
		// cannot describe Orchestra syntax without failing its own validation.
		var path = WriteFile("escaped.yaml", """
			name: escaped
			description: Shows template syntax to a model without resolving it.
			steps:
			  - name: only
			    type: Transform
			    template: "use \\{{param.name}} for parameters"
			""");

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(0);
	}

	[Fact]
	public void Validate_JsonFormat_EmitsMachineReadableResult()
	{
		var path = WriteFile("valid-json-out.yaml", ValidOrchestration);

		var result = NewTester().Run("validate", path, "--format", "json");

		result.ExitCode.Should().Be(0);
		using var doc = System.Text.Json.JsonDocument.Parse(result.Output);
		doc.RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
		doc.RootElement.GetProperty("name").GetString().Should().Be("sample");
		doc.RootElement.GetProperty("stepCount").GetInt32().Should().Be(2);
	}

	[Fact]
	public void Validate_JsonFormat_ListsErrorsWhenInvalid()
	{
		var path = WriteFile("invalid-json-out.yaml", """
			name: bad
			description: Bad reference.
			steps:
			  - name: only
			    type: Transform
			    template: "{{ghost.output}}"
			""");

		var result = NewTester().Run("validate", path, "--format", "json");

		result.ExitCode.Should().Be(1);
		using var doc = System.Text.Json.JsonDocument.Parse(result.Output);
		doc.RootElement.GetProperty("valid").GetBoolean().Should().BeFalse();
		doc.RootElement.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
	}

	[Fact]
	public void Validate_InvalidFormat_FailsValidation()
	{
		Action act = () => NewTester().Run("validate", "x.yaml", "--format", "yaml");

		act.Should().Throw<CommandRuntimeException>()
			.Where(ex => ex.Message.Contains("text") && ex.Message.Contains("json"));
	}
}
