using FluentAssertions;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Tests for `orchestra step complete|set-status` — the cross-language front door that writes
/// the Script-step control file named by ORCHESTRA_CONTROL_FILE. Pure local file I/O, no server.
/// </summary>
public class StepCommandsTests
{
	private const string EnvVar = "ORCHESTRA_CONTROL_FILE";

	private static CommandAppTester NewTester()
	{
		var tester = new CommandAppTester();
		tester.Configure(Program.Configure);
		return tester;
	}

	private static async Task WithControlFile(Func<string, Task> body)
	{
		var path = Path.Combine(Path.GetTempPath(), $"orchestra-cli-ctrl-{Guid.NewGuid():N}.json");
		var prev = Environment.GetEnvironmentVariable(EnvVar);
		Environment.SetEnvironmentVariable(EnvVar, path);
		try
		{
			await body(path);
		}
		finally
		{
			Environment.SetEnvironmentVariable(EnvVar, prev);
			if (File.Exists(path)) File.Delete(path);
		}
	}

	[Fact]
	public async Task StepComplete_Success_WritesControlJson()
	{
		await WithControlFile(async path =>
		{
			var result = NewTester().Run("step", "complete", "--status", "success", "--reason", "Inbox empty");

			result.ExitCode.Should().Be(0);
			var json = await File.ReadAllTextAsync(path);
			json.Should().Contain("\"action\":\"complete\"")
				.And.Contain("\"status\":\"success\"")
				.And.Contain("Inbox empty");
		});
	}

	[Fact]
	public async Task StepSetStatus_NoAction_WritesControlJson()
	{
		await WithControlFile(async path =>
		{
			var result = NewTester().Run("step", "set-status", "--status", "no_action", "--reason", "Nothing to do");

			result.ExitCode.Should().Be(0);
			var json = await File.ReadAllTextAsync(path);
			json.Should().Contain("\"action\":\"set_status\"")
				.And.Contain("\"status\":\"no_action\"")
				.And.Contain("Nothing to do");
		});
	}

	[Fact]
	public async Task StepSetStatus_HyphenatedStatus_IsNormalized()
	{
		await WithControlFile(async path =>
		{
			var result = NewTester().Run("step", "set-status", "--status", "no-action", "--reason", "x");

			result.ExitCode.Should().Be(0);
			(await File.ReadAllTextAsync(path)).Should().Contain("\"status\":\"no_action\"");
		});
	}

	[Fact]
	public void StepComplete_WithoutControlFileEnv_ExitsOne()
	{
		var prev = Environment.GetEnvironmentVariable(EnvVar);
		Environment.SetEnvironmentVariable(EnvVar, null);
		try
		{
			var result = NewTester().Run("step", "complete", "--status", "success", "--reason", "x");
			result.ExitCode.Should().Be(1);
		}
		finally
		{
			Environment.SetEnvironmentVariable(EnvVar, prev);
		}
	}

	[Fact]
	public void StepComplete_InvalidStatus_FailsValidation()
	{
		var tester = NewTester();

		Action act = () => tester.Run("step", "complete", "--status", "bogus");

		act.Should().Throw<CommandRuntimeException>()
			.Where(ex => ex.Message.Contains("success") && ex.Message.Contains("failed"));
	}

	[Fact]
	public void StepBranchHelp_ListsSubcommands()
	{
		var result = NewTester().Run("step", "--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("complete").And.Contain("set-status");
	}
}
