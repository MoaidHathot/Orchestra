using Microsoft.AspNetCore.Mvc;
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

// Fallback to index.html for SPA routing
app.MapFallbackToFile("index.html");

app.Run();

// Request DTOs for Portal-specific endpoints
record BrowseRequest(string? Directory);

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
