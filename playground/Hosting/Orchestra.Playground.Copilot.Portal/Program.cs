using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
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

// Determine data path - supports test isolation via environment variable or configuration
var dataPath = Environment.GetEnvironmentVariable("ORCHESTRA_PORTAL_DATA_PATH")
	?? builder.Configuration["data-path"]
	?? builder.Configuration["executions-path"]
	?? Path.Combine(builder.Environment.ContentRootPath, "data");

// Determine orchestrations scan path
var orchestrationsScanPath = Environment.GetEnvironmentVariable("ORCHESTRA_ORCHESTRATIONS_PATH")
	?? builder.Configuration["orchestrations-path"];

// Add Orchestra Host services - this registers all core services including:
// - OrchestrationRegistry, TriggerManager, FileSystemRunStore
// - Active execution tracking
// - Default execution callback (SseReporter-based)
builder.Services.AddOrchestraHost(options =>
{
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
startupLogger.LogInformation("Orchestra Portal started with data path: {DataPath}", dataPath);

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

// GET /api/folder/browse - Open native folder picker dialog (Windows only)
app.MapGet("/api/folder/browse", (IWebHostEnvironment env) =>
{
	// In the Testing environment, return a cancelled response instead of opening
	// a native dialog (which would block test execution with a modal window).
	if (env.EnvironmentName == "Testing")
		return Results.Json(new { cancelled = true, path = (string?)null });

	if (!OperatingSystem.IsWindows())
		return Results.BadRequest(new { error = "Native folder dialog is only supported on Windows." });

	string? selectedPath = null;
	var thread = new Thread(() =>
	{
		using var dialog = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "Select orchestration folder",
			ShowNewFolderButton = false,
			UseDescriptionForTitle = true,
		};
		if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
			selectedPath = dialog.SelectedPath;
	});
	thread.SetApartmentState(ApartmentState.STA);
	thread.Start();
	thread.Join();

	return selectedPath is not null
		? Results.Json(new { cancelled = false, path = selectedPath })
		: Results.Json(new { cancelled = true, path = (string?)null });
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

// Portal route aliases – the frontend UI calls /api/orchestrations/add and
// /api/orchestrations/add-json, but the Host library registers the handlers at
// POST /api/orchestrations and POST /api/orchestrations/json respectively.
// These aliases forward to the same service logic so the Portal UI works.
app.MapPost("/api/orchestrations/add", (AddOrchestrationsRequest request, OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	var added = new List<object>();
	var errors = new List<object>();

	foreach (var path in request.Paths ?? [])
	{
		try
		{
			if (!File.Exists(path))
			{
				errors.Add(new { path, error = "File not found" });
				continue;
			}

			var mcpPath = request.McpPath;
			if (string.IsNullOrWhiteSpace(mcpPath))
			{
				var dir = Path.GetDirectoryName(path)!;
				var candidate = Path.Combine(dir, "mcp.json");
				if (File.Exists(candidate))
					mcpPath = candidate;
			}

			var entry = registry.Register(path, mcpPath);
			added.Add(new { id = entry.Id, path = entry.Path, name = entry.Orchestration.Name });

			if (entry.Orchestration.Trigger is { Enabled: true } trigger)
			{
				triggerManager.RegisterTrigger(entry.Path, entry.McpPath, trigger, null, TriggerSource.Json, entry.Id);
			}
		}
		catch (Exception ex)
		{
			errors.Add(new { path, error = ex.Message });
		}
	}

	return Results.Json(new { addedCount = added.Count, added, errors });
});

app.MapPost("/api/orchestrations/add-json", (AddJsonRequest request, OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	try
	{
		if (string.IsNullOrWhiteSpace(request.Json))
			return Results.BadRequest(new { error = "JSON content is required." });

		Mcp[] mcps = [];
		if (!string.IsNullOrWhiteSpace(request.McpJson))
			mcps = OrchestrationParser.ParseMcps(request.McpJson);

		var orchestration = OrchestrationParser.ParseOrchestration(request.Json, mcps);

		var tempDir = Path.Combine(Path.GetTempPath(), "orchestra-host");
		Directory.CreateDirectory(tempDir);
		var fileName = $"{orchestration.Name}.json";
		var tempPath = Path.Combine(tempDir, fileName);
		File.WriteAllText(tempPath, request.Json);

		var entry = registry.Register(tempPath, null, orchestration);

		if (orchestration.Trigger is { Enabled: true } trigger)
		{
			triggerManager.RegisterTrigger(entry.Path, entry.McpPath, trigger, null, TriggerSource.Json, entry.Id);
		}

		return Results.Json(new { id = entry.Id, path = entry.Path, name = entry.Orchestration.Name, version = entry.Orchestration.Version });
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// Portal route alias – the frontend UI calls POST /api/cancel/{executionId}
// but the Host library registers the cancel handler at POST /api/active/{executionId}/cancel.
app.MapPost("/api/cancel/{executionId}", (
	string executionId,
	ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
{
	if (activeExecutionInfos.TryGetValue(executionId, out var info))
	{
		info.Status = "Cancelling";
		if (info.Reporter is SseReporter sseReporter)
			sseReporter.ReportStatusChange("Cancelling");
		info.CancellationTokenSource.Cancel();
		return Results.Ok(new { cancelled = true, executionId, status = "Cancelling" });
	}
	return Results.NotFound(new { error = $"No active execution with ID '{executionId}'." });
});

// Portal route alias – the frontend UI calls POST /api/orchestrations/{id}/toggle with { enabled: bool }
// but the Host library has separate /enable and /disable endpoints.
app.MapPost("/api/orchestrations/{id}/toggle", (string id, ToggleRequest request, OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	var entry = registry.Get(id);
	if (entry is null)
		return Results.NotFound(new { error = $"Orchestration '{id}' not found." });

	if (request.Enabled)
	{
		if (entry.Orchestration.Trigger is { } trigger)
		{
			var existingTrigger = triggerManager.GetTrigger(id);
			if (existingTrigger == null)
			{
				var enabledTrigger = TriggerManager.CloneTriggerConfigWithEnabled(trigger, true);
				triggerManager.RegisterTrigger(entry.Path, entry.McpPath, enabledTrigger, null, TriggerSource.Json, entry.Id);
			}
			else
			{
				triggerManager.SetTriggerEnabled(id, true);
			}
		}
		else
		{
			return Results.BadRequest(new { error = $"Orchestration '{id}' has no trigger defined." });
		}
	}
	else
	{
		triggerManager.SetTriggerEnabled(id, false);
	}

	return Results.Ok(new { id, enabled = request.Enabled });
});

// Fallback to index.html for SPA routing
app.MapFallbackToFile("index.html");

app.Run();

// Request DTOs for Portal-specific endpoints
record BrowseRequest(string? Directory);
record FolderScanRequest(string? Directory);
record ToggleRequest(bool Enabled);

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
