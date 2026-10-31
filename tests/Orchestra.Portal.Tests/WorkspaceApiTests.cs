using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;
using Orchestra.Host.Workspaces;
using Xunit;

namespace Orchestra.Portal.Tests;

[Collection(PortalHostEnvironmentCollection.Name)]
public class WorkspaceApiTests
{
	[Fact]
	public async Task StarterInstall_IsIdempotentAndRetainsUserChangesAndSourcePaths()
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		var first = await client.PostAsync("/api/workspace/templates/smoke-test", null);
		first.EnsureSuccessStatusCode();
		var info = await first.Content.ReadFromJsonAsync<JsonElement>();
		var path = info.GetProperty("path").GetString()!;
		path.Should().StartWith(factory.TestDataPath);
		await File.AppendAllTextAsync(path, "\n# user edit\n");
		var next = await client.PostAsync("/api/workspace/templates/smoke-test", null);
		next.EnsureSuccessStatusCode();
		(await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString().Should().Be(info.GetProperty("id").GetString());
		(await File.ReadAllTextAsync(path)).Should().Contain("# user edit");
	}

	[Fact]
	public async Task CreatedDocument_IsValidatedAndSavedIntoTheConfiguredWorkspace()
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		var options = factory.Services.GetRequiredService<OrchestrationHostOptions>();
		options.Scan = new() { Directory = Path.Combine(factory.TestDataPath, "existing-workspace") };
		var definition = new { name = "created", description = "Created in Portal", steps = new[] { new { name = "render", type = "Transform", template = "hello" } } };
		var saved = await client.PostAsJsonAsync("/api/workspace/orchestrations", definition);
		saved.EnsureSuccessStatusCode();
		var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
		result.GetProperty("path").GetString().Should().StartWith(options.Scan.Directory);
		var registry = factory.Services.GetRequiredService<OrchestrationRegistry>();
		registry.GetByIdOrName("created")!.SourcePath.Should().Be(result.GetProperty("path").GetString());
		(await client.PostAsJsonAsync("/api/workspace/orchestrations", definition)).StatusCode.Should().Be(HttpStatusCode.Conflict);
		var invalid = await client.PostAsJsonAsync("/api/workspace/orchestrations", new { name = "bad", description = "Bad", steps = new[] { new { name = "broken", type = "Transform", template = "{{missing.output}}" } } });
		invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		registry.GetByIdOrName("bad").Should().BeNull();
	}

	[Fact]
	public async Task Settings_CaptureTheHostsConfigAndRetainUnrelatedValues()
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		var settings = await client.GetFromJsonAsync<SettingsSnapshot>("/api/workspace/settings");
		settings!.ConfigPath.Should().StartWith(factory.TestDataPath);
		var saved = await client.PostAsJsonAsync("/api/workspace/settings", new SettingsUpdate(settings.Revision, new() { ["defaultModel"] = "test-model", ["defaultMaxConcurrency"] = 2 }));
		saved.EnsureSuccessStatusCode();
		factory.Services.GetRequiredService<OrchestrationHostOptions>().DefaultModel.Should().Be("test-model");
		(await File.ReadAllTextAsync(settings.ConfigPath)).Should().Contain("5999");
	}
}
