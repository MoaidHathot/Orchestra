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
	public void Format_PrefixesTheCommand()
		=> InvocationStyle.Format("doctor").Should().EndWith("doctor").And.StartWith(InvocationStyle.CommandPrefix);
}
