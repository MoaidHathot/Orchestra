using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrchestrationEngine.Console;
using OrchestrationEngine.Copilot;
using OrchestrationEngine.Core;
using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Services;

// Parse command line arguments
var orchestrationPath = "orchestration.json";
var mcpPath = "mcp.json";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" or "--orchestration":
            if (i + 1 < args.Length)
                orchestrationPath = args[++i];
            break;
        case "-m" or "--mcp":
            if (i + 1 < args.Length)
                mcpPath = args[++i];
            break;
        case "-h" or "--help":
            PrintUsage();
            return 0;
        default:
            // First positional argument is orchestration path for backwards compatibility
            if (!args[i].StartsWith("-") && orchestrationPath == "orchestration.json")
                orchestrationPath = args[i];
            break;
    }
}

if (!File.Exists(orchestrationPath))
{
    Console.Error.WriteLine($"Orchestration file not found: {orchestrationPath}");
    PrintUsage();
    return 1;
}

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        var configLoader = new ConfigurationLoader();
        var mcpConfig = configLoader.LoadMcpConfigurationAsync(mcpPath).GetAwaiter().GetResult();
        
        services.AddOrchestrationCore();
        services.AddCopilotAgents(mcpConfig);
        services.AddConsoleTui();
    })
    .Build();

var configLoader = host.Services.GetRequiredService<ConfigurationLoader>();
var engine = host.Services.GetRequiredService<IOrchestrationEngine>();

try
{
    var orchestration = await configLoader.LoadOrchestrationAsync(orchestrationPath);
    
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await engine.ExecuteAsync(orchestration, cts.Token);
    
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

void PrintUsage()
{
    Console.WriteLine("OrchestrationEngine.Console - Dynamic AI Orchestration Engine");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  OrchestrationEngine.Console [options] [orchestration.json]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -o, --orchestration <path>  Path to orchestration JSON file (default: orchestration.json)");
    Console.WriteLine("  -m, --mcp <path>            Path to MCP configuration JSON file (default: mcp.json)");
    Console.WriteLine("  -h, --help                  Show this help message");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  OrchestrationEngine.Console");
    Console.WriteLine("  OrchestrationEngine.Console my-pipeline.json");
    Console.WriteLine("  OrchestrationEngine.Console -o pipeline.json -m my-mcp.json");
}
