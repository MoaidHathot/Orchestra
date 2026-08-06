using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Export;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for <see cref="RunExporter"/>.
/// </summary>
public class RunExporterTests : IDisposable
{
	private const string Orchestration = "evidence-orchestration";
	private const string RunId = "efca835904b6";

	private readonly string _dataPath;
	private readonly string _outDir;
	private readonly RunAnnotationStore _annotations;
	private readonly FileSystemRunStore _runStore;
	private readonly RunExporter _exporter;

	public RunExporterTests()
	{
		_dataPath = Path.Combine(Path.GetTempPath(), $"orchestra-export-tests-{Guid.NewGuid():N}");
		_outDir = Path.Combine(_dataPath, "_out");
		Directory.CreateDirectory(_dataPath);
		Directory.CreateDirectory(_outDir);

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

	// ── Fixtures ──

	/// <summary>
	/// Writes a file into the run's temp store, exactly where <c>orchestra_save_file</c> puts it:
	/// a GUID-named file under <c>{dataPath}/temp/{orch}/{runId}/</c>.
	/// </summary>
	private string SaveTempArtifact(string content, string extension)
	{
		var dir = Path.Combine(_dataPath, "temp", Orchestration, RunId);
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, $"{Guid.NewGuid():N}.{extension}");
		File.WriteAllText(path, content);
		return path;
	}

	private static StepRunRecord Step(
		string name,
		string content,
		ExecutionStatus status = ExecutionStatus.Succeeded,
		string[]? savedFiles = null,
		string? error = null) => new()
		{
			StepName = name,
			Status = status,
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
			Content = content,
			ErrorMessage = error,
			SavedFiles = savedFiles ?? [],
		};

	private async Task SaveRunAsync(
		Dictionary<string, StepRunRecord> steps,
		ExecutionStatus status = ExecutionStatus.Succeeded,
		string finalContent = "final result",
		string[]? savedFiles = null)
	{
		await _runStore.SaveRunAsync(new OrchestrationRunRecord
		{
			RunId = RunId,
			OrchestrationName = Orchestration,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
			CompletedAt = DateTimeOffset.UtcNow,
			Status = status,
			FinalContent = finalContent,
			SavedFiles = savedFiles ?? [],
			HookExecutions = [],
			Parameters = new Dictionary<string, string> { ["topic"] = "connect" },
			StepRecords = steps,
			AllStepRecords = steps,
		}, cancellationToken: default);
	}

	// ── The core case: artifacts live outside the execution folder ──

	[Fact]
	public async Task Bundle_IncludesTempStoreArtifacts_NotJustTheInlineSummary()
	{
		// A step that produces a large document saves it and returns a short summary inline.
		// Exporting the run folder alone would capture only the summary.
		var bigDocument = new string('#', 5000);
		var artifact = SaveTempArtifact(bigDocument, "md");
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["render-report"] = Step("render-report", "Report generated and saved.", savedFiles: [artifact]),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		var files = Path.Combine(result.Path, "files");
		Directory.Exists(files).Should().BeTrue();
		var exported = Directory.GetFiles(files);
		exported.Should().HaveCount(1);
		File.ReadAllText(exported[0]).Should().Be(bigDocument);
	}

	[Fact]
	public async Task Bundle_NamesArtifactsAfterTheProducingStep()
	{
		var artifact = SaveTempArtifact("payload", "md");
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["render-report"] = Step("render-report", "saved", savedFiles: [artifact]),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		// The on-disk name is a GUID; the export must be legible.
		File.Exists(Path.Combine(result.Path, "files", "render-report.md")).Should().BeTrue();
	}

	[Fact]
	public async Task Bundle_SweepsUnattributedTempFiles()
	{
		// A file present in the temp store but not recorded on any step must still be exported.
		SaveTempArtifact("orphaned payload", "json");
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["step1"] = Step("step1", "{}"),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		var files = Directory.GetFiles(Path.Combine(result.Path, "files"));
		files.Should().HaveCount(1);
		Path.GetFileName(files[0]).Should().StartWith("_unattributed");
	}

	[Fact]
	public async Task MissingArtifact_ProducesAWarning_NotASilentGap()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["step1"] = Step("step1", "ok", savedFiles: [Path.Combine(_dataPath, "temp", Orchestration, RunId, "vanished.md")]),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		result.Warnings.Should().ContainSingle(w => w.Contains("vanished.md"));
		File.ReadAllText(Path.Combine(result.Path, "README.md")).Should().Contain("Export warnings");
	}

	// ── Step payloads ──

	[Fact]
	public async Task StepPayloads_AreWrittenAsValidJson_WithFencesStripped()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["fetch"] = Step("fetch", "```json\n{ \"items\": [1, 2, 3] }\n```"),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		var path = Path.Combine(result.Path, "steps", "fetch.json");
		File.Exists(path).Should().BeTrue();
		var parsed = JsonDocument.Parse(File.ReadAllText(path));
		parsed.RootElement.GetProperty("items").GetArrayLength().Should().Be(3);
	}

	[Fact]
	public async Task StepPayloads_ExtractJsonEmbeddedInProse()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["fetch"] = Step("fetch", "Here you go:\n{ \"ok\": true }\nHope that helps."),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		var json = File.ReadAllText(Path.Combine(result.Path, "steps", "fetch.json"));
		JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task NonJsonStep_IsPreservedVerbatimAsText()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["narrate"] = Step("narrate", "Just some prose, no JSON here."),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		var path = Path.Combine(result.Path, "steps", "narrate.txt");
		File.Exists(path).Should().BeTrue();
		File.ReadAllText(path).Should().Be("Just some prose, no JSON here.");
	}

	[Fact]
	public async Task MalformedJson_IsExportedAsTextWithAWarning()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["broken"] = Step("broken", "{ \"truncated\": "),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		File.Exists(Path.Combine(result.Path, "steps", "broken.txt")).Should().BeTrue();
		result.Warnings.Should().ContainSingle(w => w.Contains("broken") && w.Contains("did not parse"));
	}

	// ── Bundle shape ──

	[Fact]
	public async Task Bundle_ContainsTheExpectedFiles()
	{
		var artifact = SaveTempArtifact("doc", "md");
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["step1"] = Step("step1", "{}", savedFiles: [artifact]),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		File.Exists(Path.Combine(result.Path, "README.md")).Should().BeTrue();
		File.Exists(Path.Combine(result.Path, "run.json")).Should().BeTrue();
		File.Exists(Path.Combine(result.Path, "result.md")).Should().BeTrue();
		Directory.Exists(Path.Combine(result.Path, "steps")).Should().BeTrue();
		Directory.Exists(Path.Combine(result.Path, "files")).Should().BeTrue();
		result.FileCount.Should().BeGreaterThan(3);
		result.TotalBytes.Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task Bundle_RunJsonRoundTrips()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord> { ["step1"] = Step("step1", "{}") });

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Path, "run.json")));
		json.RootElement.GetProperty("runId").GetString().Should().Be(RunId);
		json.RootElement.GetProperty("orchestrationName").GetString().Should().Be(Orchestration);
	}

	// ── README ──

	[Fact]
	public async Task Readme_WarnsWhenTheRunDidNotSucceed()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["ok"] = Step("ok", "fine"),
			["bad"] = Step("bad", "", ExecutionStatus.Failed, error: "connector timed out"),
		}, status: ExecutionStatus.Failed);

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);
		var readme = File.ReadAllText(Path.Combine(result.Path, "README.md"));

		readme.Should().Contain("[!WARNING]");
		readme.Should().Contain("Failed");
		readme.Should().Contain("1 step(s) did not complete");
		readme.Should().Contain("bad");
		readme.Should().Contain("connector timed out");
	}

	[Fact]
	public async Task Readme_UsesTheAnnotationTitleAndCarriesTheNote()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord> { ["step1"] = Step("step1", "{}") });
		_annotations.Patch(RunId,
			favorite: true,
			title: "Connect evidence pack",
			tags: ["connect", "keep"],
			note: "Counts are unreliable.",
			orchestrationName: Orchestration);

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);
		var readme = File.ReadAllText(Path.Combine(result.Path, "README.md"));

		readme.Should().StartWith("# Connect evidence pack");
		readme.Should().Contain("Marked as a favorite");
		readme.Should().Contain("connect, keep");
		readme.Should().Contain("Counts are unreliable.");
		readme.Should().Contain(Orchestration);
	}

	[Fact]
	public async Task Readme_ListsStepsAndParameters()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["alpha"] = Step("alpha", "{}"),
			["beta"] = Step("beta", "{}"),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);
		var readme = File.ReadAllText(Path.Combine(result.Path, "README.md"));

		readme.Should().Contain("| alpha |").And.Contain("| beta |");
		readme.Should().Contain("| topic | connect |");
	}

	// ── Report format ──

	[Fact]
	public async Task Report_PrefersTheSavedArtifactOverTheInlineSummary()
	{
		var full = new string('x', 4000);
		var artifact = SaveTempArtifact(full, "md");
		await SaveRunAsync(
			new Dictionary<string, StepRunRecord>
			{
				["render"] = Step("render", "Saved. See file.", savedFiles: [artifact]),
			},
			finalContent: "Saved. See file.");

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Report, _outDir);

		result.Path.Should().EndWith(".md");
		File.ReadAllText(result.Path).Should().Be(full);
	}

	[Fact]
	public async Task Report_FallsBackToFinalContent()
	{
		await SaveRunAsync(
			new Dictionary<string, StepRunRecord> { ["step1"] = Step("step1", "{}") },
			finalContent: "# The whole answer");

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Report, _outDir);

		File.ReadAllText(result.Path).Should().Be("# The whole answer");
	}

	// ── Data format ──

	[Fact]
	public async Task Data_WritesOnlyStepPayloads()
	{
		var artifact = SaveTempArtifact("doc", "md");
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["step1"] = Step("step1", "{\"a\":1}", savedFiles: [artifact]),
		});

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Data, _outDir);

		Directory.Exists(Path.Combine(result.Path, "steps")).Should().BeTrue();
		File.Exists(Path.Combine(result.Path, "README.md")).Should().BeFalse();
		File.Exists(Path.Combine(result.Path, "run.json")).Should().BeFalse();
		Directory.Exists(Path.Combine(result.Path, "files")).Should().BeFalse();
	}

	// ── Archive ──

	[Fact]
	public async Task ExportToArchive_ProducesAReadableZip()
	{
		var artifact = SaveTempArtifact("doc", "md");
		await SaveRunAsync(new Dictionary<string, StepRunRecord>
		{
			["step1"] = Step("step1", "{}", savedFiles: [artifact]),
		});

		var (content, fileName, contentType) =
			await _exporter.ExportToArchiveAsync(Orchestration, RunId, RunExportFormat.Bundle);

		contentType.Should().Be("application/zip");
		fileName.Should().EndWith(".zip");

		using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
		var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
		names.Should().Contain("README.md");
		names.Should().Contain("run.json");
		names.Should().Contain(n => n.StartsWith("files/"));
	}

	[Fact]
	public async Task ExportToArchive_ReportFormat_ReturnsMarkdownNotZip()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord> { ["step1"] = Step("step1", "{}") },
			finalContent: "# Answer");

		var (content, fileName, contentType) =
			await _exporter.ExportToArchiveAsync(Orchestration, RunId, RunExportFormat.Report);

		contentType.Should().Be("text/markdown");
		fileName.Should().EndWith(".md");
		System.Text.Encoding.UTF8.GetString(content).Should().Be("# Answer");
	}

	[Fact]
	public async Task CompressExport_ReplacesTheDirectoryWithAZip()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord> { ["step1"] = Step("step1", "{}") });
		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		var zipPath = RunExporter.CompressExport(result.Path);

		File.Exists(zipPath).Should().BeTrue();
		Directory.Exists(result.Path).Should().BeFalse();
	}

	// ── Bulk selection ──

	[Fact]
	public async Task SelectRuns_ByTag_UsesOrSemantics()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord> { ["s"] = Step("s", "{}") });
		_annotations.Patch(RunId, tags: ["connect"], orchestrationName: Orchestration);

		var byMatchingTag = await _exporter.SelectRunsAsync(favoritesOnly: false, tags: ["connect", "other"]);
		var byMissingTag = await _exporter.SelectRunsAsync(favoritesOnly: false, tags: ["nothing"]);

		byMatchingTag.Should().ContainSingle(r => r.RunId == RunId);
		byMissingTag.Should().BeEmpty();
	}

	[Fact]
	public async Task SelectRuns_ByFavorite_ExcludesUnannotatedRuns()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord> { ["s"] = Step("s", "{}") });

		(await _exporter.SelectRunsAsync(favoritesOnly: true, tags: null)).Should().BeEmpty();

		_annotations.Patch(RunId, favorite: true, orchestrationName: Orchestration);
		(await _exporter.SelectRunsAsync(favoritesOnly: true, tags: null)).Should().ContainSingle();
	}

	// ── Errors ──

	[Fact]
	public async Task Export_UnknownRun_Throws()
	{
		var act = async () => await _exporter.ExportAsync(Orchestration, "ghost", RunExportFormat.Bundle, _outDir);

		await act.Should().ThrowAsync<FileNotFoundException>();
	}

	[Fact]
	public async Task ExportedFiles_AreUtf8WithoutBomAndLfOnly()
	{
		await SaveRunAsync(new Dictionary<string, StepRunRecord> { ["step1"] = Step("step1", "{}") });

		var result = await _exporter.ExportAsync(Orchestration, RunId, RunExportFormat.Bundle, _outDir);

		foreach (var file in new[] { "README.md", "run.json", "result.md" })
		{
			var bytes = File.ReadAllBytes(Path.Combine(result.Path, file));
			(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
				.Should().BeFalse($"{file} must not have a BOM");
			bytes.Should().NotContain((byte)13, $"{file} must be LF-only");
		}
	}
}
