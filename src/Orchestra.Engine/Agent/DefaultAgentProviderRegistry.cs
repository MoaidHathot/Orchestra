namespace Orchestra.Engine;

/// <summary>Captures a host's chosen default for one execution, without changing active runs.</summary>
internal sealed class DefaultAgentProviderRegistry(IAgentProviderRegistry inner, string defaultProvider) : IAgentProviderRegistry
{
	public string DefaultProviderName { get; } = defaultProvider;
	public IReadOnlyCollection<string> ProviderNames => inner.ProviderNames;
	public IReadOnlyCollection<AgentBuilder> Builders => inner.Builders;
	public AgentBuilder Resolve(string? providerName) => inner.Resolve(string.IsNullOrWhiteSpace(providerName) ? DefaultProviderName : providerName);
}
