using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Orchestra.Engine;
using Orchestra.Host.Registry;
using Orchestra.Host.Workspaces;
using Xunit;

namespace Orchestra.Portal.E2E;

[Trait("Category", "E2E")]
public class OnboardingUiTests
{
	[Fact]
	public async Task FreshProfile_DemoProviderSetupAuthoringAndRestart_UseTheRealPortal()
	{
		var directory = Directory.CreateTempSubdirectory("orchestra-onboarding-browser-").FullName;
		try
		{
			await using (var factory = new BrowserFactory(directory))
			{
				factory.UseKestrel(0);
				using var client = factory.CreateClient();
				await factory.Services.GetRequiredService<PortalWorkspaceService>().InitializeAsync(CancellationToken.None);
				using var playwright = await Playwright.CreateAsync();
				await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
				var page = await browser.NewPageAsync();
				var errors = new List<string>();
				page.PageError += (_, error) => errors.Add(error);
				await page.GotoAsync(client.BaseAddress!.ToString());
				var workspace = page.GetByRole(AriaRole.Dialog, new() { Name = "Orchestra workspace" });
				await workspace.WaitForAsync();
				await CaptureAsync(page, "welcome-desktop");
				await page.SetViewportSizeAsync(390, 844);
				await CaptureAsync(page, "welcome-mobile");
				await page.SetViewportSizeAsync(1440, 1000);
				await workspace.GetByRole(AriaRole.Button, new() { Name = "Try the demo" }).ClickAsync();
				await page.GetByText("Orchestra is working.", new() { Exact = false }).First.WaitForAsync();
				await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).First.ClickAsync();
				await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
				await workspace.GetByRole(AriaRole.Button, new() { Name = "Sign in to Copilot" }).ClickAsync();
				await workspace.GetByLabel("Authorization code", new() { Exact = true }).FillAsync("browser-code");
				await workspace.GetByRole(AriaRole.Button, new() { Name = "Submit code" }).ClickAsync();
				await workspace.GetByText("Connected test account", new() { Exact = false }).First.WaitForAsync();
				await workspace.GetByLabel("Default model", new() { Exact = true }).FillAsync("test-model");
				await workspace.GetByRole(AriaRole.Button, new() { Name = "Use provider and model" }).ClickAsync();
				await workspace.GetByText("Settings saved.", new() { Exact = false }).WaitForAsync();
				await CaptureAsync(page, "provider-settings");
				await workspace.GetByRole(AriaRole.Tab, new() { Name = "Get started", Exact = true }).ClickAsync();
				await workspace.GetByLabel("What would you like to do?").FillAsync("Create a workflow named browser-created that outputs Created in browser");
				await workspace.GetByRole(AriaRole.Button, new() { Name = "Create workflow", Exact = true }).ClickAsync();
				await page.GetByText("browser-created", new() { Exact = true }).First.WaitForAsync(new() { Timeout = 30000 });
				var registry = factory.Services.GetRequiredService<OrchestrationRegistry>();
				var created = registry.GetByIdOrName("browser-created");
				created.Should().NotBeNull();
				created!.SourcePath.Should().StartWith(directory);
				await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).First.ClickAsync();
				await page.GetByRole(AriaRole.Button, new() { Name = "Run browser-created", Exact = true }).First.ClickAsync();
				await page.GetByText("Created in browser", new() { Exact = true }).First.WaitForAsync();
				await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).First.ClickAsync();
				await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
				await workspace.GetByRole(AriaRole.Tab, new() { Name = "Execution", Exact = true }).ClickAsync();
				await workspace.GetByLabel("Maximum concurrent steps per run").FillAsync("2");
				await workspace.GetByRole(AriaRole.Button, new() { Name = "Save settings", Exact = true }).ClickAsync();
				await workspace.GetByText("Settings saved.", new() { Exact = false }).WaitForAsync();
				await workspace.GetByRole(AriaRole.Tab, new() { Name = "Get started", Exact = true }).ClickAsync();
				await workspace.GetByLabel("What would you like to do?").FillAsync("Set maximum concurrency to 3");
				await workspace.GetByRole(AriaRole.Button, new() { Name = "Help configure Orchestra" }).ClickAsync();
				await page.GetByRole(AriaRole.Button, new() { Name = "Respond to workflow" }).ClickAsync();
				await page.GetByLabel("Apply", new() { Exact = true }).CheckAsync();
				await page.GetByPlaceholder("Add a comment alongside your choice...").FillAsync("Reviewed these changes");
				await page.GetByRole(AriaRole.Button, new() { Name = "Submit response", Exact = true }).ClickAsync();
				await page.GetByText("defaultMaxConcurrency", new() { Exact = false }).First.WaitForAsync();
				for (var i = 0; i < 100; i++)
				{
					var applied = await client.GetFromJsonAsync<SettingsSnapshot>("/api/workspace/settings");
					if (applied!.Effective["defaultMaxConcurrency"]!.GetValue<int>() == 3)
						break;
					await Task.Delay(50);
				}
				errors.Should().BeEmpty();
			}
			await using (var restarted = new BrowserFactory(directory))
			{
				restarted.UseKestrel(0);
				using var client = restarted.CreateClient();
				var settings = await client.GetFromJsonAsync<SettingsSnapshot>("/api/workspace/settings");
				settings!.Effective["defaultModel"]!.GetValue<string>().Should().Be("test-model");
				settings.Effective["defaultMaxConcurrency"]!.GetValue<int>().Should().Be(3);
				settings.Effective["urls"]!.GetValue<string>().Should().Be(client.BaseAddress!.ToString().TrimEnd('/'));
				settings.Overrides.Should().ContainKey("urls");
				restarted.Services.GetRequiredService<OrchestrationRegistry>().GetByIdOrName("browser-created").Should().NotBeNull();
				var assistant = restarted.Services.GetRequiredService<OrchestrationRegistry>().GetByIdOrName("orchestra-author")!.Orchestration;
				assistant.DefaultModel.Should().Be("test-model");
				((HttpOrchestrationStep)assistant.Steps.Single(s => s.Name == "save")).Url.Should().StartWith(client.BaseAddress!.ToString());
			}
		}
		finally { await DeleteDirectoryAsync(directory); }
	}

	[Fact]
	public async Task ExistingWorkspace_KeepsItsDashboardAndSupportsYamlImportAndBuilderPreview()
	{
		var directory = Directory.CreateTempSubdirectory("orchestra-existing-browser-").FullName;
		try
		{
			var library = Path.Combine(directory, "library", "orchestrations");
			Directory.CreateDirectory(library);
			const string definition = "name: existing-workflow\ndescription: Existing user workflow\nsteps:\n  - name: answer\n    type: Transform\n    template: existing output\n";
			await File.WriteAllTextAsync(Path.Combine(library, "existing.yaml"), definition);
			await File.WriteAllTextAsync(Path.Combine(directory, "orchestra.json"), "{\n// user configuration\n\"scan\":{\"directory\":\"./library\",\"recursive\":false},\"defaultModel\":\"existing-model\"\n}");
			await using var factory = new BrowserFactory(directory);
			factory.UseKestrel(0);
			using var client = factory.CreateClient();
			await factory.Services.GetRequiredService<PortalWorkspaceService>().InitializeAsync(CancellationToken.None);
			using var playwright = await Playwright.CreateAsync();
			await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
			var page = await browser.NewPageAsync();
			await page.GotoAsync(client.BaseAddress!.ToString());
			await page.GetByRole(AriaRole.Button, new() { Name = "Run existing-workflow", Exact = true }).First.WaitForAsync();
			(await page.GetByRole(AriaRole.Dialog, new() { Name = "Orchestra workspace" }).CountAsync()).Should().Be(0);
			File.Exists(Path.Combine(library, "smoke-test.yaml")).Should().BeFalse();
			(await File.ReadAllTextAsync(Path.Combine(library, "existing.yaml"))).Should().Be(definition);
			await page.GetByRole(AriaRole.Button, new() { Name = "Visual Builder", Exact = true }).ClickAsync();
			await page.GetByRole(AriaRole.Note).GetByText("Visual Builder — Preview.", new() { Exact = true }).WaitForAsync();
			await page.GetByRole(AriaRole.Dialog, new() { Name = "Visual Orchestration Builder" }).GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
			await page.GetByRole(AriaRole.Button, new() { Name = "Get started", Exact = true }).First.ClickAsync();
			await page.GetByRole(AriaRole.Button, new() { Name = "Import a workflow", Exact = true }).ClickAsync();
			var import = page.GetByRole(AriaRole.Dialog, new() { Name = "Orchestrations", Exact = true });
			await import.GetByLabel("Folder Path", new() { Exact = true }).FillAsync(library);
			await import.GetByRole(AriaRole.Button, new() { Name = "Scan", Exact = true }).ClickAsync();
			await import.GetByRole(AriaRole.Checkbox, new() { Name = "existing.yaml existing-workflow", Exact = true }).ClickAsync();
			await import.GetByText("answer", new() { Exact = true }).WaitForAsync();
			await CaptureAsync(page, "yaml-preview");
		}
		finally { await DeleteDirectoryAsync(directory); }
	}

	private static async Task DeleteDirectoryAsync(string directory)
	{
		// WebApplicationFactory's in-process entrypoint can finish async app disposal just
		// after its test host stops. Wait for those handles to close, with a bounded failure.
		for (var attempt = 0; ; attempt++)
		{
			try
			{ Directory.Delete(directory, true); return; }
			catch (IOException) when (attempt < 20) { await Task.Delay(100); }
		}
	}

	[Fact]
	public async Task PendingConfigurationApproval_ResumesAgainstTheNewListenerAfterRestart()
	{
		var directory = Directory.CreateTempSubdirectory("orchestra-approval-restart-").FullName;
		try
		{
			await File.WriteAllTextAsync(Path.Combine(directory, "orchestra.json"), "{\"defaultModel\":\"test-model\",\"defaultMaxConcurrency\":2}");
			string runId;
			await using (var first = new BrowserFactory(directory))
			{
				first.UseKestrel(0);
				using var client = first.CreateClient();
				await first.Services.GetRequiredService<PortalWorkspaceService>().DismissAsync(CancellationToken.None);
				var response = await client.PostAsync("/api/workspace/assistants/configure-orchestra", null);
				response.EnsureSuccessStatusCode();
				var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
				var handle = await first.Services.GetRequiredService<IChildOrchestrationLauncher>().LaunchAsync(new()
				{
					OrchestrationId = id,
					Mode = ChildLaunchMode.Sync,
					Parameters = new() { ["description"] = "Set concurrency to 3" },
				}, CancellationToken.None);
				runId = handle.ExecutionId;
				var pending = first.Services.GetRequiredService<IPendingInputStore>();
				using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
				while (!(await pending.ListAsync(cancellationToken: deadline.Token)).Any(record => record.RunId == runId && record.StepName == "confirm"))
					await Task.Delay(50, deadline.Token);
				(await first.Services.GetRequiredService<ICheckpointStore>().ListCheckpointsAsync(cancellationToken: deadline.Token)).Should().Contain(checkpoint => checkpoint.RunId == runId);
			}
			await using (var restarted = new BrowserFactory(directory))
			{
				restarted.UseKestrel(0);
				using var client = restarted.CreateClient();
				using var playwright = await Playwright.CreateAsync();
				await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
				var page = await browser.NewPageAsync();
				await page.GotoAsync(client.BaseAddress!.ToString());
				await page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("Waiting for input", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).ClickAsync();
				await page.GetByLabel("Apply", new() { Exact = true }).CheckAsync();
				await page.GetByRole(AriaRole.Button, new() { Name = "Submit response", Exact = true }).ClickAsync();
				using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
				while (true)
				{
					var settings = await client.GetFromJsonAsync<SettingsSnapshot>("/api/workspace/settings", deadline.Token);
					if (settings!.Effective["defaultMaxConcurrency"]!.GetValue<int>() == 3)
						break;
					await Task.Delay(50, deadline.Token);
				}
			}
		}
		finally { await DeleteDirectoryAsync(directory); }
	}

	private static Task CaptureAsync(IPage page, string name)
	{
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root.Parent is not null && !Directory.Exists(Path.Combine(root.FullName, ".git")))
			root = root.Parent;
		var directory = Path.Combine(root.FullName, "artifacts", "onboarding", "screenshots");
		Directory.CreateDirectory(directory);
		return page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true });
	}

	private sealed class BrowserFactory(string directory) : WebApplicationFactory<Orchestra.Portal.Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Testing");
			builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["data-path"] = directory, ["Urls"] = "http://127.0.0.1:0" }));
			builder.ConfigureServices(services =>
			{
				services.Replace(ServiceDescriptor.Singleton<IProviderSetupBackend>(new SetupBackend()));
				services.AddSingleton<IAgentProviderRegistry>(new AgentProviderRegistry(new Dictionary<string, AgentBuilder> { ["copilot"] = new Author() }, "copilot"));
			});
		}
		protected override IHost CreateHost(IHostBuilder builder)
		{
			var config = Path.Combine(directory, "orchestra.json");
			if (!File.Exists(config))
				File.WriteAllText(config, "{}");
			var before = Environment.GetEnvironmentVariable("ORCHESTRA_CONFIG_PATH");
			try
			{ Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", config); return base.CreateHost(builder); }
			finally { Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", before); }
		}
	}
	private sealed class SetupBackend : IProviderSetupBackend
	{
		public ProviderReadiness[] Inspect() => [new("copilot", true, false, "Sign in to get started", [], [])];
		public async Task<ProviderReadiness> ExecuteAsync(string provider, ProviderOperation operation, Action<SetupProgress> progress, Func<CancellationToken, Task<string>> code, CancellationToken token)
		{
			progress(new("Use browser-code", "https://example.test/login", RequiresCode: true));
			(await code(token)).Should().Be("browser-code");
			return new(provider, true, true, "Connected test account", [new("test-model", "Test model")], []);
		}
	}
	private sealed class Author : AgentBuilder
	{
		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("copilot");
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
		{
			config.Model.Should().Be("test-model");
			var content = """{"name":"browser-created","description":"Made in Portal","steps":[{"name":"answer","type":"Transform","template":"Created in browser"}]}""";
			if (config.SystemPrompt!.Contains("Help configure"))
			{
				var settings = config.EngineTools.Single(t => t.Name == "orchestra_inspect_configuration").Execute("{}", config.EngineToolCtx!);
				using var parsed = JsonDocument.Parse(settings);
				content = JsonSerializer.Serialize(new { revision = parsed.RootElement.GetProperty("settings").GetProperty("revision").GetString(), changes = new { defaultMaxConcurrency = 3 } });
				return Task.FromResult<IAgent>(new Answer(content));
			}
			var validation = config.EngineTools.Single(t => t.Name == "orchestra_validate_orchestration").Execute(JsonSerializer.Serialize(new { content }), config.EngineToolCtx!);
			JsonDocument.Parse(validation).RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
			return Task.FromResult<IAgent>(new Answer(content));
		}
		private sealed class Answer(string content) : IAgent
		{
			public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
			{
				var events = Channel.CreateUnbounded<AgentEvent>();
				events.Writer.TryComplete();
				return new(events.Reader, Task.FromResult(new AgentResult { Content = content }));
			}
		}
	}
}
