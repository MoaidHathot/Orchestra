using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.McpServer;

// Ensure the thread pool has enough minimum threads to prevent starvation.
// The Orchestra server runs orchestration steps via Task.Run (for DAG parallelism)
// while also serving HTTP requests on the same server (e.g., the MCP data plane
// at /mcp/data). Under default .NET settings, the thread pool starts with a
// minimum of Environment.ProcessorCount threads and grows slowly (~1-2 threads/sec).
// When orchestration steps await Copilot SDK responses (which may call back to
// the server's own MCP endpoint), thread pool starvation can cause deadlocks.
// Setting a higher minimum prevents this by pre-allocating enough threads for
// concurrent orchestration execution and incoming HTTP request handling.
{
	ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
	var targetMin = Math.Max(workerMin, 64);
	var targetIoMin = Math.Max(ioMin, 64);
	ThreadPool.SetMinThreads(targetMin, targetIoMin);
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
	options.SingleLine = true;
	options.IncludeScopes = false;
	options.TimestampFormat = "HH:mm:ss ";
	options.ColorBehavior = LoggerColorBehavior.Enabled;
});

// Register the Copilot agent builder (required for prompt step execution)
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

// Add Orchestra Host services.  The IConfiguration-aware overload ensures that
// data-path and orchestrations-path are read from the host's IConfiguration
// (resolved after Build()), so WebApplicationFactory.ConfigureAppConfiguration
// overrides are visible for test isolation.
builder.Services.AddOrchestraHost((options, configuration) =>
{
	var dataPath = configuration["data-path"]
		?? Environment.GetEnvironmentVariable("ORCHESTRA_DATA_PATH");

	var orchestrationsScanPath = configuration["orchestrations-path"]
		?? Environment.GetEnvironmentVariable("ORCHESTRA_ORCHESTRATIONS_PATH");

	if (dataPath is not null)
		options.DataPath = dataPath;
	if (orchestrationsScanPath is not null)
	{
		// Update just the directory, preserving watch/recursive from orchestra.json
		if (options.Scan is not null)
			options.Scan.Directory = orchestrationsScanPath;
		else
			options.Scan = new ScanConfig { Directory = orchestrationsScanPath };
	}
	options.LoadPersistedOrchestrations = true;
	options.RegisterJsonTriggers = true;
});

// Add Orchestra MCP server (data-plane enabled by default, control-plane disabled by default)
builder.Services.AddOrchestraMcpServer();

// CORS: allow localhost access for local programs and tools
builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(policy =>
	{
		policy.SetIsOriginAllowed(origin =>
		{
			var uri = new Uri(origin);
			return uri.Host is "localhost" or "127.0.0.1" or "::1";
		})
		.AllowAnyMethod()
		.AllowAnyHeader()
		.AllowCredentials();
	});
});

// OpenAPI
builder.Services.AddOpenApi();

var app = builder.Build();

// Initialize Orchestra Host - loads persisted orchestrations and registers triggers
await app.Services.InitializeOrchestraHostAsync();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var resolvedDataPath = app.Services.GetRequiredService<OrchestrationHostOptions>().DataPath;
startupLogger.LogInformation("Orchestra Server started with data path: {DataPath}", resolvedDataPath);

// Middleware pipeline
app.UseOrchestraHostProblemDetails();
app.UseCors();

// OpenAPI endpoint at /openapi/v1.json
app.MapOpenApi();

// Map all Orchestra Host API endpoints
app.MapOrchestraHostEndpoints();

// Map Orchestra MCP server endpoints
app.MapOrchestraMcpEndpoints();

app.Run();

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
