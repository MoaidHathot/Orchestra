namespace Orchestra.OpenCode.Tests;

/// <summary>
/// Detects whether a usable OpenCode server is available for E2E tests: either a pre-running
/// server (<c>ORCHESTRA_OPENCODE_URL</c>) or an <c>opencode</c> binary resolvable from
/// <c>ORCHESTRA_OPENCODE_PATH</c> / PATH.
/// </summary>
internal static class OpenCodeAvailability
{
	public static bool IsAvailable { get; } = Detect();

	private static bool Detect()
	{
		// E2E is opt-in: a reachable server URL, or an explicit opt-in flag (the binary merely
		// being present on PATH does not mean OpenCode is authenticated / usable, so we don't
		// auto-run E2E just because `opencode` is installed).
		if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_URL")))
			return true;

		var optIn = Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_E2E");
		if (!string.IsNullOrWhiteSpace(optIn) && (optIn == "1" || optIn.Equals("true", StringComparison.OrdinalIgnoreCase)))
			return ResolveOnPath("opencode") is not null || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_PATH"));

		return false;
	}

	private static string? ResolveOnPath(string command)
	{
		var pathExt = OperatingSystem.IsWindows()
			? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
			: [string.Empty];

		var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

		foreach (var dir in dirs)
		{
			foreach (var ext in pathExt)
			{
				try
				{
					var candidate = Path.Combine(dir.Trim(), command + ext);
					if (File.Exists(candidate))
						return candidate;
				}
				catch
				{
					// Ignore malformed PATH entries.
				}
			}
		}

		return null;
	}
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips when no OpenCode server is available, so the E2E
/// suite is a no-op on machines / CI without OpenCode installed (xUnit 2.x conditional-skip pattern).
/// </summary>
public sealed class OpenCodeAvailableFactAttribute : FactAttribute
{
	public OpenCodeAvailableFactAttribute()
	{
		if (!OpenCodeAvailability.IsAvailable)
			Skip = "OpenCode not available. Set ORCHESTRA_OPENCODE_URL, ORCHESTRA_OPENCODE_PATH, or install 'opencode' on PATH.";
	}
}
