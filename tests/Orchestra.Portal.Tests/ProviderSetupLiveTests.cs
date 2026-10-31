using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Host.Workspaces;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Orchestra.Portal.Tests;

[Trait("Category", "E2E")]
[Collection(PortalHostEnvironmentCollection.Name)]
public class ProviderSetupLiveTests
{
	[Theory]
	[InlineData("copilot", "gpt-6-astra")]
	[InlineData("opencode", "github-copilot/gpt-6-astra")]
	public async Task RealProvider_AuthorsValidatesAndRegistersAWorkflow(string provider, string model)
	{
		await using var factory = new PortalWebApplicationFactory();
		factory.UseKestrel(0);
		using var client = factory.CreateClient();
		var options = factory.Services.GetRequiredService<OrchestrationHostOptions>();
		options.Provider = provider;
		options.DefaultModel = model;
		var prepared = await client.PostAsync("/api/workspace/assistants/create-orchestration", null);
		prepared.EnsureSuccessStatusCode();
		var id = (await prepared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
		var name = "onboarding-" + Guid.NewGuid().ToString("N")[..8];
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		var handle = await factory.Services.GetRequiredService<IChildOrchestrationLauncher>().LaunchAsync(new()
		{
			OrchestrationId = id,
			Mode = ChildLaunchMode.Sync,
			TimeoutSeconds = 240,
			Parameters = new() { ["description"] = $"Create a workflow named {name}. It must have exactly one Transform step named answer with template 'live verified'. No external tools, MCPs or prerequisites are needed in the generated workflow. All requirements are specified; no clarification is needed. Use the workspace validation tool before returning the JSON." },
		}, timeout.Token);
		var completed = await handle.Completion.WaitAsync(timeout.Token);
		completed.Status.Should().Be(ExecutionStatus.Succeeded, completed.ErrorMessage ?? completed.FinalContent);
		var created = factory.Services.GetRequiredService<OrchestrationRegistry>().GetByIdOrName(name);
		created.Should().NotBeNull();
		created!.Orchestration.Steps.Should().ContainSingle(s => s.Type == OrchestrationStepType.Transform);
	}

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
		if (provider == "opencode")
			result.Integrations.Should().NotBeEmpty();
	}
}
