using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Playground.Copilot.Portal;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
	options.SingleLine = true;
	options.IncludeScopes = false;
	options.TimestampFormat = "HH:mm:ss ";
	options.ColorBehavior = LoggerColorBehavior.Enabled;
});

// Register engine services (required by Orchestra Host)
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

// Determine data path - supports test isolation via configuration or environment variable
// Priority: configuration "data-path" > env var ORCHESTRA_PORTAL_DATA_PATH > default
// Default: %LOCALAPPDATA%/OrchestraHost (from OrchestrationHostOptions)
var dataPath = builder.Configuration["data-path"]
	?? Environment.GetEnvironmentVariable("ORCHESTRA_PORTAL_DATA_PATH")
	?? builder.Configuration["executions-path"];

// Determine orchestrations scan path
var orchestrationsScanPath = Environment.GetEnvironmentVariable("ORCHESTRA_ORCHESTRATIONS_PATH")
	?? builder.Configuration["orchestrations-path"];

// Add Orchestra Host services - this registers all core services including:
// - OrchestrationRegistry, TriggerManager, FileSystemRunStore
// - Active execution tracking
// - Default execution callback (SseReporter-based)
builder.Services.AddOrchestraHost(options =>
{
	if (dataPath is not null)
		options.DataPath = dataPath;
	options.OrchestrationsScanPath = orchestrationsScanPath;
	options.LoadPersistedOrchestrations = true;
	options.RegisterJsonTriggers = true;
});

// Portal-specific services
builder.Services.AddSingleton<PortalStatusService>();

var app = builder.Build();

// Initialize Orchestra Host - loads persisted orchestrations and registers triggers
app.Services.InitializeOrchestraHost();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var resolvedDataPath = dataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrchestraHost");
startupLogger.LogInformation("Orchestra Portal started with data path: {DataPath}", resolvedDataPath);

app.UseStaticFiles();

// Map all Orchestra Host API endpoints
// This includes: /api/orchestrations, /api/triggers, /api/webhooks, 
// /api/history, /api/active, /api/models, /api/mcps, /api/status,
// and SSE streaming endpoints
app.MapOrchestraHostEndpoints();

// Portal-specific endpoints that extend Host functionality

// GET /api/browse - Browse directories (Portal-specific for file picker UI)
app.MapGet("/api/browse", ([AsParameters] BrowseRequest request) =>
{
	var directory = request.Directory;
	if (string.IsNullOrWhiteSpace(directory))
	{
		directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	}

	if (!Directory.Exists(directory))
	{
		return Results.BadRequest(new { error = $"Directory not found: {directory}" });
	}

	try
	{
		var entries = new List<object>();

		// Add parent directory if not at root
		var parent = Directory.GetParent(directory);
		if (parent != null)
		{
			entries.Add(new
			{
				name = "..",
				path = parent.FullName,
				isDirectory = true,
				isParent = true
			});
		}

		// Add subdirectories
		foreach (var dir in Directory.GetDirectories(directory).OrderBy(d => d))
		{
			var dirInfo = new DirectoryInfo(dir);
			entries.Add(new
			{
				name = dirInfo.Name,
				path = dirInfo.FullName,
				isDirectory = true,
				isParent = false
			});
		}

		// Add JSON files
		foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(f => f))
		{
			var fileInfo = new FileInfo(file);
			entries.Add(new
			{
				name = fileInfo.Name,
				path = fileInfo.FullName,
				isDirectory = false,
				isParent = false,
				size = fileInfo.Length
			});
		}

		return Results.Json(new
		{
			currentDirectory = directory,
			entries
		});
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// GET /api/folder/browse - Open native folder picker dialog (Windows only, via PowerShell)
app.MapGet("/api/folder/browse", async (IWebHostEnvironment env) =>
{
	// In the Testing environment, return a cancelled response instead of opening
	// a native dialog (which would block test execution with a modal window).
	if (env.EnvironmentName == "Testing")
		return Results.Json(new { cancelled = true, path = (string?)null });

	if (!OperatingSystem.IsWindows())
		return Results.BadRequest(new { error = "Native folder dialog is only supported on Windows." });

	try
	{
		var psi = new System.Diagnostics.ProcessStartInfo
		{
			FileName = "powershell",
			Arguments = "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = 'Select orchestration folder'; $d.ShowNewFolderButton = $false; if ($d.ShowDialog() -eq 'OK') { $d.SelectedPath } else { '' }\"",
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		using var proc = System.Diagnostics.Process.Start(psi);
		if (proc is null)
			return Results.BadRequest(new { error = "Failed to launch folder dialog." });

		var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
		await proc.WaitForExitAsync();

		return string.IsNullOrEmpty(output)
			? Results.Json(new { cancelled = true, path = (string?)null })
			: Results.Json(new { cancelled = false, path = output });
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// POST /api/folder/scan - Scan a folder for orchestration JSON files
app.MapPost("/api/folder/scan", (FolderScanRequest request) =>
{
	try
	{
		if (string.IsNullOrWhiteSpace(request.Directory))
			return Results.BadRequest(new { error = "Directory path is required." });

		if (!Directory.Exists(request.Directory))
			return Results.BadRequest(new { error = $"Directory not found: {request.Directory}" });

		var files = Directory.GetFiles(request.Directory, "*.json", SearchOption.TopDirectoryOnly);
		var orchestrations = new List<object>();

		// Auto-detect mcp.json in the scanned directory
		string? detectedMcpPath = null;
		var mcpCandidate = Path.Combine(request.Directory, "mcp.json");
		if (File.Exists(mcpCandidate))
			detectedMcpPath = mcpCandidate;

		foreach (var file in files.OrderBy(f => f))
		{
			if (Path.GetFileName(file).Equals("mcp.json", StringComparison.OrdinalIgnoreCase))
				continue;

			try
			{
				var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);

				var perFileMcp = Path.Combine(
					Path.GetDirectoryName(file)!,
					Path.GetFileNameWithoutExtension(file) + ".mcp.json");
				var orchMcpPath = File.Exists(perFileMcp) ? perFileMcp : detectedMcpPath;

				orchestrations.Add(new
				{
					path = file,
					fileName = Path.GetFileName(file),
					name = orchestration.Name,
					description = orchestration.Description,
					stepCount = orchestration.Steps.Length,
					steps = orchestration.Steps.Select(s => s.Name).ToArray(),
					parameters = orchestration.Steps.SelectMany(s => s.Parameters).Distinct().ToArray(),
					mcpPath = orchMcpPath,
					hasInlineMcps = orchestration.Mcps.Length > 0,
					inlineMcpNames = orchestration.Mcps.Select(m => m.Name).ToArray(),
					valid = true,
					error = (string?)null,
				});
			}
			catch (Exception ex)
			{
				orchestrations.Add(new
				{
					path = file,
					fileName = Path.GetFileName(file),
					name = (string?)null,
					description = (string?)null,
					stepCount = 0,
					steps = Array.Empty<string>(),
					parameters = Array.Empty<string>(),
					mcpPath = (string?)null,
					hasInlineMcps = false,
					inlineMcpNames = Array.Empty<string>(),
					valid = false,
					error = ex.Message,
				});
			}
		}

		return Results.Json(new
		{
			directory = request.Directory,
			count = orchestrations.Count,
			mcpPath = detectedMcpPath,
			orchestrations,
		});
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// GET /api/file/read - Read a file's content (for preview in the file picker)
app.MapGet("/api/file/read", (string path) =>
{
	try
	{
		if (string.IsNullOrWhiteSpace(path))
			return Results.BadRequest(new { error = "File path is required." });

		if (!System.IO.File.Exists(path))
			return Results.NotFound(new { error = $"File not found: {path}" });

		var content = System.IO.File.ReadAllText(path);
		return Results.Content(content, "application/json");
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// Fallback to index.html for SPA routing
app.MapFallbackToFile("index.html");

app.Run();

// Request DTOs for Portal-specific endpoints
record BrowseRequest(string? Directory);
record FolderScanRequest(string? Directory);

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
