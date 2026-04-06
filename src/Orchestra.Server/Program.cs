using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.McpServer;

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
	options.OrchestrationsScanPath = orchestrationsScanPath;
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
app.Services.InitializeOrchestraHost();

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
