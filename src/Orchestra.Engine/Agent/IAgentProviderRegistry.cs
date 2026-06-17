namespace Orchestra.Engine;

/// <summary>
/// Resolves the <see cref="AgentBuilder"/> for a named agent provider (e.g. <c>"copilot"</c>,
/// <c>"opencode"</c>). Lets the engine pick a provider per orchestration / per step without
/// depending on any provider-implementation assembly (<c>Orchestra.Copilot</c>,
/// <c>Orchestra.OpenCode</c>, …). Composition roots register the concrete builders plus a
/// default provider name; the engine resolves through this abstraction.
/// </summary>
public interface IAgentProviderRegistry
{
	/// <summary>The provider used when a step / orchestration does not specify one.</summary>
	string DefaultProviderName { get; }

	/// <summary>All registered provider names.</summary>
	IReadOnlyCollection<string> ProviderNames { get; }

	/// <summary>
	/// The distinct set of builders backing this registry. Used by the executor to open a
	/// run scope per provider and by status endpoints to aggregate
	/// <see cref="AgentBuilder.GetRuntimeStatus"/> across providers.
	/// </summary>
	IReadOnlyCollection<AgentBuilder> Builders { get; }

	/// <summary>
	/// Resolves the builder for <paramref name="providerName"/>. A null/empty name resolves
	/// to <see cref="DefaultProviderName"/>. Implementations decide whether an unknown,
	/// non-empty name throws (the multi-provider <see cref="AgentProviderRegistry"/>) or
	/// falls back to the single registered builder (<see cref="SingleAgentProviderRegistry"/>).
	/// </summary>
	AgentBuilder Resolve(string? providerName);
}
