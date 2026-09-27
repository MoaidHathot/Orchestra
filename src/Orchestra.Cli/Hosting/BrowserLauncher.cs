using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orchestra.Cli.Hosting;

/// <summary>
/// Opens a URL in the user's default browser, per platform. Never throws: a missing desktop
/// environment (SSH session, container, CI) just means the URL stays printed on the console.
/// </summary>
public static class BrowserLauncher
{
	/// <summary>
	/// Launches <paramref name="url"/> with the platform's opener. Returns false when no opener
	/// could be started; the caller should then print the URL instead.
	/// </summary>
	public static bool TryOpen(string url, Action<ProcessStartInfo>? start = null)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			return false;

		try
		{
			var info = BuildStartInfo(uri.ToString());
			if (start is not null)
			{
				start(info);
				return true;
			}

			using var process = Process.Start(info);
			return process is not null;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// Per-platform opener. On Windows `explorer.exe` hands http(s) URLs to the default browser
	/// without a shell (so no cmd.exe quoting); macOS has `open`; Linux desktops have `xdg-open`.
	/// </summary>
	public static ProcessStartInfo BuildStartInfo(string url)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return new ProcessStartInfo("explorer.exe", url) { UseShellExecute = false, CreateNoWindow = true };
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			return new ProcessStartInfo("open", url) { UseShellExecute = false, CreateNoWindow = true };
		return new ProcessStartInfo("xdg-open", url) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
	}

	/// <summary>
	/// Picks the address to show a human: prefer a loopback/http address over `0.0.0.0`/`[::]`
	/// wildcards (which browsers cannot open) and http over https (no dev-cert prompt).
	/// </summary>
	public static string? PickBrowsableAddress(IReadOnlyList<string> addresses)
	{
		if (addresses.Count == 0)
			return null;

		static string Normalise(string address)
		{
			// Kestrel reports wildcards as http://0.0.0.0:5000 or http://[::]:5000.
			return address
				.Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)
				.Replace("://[::]", "://localhost", StringComparison.Ordinal)
				.Replace("://+", "://localhost", StringComparison.Ordinal)
				.Replace("://*", "://localhost", StringComparison.Ordinal);
		}

		var candidates = addresses.Select(Normalise).ToList();
		return candidates.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
			?? candidates[0];
	}
}
