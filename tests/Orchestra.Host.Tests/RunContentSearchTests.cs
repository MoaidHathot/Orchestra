using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Full-text search over run output: the index side.
/// </summary>
/// <remarks>
/// Run names are frequently machine-generated and meaningless, and annotations only help for runs
/// someone already went back and labelled. Searching what a run actually produced is the only way
/// to find a run you did not know you would need again.
/// </remarks>
public class RunContentSearchTests : IDisposable
{
	private readonly string _tempDir;
	private FileSystemRunStore _store;

	public RunContentSearchTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-fts-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemRunStore(_tempDir);
	}

	public void Dispose()
	{
		_store.Dispose();
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private static readonly DateTimeOffset s_epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
	private static int s_seq;

	private static OrchestrationRunRecord Record(
		string runId,
		string? finalContent = null,
		Dictionary<string, string>? stepContent = null,
		string orchestrationName = "quiet-orch",
		ExecutionStatus status = ExecutionStatus.Succeeded,
		string? errorMessage = null)
	{
		var startedAt = s_epoch.AddMinutes(Interlocked.Increment(ref s_seq));
		var steps = new Dictionary<string, StepRunRecord>(StringComparer.OrdinalIgnoreCase);
		foreach (var (name, content) in stepContent ?? [])
		{
			steps[name] = new StepRunRecord
			{
				StepName = name,
				Status = status,
				StartedAt = startedAt,
				CompletedAt = startedAt.AddSeconds(1),
				Content = content,
				ErrorMessage = errorMessage,
			};
		}

		return new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestrationName,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = startedAt,
			CompletedAt = startedAt.AddSeconds(5),
			Status = status,
			IsIncomplete = false,
			FinalContent = finalContent ?? string.Empty,
			SavedFiles = [],
			HookExecutions = [],
			StepRecords = steps,
			AllStepRecords = steps,
		};
	}

	private async Task<IReadOnlyList<string>> SearchAsync(string query, int limit = 50)
	{
		var filters = HistoryFilterParser.Parse(null, null, null);
		var annotations = new RunAnnotationStore(
			Path.Combine(_tempDir, "annotations"),
			Microsoft.Extensions.Logging.Abstractions.NullLogger<RunAnnotationStore>.Instance);

		var (rows, _, _) = await _store.QueryRunsAsync(filters.ToIndexQuery(annotations, query), 0, limit);
		return [.. rows.Select(r => r.RunId)];
	}

	[Fact]
	public async Task Fts5IsAvailableInTheBundledSqlite()
	{
		// The whole feature rests on the bundled SQLite being compiled with FTS5. If it is not,
		// the failure should name that directly rather than surfacing as a confusing empty result.
		await _store.SaveRunAsync(Record("r1", finalContent: "hello"), cancellationToken: default);

		var act = async () => await SearchAsync("hello");

		await act.Should().NotThrowAsync("SQLitePCLRaw's e_sqlite3 build must include FTS5");
		(await SearchAsync("hello")).Should().ContainSingle();
	}

	[Fact]
	public async Task FindsRunByWordInFinalContent()
	{
		await _store.SaveRunAsync(
			Record("has-it", finalContent: "The quarterly revenue reconciliation completed."),
			cancellationToken: default);
		await _store.SaveRunAsync(
			Record("lacks-it", finalContent: "Nothing of interest here."),
			cancellationToken: default);

		(await SearchAsync("reconciliation")).Should().BeEquivalentTo(["has-it"]);
	}

	[Fact]
	public async Task FindsRunByWordInStepContent()
	{
		await _store.SaveRunAsync(
			Record("deep", stepContent: new() { ["analyze"] = "found a discrepancy in ledger 447" }),
			cancellationToken: default);
		await _store.SaveRunAsync(Record("shallow", finalContent: "ok"), cancellationToken: default);

		(await SearchAsync("discrepancy")).Should().BeEquivalentTo(["deep"]);
	}

	[Fact]
	public async Task FindsRunByErrorMessage()
	{
		await _store.SaveRunAsync(
			Record("broke",
				stepContent: new() { ["fetch"] = "" },
				status: ExecutionStatus.Failed,
				errorMessage: "upstream returned HTTP 503 Unavailable"),
			cancellationToken: default);

		(await SearchAsync("Unavailable")).Should().BeEquivalentTo(["broke"]);
	}

	[Fact]
	public async Task MatchesWordPrefixes()
	{
		// A search box where typing "recon" finds nothing until the word is complete feels broken.
		await _store.SaveRunAsync(
			Record("r1", finalContent: "quarterly reconciliation"), cancellationToken: default);

		(await SearchAsync("recon")).Should().BeEquivalentTo(["r1"]);
	}

	[Fact]
	public async Task MultipleWordsRequireAllOfThem()
	{
		await _store.SaveRunAsync(Record("both", finalContent: "alpha and beta"), cancellationToken: default);
		await _store.SaveRunAsync(Record("one", finalContent: "alpha only"), cancellationToken: default);

		(await SearchAsync("alpha beta")).Should().BeEquivalentTo(["both"]);
	}

	[Theory]
	[InlineData("\"")]
	[InlineData("*")]
	[InlineData("AND")]
	[InlineData("NOT OR AND")]
	[InlineData("foo(bar")]
	[InlineData("^")]
	[InlineData("a\"b\"c")]
	public async Task MalformedQueriesAreTreatedAsTextRatherThanSyntax(string query)
	{
		// FTS5 has its own grammar. Passing user input to MATCH unescaped turns a search box into
		// a way to throw SQL errors at yourself.
		await _store.SaveRunAsync(Record("r1", finalContent: "ordinary content"), cancellationToken: default);

		var act = async () => await SearchAsync(query);

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task ContentSearchIsUnionedWithNameAndIdSearch()
	{
		// A query should find runs matched by any of the three, not force a choice between them.
		await _store.SaveRunAsync(
			Record("by-content", finalContent: "mentions telemetry"), cancellationToken: default);
		await _store.SaveRunAsync(
			Record("by-name", orchestrationName: "telemetry-collector", finalContent: "unrelated"),
			cancellationToken: default);
		await _store.SaveRunAsync(
			Record("telemetry-in-id", finalContent: "unrelated"), cancellationToken: default);

		(await SearchAsync("telemetry")).Should()
			.BeEquivalentTo(["by-content", "by-name", "telemetry-in-id"]);
	}

	[Fact]
	public async Task SnippetShowsWhyTheRunMatched()
	{
		await _store.SaveRunAsync(
			Record("r1", finalContent:
				"Preamble text that is long enough to be trimmed away. "
				+ "The reconciliation identified seventeen unmatched entries. "
				+ "Trailing text that is also long enough to be trimmed away."),
			cancellationToken: default);

		var filters = HistoryFilterParser.Parse(null, null, null);
		var annotations = new RunAnnotationStore(
			Path.Combine(_tempDir, "annotations"),
			Microsoft.Extensions.Logging.Abstractions.NullLogger<RunAnnotationStore>.Instance);

		var (rows, _, snippets) = await _store.QueryRunsAsync(
			filters.ToIndexQuery(annotations, "unmatched"), 0, 10);

		snippets.Should().NotBeNull();
		var snippet = snippets![rows.Single().FolderPath];
		snippet.Should().Contain("<mark>unmatched</mark>", "the matching term is highlighted");
		snippet.Should().Contain("seventeen", "surrounding context makes the hit readable");
	}

	[Fact]
	public async Task NonContentSearchesReturnNoSnippets()
	{
		await _store.SaveRunAsync(Record("r1", finalContent: "content"), cancellationToken: default);

		var filters = HistoryFilterParser.Parse(null, null, null);
		var annotations = new RunAnnotationStore(
			Path.Combine(_tempDir, "annotations"),
			Microsoft.Extensions.Logging.Abstractions.NullLogger<RunAnnotationStore>.Instance);

		var (_, _, snippets) = await _store.QueryRunsAsync(filters.ToIndexQuery(annotations), 0, 10);

		snippets.Should().BeNull("a plain listing has nothing to highlight");
	}

	// ── Index consistency ──

	[Fact]
	public async Task DeletingARunRemovesItsContentFromTheIndex()
	{
		await _store.SaveRunAsync(
			Record("doomed", finalContent: "singular distinctive phrase"), cancellationToken: default);
		(await SearchAsync("distinctive")).Should().BeEquivalentTo(["doomed"]);

		await _store.DeleteRunAsync("quiet-orch", "doomed");

		(await SearchAsync("distinctive")).Should().BeEmpty(
			"a deleted run must not linger in the full-text index");
	}

	[Fact]
	public async Task ResavingARunReplacesItsContentRatherThanAccumulating()
	{
		var record = Record("r1", finalContent: "first version of the text");
		await _store.SaveRunAsync(record, cancellationToken: default);
		await _store.SaveRunAsync(record, cancellationToken: default);

		(await SearchAsync("first")).Should().BeEquivalentTo(["r1"],
			"the run appears once, not once per save");
	}

	[Fact]
	public async Task ContentIsIndexedWhenAnExistingStoreIsScanned()
	{
		// The save path indexes from the record in memory; a store discovered on disk is indexed
		// by re-reading run.json. Both have to produce a searchable run.
		await _store.SaveRunAsync(
			Record("r1", finalContent: "rediscovered phrase", stepContent: new() { ["s"] = "step words" }),
			cancellationToken: default);
		_store.Dispose();

		File.Delete(Path.Combine(_tempDir, "executions", ".index.db"));
		_store = new FileSystemRunStore(_tempDir);

		await _store.BackfillSearchContentAsync();

		(await SearchAsync("rediscovered")).Should().BeEquivalentTo(["r1"]);
		(await SearchAsync("step")).Should().BeEquivalentTo(["r1"]);
	}

	[Fact]
	public async Task DiscoveringAStoreDoesNotBlockOnReadingContent()
	{
		// Reading every run.json for its text is a whole-store read — 142 s on a 5,421-run store.
		// A host that did that before serving its first request would look hung after an upgrade,
		// so discovery indexes metadata only and leaves the text to the backfill.
		await _store.SaveRunAsync(Record("r1", finalContent: "deferred phrase"), cancellationToken: default);
		_store.Dispose();

		File.Delete(Path.Combine(_tempDir, "executions", ".index.db"));
		_store = new FileSystemRunStore(_tempDir);

		// Metadata is available immediately...
		(await _store.GetRunSummariesAsync()).Should().ContainSingle(s => s.RunId == "r1");
		// ...but the content has not been read yet.
		(await SearchAsync("deferred")).Should().BeEmpty();

		await _store.BackfillSearchContentAsync();

		(await SearchAsync("deferred")).Should().BeEquivalentTo(["r1"]);
	}

	[Fact]
	public async Task BackfillIsIdempotentAndReportsNothingLeftToDo()
	{
		await _store.SaveRunAsync(Record("r1", finalContent: "phrase"), cancellationToken: default);
		_store.Dispose();
		File.Delete(Path.Combine(_tempDir, "executions", ".index.db"));
		_store = new FileSystemRunStore(_tempDir);

		(await _store.BackfillSearchContentAsync()).Should().Be(1);
		(await _store.BackfillSearchContentAsync()).Should().Be(0, "the backlog is drained");
		(await SearchAsync("phrase")).Should().BeEquivalentTo(["r1"], "and draining it twice is harmless");
	}

	[Fact]
	public async Task BackfillStopsCleanlyWhenTheHostIsShuttingDown()
	{
		// The backfill is fire-and-forget and can outlive a Ctrl+C. Unfinished work stays queued,
		// so being interrupted is normal rather than an error to report.
		await _store.SaveRunAsync(Record("r1", finalContent: "phrase"), cancellationToken: default);
		_store.Dispose();
		File.Delete(Path.Combine(_tempDir, "executions", ".index.db"));
		_store = new FileSystemRunStore(_tempDir);
		await _store.GetRunSummariesAsync();

		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var act = async () => await _store.BackfillSearchContentAsync(cts.Token);

		await act.Should().NotThrowAsync();
		(await _store.BackfillSearchContentAsync()).Should().Be(1,
			"the work that was skipped is still queued for the next attempt");
	}


	[Fact]
	public async Task RunsSavedWhileTheHostIsUpAreSearchableWithoutABackfill()
	{
		// The record is in hand at save time, so there is nothing to defer.
		await _store.SaveRunAsync(Record("live", finalContent: "immediate phrase"), cancellationToken: default);

		(await SearchAsync("immediate")).Should().BeEquivalentTo(["live"]);
	}

	[Fact]
	public async Task BackfillMarksUnreadableRunsDoneRatherThanRetryingForever()
	{
		await _store.SaveRunAsync(Record("r1", finalContent: "phrase"), cancellationToken: default);
		_store.Dispose();
		File.Delete(Path.Combine(_tempDir, "executions", ".index.db"));

		// Corrupt the file the backfill will try to read.
		var runJson = Directory.GetFiles(Path.Combine(_tempDir, "executions"), "run.json", SearchOption.AllDirectories).Single();
		_store = new FileSystemRunStore(_tempDir);
		await _store.GetRunSummariesAsync();          // index the metadata first
		await File.WriteAllTextAsync(runJson, "{ this is not json");

		(await _store.BackfillSearchContentAsync()).Should().Be(1);
		(await _store.BackfillSearchContentAsync()).Should().Be(0,
			"an unreadable run is marked examined, not retried on every start");
	}

	[Fact]
	public async Task SaveTimeAndRebuildProduceIdenticalSearchText()
	{
		// If these drift, whether a run is findable depends on whether it was indexed when it ran
		// or during a later rebuild — a difference nobody would think to look for.
		var record = Record(
			"r1",
			finalContent: "final words",
			stepContent: new() { ["beta"] = "beta output", ["alpha"] = "alpha output" });

		await _store.SaveRunAsync(record, cancellationToken: default);

		var fromRecord = FileSystemRunStore.BuildSearchText(record);

		var runJson = Directory.GetFiles(Path.Combine(_tempDir, "executions"), "run.json", SearchOption.AllDirectories).Single();
		var fromDisk = RunIndexProjector
			.ProjectWithContent(File.ReadAllBytes(runJson), Path.GetDirectoryName(runJson)!, includeContent: true)!
			.Value.SearchText;

		fromDisk.Should().Be(fromRecord);
	}

	[Fact]
	public async Task TraceAndPromptAreNotSearchable()
	{
		// Traces are 84% of the store and prompts are input the user wrote, not a result. Indexing
		// either would multiply the index size for matches nobody is looking for.
		var startedAt = s_epoch.AddMinutes(Interlocked.Increment(ref s_seq));
		var step = new StepRunRecord
		{
			StepName = "s",
			Status = ExecutionStatus.Succeeded,
			StartedAt = startedAt,
			CompletedAt = startedAt.AddSeconds(1),
			Content = "visible output",
			PromptSent = "zzzpromptneedlezzz",
			RawContent = "zzzrawneedlezzz",
		};

		await _store.SaveRunAsync(new OrchestrationRunRecord
		{
			RunId = "r1",
			OrchestrationName = "quiet-orch",
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = startedAt,
			CompletedAt = startedAt.AddSeconds(5),
			Status = ExecutionStatus.Succeeded,
			IsIncomplete = false,
			FinalContent = string.Empty,
			SavedFiles = [],
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord> { ["s"] = step },
			AllStepRecords = new Dictionary<string, StepRunRecord> { ["s"] = step },
		}, cancellationToken: default);

		(await SearchAsync("visible")).Should().BeEquivalentTo(["r1"]);
		(await SearchAsync("zzzpromptneedlezzz")).Should().BeEmpty("prompts are not indexed");
		(await SearchAsync("zzzrawneedlezzz")).Should().BeEmpty("pre-handler raw content is not indexed");
	}
}
