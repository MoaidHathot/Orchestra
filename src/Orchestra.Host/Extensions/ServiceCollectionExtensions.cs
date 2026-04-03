using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Orchestra.Host.Profiles;
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

		// Load config file first (orchestra.json), then let programmatic overrides win
		OrchestraConfigLoader.LoadAndApply(options);
		configure?.Invoke(options);

		// Ensure data path exists
		Directory.CreateDirectory(options.DataPath);

		// Register options
		services.AddSingleton(options);

		// Register engine services (if not already registered by the consumer)
		services.TryAddSingleton<IScheduler, OrchestrationScheduler>();

		// Engine tool registry (default includes all built-in tools; consumers can customize via AddEngineTools)
		if (!services.Any(d => d.ServiceType == typeof(EngineToolRegistry)))
		{
			services.AddSingleton(EngineToolRegistry.CreateDefault());
		}

		// Register default execution callback if none provided
		// This must be done before TriggerManager is created
		services.TryAddSingleton<ITriggerExecutionCallback, DefaultExecutionCallback>();

		// File-based run store
		services.AddSingleton<FileSystemRunStore>(sp =>
			new FileSystemRunStore(options.DataPath, sp.GetRequiredService<ILogger<FileSystemRunStore>>()));
		services.AddSingleton<IRunStore>(sp => sp.GetRequiredService<FileSystemRunStore>());

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

		// Version store for tracking orchestration version history
		services.AddSingleton<FileSystemOrchestrationVersionStore>(sp =>
			new FileSystemOrchestrationVersionStore(options.DataPath, sp.GetRequiredService<ILogger<FileSystemOrchestrationVersionStore>>()));
		services.AddSingleton<IOrchestrationVersionStore>(sp => sp.GetRequiredService<FileSystemOrchestrationVersionStore>());

		// Orchestration registry (with version store wired up)
		var registryPersistPath = Path.Combine(options.DataPath, "registered-orchestrations.json");
		services.AddSingleton<OrchestrationRegistry>(sp =>
		{
			var logger = sp.GetService<ILogger<OrchestrationRegistry>>();
			var versionStore = sp.GetRequiredService<IOrchestrationVersionStore>();
			return new OrchestrationRegistry(persistPath: registryPersistPath, logger: logger, versionStore: versionStore, dataPath: options.DataPath);
		});

		// ── Profiles & Tags ──

		// Tag store for host-managed orchestration tags
		services.AddSingleton<OrchestrationTagStore>(sp =>
			new OrchestrationTagStore(options.DataPath, sp.GetRequiredService<ILogger<OrchestrationTagStore>>()));

		// Profile store for file-system profile persistence
		services.AddSingleton<ProfileStore>(sp =>
			new ProfileStore(options.DataPath, sp.GetRequiredService<ILogger<ProfileStore>>()));

		// Profile manager as a hosted background service (schedule evaluation loop)
		services.AddSingleton<ProfileManager>(sp =>
			new ProfileManager(
				sp.GetRequiredService<ProfileStore>(),
				sp.GetRequiredService<OrchestrationTagStore>(),
				sp.GetRequiredService<OrchestrationRegistry>(),
				sp.GetRequiredService<ILogger<ProfileManager>>()));
		services.AddHostedService(sp => sp.GetRequiredService<ProfileManager>());

		// TriggerManager as a hosted background service
		services.AddSingleton<TriggerManager>(sp =>
		{
			var runsPath = Path.Combine(options.DataPath, "runs");
			Directory.CreateDirectory(runsPath);

			var triggerManager = new TriggerManager(
				sp.GetRequiredService<ConcurrentDictionary<string, CancellationTokenSource>>(),
				sp.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>(),
				sp.GetRequiredService<AgentBuilder>(),
				sp.GetRequiredService<IScheduler>(),
				sp.GetRequiredService<ILoggerFactory>(),
				sp.GetRequiredService<ILogger<TriggerManager>>(),
				runsPath,
				sp.GetRequiredService<IRunStore>(),
				sp.GetRequiredService<ICheckpointStore>(),
				sp.GetRequiredService<ITriggerExecutionCallback>(),
				sp.GetRequiredService<EngineToolRegistry>(),
				dataPath: options.DataPath);

			// Apply shutdown timeout from configuration
			triggerManager.ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds);

			return triggerManager;
		});
		services.AddHostedService(sp => sp.GetRequiredService<TriggerManager>());

		// Run retention background service (only when retention limits are configured)
		if (!options.Retention.IsForever)
		{
			services.AddSingleton(options.Retention);
			services.AddHostedService<RunRetentionService>();
		}

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

	/// <summary>
	/// Configures the engine tool registry with custom tools.
	/// Call this before AddOrchestraHost to customize the tools available to prompt steps.
	/// The configure action receives a default registry pre-populated with built-in tools.
	/// </summary>
	public static IServiceCollection AddEngineTools(
		this IServiceCollection services,
		Action<EngineToolRegistry> configure)
	{
		var registry = EngineToolRegistry.CreateDefault();
		configure(registry);
		// Remove any previously registered EngineToolRegistry
		var existing = services.FirstOrDefault(d => d.ServiceType == typeof(EngineToolRegistry));
		if (existing is not null)
			services.Remove(existing);
		services.AddSingleton(registry);
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
		var profileManager = services.GetRequiredService<ProfileManager>();

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
				if (entry.Orchestration.Trigger is { } trigger)
				{
					var triggerId = entry.Id;

					// Check for a persisted enabled-state override (user disabled/enabled this JSON trigger previously)
					var enabledOverride = triggerManager.GetJsonTriggerEnabledOverride(triggerId);
					var effectiveEnabled = enabledOverride ?? trigger.Enabled;

					if (!effectiveEnabled && !trigger.Enabled)
						continue; // Disabled in JSON and no override — skip entirely

					// Register with the effective enabled state
					var effectiveTrigger = effectiveEnabled != trigger.Enabled
						? TriggerManager.CloneTriggerConfigWithEnabled(trigger, effectiveEnabled)
						: trigger;

					triggerManager.RegisterTrigger(
						entry.Path,
						entry.McpPath,
						effectiveTrigger,
						null,
						TriggerSource.Json,
						triggerId,
						entry.Orchestration);
				}
			}
		}

		// Initialize profile manager: loads profiles, ensures default, computes initial active set
		profileManager.Initialize();

		// Subscribe TriggerManager to profile changes: enable/disable triggers based on active set
		profileManager.OnEffectiveActiveSetChanged += evt =>
		{
			foreach (var id in evt.ActivatedOrchestrationIds)
			{
				triggerManager.SetTriggerEnabled(id, true);
			}
			foreach (var id in evt.DeactivatedOrchestrationIds)
			{
				triggerManager.SetTriggerEnabled(id, false);
			}
		};

		// Apply the initial effective active set to trigger states.
		// Orchestrations NOT in the active set should have their triggers disabled.
		var activeIds = profileManager.GetEffectiveActiveOrchestrationIds();
		foreach (var trigger in triggerManager.GetAllTriggers())
		{
			if (!activeIds.Contains(trigger.Id) && trigger.Config.Enabled)
			{
				triggerManager.SetTriggerEnabled(trigger.Id, false);
			}
		}

		// Fire-and-forget preload of the run-history index so the first
		// /api/history request doesn't pay the cold-load penalty.
		var runStore = services.GetRequiredService<FileSystemRunStore>();
		_ = Task.Run(() => runStore.PreloadIndexAsync());

		return services;
	}
}
