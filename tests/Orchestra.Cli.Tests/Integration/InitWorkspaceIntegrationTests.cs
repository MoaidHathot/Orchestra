using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Cli.Init;
using Orchestra.Composition;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;
using Xunit;

namespace Orchestra.Cli.Tests.Integration;

/// <summary>
/// End-to-end contract for <c>orchestra init</c>: scaffolding a workspace must produce something
/// a real host actually registers, so <c>orchestra list</c> and <c>orchestra run &lt;name&gt;</c>
/// work by name inside it.
/// </summary>
/// <remarks>
/// This covers the chain that is easy to break silently and impossible to notice in unit tests:
/// the generated <c>scan.directory</c> has to name the workspace <em>root</em> (the host looks
/// for an <c>orchestrations/</c> subdirectory inside it), the walk-up discovery has to find the
/// project-local <c>orchestra.json</c>, and the resolved directory has to reach the registry.
/// Each of those was wrong at some point during development, and each failure mode looks
/// identical from the outside: an empty orchestration list.
/// </remarks>
[Collection(OrchestraEnvironmentCollection.Name)]
public sealed class InitWorkspaceIntegrationTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string? _savedConfigPath;

	public InitWorkspaceIntegrationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-init-e2e-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_savedConfigPath = Environment.GetEnvironmentVariable("ORCHESTRA_CONFIG_PATH");
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", _savedConfigPath);
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best-effort cleanup */ }
	}

	private string Scaffold(string templateId)
	{
		var target = Path.Combine(_tempDir, templateId);

		// A .git marker makes the workspace a discovery boundary, so the walk-up assertions
		// cannot accidentally reach a config above the temp directory.
		Directory.CreateDirectory(target);
		Directory.CreateDirectory(Path.Combine(target, ".git"));

		var templates = InitTemplateCatalog.Discover(Path.Combine(AppContext.BaseDirectory, "templates"));
		var template = InitTemplateCatalog.Find(templates, templateId);
		template.Should().NotBeNull($"template '{templateId}' must ship in the build output");

		InitScaffolder.Scaffold(
			new InitPlan(target, template!, "copilot", "claude-opus-4.8", WriteConfig: true, WriteSchemas: true, Force: false),
			Path.Combine(AppContext.BaseDirectory, "schemas"));

		return target;
	}

	[Fact]
	public void ScaffoldedWorkspace_IsDiscoveredByTheWalkUp()
	{
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", null);
		var target = Scaffold("hello");

		var resolved = OrchestraConfigLoader.ResolveConfigPath(target);

		resolved.Should().Be(Path.Combine(target, OrchestraConfigLoader.ConfigFileName));
	}

	[Fact]
	public void ScaffoldedWorkspace_IsDiscoveredFromASubdirectory()
	{
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", null);
		var target = Scaffold("hello");
		var nested = Path.Combine(target, "orchestrations");

		OrchestraConfigLoader.ResolveConfigPath(nested)
			.Should().Be(Path.Combine(target, OrchestraConfigLoader.ConfigFileName));
	}

	[Fact]
	public void ScaffoldedWorkspace_ScanDirectoryResolvesToTheWorkspaceRoot()
	{
		var target = Scaffold("hello");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", Path.Combine(target, OrchestraConfigLoader.ConfigFileName));

		var scanDirectory = OrchestraConfigLoader.ResolveConfiguredScanDirectory();

		scanDirectory.Should().Be(Path.GetFullPath(target));
		Directory.Exists(Path.Combine(scanDirectory!, InitScaffolder.OrchestrationsDirectoryName))
			.Should().BeTrue("the host scans <scan.directory>/orchestrations");
	}

	[Theory]
	[InlineData("hello")]
	[InlineData("research")]
	[InlineData("code-review")]
	public async Task ScaffoldedWorkspace_RegistersTheOrchestrationByName(string templateId)
	{
		var target = Scaffold(templateId);
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", Path.Combine(target, OrchestraConfigLoader.ConfigFileName));

		var scanDirectory = OrchestraConfigLoader.ResolveConfiguredScanDirectory();
		scanDirectory.Should().NotBeNull();

		await using var host = await StartHostAsync(
			dataPath: Path.Combine(target, ".orchestra", "data"),
			scanDirectory: scanDirectory!);

		var registry = host.Services.GetRequiredService<OrchestrationRegistry>();
		var names = registry.GetAll().Select(o => o.Orchestration.Name).ToArray();

		names.Should().Contain(templateId, "`orchestra run <name>` resolves through the registry");
	}

	private static async Task<WebApplication> StartHostAsync(string dataPath, string scanDirectory)
	{
		var builder = WebApplication.CreateBuilder();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["skip-services"] = "true" });

		// loadConfigurationFile: false mirrors the CLI's managed session, which injects the
		// resolved data path and scan directory rather than loading the whole config.
		builder.Services.AddOrchestraHost(o =>
		{
			o.DataPath = dataPath;
			o.Scan = new ScanConfig { Directory = scanDirectory, Recursive = true };
			o.EnableScheduler = false;
			o.AutoResumeCheckpointsOnStartup = false;
			o.StartExternalServices = false;
		}, loadConfigurationFile: false);

		// The registry resolves an AgentBuilder while loading orchestrations. Registration is
		// lazy (keyed singletons), so this never starts a CLI or triggers the Copilot download.
		builder.Services.AddOrchestraAgentProviders();

		var app = builder.Build();
		await app.Services.InitializeOrchestraHostAsync();
		return app;
	}
}
