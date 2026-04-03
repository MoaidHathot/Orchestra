using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.Logging;
using Orchestra.Host.Triggers;
using Orchestra.Playground.Copilot.Terminal;

// Handle --help
if (args.Contains("--help") || args.Contains("-h"))
{
	Console.WriteLine("Orchestra Terminal - Interactive TUI for Orchestra orchestrations");
	Console.WriteLine();
	Console.WriteLine("Usage: Orchestra.Playground.Copilot.Terminal [options]");
	Console.WriteLine();
	Console.WriteLine("Options:");
	Console.WriteLine("  --data-path=<path>         Path to store data (default: %LOCALAPPDATA%/OrchestraTerminal)");
	Console.WriteLine("  --orchestrations=<path>    Path to orchestrations folder to auto-load");
	Console.WriteLine("  --host-url=<url>           Base URL for Orchestra web UI (for generating links to run details)");
	Console.WriteLine("  -h, --help                 Show this help message");
	Console.WriteLine();
	Console.WriteLine("Environment Variables:");
	Console.WriteLine("  ORCHESTRA_TERMINAL_DATA_PATH     Same as --data-path");
	Console.WriteLine("  ORCHESTRA_ORCHESTRATIONS_PATH    Same as --orchestrations");
	Console.WriteLine("  ORCHESTRA_HOST_URL               Same as --host-url");
	Console.WriteLine();
	Console.WriteLine("Keyboard Shortcuts:");
	Console.WriteLine("  1-8        Switch views (Dashboard, Orchestrations, Triggers, History, Active, Event Log, MCP Servers, Checkpoints)");
	Console.WriteLine("  9          Profiles view (manage profiles & tags)");
	Console.WriteLine("  j/k or ↑/↓ Navigate up/down");
	Console.WriteLine("  Enter      Select item / Open detail");
	Console.WriteLine("  r          Run selected orchestration/trigger");
	Console.WriteLine("  a          Add orchestration file (in Orchestrations view)");
	Console.WriteLine("  s          Scan directory for orchestrations");
	Console.WriteLine("  d          Delete selected item");
	Console.WriteLine("  e          Enable/disable trigger (in Triggers view)");
	Console.WriteLine("  c          Cancel execution (in Active view)");
	Console.WriteLine("  /          Search / filter items in list views");
	Console.WriteLine("  ?          Show context-sensitive help overlay");
	Console.WriteLine("  n/p        Next/previous page (in History view)");
	Console.WriteLine("  Tab/1-4    Switch tabs (in Execution Detail: Summary, Steps, Output, Stream)");
	Console.WriteLine("  f          Toggle auto-scroll in Stream tab");
	Console.WriteLine("  u          Show run URL (in Execution Detail view)");
	Console.WriteLine("  Esc        Go back (hierarchical navigation)");
	Console.WriteLine("  Tab        Path autocomplete (when entering paths)");
	Console.WriteLine("  q          Quit (from Dashboard) or return to Dashboard");
	return;
}

// Parse command-line arguments
var dataPath = args.FirstOrDefault(a => a.StartsWith("--data-path="))?.Split('=')[1]
	?? Environment.GetEnvironmentVariable("ORCHESTRA_TERMINAL_DATA_PATH")
	?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrchestraTerminal");

var orchestrationPath = args.FirstOrDefault(a => a.StartsWith("--orchestrations="))?.Split('=')[1]
	?? Environment.GetEnvironmentVariable("ORCHESTRA_ORCHESTRATIONS_PATH");

var hostUrl = args.FirstOrDefault(a => a.StartsWith("--host-url="))?.Split('=')[1]
	?? Environment.GetEnvironmentVariable("ORCHESTRA_HOST_URL");

// Ensure data path exists
Directory.CreateDirectory(dataPath);

// Build the host
var builder = Host.CreateApplicationBuilder(args);

// Configure logging to file instead of console (TUI will handle display)
// Read log level from orchestra.json config (if present) so Debug/Trace can be enabled centrally
var configFile = OrchestraConfigLoader.Load();
var fileLogLevel = Enum.TryParse<LogLevel>(configFile?.LogLevel, ignoreCase: true, out var parsedLevel)
	? parsedLevel
	: LogLevel.Information;

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(fileLogLevel);
builder.Logging.AddFile(Path.Combine(dataPath, "logs", "terminal.log"), fileLogLevel);

// Register engine services (required by Orchestra Host)
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

// TUI components - must register callback BEFORE AddOrchestraHost
// so it overrides the default callback
builder.Services.AddSingleton<TerminalOrchestrationReporter>();
builder.Services.AddTriggerExecutionCallback<TerminalExecutionCallback>();

// Add Orchestra Host services - this registers all core services including:
// - OrchestrationRegistry, TriggerManager, FileSystemRunStore
// - Active execution tracking
// Since we registered TerminalExecutionCallback above, it will be used instead of the default
builder.Services.AddOrchestraHost(options =>
{
	options.DataPath = dataPath;
	options.OrchestrationsScanPath = orchestrationPath;
	options.LoadPersistedOrchestrations = true;
	options.RegisterJsonTriggers = true;
	options.HostBaseUrl = hostUrl;
});

// TUI component
builder.Services.AddSingleton<TerminalUI>();

// Build and start
var host = builder.Build();

// Initialize Orchestra Host - loads persisted orchestrations and registers triggers
host.Services.InitializeOrchestraHost();

// Start the host in the background
var hostCts = new CancellationTokenSource();
var hostTask = host.RunAsync(hostCts.Token);

// Start the TUI
var terminalUI = host.Services.GetRequiredService<TerminalUI>();
await terminalUI.RunAsync(hostCts.Token);

// Shutdown
hostCts.Cancel();
await hostTask;
