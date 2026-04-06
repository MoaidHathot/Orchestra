using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Orchestra.Host.Hosting;

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

		// Apply config file settings first (if present)
		var configFile = OrchestraConfigLoader.Load();
		if (configFile?.McpServer is { } mcpConfig)
		{
			if (mcpConfig.DataPlaneEnabled.HasValue)
				options.DataPlaneEnabled = mcpConfig.DataPlaneEnabled.Value;
			if (mcpConfig.DataPlaneRoute is not null)
				options.DataPlaneRoute = mcpConfig.DataPlaneRoute;
			if (mcpConfig.ControlPlaneEnabled.HasValue)
				options.ControlPlaneEnabled = mcpConfig.ControlPlaneEnabled.Value;
			if (mcpConfig.ControlPlaneRoute is not null)
				options.ControlPlaneRoute = mcpConfig.ControlPlaneRoute;
			if (mcpConfig.MaxNestingDepth.HasValue)
				options.MaxNestingDepth = mcpConfig.MaxNestingDepth.Value;
		}

		// Then let programmatic overrides win
		configure?.Invoke(options);
		services.AddSingleton(options);

		// Register all tools. Route-based filtering in ConfigureSessionOptions
		// controls which tools are available per endpoint.
		var builder = services.AddMcpServer(mcpOptions =>
		{
			mcpOptions.ServerInfo = new()
			{
				Name = "Orchestra",
				Version = "1.0.0",
			};
		})
		.WithHttpTransport(httpOptions =>
		{
			httpOptions.ConfigureSessionOptions = (httpContext, sessionOptions, _) =>
			{
				var toolCollection = sessionOptions.ToolCollection;
				if (toolCollection is null)
					return Task.CompletedTask;

				var path = httpContext.Request.Path.Value ?? "";

				if (path.StartsWith(options.ControlPlaneRoute, StringComparison.OrdinalIgnoreCase))
				{
					// Control plane: keep only ControlPlaneTools
					RemoveToolsNotOfType<ControlPlaneTools>(toolCollection);
				}
				else
				{
					// Data plane (default): keep only DataPlaneTools
					RemoveToolsNotOfType<DataPlaneTools>(toolCollection);
				}

				return Task.CompletedTask;
			};
		})
		.WithTools<DataPlaneTools>();

		if (options.ControlPlaneEnabled)
		{
			builder.WithTools<ControlPlaneTools>();
		}

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

		if (options.ControlPlaneEnabled)
		{
			endpoints.MapMcp(options.ControlPlaneRoute);
		}

		return endpoints;
	}

	/// <summary>
	/// Removes all tools from the collection that were NOT defined in the specified type.
	/// Uses reflection to match tool names against methods with <see cref="McpServerToolAttribute"/>.
	/// When the attribute specifies a custom <c>Name</c>, that name is used; otherwise the method name.
	/// </summary>
	private static void RemoveToolsNotOfType<T>(McpServerPrimitiveCollection<McpServerTool> tools)
	{
		var keepNames = new HashSet<string>(
			typeof(T).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
				.Select(m =>
				{
					var attr = m.GetCustomAttributes(typeof(McpServerToolAttribute), false)
						.OfType<McpServerToolAttribute>()
						.FirstOrDefault();
					if (attr is null) return null;
					// Use the explicit Name from the attribute if set, otherwise fall back to the method name
					return !string.IsNullOrEmpty(attr.Name) ? attr.Name : m.Name;
				})
				.Where(name => name is not null)!,
			StringComparer.OrdinalIgnoreCase);

		var toRemove = tools.Where(t => !keepNames.Contains(t.ProtocolTool.Name)).ToList();
		foreach (var tool in toRemove)
		{
			tools.Remove(tool);
		}
	}
}
