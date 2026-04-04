using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace Orchestra.Host.McpServer;

/// <summary>
/// Extension methods for registering and mapping Orchestra MCP server endpoints.
/// </summary>
public static class McpServerExtensions
{
	/// <summary>
	/// Adds Orchestra MCP server services to the service collection.
	/// Call this after <c>AddOrchestraHost()</c> to enable MCP server endpoints.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <param name="configure">Optional configuration action for MCP server options.</param>
	/// <returns>The service collection for chaining.</returns>
	public static IServiceCollection AddOrchestraMcpServer(
		this IServiceCollection services,
		Action<McpServerOptions>? configure = null)
	{
		var options = new McpServerOptions();
		configure?.Invoke(options);
		services.AddSingleton(options);

		// Register the MCP server with HTTP transport and data plane tools
		services.AddMcpServer(mcpOptions =>
		{
			mcpOptions.ServerInfo = new()
			{
				Name = "Orchestra",
				Version = "1.0.0",
			};
		})
		.WithHttpTransport()
		.WithTools<DataPlaneTools>();

		return services;
	}

	/// <summary>
	/// Maps Orchestra MCP server endpoints to the application.
	/// Maps both data-plane and control-plane MCP endpoints based on configuration.
	/// </summary>
	/// <param name="endpoints">The endpoint route builder.</param>
	/// <returns>The endpoint route builder for chaining.</returns>
	public static IEndpointRouteBuilder MapOrchestraMcpEndpoints(this IEndpointRouteBuilder endpoints)
	{
		var options = endpoints.ServiceProvider.GetService<McpServerOptions>() ?? new McpServerOptions();

		if (options.DataPlaneEnabled)
		{
			endpoints.MapMcp(options.DataPlaneRoute);
		}

		return endpoints;
	}
}
