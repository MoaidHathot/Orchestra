using FluentAssertions;
using Orchestra.Copilot;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests for <see cref="CopilotPreflight"/>'s installed-version probe, which exists because
/// doctor used to label whatever binary it found with the version Orchestra <em>pins</em> --
/// wrong for an explicit path, and wrong for a cached binary that self-updated in place.
/// </summary>
public class CopilotPreflightTests
{
	[Theory]
	[InlineData("GitHub Copilot CLI 1.0.85.\r\nRun 'copilot update' to check for updates.\r\n", "1.0.85")]
	[InlineData("GitHub Copilot CLI 1.0.84-5.", "1.0.84-5")]
	[InlineData("1.0.0-beta-2", "1.0.0-beta-2")]
	[InlineData("v2.10.3", "2.10.3")]
	[InlineData("copilot 0.0.405", "0.0.405")]
	public void ParseCliVersion_ExtractsTheVersionToken(string banner, string expected)
	{
		CopilotPreflight.ParseCliVersion(banner).Should().Be(expected);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("GitHub Copilot CLI")]
	[InlineData("error: unknown option --version")]
	public void ParseCliVersion_ReturnsNull_WhenNothingLooksLikeAVersion(string? banner)
	{
		CopilotPreflight.ParseCliVersion(banner).Should().BeNull();
	}

	[Fact]
	public async Task TryGetInstalledVersionAsync_MissingBinary_ReturnsNull()
	{
		var result = await CopilotPreflight.TryGetInstalledVersionAsync(
			Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}", "copilot.exe"));

		result.Should().BeNull();
	}

	[Fact]
	public async Task TryGetInstalledVersionAsync_RunsTheBinaryAndParsesItsBanner()
	{
		// A stand-in for copilot.exe that prints the real CLI's banner shape. Exercises the
		// actual process launch + stdout capture rather than just the regex.
		var dir = Path.Combine(Path.GetTempPath(), $"orchestra-preflight-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		try
		{
			var fakeCli = WriteFakeCli(dir, "GitHub Copilot CLI 9.8.7.");

			var result = await CopilotPreflight.TryGetInstalledVersionAsync(fakeCli, TimeSpan.FromSeconds(30));

			result.Should().Be("9.8.7");
		}
		finally
		{
			try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
		}
	}

	[Fact]
	public async Task TryGetInstalledVersionAsync_BinaryThatHangs_TimesOutToNull()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"orchestra-preflight-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		try
		{
			var fakeCli = WriteHangingFakeCli(dir);

			var result = await CopilotPreflight.TryGetInstalledVersionAsync(fakeCli, TimeSpan.FromSeconds(2));

			result.Should().BeNull("a diagnostic probe must never wedge doctor on a broken binary");
		}
		finally
		{
			try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
		}
	}

	private static string WriteFakeCli(string dir, string banner)
	{
		if (OperatingSystem.IsWindows())
		{
			// CreateProcess runs .cmd files through cmd.exe implicitly, which is exactly what
			// Process.Start(UseShellExecute=false) relies on here.
			var path = Path.Combine(dir, "copilot.cmd");
			File.WriteAllText(path, $"@echo off\r\necho {banner}\r\n");
			return path;
		}

		var script = Path.Combine(dir, "copilot");
		File.WriteAllText(script, $"#!/bin/sh\necho '{banner}'\n");
		File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return script;
	}

	private static string WriteHangingFakeCli(string dir)
	{
		if (OperatingSystem.IsWindows())
		{
			var path = Path.Combine(dir, "copilot.cmd");
			File.WriteAllText(path, "@echo off\r\nping -n 30 127.0.0.1 > nul\r\n");
			return path;
		}

		var script = Path.Combine(dir, "copilot");
		File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
		File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return script;
	}
}
