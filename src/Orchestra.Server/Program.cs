using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;

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

// Configuration: data path and orchestrations scan path from CLI args, env vars, or config
var dataPath = Environment.GetEnvironmentVariable("ORCHESTRA_DATA_PATH")
	?? builder.Configuration["data-path"];

var orchestrationsScanPath = Environment.GetEnvironmentVariable("ORCHESTRA_ORCHESTRATIONS_PATH")
	?? builder.Configuration["orchestrations-path"];

// Add Orchestra Host services
builder.Services.AddOrchestraHost(options =>
{
	if (dataPath is not null)
		options.DataPath = dataPath;
	options.OrchestrationsScanPath = orchestrationsScanPath;
	options.LoadPersistedOrchestrations = true;
	options.RegisterJsonTriggers = true;
});

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
var resolvedDataPath = dataPath ?? Path.Combine(
	Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrchestraHost");
startupLogger.LogInformation("Orchestra Server started with data path: {DataPath}", resolvedDataPath);

// Middleware pipeline
app.UseOrchestraHostProblemDetails();
app.UseCors();

// OpenAPI endpoint at /openapi/v1.json
app.MapOpenApi();

// Map all Orchestra Host API endpoints
app.MapOrchestraHostEndpoints();

app.Run();

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
