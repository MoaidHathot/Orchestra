using FluentAssertions;
using Spectre.Console.Testing;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="FirstRunBanner"/>.
/// </summary>
/// <remarks>
/// The banner replaces real help output, so the guard conditions matter more than the wording:
/// it must never fire for a command invocation, and never when stdout is redirected (scripts and
/// test harnesses rely on the full verb list being printed).
/// </remarks>
[Collection(OrchestraEnvironmentCollection.Name)]
public sealed class FirstRunBannerTests
{
	[Fact]
	public void ShouldShow_WithArguments_IsFalse()
		=> FirstRunBanner.ShouldShow(["list"], outputRedirected: false).Should().BeFalse();

	[Fact]
	public void ShouldShow_WithHelpFlag_IsFalse()
		=> FirstRunBanner.ShouldShow(["--help"], outputRedirected: false).Should().BeFalse();

	[Fact]
	public void ShouldShow_WhenOutputIsRedirected_IsFalse()
	{
		// Piping `orchestra` must keep producing the parseable command list.
		FirstRunBanner.ShouldShow([], outputRedirected: true).Should().BeFalse();
	}

	[Fact]
	public void ShouldShow_WithExistingConfiguration_IsFalse()
	{
		var temp = Path.Combine(Path.GetTempPath(), $"orchestra-banner-{Guid.NewGuid():N}");
		Directory.CreateDirectory(temp);
		var configPath = Path.Combine(temp, "orchestra.json");
		File.WriteAllText(configPath, "{}");

		var saved = Environment.GetEnvironmentVariable("ORCHESTRA_CONFIG_PATH");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);
		try
		{
			FirstRunBanner.ShouldShow([], outputRedirected: false)
				.Should().BeFalse("a user with configuration is not on their first run");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", saved);
			try { Directory.Delete(temp, recursive: true); } catch { /* best-effort */ }
		}
	}

	[Fact]
	public void Write_NamesInitAndDoctor()
	{
		// AnsiConsole holds its own writer and ignores Console.SetOut, so capture via a
		// TestConsole rather than redirecting stdout.
		var console = new TestConsole();

		FirstRunBanner.Write("1.2.3", console);

		console.Output.Should().Contain("init");
		console.Output.Should().Contain("doctor");
		console.Output.Should().Contain("1.2.3");
	}
}
