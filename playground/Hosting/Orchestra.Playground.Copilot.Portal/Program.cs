using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.McpServer;
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

// Add Orchestra Host services - this registers all core services including:
// - OrchestrationRegistry, TriggerManager, FileSystemRunStore
// - Active execution tracking
// - Default execution callback (SseReporter-based)
//
// Data-path resolution uses the IConfiguration-aware overload of AddOrchestraHost.
// The configure callback receives IConfiguration from the service provider (resolved
// after Build()), so values injected by WebApplicationFactory.ConfigureAppConfiguration
// (e.g. "data-path" for test isolation) are guaranteed to be visible.
builder.Services.AddOrchestraHost((options, configuration) =>
{
	var dataPath = configuration["data-path"]
		?? Environment.GetEnvironmentVariable("ORCHESTRA_PORTAL_DATA_PATH")
		?? configuration["executions-path"];

	if (dataPath is not null)
		options.DataPath = dataPath;

	options.OrchestrationsScanPath =
		Environment.GetEnvironmentVariable("ORCHESTRA_ORCHESTRATIONS_PATH")
		?? configuration["orchestrations-path"];

	options.LoadPersistedOrchestrations = true;
	options.RegisterJsonTriggers = true;
});

// Add Orchestra MCP server (data-plane enabled by default, control-plane disabled by default)
builder.Services.AddOrchestraMcpServer();

// Portal-specific services
builder.Services.AddSingleton<PortalStatusService>();

var app = builder.Build();

// Initialize Orchestra Host - loads persisted orchestrations and registers triggers
app.Services.InitializeOrchestraHost();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var resolvedDataPath = app.Services.GetRequiredService<OrchestrationHostOptions>().DataPath;
startupLogger.LogInformation("Orchestra Portal started with data path: {DataPath}", resolvedDataPath);

app.UseStaticFiles();

// Map all Orchestra Host API endpoints
// This includes: /api/orchestrations, /api/triggers, /api/webhooks, 
// /api/history, /api/active, /api/models, /api/mcps, /api/status,
// and SSE streaming endpoints
app.MapOrchestraHostEndpoints();

// Map Orchestra MCP server endpoints
app.MapOrchestraMcpEndpoints();

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

		foreach (var file in files.OrderBy(f => f))
		{
			if (Path.GetFileName(file).Equals("orchestra.mcp.json", StringComparison.OrdinalIgnoreCase))
				continue;

			try
			{
				var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);

				orchestrations.Add(new
				{
					path = file,
					fileName = Path.GetFileName(file),
					name = orchestration.Name,
					description = orchestration.Description,
					stepCount = orchestration.Steps.Length,
					steps = orchestration.Steps.Select(s => s.Name).ToArray(),
					parameters = orchestration.Steps.SelectMany(s => s.Parameters).Distinct().ToArray(),
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
