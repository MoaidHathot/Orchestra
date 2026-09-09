using FluentAssertions;
using Orchestra.Cli.Commands;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="InvocationStyle"/>: "next step" hints must name a command the user can
/// actually run. A <c>dnx</c> user has no <c>orchestra</c> on PATH.
/// </summary>
public sealed class InvocationStyleTests
{
	[Theory]
	// dnx executes the tool straight out of the NuGet package cache.
	[InlineData(@"C:\Users\x\.nuget\packages\orchestra\0.7.21\tools\net10.0\any\", "dnx Orchestra --yes --")]
	[InlineData("/home/x/.nuget/packages/orchestra/0.7.21/tools/net10.0/any/", "dnx Orchestra --yes --")]
	// A relocated NUGET_PACKAGES root still has the package-shaped tail.
	[InlineData(@"D:\nuget-cache\packages\orchestra\0.7.21\tools\net10.0\any\", "dnx Orchestra --yes --")]
	public void Detect_PackageCacheLayout_UsesDnxPrefix(string baseDirectory, string expected)
		=> InvocationStyle.Detect(baseDirectory).Should().Be(expected);

	[Theory]
	// A global install always lands under the tool store.
	[InlineData(@"C:\Users\x\.dotnet\tools\.store\orchestra\0.7.21\orchestra\0.7.21\tools\net10.0\any\")]
	[InlineData("/home/x/.dotnet/tools/.store/orchestra/0.7.21/orchestra/0.7.21/tools/net10.0/any/")]
	// Unknown layouts (dev builds, self-contained publishes) fall back to the documented form.
	[InlineData(@"P:\Github\Orchestra\src\Orchestra.Cli\bin\Debug\net10.0\")]
	[InlineData("")]
	public void Detect_ToolStoreOrUnknownLayout_UsesPlainPrefix(string baseDirectory)
		=> InvocationStyle.Detect(baseDirectory).Should().Be("orchestra");

	[Fact]
	public void Detect_ToolStoreWinsOverPackageCacheShapedPath()
	{
		// A relocated tools directory can sit inside a path that also looks like a package cache;
		// the .store marker is the authoritative signal for an installed tool.
		var path = @"C:\nuget\packages\orchestra\.store\orchestra\0.7.21\tools\net10.0\any\";

		InvocationStyle.Detect(path).Should().Be("orchestra");
	}

	[Fact]
	public void Detect_PackageCache_WithLocalToolManifest_UsesDotnetPrefix()
	{
		// A local tool and dnx both run from ~/.nuget/packages, so the manifest is the only
		// way to tell a `dotnet tool install` user from a `dnx` user - and telling the former
		// to type `dnx Orchestra --yes --` hands them a command they never used.
		var root = Path.Combine(Path.GetTempPath(), $"orchestra-manifest-{Guid.NewGuid():N}");
		var nested = Path.Combine(root, "src", "deep");
		Directory.CreateDirectory(Path.Combine(root, ".config"));
		Directory.CreateDirectory(nested);
		File.WriteAllText(
			Path.Combine(root, ".config", "dotnet-tools.json"),
			"""{ "version": 1, "isRoot": true, "tools": { "orchestra": { "version": "0.8.0", "commands": ["orchestra"] } } }""");

		try
		{
			InvocationStyle.Detect("/home/x/.nuget/packages/orchestra/0.8.0/tools/net10.0/any/", nested)
				.Should().Be(InvocationStyle.LocalToolPrefix, "the manifest is found by walking up from a nested working directory");
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
		}
	}

	[Fact]
	public void Detect_PackageCache_WithManifestForADifferentTool_StillUsesDnx()
	{
		var root = Path.Combine(Path.GetTempPath(), $"orchestra-manifest-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		File.WriteAllText(
			Path.Combine(root, "dotnet-tools.json"),
			"""{ "version": 1, "isRoot": true, "tools": { "dotnet-ef": { "version": "10.0.0", "commands": ["dotnet-ef"] } } }""");

		try
		{
			InvocationStyle.Detect("/home/x/.nuget/packages/orchestra/0.8.0/tools/net10.0/any/", root)
				.Should().Be(InvocationStyle.DnxPrefix);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
		}
	}

	[Fact]
	public void Detect_MalformedManifest_DoesNotThrow()
	{
		var root = Path.Combine(Path.GetTempPath(), $"orchestra-manifest-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		File.WriteAllText(Path.Combine(root, "dotnet-tools.json"), "{ not json");

		try
		{
			var act = () => InvocationStyle.Detect("/home/x/.nuget/packages/orchestra/0.8.0/tools/net10.0/any/", root);
			act.Should().NotThrow("this only picks the wording of a hint");
			act().Should().Be(InvocationStyle.DnxPrefix);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
		}
	}
	[Fact]
	public void Format_PrefixesTheCommand()
		=> InvocationStyle.Format("doctor").Should().EndWith("doctor").And.StartWith(InvocationStyle.CommandPrefix);
}
