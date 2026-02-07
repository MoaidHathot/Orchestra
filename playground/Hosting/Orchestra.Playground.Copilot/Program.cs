using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orchestra.Engine;
using Orchestra.Playground.Copilot;

var (orchestrationPath, mcpPath) = ParseArgs(args);

var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath);
var mcps = OrchestrationParser.ParseMcpFile(mcpPath);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOrchestra();
builder.Services.AddSingleton(orchestration);
builder.Services.AddSingleton(mcps);
builder.Services.Configure<OrchestraOptions>(options =>
{
	options.OrchestrationPath = orchestrationPath;
	options.McpPath = mcpPath;
});

var host = builder.Build();

var worker = host.Services.GetRequiredService<OrchestraWorker>();
await worker.RunAsync();

static (string orchestration, string mcp) ParseArgs(string[] args)
{
	string? orchestration = null;
	string? mcp = null;

	for (var i = 0; i < args.Length - 1; i++)
	{
		switch (args[i])
		{
			case "-orchestration":
				orchestration = args[++i];
				break;
			case "-mcp":
				mcp = args[++i];
				break;
		}
	}

	ArgumentException.ThrowIfNullOrWhiteSpace(orchestration, "-orchestration");
	ArgumentException.ThrowIfNullOrWhiteSpace(mcp, "-mcp");

	return (orchestration, mcp);
}
