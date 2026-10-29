using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

public class PortalStartupTests
{
	[Fact]
	public async Task Portal_BackendStartupFailure_ExitsNonzeroWithBackendAndStderr()
	{
		var directory = Directory.CreateTempSubdirectory("orchestra-portal-startup-").FullName;
		try
		{
			const string error = "The process cannot access the file 'fixture.nupkg' because it is being used by another process.";
			var windows = OperatingSystem.IsWindows();
			var script = Path.Combine(directory, windows ? "backend.cmd" : "backend.sh");
			// An exit code of zero is still a failure when the MCP handshake never completed,
			// matching the package-launcher failure reported by icm-full.
			File.WriteAllText(script, windows
				? $"@echo off\necho {error} 1>&2\nexit /b 0\n"
				: $"printf '%s\\n' \"{error}\" >&2\nexit 0\n");
			var configPath = Path.Combine(directory, "orchestra.json");
			File.WriteAllText(configPath, """
				{"logLevel":"Warning","mcpServer":{"backendStartupTimeoutSeconds":3}}
				""");
			File.WriteAllText(Path.Combine(directory, "orchestra.mcp.json"), JsonSerializer.Serialize(new
			{
				mcps = new[]
				{
					new
					{
						name = "icm-full",
						type = "local",
						command = windows ? "cmd.exe" : "/bin/sh",
						arguments = windows ? new[] { "/d", "/c", script } : new[] { script },
					},
				},
			}));

			var startInfo = new ProcessStartInfo("dotnet")
			{
				WorkingDirectory = directory,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			foreach (var key in startInfo.Environment.Keys.Where(k => k.StartsWith("ORCHESTRA_", StringComparison.OrdinalIgnoreCase)).ToArray())
				startInfo.Environment.Remove(key);
			startInfo.Environment["ORCHESTRA_CONFIG_PATH"] = configPath;
			startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
			startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
			foreach (var arg in new[]
			{
				typeof(Program).Assembly.Location, "portal", "--urls", "http://127.0.0.1:0",
				"--data-path", Path.Combine(directory, "data"),
			})
				startInfo.ArgumentList.Add(arg);

			using var process = Process.Start(startInfo)!;
			try
			{
				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
				await process.WaitForExitAsync(timeout.Token);
				var output = await stdout + await stderr;

				process.ExitCode.Should().Be(1, output);
				output.Should().Contain("icm-full").And.Contain("fixture.nupkg");
				output.Should().Contain("failed startup validation");
			}
			finally
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
					await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
				}
			}
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
