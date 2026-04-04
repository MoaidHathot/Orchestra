using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

public class CommandStepExecutorTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILogger<CommandStepExecutor> _logger = NullLoggerFactory.Instance.CreateLogger<CommandStepExecutor>();

	private CommandStepExecutor CreateExecutor() => new(_reporter, _logger);

	private static CommandOrchestrationStep CreateCommandStep(
		string name = "cmd-step",
		string command = "dotnet",
		string[]? arguments = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environment = null,
		bool includeStdErr = false,
		string[]? dependsOn = null,
		string[]? parameters = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Command,
		DependsOn = dependsOn ?? [],
		Parameters = parameters ?? [],
		Command = command,
		Arguments = arguments ?? [],
		WorkingDirectory = workingDirectory,
		Environment = environment ?? [],
		IncludeStdErr = includeStdErr,
	};

	#region Success Scenarios

	[Fact]
	public async Task ExecuteAsync_SimpleCommand_ReturnsSuccessWithStdout()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateCommandStep(command: "dotnet", arguments: ["--version"]);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task ExecuteAsync_CommandWithOutput_CapturesStdout()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEchoCommand("Hello Orchestra");
		var step = CreateCommandStep(command: cmd, arguments: args);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("Hello Orchestra");
	}

	#endregion

	#region Failure Scenarios

	[Fact]
	public async Task ExecuteAsync_CommandNotFound_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateCommandStep(command: "nonexistent-binary-xyz-123");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		// The command is routed through the platform shell (cmd.exe /c on Windows,
		// /bin/sh -c on Linux), so the shell itself starts but returns a non-zero
		// exit code when the command is not found.
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("nonexistent-binary-xyz-123");
	}

	[Fact]
	public async Task ExecuteAsync_NonZeroExitCode_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor();
		// Use 'dotnet help nonexistent-command-xyz' which returns non-zero on all platforms
		var step = CreateCommandStep(
			command: "dotnet",
			arguments: ["help", "nonexistent-command-xyz-123"]);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	#endregion

	#region Template Resolution

	[Fact]
	public async Task ExecuteAsync_TemplateResolution_ResolvesParametersInArguments()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEchoCommand("{{param.message}}");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			parameters: ["message"]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["message"] = "resolved-value"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("resolved-value");
	}

	[Fact]
	public async Task ExecuteAsync_DependencyOutput_ResolvesInArguments()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEchoCommand("{{step1.output}}");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			dependsOn: ["step1"]);

		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };
		context.AddResult("step1", ExecutionResult.Succeeded("dep-output"));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("dep-output");
	}

	#endregion

	#region Environment Variables

	[Fact]
	public async Task ExecuteAsync_WithEnvironmentVariables_SetsEnvironment()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEnvPrintCommand("ORCHESTRA_TEST_VAR");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			environment: new Dictionary<string, string>
			{
				["ORCHESTRA_TEST_VAR"] = "env-value-123"
			});
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("env-value-123");
	}

	[Fact]
	public async Task ExecuteAsync_EnvironmentVariableWithTemplate_ResolvesTemplate()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEnvPrintCommand("ORCHESTRA_DYNAMIC");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			environment: new Dictionary<string, string>
			{
				["ORCHESTRA_DYNAMIC"] = "{{param.envValue}}"
			},
			parameters: ["envValue"]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["envValue"] = "dynamic-env-resolved"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("dynamic-env-resolved");
	}

	#endregion

	#region IncludeStdErr

	[Fact]
	public async Task ExecuteAsync_IncludeStdErr_CapturesBothStreams()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetStdoutAndStderrCommand("stdout-data", "stderr-data");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			includeStdErr: true);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("stdout-data");
		result.Content.Should().Contain("stderr-data");
	}

	[Fact]
	public async Task ExecuteAsync_ExcludeStdErr_OnlyCapturesStdout()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetStdoutAndStderrCommand("stdout-only", "hidden-stderr");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			includeStdErr: false);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("stdout-only");
		result.Content.Should().NotContain("hidden-stderr");
	}

	#endregion

	#region Wrong Step Type

	[Fact]
	public async Task ExecuteAsync_WrongStepType_ThrowsInvalidOperationException()
	{
		// Arrange
		var executor = CreateExecutor();
		var wrongStep = new PromptOrchestrationStep
		{
			Name = "wrong-step",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			SystemPrompt = "system",
			UserPrompt = "user",
			Model = "claude-opus-4.5"
		};

		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var act = () => executor.ExecuteAsync(wrongStep, context);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*CommandStepExecutor*PromptOrchestrationStep*CommandOrchestrationStep*");
	}

	#endregion

	#region Cancellation

	[Fact]
	public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceledException()
	{
		// Arrange
		var executor = CreateExecutor();
		// Use a cross-platform command that runs long enough to cancel.
		// The executor wraps commands in the platform shell, so pass raw commands.
		var step = OperatingSystem.IsWindows()
			? CreateCommandStep(command: "ping", arguments: ["-n", "30", "127.0.0.1"])
			: CreateCommandStep(command: "sleep", arguments: ["30"]);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

		// Act
		var act = () => executor.ExecuteAsync(step, context, cts.Token);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	#endregion

	#region StepType Property

	[Fact]
	public void StepType_ReturnsCommand()
	{
		// Arrange
		var executor = CreateExecutor();

		// Assert
		executor.StepType.Should().Be(OrchestrationStepType.Command);
	}

	#endregion

	#region Cross-platform helpers

	/// <summary>Returns a raw command that echoes the given text to stdout.
	/// The CommandStepExecutor already wraps commands in the platform shell,
	/// so we pass raw commands (not shell-wrapped).</summary>
	private static (string cmd, string[] args) GetEchoCommand(string text) =>
		("echo", [text]);

	/// <summary>Returns a raw shell command string as a single argument.
	/// On Windows, 'exit 1' needs to go through cmd /c, but the executor already does that.
	/// On Linux, we need a command that returns a non-zero exit code.</summary>
	private static (string cmd, string[] args) GetShellCommand(string shellCommand) =>
		OperatingSystem.IsWindows()
			? ("cmd", ["/c", shellCommand])
			: ("sh", ["-c", shellCommand]);

	/// <summary>Returns a command that prints an environment variable.</summary>
	private static (string cmd, string[] args) GetEnvPrintCommand(string envVarName) =>
		OperatingSystem.IsWindows()
			? ("cmd", ["/c", $"echo %{envVarName}%"])
			: ("printenv", [envVarName]);

	/// <summary>Returns a command that writes to both stdout and stderr.</summary>
	private static (string cmd, string[] args) GetStdoutAndStderrCommand(string stdoutText, string stderrText) =>
		OperatingSystem.IsWindows()
			? ("cmd", ["/c", $"echo {stdoutText} && echo {stderrText} 1>&2"])
			: ("sh", ["-c", $"echo {stdoutText} && echo {stderrText} >&2"]);

	#endregion
}
