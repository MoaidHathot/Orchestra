using System.Runtime.InteropServices;

namespace Orchestra.Engine;

/// <summary>
/// Resolves a bare command name (e.g. <c>dnx</c>, <c>npx</c>) to a concrete executable path
/// using <c>PATH</c> + <c>PATHEXT</c>.
/// <para>
/// This exists because a spawned MCP stdio server's <c>command</c> is ultimately launched by a
/// downstream runtime (the GitHub Copilot SDK or an <c>opencode serve</c> process) with
/// <c>UseShellExecute=false</c> (no shell). On Windows, <c>CreateProcess</c> does NOT search
/// <c>PATH</c>/<c>PATHEXT</c>, so a bare <c>command</c> that is really a shell shim
/// (<c>dnx.cmd</c>, <c>npx.cmd</c>) fails with "The system cannot find the file specified" — the
/// child never starts, no MCP <c>initialize</c> handshake ever completes, and the agent turn
/// hangs indefinitely.
/// </para>
/// <para>
/// Orchestra already applies equivalent resolution when it spawns processes itself
/// (<c>ManagedProcess</c>, <c>ServiceManager</c>, <c>OpenCodeServerProcess</c>); this helper is
/// the single place that resolution is applied to the MCP <c>command</c> before it is handed to
/// a provider runtime for spawning.
/// </para>
/// </summary>
public static class ExecutableResolver
{
    /// <summary>
    /// Resolves <paramref name="command"/> to a full executable path when it is a bare name that
    /// requires <c>PATHEXT</c> resolution on Windows. Returns the input unchanged when it already
    /// contains a directory separator, when running off Windows, or when no match is found (Unix
    /// and the downstream runtime may still resolve it via the shell/PATH).
    /// </summary>
    /// <param name="command">The command as declared on the MCP entry (e.g. <c>dnx</c>).</param>
    /// <returns>A resolved absolute path, or <paramref name="command"/> unchanged.</returns>
    public static string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return command;

        // Already a rooted/relative path — the runtime can launch it directly.
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return command;

        // Only Windows needs PATHEXT resolution; other platforms locate PATH commands natively.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return command;

        // If the caller already supplied an extension that exists on PATH, honor it as-is.
        var hasKnownExtension = Path.HasExtension(command);

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            // Exact match first (covers a command that already carries a valid extension).
            if (hasKnownExtension && TryCandidate(directory, command, out var exact))
                return exact;

            foreach (var extension in extensions)
            {
                if (TryCandidate(directory, command + extension, out var candidate))
                    return candidate;
            }
        }

        // Fall back to the original command; let the downstream runtime try to resolve it.
        return command;
    }

    private static bool TryCandidate(string directory, string fileName, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                resolved = candidate;
                return true;
            }
        }
        catch
        {
            // Ignore malformed PATH entries (invalid path characters, etc.).
        }

        return false;
    }
}
