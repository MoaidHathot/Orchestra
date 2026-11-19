using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Orchestra.Engine;

namespace Orchestra.Host.Mcp;

/// <summary>Exercise the same backend and tool policy a prompt step receives, without starting workflows.</summary>
public static class ManagedMcpProbe
{
	public static async Task<string[]> ListToolsAsync(RemoteMcp definition, CancellationToken token)
	{
		await using var manager = new McpManager(NullLogger<McpManager>.Instance) { ProbeDelegatedAtStartup = true };
		await manager.InitializeAsync([definition], token);
		var route = (RemoteMcp)manager.Resolve([definition])[0];
		var transport = new HttpClientTransport(new() { Endpoint = new Uri(route.Endpoint), TransportMode = HttpTransportMode.StreamableHttp });
		await using var client = await McpClient.CreateAsync(transport, cancellationToken: token);
		var tools = await client.ListToolsAsync(cancellationToken: token);
		if (definition.AllowedTools is not null && tools.Count == 0)
			throw new InvalidOperationException($"MCP '{definition.Name}' exposed none of its configured allowedTools.");
		return tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
	}
}
