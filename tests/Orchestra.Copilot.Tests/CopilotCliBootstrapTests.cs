using System.Reflection;
using System.Runtime.InteropServices;
using FluentAssertions;
using Orchestra.Copilot;
using Xunit;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Unit tests for <see cref="CopilotCliBootstrap"/> -- the lazy first-run downloader that
/// replaced the SDK's build-time auto-bundle for dotnet-tool distribution.
///
/// We exercise the pure (no-I/O) parts directly. The download path itself is integration-
/// only territory (depends on the public npm registry) and is exercised end-to-end by
/// `dotnet pack` smoke tests and by the user's first `orchestra run` against a registry
/// that lacks a bundled binary.
/// </summary>
public class CopilotCliBootstrapTests
{
	[Fact]
	public void CopilotCliVersion_Matches_GitHub_Copilot_SDK_Props()
	{
		// The bootstrap hardcodes the CLI version to download. The SDK ships the same
		// version in its build .props ($(CopilotCliVersion)). If we drift, the bootstrap
		// downloads a binary that doesn't match what the SDK was built against -- subtle
		// protocol-version mismatches at runtime.
		//
		// Find the SDK's .props file by walking up from our test assembly to the repo
		// root and querying the user's nuget cache. This is brittle but deterministic
		// for in-repo CI builds; tolerate the file not existing on packed test scenarios.
		var sdkPropsPath = TryLocateSdkPropsFile();
		if (sdkPropsPath is null)
		{
			// In environments where the SDK package isn't cached (rare; CI restores it
			// before tests run), skip the cross-check rather than fail spuriously.
			return;
		}

		var sdkVersion = ExtractCopilotCliVersion(sdkPropsPath);
		sdkVersion.Should().NotBeNullOrWhiteSpace("the SDK .props must define $(CopilotCliVersion)");
		CopilotCliBootstrap.CopilotCliVersion.Should().Be(sdkVersion,
			"the bootstrap's hardcoded CLI version must match what the SDK is built against. " +
			"After bumping GitHub.Copilot.SDK in Directory.Packages.props, also bump " +
			"CopilotCliBootstrap.CopilotCliVersion to the new $(CopilotCliVersion) value.");
	}

	[Fact]
	public void ResolveHostPlatform_ReturnsCurrentOsAndArch()
	{
		// We can't pin a concrete RID since the test runs cross-platform, but we can
		// verify the shape and consistency of the resolver.
		var resolve = typeof(CopilotCliBootstrap).GetMethod("ResolveHostPlatform",
			BindingFlags.NonPublic | BindingFlags.Static);
		resolve.Should().NotBeNull();

		var result = ((string Rid, string NpmPlatform, string BinaryName))resolve!.Invoke(null, null)!;

		result.Rid.Should().MatchRegex("^(win|linux|osx)-(x64|arm64)$",
			"RID must be one of the six supported (OS, arch) pairs");
		result.NpmPlatform.Should().MatchRegex("^(win32|linux|darwin)-(x64|arm64)$");
		result.BinaryName.Should().BeOneOf("copilot", "copilot.exe");

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			result.Rid.Should().StartWith("win-");
			result.NpmPlatform.Should().StartWith("win32-");
			result.BinaryName.Should().Be("copilot.exe");
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			result.Rid.Should().StartWith("osx-");
			result.NpmPlatform.Should().StartWith("darwin-");
			result.BinaryName.Should().Be("copilot");
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			result.Rid.Should().StartWith("linux-");
			result.NpmPlatform.Should().StartWith("linux-");
			result.BinaryName.Should().Be("copilot");
		}
	}

	[Fact]
	public void GetCacheDir_ContainsRidAndVersion()
	{
		// The cache path must include both the RID and the version so two different
		// CLI versions (or two different RIDs from the same multi-arch dev machine)
		// don't clobber each other on disk.
		var get = typeof(CopilotCliBootstrap).GetMethod("GetCacheDir",
			BindingFlags.NonPublic | BindingFlags.Static);
		get.Should().NotBeNull();

		var dir = (string)get!.Invoke(null, new object[] { "linux-x64", "9.9.9-test" })!;

		dir.Should().Contain("Orchestra");
		dir.Should().Contain("copilot-cli");
		dir.Should().Contain("9.9.9-test");
		dir.Should().Contain("linux-x64");
	}

	[Fact]
	public async Task EnsureAsync_RespectsExplicitCliPathOverride()
	{
		// The override env var must short-circuit the entire bootstrap so users with a
		// pre-installed `npm i -g @github/copilot` can point at it without any download.
		const string sentinelPath = "/some/preinstalled/copilot";
		var prior = Environment.GetEnvironmentVariable(CopilotCliBootstrap.ExplicitCliPathEnvVar);
		try
		{
			Environment.SetEnvironmentVariable(CopilotCliBootstrap.ExplicitCliPathEnvVar, sentinelPath);

			var result = await CopilotCliBootstrap.EnsureAsync();

			result.Should().Be(sentinelPath,
				"the override must be returned verbatim, even though the path doesn't actually exist on disk -- validation is the SDK's job");
		}
		finally
		{
			Environment.SetEnvironmentVariable(CopilotCliBootstrap.ExplicitCliPathEnvVar, prior);
		}
	}

	[Fact]
	public async Task EnsureAsync_OverrideTrimsWhitespace()
	{
		// Defensive: shells sometimes leak trailing whitespace into env vars.
		const string padded = "   /padded/copilot.exe   ";
		var prior = Environment.GetEnvironmentVariable(CopilotCliBootstrap.ExplicitCliPathEnvVar);
		try
		{
			Environment.SetEnvironmentVariable(CopilotCliBootstrap.ExplicitCliPathEnvVar, padded);

			var result = await CopilotCliBootstrap.EnsureAsync();

			result.Should().Be("/padded/copilot.exe");
		}
		finally
		{
			Environment.SetEnvironmentVariable(CopilotCliBootstrap.ExplicitCliPathEnvVar, prior);
		}
	}

	private static string? TryLocateSdkPropsFile()
	{
		// The SDK's .props file is at:
		//   <userprofile>/.nuget/packages/github.copilot.sdk/<version>/build/GitHub.Copilot.SDK.props
		var nugetRoot = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".nuget", "packages", "github.copilot.sdk");
		if (!Directory.Exists(nugetRoot)) return null;

		// Pick the highest-named version directory (lexicographic; works for the current
		// "0.x.y" scheme). We don't know the exact pinned version from the test context
		// without parsing csproj, so we trust the restore to have laid down the matching one.
		var versionDir = Directory.EnumerateDirectories(nugetRoot)
			.OrderByDescending(d => d, StringComparer.Ordinal)
			.FirstOrDefault();
		if (versionDir is null) return null;

		var propsPath = Path.Combine(versionDir, "build", "GitHub.Copilot.SDK.props");
		return File.Exists(propsPath) ? propsPath : null;
	}

	private static string? ExtractCopilotCliVersion(string propsPath)
	{
		var content = File.ReadAllText(propsPath);
		// Cheap parse -- the SDK .props is a hand-authored single-line element.
		var match = System.Text.RegularExpressions.Regex.Match(
			content,
			@"<CopilotCliVersion>([^<]+)</CopilotCliVersion>");
		return match.Success ? match.Groups[1].Value.Trim() : null;
	}
}
