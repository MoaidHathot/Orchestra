using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Export;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// End-to-end tests for <c>GET /api/history/{name}/{runId}/export</c>.
/// </summary>
public class RunsApiExportTests : IDisposable
{
	private const string Orchestration = "export-orchestration";
	private const string RunId = "a1b2c3d4e5f6";

	private readonly string _dataPath;
	private readonly RunAnnotationStore _annotations;
	private readonly FileSystemRunStore _runStore;
	private readonly RunExporter _exporter;

	public RunsApiExportTests()
	{
		_dataPath = Path.Combine(Path.GetTempPath(), $"orchestra-export-api-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_dataPath);
		_annotations = new RunAnnotationStore(_dataPath, NullLogger<RunAnnotationStore>.Instance);
		_runStore = new FileSystemRunStore(_dataPath, NullLogger<FileSystemRunStore>.Instance, _annotations);
		_exporter = new RunExporter(_dataPath, _runStore, _annotations, NullLogger<RunExporter>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataPath))
		{
			try { Directory.Delete(_dataPath, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private async Task SaveRunAsync()
	{
		var tempDir = Path.Combine(_dataPath, "temp", Orchestration, RunId);
		Directory.CreateDirectory(tempDir);
		var artifact = Path.Combine(tempDir, $"{Guid.NewGuid():N}.md");
		await File.WriteAllTextAsync(artifact, "# The real deliverable");

		var step = new StepRunRecord
		{
			StepName = "render",
			Status = ExecutionStatus.Succeeded,
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
			CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			Content = "Saved to file.",
			SavedFiles = [artifact],
		};

		await _runStore.SaveRunAsync(new OrchestrationRunRecord
		{
			RunId = RunId,
			OrchestrationName = Orchestration,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CompletedAt = DateTimeOffset.UtcNow,
			Status = ExecutionStatus.Succeeded,
			FinalContent = "Saved to file.",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord> { ["render"] = step },
			AllStepRecords = new Dictionary<string, StepRunRecord> { ["render"] = step },
		}, cancellationToken: default);
	}

	private IHost CreateHost()
	{
		var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		var activeExecutionInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
		var triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			activeExecutionInfos,
			agentBuilder: null!,
			scheduler: new OrchestrationScheduler(),
			loggerFactory: NullLoggerFactory.Instance,
			logger: NullLogger<TriggerManager>.Instance,
			runsDir: Path.GetTempPath(),
			runStore: _runStore,
			checkpointStore: Substitute.For<ICheckpointStore>(),
			launcher: Substitute.For<IChildOrchestrationLauncher>());

		var host = new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddRouting();
					services.AddSingleton(_runStore);
					services.AddSingleton(_annotations);
					services.AddSingleton(_exporter);
					services.AddSingleton(activeExecutionInfos);
					services.AddSingleton(triggerManager);
					services.AddSingleton(new OrchestrationRegistry());
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapRunsApi(jsonOptions));
				});
			})
			.Build();

		host.Start();
		return host;
	}

	private static string ExportUrl(string? format = null) =>
		$"/api/history/{Orchestration}/{RunId}/export" + (format is null ? "" : $"?format={format}");

	[Fact]
	public async Task Export_DefaultsToBundle_AndReturnsAZip()
	{
		await SaveRunAsync();
		using var host = CreateHost();

		var response = await host.GetTestClient().GetAsync(ExportUrl());

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");

		using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
		var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
		names.Should().Contain("README.md");
		names.Should().Contain("run.json");
		names.Should().Contain(n => n.StartsWith("files/"), "the temp-store artifact must be included");
	}

	[Fact]
	public async Task Export_ArchiveCarriesTheRealDeliverable()
	{
		await SaveRunAsync();
		using var host = CreateHost();

		var stream = await host.GetTestClient().GetStreamAsync(ExportUrl("bundle"));
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
		var entry = archive.Entries.Single(e => e.FullName.Replace('\\', '/').StartsWith("files/"));

		using var reader = new StreamReader(entry.Open());
		(await reader.ReadToEndAsync()).Should().Be("# The real deliverable");
	}

	[Fact]
	public async Task Export_ReportFormat_ReturnsMarkdown()
	{
		await SaveRunAsync();
		using var host = CreateHost();

		var response = await host.GetTestClient().GetAsync(ExportUrl("report"));

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
		(await response.Content.ReadAsStringAsync()).Should().Be("# The real deliverable");
	}

	[Fact]
	public async Task Export_DataFormat_ContainsOnlySteps()
	{
		await SaveRunAsync();
		using var host = CreateHost();

		var stream = await host.GetTestClient().GetStreamAsync(ExportUrl("data"));
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
		var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

		names.Should().OnlyContain(n => n.StartsWith("steps/"));
	}

	[Fact]
	public async Task Export_UnknownFormat_Returns400()
	{
		await SaveRunAsync();
		using var host = CreateHost();

		var response = await host.GetTestClient().GetAsync(ExportUrl("nonsense"));

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Export_UnknownRun_Returns404()
	{
		using var host = CreateHost();

		var response = await host.GetTestClient().GetAsync($"/api/history/{Orchestration}/ghost/export");

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Export_AnnotationAppearsInTheReadme()
	{
		await SaveRunAsync();
		_annotations.Patch(RunId, favorite: true, title: "Kept for review",
			note: "Check the caveats.", orchestrationName: Orchestration);
		using var host = CreateHost();

		var stream = await host.GetTestClient().GetStreamAsync(ExportUrl("bundle"));
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
		using var reader = new StreamReader(archive.GetEntry("README.md")!.Open());
		var readme = await reader.ReadToEndAsync();

		readme.Should().Contain("Kept for review");
		readme.Should().Contain("Check the caveats.");
	}
}
