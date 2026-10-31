using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeCliInstallerTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task MirrorFallback_UsesPackageMetadataAndDoesNotMaskFailuresDuringCleanup(bool secondMirrorAvailable)
	{
		var root = Directory.CreateTempSubdirectory("orchestra-mirror-").FullName;
		try
		{
			var executable = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
			using var bytes = new MemoryStream();
			using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
			using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
				writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/bin/" + executable) { DataStream = new MemoryStream("native binary"u8.ToArray()) });
			var archive = bytes.ToArray();
			var metadata = JsonSerializer.Serialize(new { versions = new Dictionary<string, object> { [OpenCodeCliInstaller.Version] = new { dist = new { integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(archive)), tarball = "https://artifacts.test/binary.tgz" } } } });
			var handler = new MirrorHandler(archive, metadata, secondMirrorAvailable);
			using var http = new HttpClient(handler);
			var path = Path.Combine(root, "not-created-yet", executable);
			var install = () => OpenCodeCliInstaller.EnsureAsync(path, ["https://first.test", "https://second.test"], http, _ => { }, CancellationToken.None);
			if (secondMirrorAvailable)
			{
				(await install()).Should().Be(path);
				(await File.ReadAllTextAsync(path)).Should().Be("native binary");
				handler.Requests.Should().Contain("https://artifacts.test/binary.tgz");
			}
			else
			{
				await install.Should().ThrowAsync<IOException>().WithMessage("Could not install OpenCode*");
				Directory.Exists(Path.GetDirectoryName(path)).Should().BeFalse();
			}
			handler.Requests.Should().Contain(url => url.StartsWith("https://second.test/") && !url.EndsWith(OpenCodeCliInstaller.Version));
		}
		finally { Directory.Delete(root, true); }
	}

	private sealed class MirrorHandler(byte[] archive, string metadata, bool available) : HttpMessageHandler
	{
		public List<string> Requests { get; } = [];
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
		{
			Requests.Add(request.RequestUri!.AbsoluteUri);
			if (request.RequestUri.Host == "artifacts.test")
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
			return Task.FromResult(new HttpResponseMessage(request.RequestUri.Host == "second.test" && available ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new StringContent(metadata) });
		}
	}

	[Fact]
	[Trait("Category", "E2E")]
	public async Task NativePackage_CanBeDownloadedVerifiedAndLaunchedWithoutNpm()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		var path = await OpenCodeCliInstaller.EnsureAsync(_ => { }, timeout.Token);
		using var process = new System.Diagnostics.Process
		{
			StartInfo = new(path, "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true },
		};
		process.Start();
		try
		{
			var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
			var error = process.StandardError.ReadToEndAsync(timeout.Token);
			await process.WaitForExitAsync(timeout.Token);
			process.ExitCode.Should().Be(0, await error);
			(await output).Should().Contain(OpenCodeCliInstaller.Version);
		}
		finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Install_VerifiesIntegrityAndExtractsOnlyTheBinary(bool validHash)
	{
		var root = Directory.CreateTempSubdirectory("orchestra-opencode-install-").FullName;
		try
		{
			var archive = Path.Combine(root, "package.tgz");
			var binary = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
			await using (var file = File.Create(archive))
			await using (var gzip = new GZipStream(file, CompressionMode.Compress))
			using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
			{
				writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/bin/" + binary) { DataStream = new MemoryStream("binary"u8.ToArray()) });
				writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../outside.txt") { DataStream = new MemoryStream("ignored"u8.ToArray()) });
			}
			var hash = "sha512-" + Convert.ToBase64String(SHA512.HashData(await File.ReadAllBytesAsync(archive)));
			var destination = Path.Combine(root, "cache", binary);
			var install = () => OpenCodeCliInstaller.InstallArchiveAsync(archive, destination, validHash ? hash : "sha512-invalid", CancellationToken.None);
			if (validHash)
			{
				await install();
				(await File.ReadAllTextAsync(destination)).Should().Be("binary");
				Directory.GetFiles(Path.Combine(root, "cache")).Should().ContainSingle();
			}
			else
			{
				await install.Should().ThrowAsync<IOException>();
				File.Exists(destination).Should().BeFalse();
			}
		}
		finally { Directory.Delete(root, true); }
	}
}
