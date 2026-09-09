using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace Orchestra.Portal.E2E;

/// <summary>
/// Manages the Portal server lifecycle for E2E tests.
/// Starts the server before tests and stops it after.
/// Always starts its own isolated server on a dynamic port to prevent
/// test data from polluting a user's real Portal instance.
/// </summary>
public class PortalServerFixture : IAsyncLifetime
{
	private Process? _serverProcess;
	private readonly List<string> _serverOutput = new();
	private readonly List<string> _serverErrors = new();
	private readonly string _testDataPath;
	
	public string BaseUrl { get; private set; } = null!;
	public bool IsRunning { get; private set; }
	public string? StartupError { get; private set; }
	public string TestDataPath => _testDataPath;

	public PortalServerFixture()
	{
		// Create isolated test data directory
		_testDataPath = Path.Combine(Path.GetTempPath(), "Orchestra.Portal.E2E", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_testDataPath);
		Console.WriteLine($"E2E Test data directory: {_testDataPath}");
	}

	public async Task InitializeAsync()
	{
		// Always start our own isolated server on a dynamic port.
		// Never reuse an existing server — that would pollute the user's real data directory.
		var port = GetAvailablePort();
		BaseUrl = $"http://localhost:{port}";

		var projectPath = FindProjectPath();
		Console.WriteLine($"Starting Portal server from: {projectPath}");
		Console.WriteLine($"Using isolated test data path: {_testDataPath}");
		Console.WriteLine($"Using dynamic port: {port}");
		
		_serverProcess = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				Arguments = $"run --project \"{projectPath}\" --urls {BaseUrl} --no-build",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = projectPath
			}
		};
		
		// Set environment variable for test data isolation
		_serverProcess.StartInfo.EnvironmentVariables["ORCHESTRA_PORTAL_DATA_PATH"] = _testDataPath;

		_serverProcess.OutputDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				_serverOutput.Add(e.Data);
				Console.WriteLine($"[Server] {e.Data}");
			}
		};
		_serverProcess.ErrorDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				_serverErrors.Add(e.Data);
				Console.WriteLine($"[Server Error] {e.Data}");
			}
		};

		try
		{
			_serverProcess.Start();
			_serverProcess.BeginOutputReadLine();
			_serverProcess.BeginErrorReadLine();
		}
		catch (Exception ex)
		{
			StartupError = $"Failed to start server process: {ex.Message}";
			Console.WriteLine(StartupError);
			return;
		}

		// Wait for server to be ready
		var maxRetries = 60; // Wait up to 60 seconds
		for (var i = 0; i < maxRetries; i++)
		{
			await Task.Delay(1000);
			
			// Check if process exited unexpectedly
			if (_serverProcess.HasExited)
			{
				StartupError = $"Server process exited with code {_serverProcess.ExitCode}. Errors: {string.Join("\n", _serverErrors)}";
				Console.WriteLine(StartupError);
				return;
			}
			
			if (await CheckServerRunning())
			{
				IsRunning = true;
				Console.WriteLine($"Portal server started successfully on {BaseUrl}");
				return;
			}
		}

		StartupError = $"Portal server failed to start within {maxRetries} seconds. Last output: {string.Join("\n", _serverOutput.TakeLast(10))}";
		Console.WriteLine(StartupError);
	}

	private async Task<bool> CheckServerRunning()
	{
		try
		{
			using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
			var response = await httpClient.GetAsync($"{BaseUrl}/api/orchestrations");
			return response.IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}

	public Task DisposeAsync()
	{
		// Stop the server (we always own it)
		if (_serverProcess != null && !_serverProcess.HasExited)
		{
			try
			{
				_serverProcess.Kill(entireProcessTree: true);
				Console.WriteLine("Server process stopped");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error killing server process: {ex.Message}");
			}
			_serverProcess.Dispose();
		}
		
		// Clean up test data directory
		if (Directory.Exists(_testDataPath))
		{
			try
			{
				// Give the server a moment to release file handles
				Thread.Sleep(500);
				Directory.Delete(_testDataPath, recursive: true);
				Console.WriteLine($"Cleaned up test data directory: {_testDataPath}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Warning: Could not clean up test data directory: {ex.Message}");
				// Don't fail the test for cleanup issues
			}
		}
		
		return Task.CompletedTask;
	}

	/// <summary>
	/// Gets an available TCP port by binding to port 0 and reading the assigned port.
	/// </summary>
	private static int GetAvailablePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private static string FindProjectPath()
	{
		var currentDir = Directory.GetCurrentDirectory();
		Console.WriteLine($"Current directory: {currentDir}");
		
		var searchPaths = new[]
		{
			Path.Combine(currentDir, "..", "..", "..", "..", "..", "playground", "Hosting", "Orchestra.Playground.Copilot.Portal"),
			Path.Combine(currentDir, "playground", "Hosting", "Orchestra.Playground.Copilot.Portal"),
		};

		foreach (var path in searchPaths)
		{
			var fullPath = Path.GetFullPath(path);
			Console.WriteLine($"Checking path: {fullPath}");
			if (Directory.Exists(fullPath))
			{
				Console.WriteLine($"Found project at: {fullPath}");
				return fullPath;
			}
		}

		// Try to find from solution root
		var dir = new DirectoryInfo(currentDir);
		while (dir != null)
		{
			var portalPath = Path.Combine(dir.FullName, "playground", "Hosting", "Orchestra.Playground.Copilot.Portal");
			if (Directory.Exists(portalPath))
			{
				Console.WriteLine($"Found project at: {portalPath}");
				return portalPath;
			}
			dir = dir.Parent;
		}

		throw new Exception($"Could not find Portal project path. Started from: {currentDir}");
	}
}
