using System.Runtime.InteropServices;
using FluentAssertions;
using Orchestra.Engine;

namespace Orchestra.Engine.Tests.McpResolution;

public class ExecutableResolverTests
{
    [Fact]
    public void Resolve_NullOrWhitespace_ReturnsInputUnchanged()
    {
        ExecutableResolver.Resolve("").Should().Be("");
        ExecutableResolver.Resolve("   ").Should().Be("   ");
    }

    [Fact]
    public void Resolve_PathWithDirectorySeparator_ReturnsInputUnchanged()
    {
        // Already a path — no PATHEXT resolution should be attempted on any platform.
        var withForwardSlash = "some/dir/dnx";
        var withBackSlash = @"some\dir\dnx";

        ExecutableResolver.Resolve(withForwardSlash).Should().Be(withForwardSlash);
        ExecutableResolver.Resolve(withBackSlash).Should().Be(withBackSlash);
    }

    [Fact]
    public void Resolve_BareCommandNotOnPath_ReturnsInputUnchanged()
    {
        // A command that does not exist on PATH must fall through untouched so the downstream
        // runtime can still attempt its own resolution (and so behavior is stable in CI).
        const string bogus = "orchestra-nonexistent-command-xyz";
        ExecutableResolver.Resolve(bogus).Should().Be(bogus);
    }

    [Fact]
    public void Resolve_NonWindows_ReturnsBareCommandUnchanged()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Windows path is covered by the shim test below.

        // On Unix, a bare command is resolved by the shell/exec at spawn time, so the resolver
        // intentionally leaves it as-is.
        ExecutableResolver.Resolve("dnx").Should().Be("dnx");
    }

    [Fact]
    public void Resolve_WindowsCmdShimOnPath_ResolvesToFullCmdPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // Arrange: a fake "widget.cmd" placed in a temp directory prepended to PATH. This mirrors
        // the real-world dnx.cmd/npx.cmd shim case that fails under CreateProcess without PATHEXT
        // resolution — the exact cause of the inline-MCP hang.
        var tempDir = Path.Combine(Path.GetTempPath(), "orchestra-exeresolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var shimPath = Path.Combine(tempDir, "widget.cmd");
        File.WriteAllText(shimPath, "@echo off\r\n");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalPathExt = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Environment.SetEnvironmentVariable("PATH", tempDir + Path.PathSeparator + originalPath);
            // Ensure .CMD is a recognized executable extension for the resolver.
            if (string.IsNullOrEmpty(originalPathExt) ||
                !originalPathExt.Split(';').Any(e => e.Trim().Equals(".CMD", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable("PATHEXT", (originalPathExt ?? "") + ";.CMD");
            }

            var resolved = ExecutableResolver.Resolve("widget");

            // PATHEXT casing (.CMD) may differ from the file's on-disk casing (.cmd); Windows FS
            // is case-insensitive, so compare accordingly.
            string.Equals(resolved, shimPath, StringComparison.OrdinalIgnoreCase)
                .Should().BeTrue($"resolved '{resolved}' should equal '{shimPath}' (case-insensitive)");
            Path.IsPathRooted(resolved).Should().BeTrue("a resolved shim must be an absolute path so CreateProcess can launch it");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PATHEXT", originalPathExt);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Resolve_WindowsCommandWithExplicitExtension_HonorsIt()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "orchestra-exeresolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var shimPath = Path.Combine(tempDir, "gadget.cmd");
        File.WriteAllText(shimPath, "@echo off\r\n");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", tempDir + Path.PathSeparator + originalPath);

            // Caller already supplied the extension; the exact file must be located as-is.
            var resolved = ExecutableResolver.Resolve("gadget.cmd");

            string.Equals(resolved, shimPath, StringComparison.OrdinalIgnoreCase)
                .Should().BeTrue($"resolved '{resolved}' should equal '{shimPath}' (case-insensitive)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
