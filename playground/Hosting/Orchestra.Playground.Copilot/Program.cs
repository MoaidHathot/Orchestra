using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orchestra.Engine;
using Orchestra.Playground.Copilot;

var (orchestrationPath, mcpPath, parameters, printResult) = ParseArgs(args);

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
await worker.RunAsync(parameters, printResult);

static (string orchestration, string mcp, Dictionary<string, string> parameters, bool printResult) ParseArgs(string[] args)
{
	string? orchestration = null;
	string? mcp = null;
	var parameters = new Dictionary<string, string>();
	var printResult = false;

	for (var i = 0; i < args.Length; i++)
	{
		switch (args[i])
		{
			case "-orchestration" when i + 1 < args.Length:
				orchestration = args[++i];
				break;
			case "-mcp" when i + 1 < args.Length:
				mcp = args[++i];
				break;
			case "-param" when i + 1 < args.Length:
				var param = args[++i];
				var eqIndex = param.IndexOf('=');
				if (eqIndex > 0)
				{
					var key = param[..eqIndex];
					var value = param[(eqIndex + 1)..];
					parameters[key] = value;
				}
				break;
			case "-print":
				printResult = true;
				break;
		}
	}

	ArgumentException.ThrowIfNullOrWhiteSpace(orchestration, "-orchestration");
	ArgumentException.ThrowIfNullOrWhiteSpace(mcp, "-mcp");

	return (orchestration, mcp, parameters, printResult);
}
