using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Orchestra.Mcp.Graph.Authentication;
using Orchestra.Mcp.Graph.Configuration;
using Orchestra.Mcp.Graph.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (required for MCP stdio transport)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Configure Graph options from environment variables
var graphOptions = new GraphOptions();
builder.Services.AddSingleton(graphOptions);

// Add HttpClient
builder.Services.AddHttpClient();

// Add authentication services
builder.Services.AddSingleton<TokenCache>();
builder.Services.AddSingleton<AzureCliTokenProvider>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<GraphOptions>();
    var tokenCache = sp.GetRequiredService<TokenCache>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<OAuthTokenProvider>>();
    return new OAuthTokenProvider(options, tokenCache, httpClientFactory.CreateClient(), logger);
});
builder.Services.AddSingleton<DualTokenProvider>();
builder.Services.AddSingleton<ITokenProvider>(sp => sp.GetRequiredService<DualTokenProvider>());

// Add Graph API client
builder.Services.AddSingleton(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var tokenProvider = sp.GetRequiredService<ITokenProvider>();
    var options = sp.GetRequiredService<GraphOptions>();
    var logger = sp.GetRequiredService<ILogger<GraphApiClient>>();
    return new GraphApiClient(httpClientFactory.CreateClient(), tokenProvider, options, logger);
});

// Add MCP server with stdio transport and auto-discover tools
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
