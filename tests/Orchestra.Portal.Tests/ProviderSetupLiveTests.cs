using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Host.Workspaces;
using Xunit;

namespace Orchestra.Portal.Tests;

[Trait("Category", "E2E")]
[Collection(PortalHostEnvironmentCollection.Name)]
public class ProviderSetupLiveTests
{
	[Theory]
	[InlineData("copilot")]
	[InlineData("opencode")]
	public async Task InstalledProvider_ReportsRealModelsAndClosesItsDiagnosticConnection(string provider)
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		var backend = factory.Services.GetRequiredService<IProviderSetupBackend>();
		var result = await backend.ExecuteAsync(provider, new("check"), _ => { }, _ => throw new NotSupportedException(), timeout.Token);
		result.Installed.Should().BeTrue();
		result.Models.Should().NotBeEmpty();
		if (provider == "opencode") result.Integrations.Should().NotBeEmpty();
	}
}
