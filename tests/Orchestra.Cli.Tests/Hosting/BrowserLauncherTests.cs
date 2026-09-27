using System.Diagnostics;
using FluentAssertions;
using Orchestra.Cli.Hosting;
using Xunit;

namespace Orchestra.Cli.Tests.Hosting;

/// <summary>
/// <see cref="BrowserLauncher"/> behind <c>orchestra portal --open</c>.
/// </summary>
public class BrowserLauncherTests
{
	[Theory]
	[InlineData("http://0.0.0.0:5000", "http://localhost:5000")]
	[InlineData("http://[::]:5000", "http://localhost:5000")]
	[InlineData("http://+:5000", "http://localhost:5000")]
	[InlineData("http://127.0.0.1:5100", "http://127.0.0.1:5100")]
	public void PickBrowsableAddress_RewritesWildcardsToLocalhost(string bound, string expected)
	{
		BrowserLauncher.PickBrowsableAddress([bound]).Should().Be(expected);
	}

	[Fact]
	public void PickBrowsableAddress_PrefersHttpOverHttps_AndReturnsNullForNone()
	{
		BrowserLauncher.PickBrowsableAddress(["https://localhost:5001", "http://localhost:5000"]).Should().Be("http://localhost:5000");
		BrowserLauncher.PickBrowsableAddress(["https://localhost:5001"]).Should().Be("https://localhost:5001");
		BrowserLauncher.PickBrowsableAddress([]).Should().BeNull();
	}

	[Fact]
	public void TryOpen_UsesThePlatformOpener_WithoutAShell()
	{
		ProcessStartInfo? captured = null;

		var opened = BrowserLauncher.TryOpen("http://localhost:5000", info => captured = info);

		opened.Should().BeTrue();
		captured.Should().NotBeNull();
		captured!.UseShellExecute.Should().BeFalse("no cmd.exe quoting surface");
		captured.ArgumentList.Concat([captured.Arguments]).Should().Contain(a => a.Contains("http://localhost:5000"));
		if (OperatingSystem.IsWindows())
			captured.FileName.Should().Be("explorer.exe");
		else if (OperatingSystem.IsMacOS())
			captured.FileName.Should().Be("open");
		else
			captured.FileName.Should().Be("xdg-open");
	}

	[Theory]
	[InlineData("not a url")]
	[InlineData("file:///etc/passwd")]
	[InlineData("javascript:alert(1)")]
	public void TryOpen_RefusesNonHttpTargets(string url)
	{
		var started = false;

		BrowserLauncher.TryOpen(url, _ => started = true).Should().BeFalse();

		started.Should().BeFalse();
	}
}
