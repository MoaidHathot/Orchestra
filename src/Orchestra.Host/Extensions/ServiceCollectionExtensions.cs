using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
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
		return services.AddOrchestraHost((options, _) => configure?.Invoke(options));
	}

	/// <summary>
	/// Adds Orchestra Host services to the service collection with access to <see cref="IConfiguration"/>.
	/// The configure callback receives the host's <see cref="IConfiguration"/> resolved from the
	/// service provider, which includes values injected by <c>WebApplicationFactory.ConfigureAppConfiguration</c>.
	/// This overload is the recommended way to wire data-path and other settings in ASP.NET Core apps
	/// because it guarantees test overrides are visible.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <param name="configure">Configuration action receiving options and the host's IConfiguration.</param>
	/// <returns>The service collection for chaining.</returns>
	public static IServiceCollection AddOrchestraHost(
		this IServiceCollection services,
		Action<OrchestrationHostOptions, IConfiguration> configure)
	{
		// Register OrchestrationHostOptions via a factory delegate so creation is
		// deferred until first resolution.  This is critical for
		// WebApplicationFactory-based tests: their ConfigureAppConfiguration
		// callbacks run during Build(), which is *after* AddOrchestraHost() but
		// *before* service resolution.  By deferring options creation we allow
		// test overrides (e.g. data-path) to take effect.
		//
		// We resolve IConfiguration from the service provider — this is the
		// *host's* configuration which includes all sources added by the factory.
		services.AddSingleton(sp =>
		{
			var options = new OrchestrationHostOptions();
			var configuration = sp.GetRequiredService<IConfiguration>();
			var configLogger = sp.GetRequiredService<ILoggerFactory>()
				.CreateLogger("Orchestra.Host.OrchestraConfigLoader");

			// Load config file first (orchestra.json), then let programmatic overrides win
			OrchestraConfigLoader.LoadAndApply(options, configLogger);
			configure.Invoke(options, configuration);

			// Resolve HostBaseUrl: prefer the application's own listening address over
			// the value from orchestra.json.  The config file value may target a different
			// host (e.g. the Server on :5200 while the Portal runs on :5100).  Using the
			// current process's address ensures {{server.url}} resolves correctly for
			// self-referential MCP data-plane connections.
			var appUrl = configuration["Urls"]
				?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
				?? Environment.GetEnvironmentVariable("DOTNET_URLS");
			if (appUrl is not null)
			{
				options.HostBaseUrl = appUrl
					.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.FirstOrDefault()
					?? options.HostBaseUrl;
			}

			// Ensure data path exists
			Directory.CreateDirectory(options.DataPath);

			return options;
		});

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

		// Dashboard event broadcaster: fans out profile/execution changes to connected Portal
		// SSE clients so the UI can update in real time instead of polling.
		services.TryAddSingleton<DashboardEventBroadcaster>();

		// Reporter factory: creates SseReporter instances for all execution paths.
		// Tests can override this with NullOrchestrationReporterFactory via DI.
		services.TryAddSingleton<IOrchestrationReporterFactory, SseReporterFactory>();

		// File-based run store
		services.AddSingleton<FileSystemRunStore>(sp =>
		{
			var opts = sp.GetRequiredService<OrchestrationHostOptions>();
			return new FileSystemRunStore(opts.DataPath, sp.GetRequiredService<ILogger<FileSystemRunStore>>());
		});
		services.AddSingleton<IRunStore>(sp => sp.GetRequiredService<FileSystemRunStore>());

		// File-based checkpoint store
		services.AddSingleton<FileSystemCheckpointStore>(sp =>
		{
			var opts = sp.GetRequiredService<OrchestrationHostOptions>();
			return new FileSystemCheckpointStore(opts.DataPath, sp.GetRequiredService<ILogger<FileSystemCheckpointStore>>());
		});
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
		{
			var opts = sp.GetRequiredService<OrchestrationHostOptions>();
			return new FileSystemOrchestrationVersionStore(opts.DataPath, sp.GetRequiredService<ILogger<FileSystemOrchestrationVersionStore>>());
		});
		services.AddSingleton<IOrchestrationVersionStore>(sp => sp.GetRequiredService<FileSystemOrchestrationVersionStore>());

		// Orchestration registry (with version store wired up)
		services.AddSingleton<OrchestrationRegistry>(sp =>
		{
			var opts = sp.GetRequiredService<OrchestrationHostOptions>();
			var registryPersistPath = Path.Combine(opts.DataPath, "registered-orchestrations.json");
			var logger = sp.GetService<ILogger<OrchestrationRegistry>>();
			var versionStore = sp.GetRequiredService<IOrchestrationVersionStore>();
			return new OrchestrationRegistry(persistPath: registryPersistPath, logger: logger, versionStore: versionStore, dataPath: opts.DataPath);
		});

		// McpManager: manages globally shared MCP servers from orchestra.mcp.json
		services.AddSingleton<McpManager>();

		// ── Profiles & Tags ──

		// Tag store for host-managed orchestration tags
		services.AddSingleton<OrchestrationTagStore>(sp =>
		{
			var opts = sp.GetRequiredService<OrchestrationHostOptions>();
			return new OrchestrationTagStore(opts.DataPath, sp.GetRequiredService<ILogger<OrchestrationTagStore>>());
		});

		// Profile store for file-system profile persistence
		services.AddSingleton<ProfileStore>(sp =>
		{
			var opts = sp.GetRequiredService<OrchestrationHostOptions>();
			return new ProfileStore(opts.DataPath, sp.GetRequiredService<ILogger<ProfileStore>>());
		});

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
			var opts = sp.GetRequiredService<OrchestrationHostOptions>();
			var runsPath = Path.Combine(opts.DataPath, "runs");
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
				mcpResolver: sp.GetRequiredService<McpManager>(),
				dataPath: opts.DataPath,
				serverUrl: opts.HostBaseUrl,
				defaultModel: opts.DefaultModel);

			// Apply shutdown timeout from configuration
			triggerManager.ShutdownTimeout = TimeSpan.FromSeconds(opts.ShutdownTimeoutSeconds);

			return triggerManager;
		});
		services.AddHostedService(sp => sp.GetRequiredService<TriggerManager>());

		// Run retention background service — registered unconditionally; the service
		// itself checks at resolution time whether retention is configured.
		services.AddSingleton(sp => sp.GetRequiredService<OrchestrationHostOptions>().Retention);
		services.AddHostedService<RunRetentionService>();

		// Orchestration sync service — watches the scan directory for file changes.
		// Registered unconditionally; the service exits immediately if watch is disabled.
		services.AddHostedService<OrchestrationSyncService>();

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

	private static void TryAddSingleton<TService>(this IServiceCollection services)
		where TService : class
	{
		if (!services.Any(d => d.ServiceType == typeof(TService)))
		{
			services.AddSingleton<TService>();
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
	public static async Task InitializeOrchestraHostAsync(this IServiceProvider services)
	{
		var options = services.GetRequiredService<OrchestrationHostOptions>();
		var registry = services.GetRequiredService<OrchestrationRegistry>();
		var triggerManager = services.GetRequiredService<TriggerManager>();
		var profileManager = services.GetRequiredService<ProfileManager>();
		var mcpManager = services.GetRequiredService<McpManager>();
		var initLogger = services.GetRequiredService<ILoggerFactory>()
			.CreateLogger("Orchestra.Host.Initialization");

		// Initialize McpManager: load global orchestra.mcp.json and start proxy
		var globalMcpPath = OrchestraConfigLoader.ResolveGlobalMcpPath();
		Engine.Mcp[] globalMcps = [];
		if (globalMcpPath is not null)
		{
			globalMcps = OrchestrationParser.ParseMcpFile(globalMcpPath);
		}
		await mcpManager.InitializeAsync(globalMcps);

		// Make global MCPs available to the registry for parsing orchestration files
		registry.GlobalMcps = globalMcps;
		triggerManager.GlobalMcps = globalMcps;

		// Load persisted orchestrations
		if (options.LoadPersistedOrchestrations)
		{
			registry.LoadFromDisk();
		}

		// Sync orchestrations and profiles from the configured scan directory
		var scanConfig = options.Scan;
		if (scanConfig is not null && Directory.Exists(scanConfig.Directory))
		{
			initLogger.LogInformation(
				"Scan config active: Directory={Directory}, Watch={Watch}, Recursive={Recursive}",
				scanConfig.Directory, scanConfig.Watch, scanConfig.Recursive);

			// Sync orchestrations from the orchestrations/ subdirectory
			var orchestrationsDir = Path.Combine(scanConfig.Directory, OrchestrationSyncService.OrchestrationsDirName);
			if (Directory.Exists(orchestrationsDir))
			{
				registry.SyncDirectory(orchestrationsDir, scanConfig.Recursive);
			}

			// Sync profiles from the profiles/ subdirectory
			var profileStore = services.GetRequiredService<ProfileStore>();
			var profilesDir = Path.Combine(scanConfig.Directory, OrchestrationSyncService.ProfilesDirName);
			if (Directory.Exists(profilesDir))
			{
				var syncResult = profileStore.SyncDirectory(profilesDir);
				initLogger.LogInformation(
					"Profile sync from {Directory}: {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Failed} failed",
					profilesDir, syncResult.Added, syncResult.Updated, syncResult.Removed, syncResult.Unchanged, syncResult.Failed);
			}
			else
			{
				initLogger.LogDebug("No profiles/ subdirectory found in scan directory {Directory}", scanConfig.Directory);
			}
		}
		else if (scanConfig is not null)
		{
			initLogger.LogWarning("Scan directory does not exist: {Directory}", scanConfig.Directory);
		}
		else
		{
			initLogger.LogDebug("No scan config set. Profiles will not be auto-loaded from a workspace directory.");
		}

		// Register triggers for loaded orchestrations
		if (options.RegisterJsonTriggers)
		{
			foreach (var entry in registry.GetAll())
			{
				var trigger = entry.Orchestration.Trigger;
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
					effectiveTrigger,
					null,
					TriggerSource.Json,
					entry.Id,
					entry.Orchestration);
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

		// Subscribe the dashboard broadcaster to profile changes so the Portal receives a
		// real-time push when schedules activate/deactivate profiles (no polling needed).
		var dashboardBroadcaster = services.GetRequiredService<DashboardEventBroadcaster>();
		profileManager.OnEffectiveActiveSetChanged += evt =>
		{
			dashboardBroadcaster.BroadcastProfileActiveSetChanged(
				evt.ActivatedOrchestrationIds,
				evt.DeactivatedOrchestrationIds,
				evt.Trigger);
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
		_ = Task.Run(async () =>
		{
			try
			{
				await runStore.PreloadIndexAsync();
			}
			catch (Exception ex)
			{
				var preloadLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ServiceProviderExtensions));
				preloadLogger.LogError(ex, "Failed to preload run-history index");
			}
		});
	}
}
