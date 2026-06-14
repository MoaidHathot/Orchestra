using FluentAssertions;
using Xunit;

namespace Orchestra.Exec.Tests;

/// <summary>
/// Unit tests for <see cref="ExecOptions.Parse"/> — pins the CLI surface (mutually exclusive
/// targets, required target, repeated --param, --help) without booting a host.
/// </summary>
public class ExecOptionsTests
{
	[Fact]
	public void Parse_Help_SetsShowHelp()
	{
		ExecOptions.Parse(["--help"]).ShowHelp.Should().BeTrue();
		ExecOptions.Parse(["-h"]).ShowHelp.Should().BeTrue();
	}

	[Fact]
	public void Parse_NoTarget_IsError()
	{
		var options = ExecOptions.Parse(["--quiet"]);
		options.Error.Should().NotBeNull();
	}

	[Fact]
	public void Parse_BothTargets_IsError()
	{
		var options = ExecOptions.Parse(["--run", "a", "--run-file", "b.json"]);
		options.Error.Should().NotBeNull();
	}

	[Fact]
	public void Parse_RunWithParamsAndFlags_IsParsed()
	{
		var options = ExecOptions.Parse(
			["--run", "demo", "--param", "topic=AI", "--param", "len=short", "-q", "--run-timeout", "30"]);

		options.Error.Should().BeNull();
		options.RunId.Should().Be("demo");
		options.Quiet.Should().BeTrue();
		options.TimeoutSeconds.Should().Be(30);
		options.Parameters.Should().Contain(new KeyValuePair<string, string>("topic", "AI"));
		options.Parameters.Should().Contain(new KeyValuePair<string, string>("len", "short"));
	}

	[Fact]
	public void Parse_MissingValue_IsError()
	{
		ExecOptions.Parse(["--run"]).Error.Should().NotBeNull();
		ExecOptions.Parse(["--run", "x", "--run-timeout", "notanumber"]).Error.Should().NotBeNull();
	}

	[Fact]
	public void Parse_UnknownArgument_IsError()
	{
		ExecOptions.Parse(["--run", "x", "--bogus"]).Error.Should().NotBeNull();
	}
}
