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

	[Fact]
	public async Task Integrations_PreserveSecretsWhenEditingARedactedDefinition()
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		var initial = await client.GetFromJsonAsync<JsonElement>("/api/workspace/integrations");
		var definition = new System.Text.Json.Nodes.JsonObject { ["name"] = "example", ["type"] = "Remote", ["endpoint"] = "https://example.test/mcp", ["headers"] = new System.Text.Json.Nodes.JsonObject { ["Authorization"] = "Bearer private-value" } };
		var saved = await client.PostAsJsonAsync("/api/workspace/integrations", new IntegrationUpdate(initial.GetProperty("revision").GetString()!, definition));
		saved.EnsureSuccessStatusCode();
		var raw = await saved.Content.ReadAsStringAsync();
		raw.Should().NotContain("private-value");
		var snapshot = System.Text.Json.Nodes.JsonNode.Parse(raw)!;
		var edited = snapshot["definitions"]![0]!.DeepClone().AsObject();
		edited["endpoint"] = "https://example.test/updated";
		(await client.PostAsJsonAsync("/api/workspace/integrations", new IntegrationUpdate(snapshot["revision"]!.GetValue<string>(), edited))).EnsureSuccessStatusCode();
		(await File.ReadAllTextAsync(snapshot["path"]!.GetValue<string>())).Should().Contain("Bearer private-value").And.Contain("updated");
		edited["headers"] = new System.Text.Json.Nodes.JsonObject();
		var latest = await client.GetFromJsonAsync<JsonElement>("/api/workspace/integrations");
		var cleared = await client.PostAsJsonAsync("/api/workspace/integrations", new IntegrationUpdate(latest.GetProperty("revision").GetString()!, edited));
		cleared.EnsureSuccessStatusCode();
		(await File.ReadAllTextAsync(snapshot["path"]!.GetValue<string>())).Should().NotContain("private-value");
		var ready = await cleared.Content.ReadFromJsonAsync<JsonElement>();
		(await client.PostAsJsonAsync("/api/workspace/integrations/example/remove", new IntegrationRemoval(ready.GetProperty("revision").GetString()!))).EnsureSuccessStatusCode();
		var removed = await client.GetFromJsonAsync<JsonElement>("/api/workspace/integrations");
		removed.GetProperty("definitions").GetArrayLength().Should().Be(0);
	}

	[Fact]
	public async Task NewDocument_ResolvesPromptFilesRelativeToItsFutureSourcePath()
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		var root = factory.Services.GetRequiredService<PortalWorkspaceService>().Root;
		Directory.CreateDirectory(Path.Combine(root, "orchestrations", "prompts"));
		await File.WriteAllTextAsync(Path.Combine(root, "orchestrations", "prompts", "system.md"), "Source-relative instruction");
		var saved = await client.PostAsJsonAsync("/api/workspace/orchestrations", new
		{
			name = "with-prompt-file",
			description = "Source-relative",
			defaultModel = "test",
			steps = new[] { new { name = "answer", type = "Prompt", systemPromptFile = "prompts/system.md", userPrompt = "answer" } },
		});
		saved.EnsureSuccessStatusCode();
		var definition = factory.Services.GetRequiredService<OrchestrationRegistry>().GetByIdOrName("with-prompt-file")!.Orchestration;
		((Orchestra.Engine.PromptOrchestrationStep)definition.Steps.Single()).SystemPrompt.Should().Be("Source-relative instruction");
	}

	[Fact]
	public async Task UnwritableWorkspace_DoesNotPreventOnboardingOrSettings()
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		var file = Path.Combine(factory.TestDataPath, "not-a-directory");
		await File.WriteAllTextAsync(file, "existing user file");
		factory.Services.GetRequiredService<OrchestrationHostOptions>().Scan = new() { Directory = file };
		await factory.Services.GetRequiredService<PortalWorkspaceService>().InitializeAsync(CancellationToken.None);
		var status = await client.GetFromJsonAsync<JsonElement>("/api/workspace");
		status.GetProperty("initializationError").GetString().Should().Contain("Could not install");
		(await File.ReadAllTextAsync(file)).Should().Be("existing user file");
		(await client.GetAsync("/api/workspace/settings")).EnsureSuccessStatusCode();
	}
}
