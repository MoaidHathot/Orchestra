namespace Orchestra.Engine;

/// <summary>
/// Default <see cref="IAgentProviderRegistry"/> backed by a name → builder map. Provider
/// names match case-insensitively. An unknown, non-empty provider name throws so
/// orchestration authors catch typos early; a null/empty name resolves to the configured
/// <see cref="DefaultProviderName"/>.
/// </summary>
public sealed class AgentProviderRegistry : IAgentProviderRegistry
{
	private readonly Dictionary<string, AgentBuilder> _builders;
	private readonly AgentBuilder[] _distinctBuilders;

	public AgentProviderRegistry(IReadOnlyDictionary<string, AgentBuilder> builders, string defaultProviderName)
	{
		ArgumentNullException.ThrowIfNull(builders);
		ArgumentException.ThrowIfNullOrWhiteSpace(defaultProviderName);
		if (builders.Count == 0)
			throw new ArgumentException("At least one agent provider must be registered.", nameof(builders));

		_builders = new Dictionary<string, AgentBuilder>(builders, StringComparer.OrdinalIgnoreCase);
		if (!_builders.ContainsKey(defaultProviderName))
		{
			throw new ArgumentException(
				$"Default provider '{defaultProviderName}' is not among the registered providers: " +
				$"{string.Join(", ", _builders.Keys)}.",
				nameof(defaultProviderName));
		}

		DefaultProviderName = defaultProviderName;
		// Reference-distinct: aliases that map to the same builder instance collapse to one
		// entry so the executor opens a single run scope per backing builder.
		_distinctBuilders = [.. _builders.Values.Distinct()];
	}

	public string DefaultProviderName { get; }

	public IReadOnlyCollection<string> ProviderNames => _builders.Keys;

	public IReadOnlyCollection<AgentBuilder> Builders => _distinctBuilders;

	public AgentBuilder Resolve(string? providerName)
	{
		if (string.IsNullOrWhiteSpace(providerName))
			return _builders[DefaultProviderName];

		if (_builders.TryGetValue(providerName.Trim(), out var builder))
			return builder;

		throw new InvalidOperationException(
			$"Unknown agent provider '{providerName}'. Registered providers: {string.Join(", ", _builders.Keys)}. " +
			"Set a valid 'provider' on the step, 'defaultProvider' on the orchestration, or configure the host default provider.");
	}
}

/// <summary>
/// Back-compat <see cref="IAgentProviderRegistry"/> that wraps a single
/// <see cref="AgentBuilder"/>. Every provider name resolves to the one builder, preserving
/// the historical single-provider behaviour for hosts and tests that register exactly one
/// agent builder. This is what the engine's <see cref="AgentBuilder"/>-only constructors use.
/// A null builder is tolerated (some hosts/tests run orchestrations with no Prompt steps and
/// pass none); resolving one then throws a clear error rather than NRE-ing.
/// </summary>
public sealed class SingleAgentProviderRegistry : IAgentProviderRegistry
{
	private readonly AgentBuilder? _builder;
	private readonly AgentBuilder[] _builders;

	public SingleAgentProviderRegistry(AgentBuilder? builder, string providerName = "default")
	{
		_builder = builder;
		DefaultProviderName = string.IsNullOrWhiteSpace(providerName) ? "default" : providerName;
		_builders = builder is null ? [] : [builder];
	}

	public string DefaultProviderName { get; }

	public IReadOnlyCollection<string> ProviderNames => [DefaultProviderName];

	public IReadOnlyCollection<AgentBuilder> Builders => _builders;

	public AgentBuilder Resolve(string? providerName)
		=> _builder ?? throw new InvalidOperationException(
			"No agent builder is configured. Register an AgentBuilder / IAgentProviderRegistry before executing Prompt steps.");
}
