using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Portal.Tests;

/// <summary>
/// Regression guard for the Portal provider-routing bug: the Portal composition root used to
/// register a single non-keyed <c>CopilotAgentBuilder</c> and never call
/// <c>AddOrchestraAgentProviders()</c>, so the only <see cref="IAgentProviderRegistry"/> was the
/// <c>AddOrchestraHost</c> fallback <c>SingleAgentProviderRegistry(copilot)</c> — which ignores a
/// step's <c>provider</c> and silently ran every step (e.g. <c>provider: opencode</c>) on Copilot.
///
/// These tests assert the Portal host now resolves the multi-provider registry with both
/// <c>copilot</c> and <c>opencode</c> registered as distinct builders.
/// </summary>
public class ProviderRegistrationTests : IClassFixture<PortalWebApplicationFactory>
{
	private readonly PortalWebApplicationFactory _factory;

	public ProviderRegistrationTests(PortalWebApplicationFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public void Portal_RegistersMultiProviderRegistry_WithCopilotAndOpenCode()
	{
		// Force host construction, then read the DI graph the Portal actually composed.
		_ = _factory.CreateClient();
		var registry = _factory.Services.GetRequiredService<IAgentProviderRegistry>();

		// The multi-provider registry knows both providers by name; the single-provider fallback
		// would only report its one "default"/"copilot" name.
		registry.ProviderNames.Should().Contain(
			new[] { "copilot", "opencode" },
			because: "the Portal must call AddOrchestraAgentProviders() so per-step `provider` is honored");
	}

	[Fact]
	public void Portal_ResolvesOpenCodeProvider_ToADistinctBuilderFromCopilot()
	{
		_ = _factory.CreateClient();
		var registry = _factory.Services.GetRequiredService<IAgentProviderRegistry>();

		var copilot = registry.Resolve("copilot");
		var opencode = registry.Resolve("opencode");

		copilot.GetCapabilities().Provider.Should().Be("copilot");
		opencode.GetCapabilities().Provider.Should().Be("opencode");
		opencode.Should().NotBeSameAs(copilot,
			because: "a `provider: opencode` step must not be routed to the Copilot builder");
	}

	[Fact]
	public void Portal_UnknownProvider_ThrowsRatherThanSilentlyFallingBackToCopilot()
	{
		_ = _factory.CreateClient();
		var registry = _factory.Services.GetRequiredService<IAgentProviderRegistry>();

		var act = () => registry.Resolve("does-not-exist");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unknown agent provider 'does-not-exist'*");
	}
}
