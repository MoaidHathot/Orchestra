using FluentAssertions;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Smoke tests for <see cref="Program.Configure"/>: verifies the CommandApp wiring is
/// internally consistent (no duplicated names, every command registers cleanly, per-command
/// <c>--help</c> works) without hitting a real server.
///
/// We intentionally do NOT exercise commands that would call <see cref="OrchestraClient"/>
/// — those go through the live HTTP path, which we cover in the integration tests using
/// <c>WebApplicationFactory</c>. Here we only assert parser-level behaviours.
/// </summary>
public class ProgramWiringTests
{
	private static CommandAppTester NewTester()
	{
		var tester = new CommandAppTester();
		tester.Configure(Program.Configure);
		return tester;
	}

	[Fact]
	public void RootHelp_LeavesExitCodeZero_AndListsCorePrimitives()
	{
		var tester = NewTester();

		var result = tester.Run("--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("orchestra")
			.And.Contain("list")
			.And.Contain("run")
			.And.Contain("attach")
			.And.Contain("runs")
			.And.Contain("triggers")
			.And.Contain("profiles")
			.And.Contain("tags")
			.And.Contain("pending")
			.And.Contain("respond");
	}

	[Fact]
	public void ListHelp_ShowsTheNewFilterFlags()
	{
		// Pin the new flags so a future "let's rename --tag" doesn't slip through.
		var tester = NewTester();

		var result = tester.Run("list", "--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("--filter");
		result.Output.Should().Contain("--tag");
		result.Output.Should().Contain("--enabled");
		result.Output.Should().Contain("--disabled");
	}

	[Fact]
	public void RunHelp_DocumentsStreamingFlagsAndParam()
	{
		var tester = NewTester();

		var result = tester.Run("run", "--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("--param");
		result.Output.Should().Contain("--no-interactive");
		result.Output.Should().Contain("--quiet");
		result.Output.Should().Contain("--verbose");
		result.Output.Should().Contain("--by");
	}

	[Fact]
	public void RunsBranchHelp_ListsSubcommands()
	{
		var tester = NewTester();

		var result = tester.Run("runs", "--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("list");
		result.Output.Should().Contain("get");
		result.Output.Should().Contain("delete");
	}

	[Theory]
	[InlineData("triggers")]
	[InlineData("profiles")]
	[InlineData("tags")]
	public void BranchHelp_ExitsCleanly(string branch)
	{
		var tester = NewTester();

		var result = tester.Run(branch, "--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("list",
			$"branch '{branch}' must always offer a 'list' subcommand");
	}

	[Fact]
	public void ListAlias_Ls_IsAvailable()
	{
		// The migration introduced the `ls` alias for muscle-memory; lock it in.
		var tester = NewTester();

		var result = tester.Run("ls", "--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("orchestrations");
	}

	[Fact]
	public void UnknownCommand_FailsWithCommandRuntimeException()
	{
		// PropagateExceptions() is set on the configurator so Spectre throws the parse
		// error out of CommandAppTester.Run instead of swallowing it.
		var tester = NewTester();

		Action act = () => tester.Run("definitely-not-a-command");

		act.Should().Throw<CommandParseException>();
	}

	[Fact]
	public void RespondWithoutChoiceOrReply_FailsValidation()
	{
		// Validation lives on RespondSettings.Validate(). Confirm Spectre surfaces it as
		// a CommandRuntimeException with the user-facing message intact.
		var tester = NewTester();

		Action act = () => tester.Run("respond", "orch", "run-1", "step-1");

		act.Should().Throw<CommandRuntimeException>()
			.Where(ex => ex.Message.Contains("--choice") && ex.Message.Contains("--reply"));
	}

	[Fact]
	public void ListWithBothEnabledAndDisabled_FailsValidation()
	{
		// ListSettings.Validate() prevents the contradictory combination.
		var tester = NewTester();

		Action act = () => tester.Run("list", "--enabled", "--disabled");

		act.Should().Throw<CommandRuntimeException>()
			.Where(ex => ex.Message.Contains("--enabled") && ex.Message.Contains("--disabled"));
	}

	[Theory]
	[InlineData("list")]
	[InlineData("get")]
	[InlineData("register")]
	[InlineData("runs", "list")]
	[InlineData("triggers", "list")]
	[InlineData("tags", "list")]
	public void ManagedVerbHelp_AdvertisesModeFlag(params string[] command)
	{
		// The managed Group-A verbs all inherit the connect-or-spawn --mode flag.
		var tester = NewTester();

		var result = tester.Run([.. command, "--help"]);

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("--mode");
	}

	[Fact]
	public void ListWithInvalidMode_FailsValidation()
	{
		// ManagedCommandSettings.Validate() rejects an unknown --mode value.
		var tester = NewTester();

		Action act = () => tester.Run("list", "--mode", "bogus");

		act.Should().Throw<CommandRuntimeException>()
			.Where(ex => ex.Message.Contains("auto, existing, or isolated"));
	}

	[Fact]
	public void LiveVerbHelp_DoesNotAdvertiseModeFlag()
	{
		// Live-runtime verbs (active/cancel/server-status/…) are server-required and must NOT
		// inherit the spawn-capable --mode flag.
		var tester = NewTester();

		var result = tester.Run("server-status", "--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().NotContain("--mode");
	}
}
