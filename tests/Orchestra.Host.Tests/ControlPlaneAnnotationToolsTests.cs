using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Export;
using Orchestra.Host.McpServer;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for the control-plane MCP tools that expose run curation and export:
/// <c>annotate_run</c>, <c>list_run_annotations</c>, <c>export_run</c>, and the annotation
/// fields and filters on <c>list_runs</c>.
/// </summary>
public class ControlPlaneAnnotationToolsTests : IDisposable
{
	private const string Orchestration = "mcp-orchestration";

	private readonly string _dataPath;
	private readonly RunAnnotationStore _annotations;
	private readonly FileSystemRunStore _runStore;
	private readonly RunExporter _exporter;

	public ControlPlaneAnnotationToolsTests()
	{
		_dataPath = Path.Combine(Path.GetTempPath(), $"orchestra-mcp-annotations-{Guid.NewGuid():N}");
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

	private async Task SaveRunAsync(string runId)
	{
		var step = new StepRunRecord
		{
			StepName = "step1",
			Status = ExecutionStatus.Succeeded,
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
			CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			Content = "{\"ok\":true}",
		};

		await _runStore.SaveRunAsync(new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = Orchestration,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CompletedAt = DateTimeOffset.UtcNow,
			Status = ExecutionStatus.Succeeded,
			FinalContent = "done",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord> { ["step1"] = step },
			AllStepRecords = new Dictionary<string, StepRunRecord> { ["step1"] = step },
		}, cancellationToken: default);
	}

	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	// ── annotate_run ──

	[Fact]
	public async Task AnnotateRun_SetsEveryField()
	{
		await SaveRunAsync("run1");

		var result = Parse(await ControlPlaneTools.AnnotateRun(
			_runStore, _annotations, Orchestration, "run1",
			favorite: true, title: "Connect pack", tags: "connect, keep", note: "caveats"));

		result.GetProperty("favorite").GetBoolean().Should().BeTrue();
		result.GetProperty("title").GetString().Should().Be("Connect pack");
		result.GetProperty("note").GetString().Should().Be("caveats");
		result.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
			.Should().BeEquivalentTo(["connect", "keep"]);
	}

	[Fact]
	public async Task AnnotateRun_LeavesOmittedFieldsUntouched()
	{
		await SaveRunAsync("run1");
		await ControlPlaneTools.AnnotateRun(
			_runStore, _annotations, Orchestration, "run1", favorite: true, tags: "connect");

		var result = Parse(await ControlPlaneTools.AnnotateRun(
			_runStore, _annotations, Orchestration, "run1", title: "Renamed"));

		result.GetProperty("title").GetString().Should().Be("Renamed");
		result.GetProperty("favorite").GetBoolean().Should().BeTrue("favorite was not supplied");
		result.GetProperty("tags").EnumerateArray().Should().HaveCount(1);
	}

	[Fact]
	public async Task AnnotateRun_UnknownRun_ReturnsError()
	{
		var result = Parse(await ControlPlaneTools.AnnotateRun(
			_runStore, _annotations, Orchestration, "ghost", favorite: true));

		result.GetProperty("error").GetString().Should().Contain("not found");
	}

	// ── list_runs projection + filters ──

	[Fact]
	public async Task ListRuns_ProjectsAnnotationFields()
	{
		await SaveRunAsync("run1");
		_annotations.Patch("run1", favorite: true, title: "Kept", tags: ["connect"], orchestrationName: Orchestration);

		var result = Parse(await ControlPlaneTools.ListRuns(_runStore, _annotations));
		var run = result.GetProperty("runs").EnumerateArray().Single();

		run.GetProperty("favorite").GetBoolean().Should().BeTrue();
		run.GetProperty("title").GetString().Should().Be("Kept");
		run.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().BeEquivalentTo(["connect"]);
	}

	[Fact]
	public async Task ListRuns_UnannotatedRun_ReportsFalseAndEmpties()
	{
		await SaveRunAsync("run1");

		var result = Parse(await ControlPlaneTools.ListRuns(_runStore, _annotations));
		var run = result.GetProperty("runs").EnumerateArray().Single();

		run.GetProperty("favorite").GetBoolean().Should().BeFalse();
		run.GetProperty("tags").GetArrayLength().Should().Be(0);
	}

	[Fact]
	public async Task ListRuns_FavoritesOnly_Filters()
	{
		await SaveRunAsync("fav");
		await SaveRunAsync("plain");
		_annotations.Patch("fav", favorite: true, orchestrationName: Orchestration);

		var result = Parse(await ControlPlaneTools.ListRuns(_runStore, _annotations, favoritesOnly: true));

		result.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["fav"]);
	}

	[Fact]
	public async Task ListRuns_TagFilter_UsesOrSemantics()
	{
		await SaveRunAsync("a");
		await SaveRunAsync("b");
		await SaveRunAsync("c");
		_annotations.Patch("a", tags: ["connect"], orchestrationName: Orchestration);
		_annotations.Patch("b", tags: ["other"], orchestrationName: Orchestration);

		var result = Parse(await ControlPlaneTools.ListRuns(_runStore, _annotations, tags: "connect,other"));

		result.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["a", "b"]);
	}

	// ── list_run_annotations ──

	[Fact]
	public async Task ListRunAnnotations_ReturnsItemsAndTagCounts()
	{
		await SaveRunAsync("run1");
		await SaveRunAsync("run2");
		_annotations.Patch("run1", tags: ["connect", "keep"], orchestrationName: Orchestration);
		_annotations.Patch("run2", tags: ["connect"], orchestrationName: Orchestration);

		var result = Parse(await ControlPlaneTools.ListRunAnnotations(_runStore, _annotations));

		result.GetProperty("count").GetInt32().Should().Be(2);
		var tags = result.GetProperty("tags").EnumerateArray()
			.ToDictionary(t => t.GetProperty("tag").GetString()!, t => t.GetProperty("count").GetInt32());
		tags["connect"].Should().Be(2);
		tags["keep"].Should().Be(1);
	}

	[Fact]
	public async Task ListRunAnnotations_FlagsOrphans()
	{
		await SaveRunAsync("run1");
		_annotations.Patch("run1", favorite: true, orchestrationName: Orchestration);
		_annotations.Patch("ghost", favorite: true, orchestrationName: Orchestration);

		var all = Parse(await ControlPlaneTools.ListRunAnnotations(_runStore, _annotations));
		all.GetProperty("orphanCount").GetInt32().Should().Be(1);

		var orphans = Parse(await ControlPlaneTools.ListRunAnnotations(_runStore, _annotations, orphansOnly: true));
		orphans.GetProperty("annotations").EnumerateArray()
			.Select(a => a.GetProperty("runId").GetString())
			.Should().BeEquivalentTo(["ghost"]);
	}

	// ── export_run ──

	[Fact]
	public async Task ExportRun_WritesABundle()
	{
		await SaveRunAsync("run1");
		var outDir = Path.Combine(_dataPath, "_out");

		var result = Parse(await ControlPlaneTools.ExportRun(_exporter, Orchestration, "run1", outDir));

		var path = result.GetProperty("path").GetString()!;
		Directory.Exists(path).Should().BeTrue();
		File.Exists(Path.Combine(path, "README.md")).Should().BeTrue();
		File.Exists(Path.Combine(path, "run.json")).Should().BeTrue();
		result.GetProperty("fileCount").GetInt32().Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task ExportRun_ZipOption_ProducesAnArchive()
	{
		await SaveRunAsync("run1");
		var outDir = Path.Combine(_dataPath, "_zip");

		var result = Parse(await ControlPlaneTools.ExportRun(_exporter, Orchestration, "run1", outDir, zip: true));

		var path = result.GetProperty("path").GetString()!;
		path.Should().EndWith(".zip");
		File.Exists(path).Should().BeTrue();
	}

	[Fact]
	public async Task ExportRun_UnknownFormat_ReturnsError()
	{
		await SaveRunAsync("run1");

		var result = Parse(await ControlPlaneTools.ExportRun(
			_exporter, Orchestration, "run1", Path.Combine(_dataPath, "_x"), format: "nonsense"));

		result.GetProperty("error").GetString().Should().Contain("Unknown export format");
	}

	[Fact]
	public async Task ExportRun_UnknownRun_ReturnsError()
	{
		var result = Parse(await ControlPlaneTools.ExportRun(
			_exporter, Orchestration, "ghost", Path.Combine(_dataPath, "_x")));

		result.GetProperty("error").GetString().Should().Contain("not found");
	}

	[Fact]
	public async Task ExportRun_MissingOutputDirectory_ReturnsError()
	{
		await SaveRunAsync("run1");

		var result = Parse(await ControlPlaneTools.ExportRun(_exporter, Orchestration, "run1", "  "));

		result.GetProperty("error").GetString().Should().Contain("outputDirectory");
	}
}
