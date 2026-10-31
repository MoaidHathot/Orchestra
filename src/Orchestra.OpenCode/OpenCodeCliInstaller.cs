using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orchestra.OpenCode;

/// <summary>Installs a pinned native V2 binary in Orchestra's cache; no Node/npm prerequisite.</summary>
public static class OpenCodeCliInstaller
{
	public const string Version = "2.0.26";
	private static readonly SemaphoreSlim Gate = new(1, 1);
	public static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orchestra", "opencode-cli", Version, PackagePlatform(), OperatingSystem.IsWindows() ? "opencode.exe" : "opencode");
	internal static string PackagePlatform()
	{
		var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : throw new PlatformNotSupportedException();
		var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new PlatformNotSupportedException() };
		return $"{os}-{arch}" + (arch == "x64" ? "-baseline" : "") + (os == "linux" && RuntimeInformation.RuntimeIdentifier.Contains("musl") ? "-musl" : "");
	}
	public static async Task<string> EnsureAsync(Action<string> progress, CancellationToken token)
	{
		await Gate.WaitAsync(token);
		try
		{
			var path = CachePath;
			if (File.Exists(path)) return path;
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
			var package = "cli-" + PackagePlatform();
			var errors = new List<string>();
			foreach (var registry in Registries())
			{
				var archive = Path.Combine(Path.GetTempPath(), $"orchestra-opencode-{Guid.NewGuid():N}.tgz");
				var staged = path + $".{Guid.NewGuid():N}.tmp";
				try
				{
					progress($"Downloading OpenCode {Version}…");
					using var metadata = JsonDocument.Parse(await http.GetStringAsync($"{registry}/@opencode/{package}/{Version}", token));
					var integrity = metadata.RootElement.GetProperty("dist").GetProperty("integrity").GetString()!;
					using var response = await http.GetAsync($"{registry}/@opencode/{package}/-/{package}-{Version}.tgz", HttpCompletionOption.ResponseHeadersRead, token);
					response.EnsureSuccessStatusCode();
					await using (var file = File.Create(archive)) { await response.Content.CopyToAsync(file, token); }
					progress("Verifying and extracting OpenCode…");
					await InstallArchiveAsync(archive, path, integrity, token);
					return path;
				}
				catch (Exception ex) when (ex is HttpRequestException or IOException) { errors.Add(ex.Message); }
				finally { File.Delete(archive); File.Delete(staged); }
			}
			throw new IOException("Could not install OpenCode. Set ORCHESTRA_OPENCODE_NPM_REGISTRY to a reachable npm mirror. " + string.Join("; ", errors));
		}
		finally { Gate.Release(); }
	}
	internal static async Task InstallArchiveAsync(string archive, string path, string integrity, CancellationToken token)
	{
		await using (var file = File.OpenRead(archive))
			if (integrity != "sha512-" + Convert.ToBase64String(await SHA512.HashDataAsync(file, token))) throw new IOException("OpenCode archive integrity check failed.");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var staged = path + $".{Guid.NewGuid():N}.tmp";
		try
		{
			await using var input = File.OpenRead(archive);
			await using var gzip = new GZipStream(input, CompressionMode.Decompress);
			using var tar = new TarReader(gzip);
			while (await tar.GetNextEntryAsync(cancellationToken: token) is { } entry)
			{
				if (Path.GetFileName(entry.Name) != Path.GetFileName(path) || entry.DataStream is null) continue;
				await using (var binary = File.Create(staged)) { await entry.DataStream.CopyToAsync(binary, token); }
				if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
				try { File.Move(staged, path, overwrite: false); }
				catch (IOException) when (File.Exists(path)) { }
				return;
			}
			throw new IOException("OpenCode archive did not contain its native binary.");
		}
		finally { File.Delete(staged); }
	}
	private static IEnumerable<string> Registries()
	{
		var explicitRegistry = Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_NPM_REGISTRY");
		if (!string.IsNullOrWhiteSpace(explicitRegistry)) return [explicitRegistry.TrimEnd('/')];
		var values = new List<string>();
		var npmrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");
		if (File.Exists(npmrc))
		{
			foreach (var line in File.ReadLines(npmrc))
			{
				var parts = line.Split('=', 2);
				if (parts.Length == 2 && parts[0].Trim() is "@opencode:registry" or "registry") values.Add(parts[1].Trim());
			}
		}
		var environment = Environment.GetEnvironmentVariable("npm_config_registry");
		if (!string.IsNullOrWhiteSpace(environment)) values.Insert(0, environment);
		values.Add("https://registry.npmjs.org");
		return values.Where(v => Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https").Select(v => v.TrimEnd('/')).Distinct();
	}
}
