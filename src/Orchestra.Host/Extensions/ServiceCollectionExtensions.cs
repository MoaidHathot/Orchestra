using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Extensions;

/// <summary>
/// Extension methods for registering Orchestra Host services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Adds Orchestra Host services to the service collection.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <param name="configure">Optional configuration action.</param>
	/// <returns>The service collection for chaining.</returns>
	public static IServiceCollection AddOrchestraHost(
		this IServiceCollection services,
		Action<OrchestrationHostOptions>? configure = null)
	{
		var options = new OrchestrationHostOptions();
		configure?.Invoke(options);

		// Ensure data path exists
		Directory.CreateDirectory(options.DataPath);

		// Register options
		services.AddSingleton(options);

		// Register engine services (if not already registered by the consumer)
		services.TryAddSingleton<IScheduler, OrchestrationScheduler>();

		// Register default execution callback if none provided
		// This must be done before TriggerManager is created
		services.TryAddSingleton<ITriggerExecutionCallback, DefaultExecutionCallback>();

		// File-based run store
		var runStore = new FileSystemRunStore(options.DataPath);
		services.AddSingleton<FileSystemRunStore>(runStore);
		services.AddSingleton<IRunStore>(runStore);

		// File-based checkpoint store
		services.AddSingleton<FileSystemCheckpointStore>(sp =>
			new FileSystemCheckpointStore(options.DataPath, sp.GetRequiredService<ILogger<FileSystemCheckpointStore>>()));
		services.AddSingleton<ICheckpointStore>(sp => sp.GetRequiredService<FileSystemCheckpointStore>());

		// Step type parser registry with built-in parsers (stateless, safe as singleton)
		services.AddSingleton<StepTypeParserRegistry>(_ => OrchestrationParser.CreateDefaultParserRegistry());

		// Active executions tracking
		var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		var activeExecutionInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		services.AddSingleton(activeExecutions);
		services.AddSingleton(activeExecutionInfos);

		// Orchestration registry
		var registryPersistPath = Path.Combine(options.DataPath, "registered-orchestrations.json");
		services.AddSingleton<OrchestrationRegistry>(sp =>
		{
			var logger = sp.GetService<ILogger<OrchestrationRegistry>>();
			return new OrchestrationRegistry(persistPath: registryPersistPath, logger: logger);
		});

		// TriggerManager as a hosted background service
		services.AddSingleton<TriggerManager>(sp =>
		{
			var runsPath = Path.Combine(options.DataPath, "runs");
			Directory.CreateDirectory(runsPath);

			return new TriggerManager(
				sp.GetRequiredService<ConcurrentDictionary<string, CancellationTokenSource>>(),
				sp.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>(),
				sp.GetRequiredService<AgentBuilder>(),
				sp.GetRequiredService<IScheduler>(),
				sp.GetRequiredService<ILoggerFactory>(),
				sp.GetRequiredService<ILogger<TriggerManager>>(),
				runsPath,
				sp.GetRequiredService<IRunStore>(),
				sp.GetRequiredService<ICheckpointStore>(),
				sp.GetRequiredService<ITriggerExecutionCallback>());
		});
		services.AddHostedService(sp => sp.GetRequiredService<TriggerManager>());

		return services;
	}

	/// <summary>
	/// Adds a trigger execution callback to the service collection.
	/// Call this before AddOrchestraHost if you want to provide a custom callback.
	/// </summary>
	public static IServiceCollection AddTriggerExecutionCallback<T>(this IServiceCollection services)
		where T : class, ITriggerExecutionCallback
	{
		services.AddSingleton<ITriggerExecutionCallback, T>();
		return services;
	}

	/// <summary>
	/// Adds a trigger execution callback instance to the service collection.
	/// Call this before AddOrchestraHost if you want to provide a custom callback.
	/// </summary>
	public static IServiceCollection AddTriggerExecutionCallback(
		this IServiceCollection services,
		ITriggerExecutionCallback callback)
	{
		services.AddSingleton(callback);
		return services;
	}

	// Helper to conditionally register services
	private static void TryAddSingleton<TService, TImplementation>(this IServiceCollection services)
		where TService : class
		where TImplementation : class, TService
	{
		if (!services.Any(d => d.ServiceType == typeof(TService)))
		{
			services.AddSingleton<TService, TImplementation>();
		}
	}
}

/// <summary>
/// Extension methods for initializing Orchestra Host after the host is built.
/// </summary>
public static class ServiceProviderExtensions
{
	/// <summary>
	/// Initializes Orchestra Host by loading persisted orchestrations and registering triggers.
	/// Call this after the host is built but before it starts.
	/// </summary>
	public static IServiceProvider InitializeOrchestraHost(this IServiceProvider services)
	{
		var options = services.GetRequiredService<OrchestrationHostOptions>();
		var registry = services.GetRequiredService<OrchestrationRegistry>();
		var triggerManager = services.GetRequiredService<TriggerManager>();

		// Load persisted orchestrations
		if (options.LoadPersistedOrchestrations)
		{
			registry.LoadFromDisk();
		}

		// Scan for orchestrations in the specified directory
		if (!string.IsNullOrEmpty(options.OrchestrationsScanPath) && Directory.Exists(options.OrchestrationsScanPath))
		{
			registry.ScanDirectory(options.OrchestrationsScanPath);
		}

		// Register triggers for loaded orchestrations
		if (options.RegisterJsonTriggers)
		{
			foreach (var entry in registry.GetAll())
			{
				if (entry.Orchestration.Trigger is { } trigger && trigger.Enabled)
				{
					triggerManager.RegisterTrigger(
						entry.Path,
						entry.McpPath,
						trigger,
						null,
						TriggerSource.Json,
						entry.Id);
				}
			}
		}

		return services;
	}
}
