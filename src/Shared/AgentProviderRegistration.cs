using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.OpenCode;

namespace Orchestra.Composition;

/// <summary>
/// Shared composition helper (linked into <c>Orchestra.Server</c> and <c>Orchestra.Exec</c>)
/// that registers every agent provider as a keyed <see cref="AgentBuilder"/> plus an
/// <see cref="IAgentProviderRegistry"/> for per-step / per-orchestration provider selection.
/// The default provider comes from <c>orchestra.json</c>'s top-level <c>provider</c>
/// (<see cref="OrchestrationHostOptions.Provider"/>), falling back to <c>copilot</c>.
/// </summary>
internal static class AgentProviderRegistration
{
	public const string Copilot = "copilot";
	public const string OpenCode = "opencode";

	public static IServiceCollection AddOrchestraAgentProviders(this IServiceCollection services)
	{
		services.AddKeyedSingleton<AgentBuilder>(Copilot, (sp, _) => CreateCopilotBuilder(sp));
		services.AddKeyedSingleton<AgentBuilder>(OpenCode, (sp, _) => CreateOpenCodeBuilder(sp));

		services.AddSingleton<IAgentProviderRegistry>(sp =>
		{
			var options = sp.GetRequiredService<OrchestrationHostOptions>();
			var builders = new Dictionary<string, AgentBuilder>(StringComparer.OrdinalIgnoreCase)
			{
				[Copilot] = sp.GetRequiredKeyedService<AgentBuilder>(Copilot),
				[OpenCode] = sp.GetRequiredKeyedService<AgentBuilder>(OpenCode),
			};

			var defaultProvider = string.IsNullOrWhiteSpace(options.Provider) ? Copilot : options.Provider!.Trim();
			if (!builders.ContainsKey(defaultProvider))
				defaultProvider = Copilot;

			// Test-override hook: a non-keyed AgentBuilder (e.g. a fake registered via
			// hooks.ConfigureServices / WebApplicationFactory) becomes the default provider so
			// existing single-provider tests keep injecting their builder. Production registers no
			// non-keyed AgentBuilder, so this is null and the configured copilot/opencode default wins.
			var overrideBuilder = sp.GetService<AgentBuilder>();
			if (overrideBuilder is not null)
			{
				builders["__override__"] = overrideBuilder;
				defaultProvider = "__override__";
			}

			return new AgentProviderRegistry(builders, defaultProvider);
		});

		return services;
	}

	private static CopilotAgentBuilder CreateCopilotBuilder(IServiceProvider sp)
	{
		var options = sp.GetRequiredService<OrchestrationHostOptions>();
		var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
		var swap = options.Copilot.Swap;
		var copilotPoolOptions = new CopilotAgentPoolOptions
		{
			// Only override the built-in defaults when the host actually set a value; otherwise the
			// options-class defaults (kept in sync across providers) are the single source of truth.
			CliSwapBudgetPerStep = swap.BudgetPerStep,
			ResumeOnSwapEnabled = swap.ResumeOnSwap,
			ResumeAlreadyInUseWait = TimeSpan.FromSeconds(Math.Max(0, swap.ResumeAlreadyInUseWaitSeconds)),
			ResumeAlreadyInUsePollInterval = TimeSpan.FromMilliseconds(Math.Max(1, swap.ResumeAlreadyInUsePollIntervalMs)),
			GitHubToken = options.Copilot.GitHubToken,
			UseLoggedInUser = options.Copilot.UseLoggedInUser,
			McpStartupTimeout = options.Copilot.McpStartupTimeoutSeconds is { } mcpStartupSeconds
				? TimeSpan.FromSeconds(Math.Max(0, mcpStartupSeconds))
				: new CopilotAgentPoolOptions().McpStartupTimeout,
		};
		if (options.AgentPool.MinInstances is { } copilotMin) copilotPoolOptions.DefaultMinInstances = copilotMin;
		if (options.AgentPool.MaxInstances is { } copilotMax) copilotPoolOptions.DefaultMaxInstancesPerRun = copilotMax;
		if (options.AgentPool.MaxSessionsPerInstance is { } copilotSessions) copilotPoolOptions.DefaultMaxSessionsPerInstance = copilotSessions;
		if (options.AgentPool.IdleTimeoutSeconds is { } copilotIdle) copilotPoolOptions.DefaultIdleTimeoutSeconds = copilotIdle;

		return new CopilotAgentBuilder(loggerFactory, copilotPoolOptions);
	}

	private static OpenCodeAgentBuilder CreateOpenCodeBuilder(IServiceProvider sp)
	{
		var options = sp.GetRequiredService<OrchestrationHostOptions>();
		var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
		var oc = options.OpenCode;

		var poolOptions = new OpenCodeAgentPoolOptions();
		// Only override the built-in defaults when the host actually set a value; otherwise the
		// options-class defaults (kept in sync across providers) are the single source of truth.
		if (options.AgentPool.MinInstances is { } ocMin) poolOptions.DefaultMinInstances = ocMin;
		if (options.AgentPool.MaxInstances is { } ocMax) poolOptions.DefaultMaxInstancesPerRun = ocMax;
		if (options.AgentPool.MaxSessionsPerInstance is { } ocSessions) poolOptions.DefaultMaxSessionsPerInstance = ocSessions;
		if (options.AgentPool.IdleTimeoutSeconds is { } ocIdle) poolOptions.DefaultIdleTimeoutSeconds = ocIdle;

		if (!string.IsNullOrWhiteSpace(oc.CliPath)) poolOptions.CliPath = oc.CliPath;
		if (!string.IsNullOrWhiteSpace(oc.Hostname)) poolOptions.Hostname = oc.Hostname!;
		if (!string.IsNullOrWhiteSpace(oc.ServerPassword)) poolOptions.ServerPassword = oc.ServerPassword;
		if (!string.IsNullOrWhiteSpace(oc.ServerUsername)) poolOptions.ServerUsername = oc.ServerUsername!;
		if (!string.IsNullOrWhiteSpace(oc.FallbackProvider)) poolOptions.FallbackProvider = oc.FallbackProvider!;
		if (oc.StartupTimeoutSeconds is > 0) poolOptions.StartupTimeout = TimeSpan.FromSeconds(oc.StartupTimeoutSeconds.Value);
		if (oc.EngineToolBridgeEnabled.HasValue) poolOptions.EngineToolBridgeEnabled = oc.EngineToolBridgeEnabled.Value;
		if (oc.SwapBudgetPerStep is >= 0) poolOptions.SwapBudgetPerStep = oc.SwapBudgetPerStep.Value;
		if (oc.ResumeOnSwapEnabled.HasValue) poolOptions.ResumeOnSwapEnabled = oc.ResumeOnSwapEnabled.Value;

		return new OpenCodeAgentBuilder(loggerFactory, poolOptions);
	}
}
