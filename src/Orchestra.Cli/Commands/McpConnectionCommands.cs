using System.ComponentModel;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

public sealed class McpConnectionSettings : CommandSettings
{
	[CommandArgument(0, "<NAME>")]
	[Description("Global remote MCP name in the effective orchestra.mcp.json.")]
	public required string Name { get; set; }

	internal RemoteMcp GetConnection()
	{
		var path = OrchestraConfigLoader.ResolveSidecar(OrchestraConfigLoader.McpConfigFileName).EffectivePath
			?? throw new InvalidOperationException("No orchestra.mcp.json beside the effective orchestra.json.");
		return OrchestrationParser.ParseNamedMcpFile(path, Name) as RemoteMcp
			?? throw new InvalidOperationException($"Remote MCP '{Name}' not found in {path}.");
	}
}

public sealed class McpConnectCommand(IAnsiConsole console) : AsyncCommand<McpConnectionSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, McpConnectionSettings settings)
	{
		var connection = settings.GetConnection();
		if (connection.Auth is null) throw new InvalidOperationException($"No Entra authentication configured for '{connection.Name}'.");
		console.WriteLine($"Sign in to {connection.Endpoint} (tenant {connection.Auth.TenantId}, scopes: {string.Join(", ", connection.Auth.Scopes)}).");
		using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		await new EntraMcpCredentials().ConnectAsync(connection, deadline.Token);
		console.WriteLine($"Account saved. Check silent discovery with: orchestra mcp test {connection.Name}");
		return 0;
	}
}

public sealed class McpTestCommand(IAnsiConsole console) : AsyncCommand<McpConnectionSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, McpConnectionSettings settings)
	{
		var connection = settings.GetConnection();
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
		var tools = await ManagedMcpProbe.ListToolsAsync(connection, deadline.Token);
		console.WriteLine($"{connection.Name}: {tools.Length} tools: {string.Join(", ", tools)}");
		return 0;
	}
}
