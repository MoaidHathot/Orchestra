using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// Verifies the real <c>Orchestra.Server</c> composition root registers every agent provider
/// via <c>AddOrchestraAgentProviders()</c> — a multi-provider <see cref="AgentProviderRegistry"/>
/// with both <c>copilot</c> and <c>opencode</c> resolvable to distinct builders — rather than a
/// single-provider fallback that would ignore a step's <c>provider</c> and silently route every
/// step to one backend. Complements <c>Orchestra.Portal.Tests.ProviderRegistrationTests</c>.
/// </summary>
public class ProviderRegistrationTests : IClassFixture<ServerWebApplicationFactory>
{
	private readonly ServerWebApplicationFactory _factory;

	public ProviderRegistrationTests(ServerWebApplicationFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public void Server_RegistersMultiProviderRegistry_WithCopilotAndOpenCode()
	{
		// Force host construction, then read the DI graph the Server actually composed.
		_ = _factory.CreateClient();
		var registry = _factory.Services.GetRequiredService<IAgentProviderRegistry>();

		registry.ProviderNames.Should().Contain(
			new[] { "copilot", "opencode" },
			because: "the Server must call AddOrchestraAgentProviders() so per-step `provider` is honored");
	}

	[Fact]
	public void Server_ResolvesOpenCodeProvider_ToADistinctBuilderFromCopilot()
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
	public void Server_UnknownProvider_ThrowsRatherThanSilentlyFallingBackToCopilot()
	{
		_ = _factory.CreateClient();
		var registry = _factory.Services.GetRequiredService<IAgentProviderRegistry>();

		var act = () => registry.Resolve("does-not-exist");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unknown agent provider 'does-not-exist'*");
	}
}
