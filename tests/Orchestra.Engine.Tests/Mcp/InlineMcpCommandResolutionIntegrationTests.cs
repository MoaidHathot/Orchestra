using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;

namespace Orchestra.Engine.Tests.McpResolution;

/// <summary>
/// End-to-end guard for the inline `type: local` MCP hang. Both providers hand an MCP's raw
/// <c>command</c> to a downstream runtime that spawns it with <c>UseShellExecute=false</c> (the
/// Copilot SDK; an <c>opencode serve</c> child). On Windows that spawn does not search
/// PATH/PATHEXT, so a bare shim name (<c>dnx</c> = <c>dnx.cmd</c>) fails to start, the MCP
/// <c>initialize</c> handshake never completes, and the step hangs until cancelled.
/// <para>
/// These tests reproduce that exact spawn (a real child process + a real stdio JSON-RPC
/// <c>initialize</c> round-trip) against a self-contained MCP shim — no NuGet, no network, no
/// provider binaries — so they are safe to run in CI on Windows, Linux, and macOS.
/// </para>
/// </summary>
public class InlineMcpCommandResolutionIntegrationTests
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public async Task ResolvedCommand_SpawnsAndCompletesInitializeHandshake()
    {
        using var shim = McpShim.Create();

        // The LocalMcp declares a *bare* command, exactly like `"command": "dnx"` in an
        // orchestration. This is what the provider config builders receive.
        var localCommand = shim.BareCommandName;

        // What the provider now does before handing the command to the runtime.
        var resolved = ExecutableResolver.Resolve(localCommand);

        if (IsWindows)
            Path.IsPathRooted(resolved).Should().BeTrue("on Windows the shim must resolve to a full path so CreateProcess can launch it");

        // Spawn the resolved command exactly as the SDK/opencode do (no shell) and run a real
        // MCP initialize handshake over stdio.
        var response = await SpawnAndInitializeAsync(resolved, shim.Arguments);

        response.Should().NotBeNull("the resolved command must launch and answer initialize");
        response!.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        response.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        response.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name")
            .GetString().Should().Be("orchestra-test-mcp");
    }

    [Fact]
    public async Task BareShimCommand_OnWindows_FailsToSpawn_DemonstratingTheBug()
    {
        if (!IsWindows)
            return; // The PATHEXT gap is Windows-specific; Unix resolves bare names at exec time.

        using var shim = McpShim.Create();

        // Spawning the BARE shim name (pre-fix behavior) must throw — this is precisely why the
        // inline `dnx` MCP hung: the child never starts, so no initialize response ever arrives.
        var act = async () => await SpawnAndInitializeAsync(shim.BareCommandName, shim.Arguments);

        await act.Should().ThrowAsync<Win32Exception>(
            "CreateProcess with UseShellExecute=false does not resolve a bare .cmd name via PATHEXT");
    }

    /// <summary>
    /// Spawns <paramref name="fileName"/> (+ args) with <c>UseShellExecute=false</c>, sends a
    /// single JSON-RPC <c>initialize</c> request on stdin, and returns the first stdout line
    /// parsed as JSON (or null on timeout). Mirrors how a provider runtime drives an MCP stdio
    /// server. Throws <see cref="Win32Exception"/> when the process cannot be started.
    /// </summary>
    private static async Task<JsonDocument?> SpawnAndInitializeAsync(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!; // may throw Win32Exception — intentional for the bug test

        const string initialize =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}";
        await process.StandardInput.WriteLineAsync(initialize);
        await process.StandardInput.FlushAsync();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var line = await process.StandardOutput.ReadLineAsync(cts.Token);
            return line is null ? null : JsonDocument.Parse(line);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A throwaway, dependency-free MCP stdio server placed behind a bare shim name on a temp PATH
    /// directory. On Windows it is a <c>.cmd</c> that runs an inline PowerShell script; on Unix a
    /// <c>.sh</c>. It reads one line of stdin and, when it looks like an <c>initialize</c> request,
    /// writes a single-line JSON-RPC initialize response — enough to prove the spawn + handshake.
    /// </summary>
    private sealed class McpShim : IDisposable
    {
        private readonly string _dir;
        private readonly string? _originalPath;

        public string BareCommandName { get; }
        public IReadOnlyList<string> Arguments { get; }

        private McpShim(string dir, string bareCommandName, IReadOnlyList<string> arguments, string? originalPath)
        {
            _dir = dir;
            BareCommandName = bareCommandName;
            Arguments = arguments;
            _originalPath = originalPath;
        }

        public static McpShim Create()
        {
            var dir = Path.Combine(Path.GetTempPath(), "orchestra-mcpshim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            const string response =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"serverInfo\":{\"name\":\"orchestra-test-mcp\",\"version\":\"1.0.0\"}}}";

            string bareName;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // A .cmd shim mirrors dnx.cmd/npx.cmd — the exact case that fails under CreateProcess.
                bareName = "orchestramcp";
                var cmdPath = Path.Combine(dir, bareName + ".cmd");
                // Read one line from stdin, emit the initialize response line. Use PowerShell for
                // reliable single-line stdout without CRLF surprises.
                var ps = "$null = [Console]::In.ReadLine(); [Console]::Out.WriteLine('" + response + "')";
                var cmd = "@echo off\r\npowershell -NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"\r\n";
                File.WriteAllText(cmdPath, cmd);
            }
            else
            {
                bareName = "orchestramcp";
                var shPath = Path.Combine(dir, bareName);
                var sh = "#!/bin/sh\nread _line\nprintf '%s\\n' '" + response + "'\n";
                File.WriteAllText(shPath, sh);
                // chmod +x
                var chmod = Process.Start(new ProcessStartInfo("/bin/chmod", "+x \"" + shPath + "\"") { UseShellExecute = false });
                chmod!.WaitForExit();
            }

            var originalPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + originalPath);

            return new McpShim(dir, bareName, Array.Empty<string>(), originalPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
