using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeCliInstallerTests
{
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
