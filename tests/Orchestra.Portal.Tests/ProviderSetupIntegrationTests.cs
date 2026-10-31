using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestra.Host.Workspaces;
using Xunit;

namespace Orchestra.Portal.Tests;

[Collection(PortalHostEnvironmentCollection.Name)]
public class ProviderSetupIntegrationTests
{
	[Fact]
	public async Task SetupApi_WorksBeforeAuthentication_AndReturnsConfirmedModels()
	{
		using var factory = new PortalWebApplicationFactory();
		using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IProviderSetupBackend>(new FakeSetup()))));
		using var client = configured.CreateClient();
		var initial = await client.GetAsync("/api/workspace/providers");
		initial.EnsureSuccessStatusCode();
		var response = await client.PostAsJsonAsync("/api/workspace/providers/copilot/operations", new ProviderOperation("check"));
		response.EnsureSuccessStatusCode();
		var started = (await response.Content.ReadFromJsonAsync<SetupJob>())!;
		SetupJob? job = null;
		for (var i = 0; i < 50; i++)
		{
			job = await client.GetFromJsonAsync<SetupJob>($"/api/workspace/jobs/{started.Id}");
			if (job!.State != "running") break;
			await Task.Delay(10);
		}
		job!.State.Should().Be("succeeded");
		job.Result!.Models.Should().ContainSingle(m => m.Id == "test-model");
	}

	private sealed class FakeSetup : IProviderSetupBackend
	{
		public ProviderReadiness[] Inspect() => [new("copilot", true, false, "Sign in", [], [])];
		public Task<ProviderReadiness> ExecuteAsync(string provider, ProviderOperation operation, Action<SetupProgress> progress, Func<CancellationToken, Task<string>> readCode, CancellationToken token)
			=> Task.FromResult(new ProviderReadiness(provider, true, true, "Connected", [new("test-model", "Test model")], []));
	}
}
