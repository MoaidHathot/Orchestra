using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Api;

namespace Orchestra.Host.Persistence;

/// <summary>
/// SQLite-backed index over the run history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived, never authoritative.</b> The run artifacts stay plain on disk exactly as before —
/// this file is a queryable projection of them and can be deleted at any time, after which it is
/// rebuilt from <c>run.json</c>. Nothing is stored here that cannot be recomputed.
/// </para>
/// <para>
/// It exists because the previous in-memory index was rebuilt from scratch on every process start
/// by deserializing every <c>run.json</c> — measured at 5,748 MB across 5,421 runs on a real store,
/// to produce roughly twenty scalar fields per run. Every CLI invocation that spawns a throwaway
/// host paid that cost too.
/// </para>
/// <para>
/// <b>Why a folder path is the key.</b> Run folders are write-once: <c>SaveRunAsync</c> creates one
/// and never modifies it, and mutable per-run state (annotations, checkpoints, temp files) lives in
/// separate roots. An index row keyed on folder path therefore cannot go stale — only additions and
/// deletions need reconciling, and a directory enumeration finds both in about 250 ms.
/// </para>
/// <para>
/// <b>Concurrency.</b> One connection, guarded by a lock. SQLite serializes internally but the
/// ADO.NET objects are not thread-safe, and the write volume here (one row per completed run) does
/// not justify a pool.
/// </para>
/// </remarks>
internal sealed partial class SqliteRunIndex : IDisposable
{
	/// <summary>
	/// Bumped whenever the projected columns change. A mismatch drops and rebuilds rather than
	/// migrating — the data is derived, so a rebuild is always correct and always cheaper to
	/// reason about than a migration path.
	/// </summary>
	private const int SchemaVersion = 3;

	/// <summary>
	/// Appended to every ORDER BY so paging is a true partition of the result set.
	/// </summary>
	/// <remarks>
	/// Start timestamps are not unique — runs launched in one batch share an instant — and SQLite
	/// is free to return tied rows in any order it likes, independently per query. LIMIT/OFFSET
	/// over an unstable order silently repeats some rows and drops others. The folder path is the
	/// primary key, so adding it makes the order total.
	/// </remarks>
	private const string StableOrder = " ORDER BY started_at_ticks DESC, folder_path DESC";

	private SqliteConnection _connection;
	private readonly Lock _gate = new();
	private readonly ILogger _logger;

	public string DatabasePath { get; }

	public SqliteRunIndex(string databasePath, ILogger logger)
	{
		DatabasePath = databasePath;
		_logger = logger;

		var directory = Path.GetDirectoryName(databasePath);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);

		try
		{
			_connection = OpenAndPrepare();
		}
		catch (SqliteException ex)
		{
			// The file exists but is not a usable database — truncated by a crash, replaced by
			// something else, or written by an incompatible build. Since every row is derived from
			// run.json, discarding it costs one rebuild and is strictly better than failing to
			// start.
			LogIndexUnusable(ex, databasePath);
			DiscardDatabaseFiles();
			_connection = OpenAndPrepare();
		}
	}

	private SqliteConnection OpenAndPrepare()
	{
		var connection = new SqliteConnection(new SqliteConnectionStringBuilder
		{
			DataSource = DatabasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Pooling = false,
		}.ToString());

		connection.Open();
		_connection = connection;

		try
		{
			// WAL keeps readers (a second host, the portal) from blocking the writer, and survives
			// a crash without corrupting the file. NORMAL is the right durability trade for a
			// cache: losing the last few rows just means re-projecting those runs on next start.
			Execute("PRAGMA journal_mode=WAL;");
			Execute("PRAGMA synchronous=NORMAL;");
			EnsureSchema();
		}
		catch
		{
			connection.Dispose();
			throw;
		}

		return connection;
	}

	private void DiscardDatabaseFiles()
	{
		foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
		{
			try
			{
				if (File.Exists(path))
					File.Delete(path);
			}
			catch (IOException)
			{
				// Another process may still hold it; the retry below will surface a clearer error.
			}
		}
	}

	// ── Schema ──

	private void EnsureSchema()
	{
		var existing = ReadSchemaVersion();
		if (existing is not null && existing != SchemaVersion)
		{
			LogSchemaReset(existing.Value, SchemaVersion);
			Execute("DROP TABLE IF EXISTS runs;");
			Execute("DROP TABLE IF EXISTS runs_fts;");
			Execute("DROP TABLE IF EXISTS schema_info;");
			existing = null;
		}

		Execute("""
			CREATE TABLE IF NOT EXISTS runs (
				folder_path           TEXT NOT NULL PRIMARY KEY,
				run_id                TEXT NOT NULL,
				orchestration_name    TEXT NOT NULL,
				orchestration_version TEXT NOT NULL,
				triggered_by          TEXT NOT NULL,
				started_at            TEXT NOT NULL,
				started_at_ticks      INTEGER NOT NULL,
				completed_at          TEXT NOT NULL,
				status                TEXT NOT NULL,
				trigger_id            TEXT NULL,
				failed_step_name      TEXT NULL,
				error_message         TEXT NULL,
				completion_reason     TEXT NULL,
				completed_by_step     TEXT NULL,
				is_incomplete         INTEGER NOT NULL,
				cancellation_json     TEXT NULL,
				hook_execution_count  INTEGER NOT NULL,
				retried_from_run_id   TEXT NULL,
				retry_mode            TEXT NULL,
				parent_execution_id   TEXT NULL,
				parent_step_name      TEXT NULL,
				root_execution_id     TEXT NULL,
				nesting_depth         INTEGER NOT NULL,
				origin                TEXT NOT NULL,
				fts_rowid             INTEGER NULL,
				fts_indexed           INTEGER NOT NULL DEFAULT 0
			);
			""");

		// Full-text index over the run's own output. Content is stored rather than referenced,
		// which costs disk but is what lets snippet() show *why* a run matched — the excerpt is
		// the difference between a list of run ids and a usable search result.
		//
		// unicode61 rather than porter: run output is full of identifiers, paths and log lines,
		// where stemming produces matches a user did not ask for and cannot predict.
		Execute("""
			CREATE VIRTUAL TABLE IF NOT EXISTS runs_fts USING fts5(
				content,
				run_id UNINDEXED,
				tokenize = 'unicode61'
			);
			""");

		// started_at_ticks (UTC) rather than the text column, so ordering is correct regardless of
		// the offset each run was written with.
		Execute("CREATE INDEX IF NOT EXISTS ix_runs_started ON runs(started_at_ticks DESC);");
		Execute("CREATE INDEX IF NOT EXISTS ix_runs_orch ON runs(orchestration_name, started_at_ticks DESC);");
		Execute("CREATE INDEX IF NOT EXISTS ix_runs_run_id ON runs(run_id);");
		Execute("CREATE INDEX IF NOT EXISTS ix_runs_trigger ON runs(trigger_id);");
		Execute("CREATE INDEX IF NOT EXISTS ix_runs_parent ON runs(parent_execution_id);");
		Execute("CREATE INDEX IF NOT EXISTS ix_runs_root ON runs(root_execution_id);");
		Execute("CREATE INDEX IF NOT EXISTS ix_runs_origin ON runs(origin, started_at_ticks DESC);");

		// Partial index over the content-indexing backlog. It costs nothing once the backlog is
		// empty, which is the steady state, and turns "what still needs reading" into a lookup
		// rather than a scan of the whole history.
		Execute("CREATE INDEX IF NOT EXISTS ix_runs_fts_pending ON runs(folder_path) WHERE fts_indexed = 0;");

		if (existing is null)
		{
			Execute("CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);");
			Execute("DELETE FROM schema_info;");
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "INSERT INTO schema_info (version) VALUES ($v);";
			cmd.Parameters.AddWithValue("$v", SchemaVersion);
			cmd.ExecuteNonQuery();
		}
	}

	private int? ReadSchemaVersion()
	{
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT version FROM schema_info LIMIT 1;";
			var result = cmd.ExecuteScalar();
			return result is null or DBNull ? null : Convert.ToInt32(result);
		}
		catch (SqliteException)
		{
			// Table absent on a fresh database.
			return null;
		}
	}

	// ── Writes ──

	/// <summary>
	/// Inserts or replaces index rows without examining run content, leaving them queued for the
	/// content backfill.
	/// </summary>
	/// <remarks>
	/// Used when an existing store is discovered on disk. Reading every <c>run.json</c> for its
	/// text takes minutes on a large store — measured at 142 s for 5,421 runs — and doing that
	/// before the host will answer a request trades a working history panel for a search feature
	/// nobody has asked for yet. Metadata lands immediately; the text follows in the background.
	/// </remarks>
	public void UpsertMany(IEnumerable<RunIndex> entries) =>
		UpsertMany(entries.Select(e => new RunProjection(e, null)), contentExamined: false);

	/// <summary>
	/// Inserts or replaces a batch of index rows, together with their full-text content.
	/// </summary>
	/// <remarks>
	/// FTS5 has no upsert, and its rowid is the only cheap way back to a document — the other
	/// columns are <c>UNINDEXED</c>, so matching on them means scanning the whole index. Each
	/// <c>runs</c> row therefore stores the rowid of its FTS document, letting a re-index of one
	/// run drop exactly one document instead of searching for it.
	/// </remarks>
	public void UpsertMany(IEnumerable<RunProjection> projections) =>
		UpsertMany(projections, contentExamined: true);

	/// <param name="contentExamined">
	/// Whether the run's content has been looked at. A run with no text at all is still
	/// "examined", so it is not re-read on every start forever.
	/// </param>
	private void UpsertMany(IEnumerable<RunProjection> projections, bool contentExamined)
	{
		lock (_gate)
		{
			using var transaction = _connection.BeginTransaction();

			using var findFts = _connection.CreateCommand();
			findFts.Transaction = transaction;
			findFts.CommandText = "SELECT fts_rowid FROM runs WHERE folder_path = $folder;";
			var findFolder = findFts.Parameters.Add("$folder", SqliteType.Text);

			using var deleteFts = _connection.CreateCommand();
			deleteFts.Transaction = transaction;
			deleteFts.CommandText = "DELETE FROM runs_fts WHERE rowid = $rid;";
			var deleteRowId = deleteFts.Parameters.Add("$rid", SqliteType.Integer);

			using var insertFts = _connection.CreateCommand();
			insertFts.Transaction = transaction;
			insertFts.CommandText =
				"INSERT INTO runs_fts (content, run_id) VALUES ($content, $runId); SELECT last_insert_rowid();";
			var insertContent = insertFts.Parameters.Add("$content", SqliteType.Text);
			var insertRunId = insertFts.Parameters.Add("$runId", SqliteType.Text);

			using var cmd = _connection.CreateCommand();
			cmd.Transaction = transaction;
			cmd.CommandText = """
				INSERT OR REPLACE INTO runs (
					folder_path, run_id, orchestration_name, orchestration_version, triggered_by,
					started_at, started_at_ticks, completed_at, status, trigger_id,
					failed_step_name, error_message, completion_reason, completed_by_step,
					is_incomplete, cancellation_json, hook_execution_count,
					retried_from_run_id, retry_mode,
					parent_execution_id, parent_step_name, root_execution_id, nesting_depth, origin,
					fts_rowid, fts_indexed)
				VALUES (
					$folder, $runId, $orch, $version, $triggeredBy,
					$startedAt, $startedTicks, $completedAt, $status, $triggerId,
					$failedStep, $error, $completionReason, $completedByStep,
					$isIncomplete, $cancellation, $hookCount,
					$retriedFrom, $retryMode,
					$parentExec, $parentStep, $rootExec, $depth, $origin,
					$ftsRowId, $ftsIndexed);
				""";

			var p = cmd.Parameters;
			p.Add("$folder", SqliteType.Text);
			p.Add("$runId", SqliteType.Text);
			p.Add("$orch", SqliteType.Text);
			p.Add("$version", SqliteType.Text);
			p.Add("$triggeredBy", SqliteType.Text);
			p.Add("$startedAt", SqliteType.Text);
			p.Add("$startedTicks", SqliteType.Integer);
			p.Add("$completedAt", SqliteType.Text);
			p.Add("$status", SqliteType.Text);
			p.Add("$triggerId", SqliteType.Text);
			p.Add("$failedStep", SqliteType.Text);
			p.Add("$error", SqliteType.Text);
			p.Add("$completionReason", SqliteType.Text);
			p.Add("$completedByStep", SqliteType.Text);
			p.Add("$isIncomplete", SqliteType.Integer);
			p.Add("$cancellation", SqliteType.Text);
			p.Add("$hookCount", SqliteType.Integer);
			p.Add("$retriedFrom", SqliteType.Text);
			p.Add("$retryMode", SqliteType.Text);
			p.Add("$parentExec", SqliteType.Text);
			p.Add("$parentStep", SqliteType.Text);
			p.Add("$rootExec", SqliteType.Text);
			p.Add("$depth", SqliteType.Integer);
			p.Add("$origin", SqliteType.Text);
			p.Add("$ftsRowId", SqliteType.Integer);
			p.Add("$ftsIndexed", SqliteType.Integer);
			p["$ftsIndexed"].Value = contentExamined ? 1 : 0;

			foreach (var (entry, searchText) in projections)
			{
				p["$folder"].Value = entry.FolderPath;
				p["$runId"].Value = entry.RunId;
				p["$orch"].Value = entry.OrchestrationName;
				p["$version"].Value = entry.OrchestrationVersion;
				p["$triggeredBy"].Value = entry.TriggeredBy;
				p["$startedAt"].Value = entry.StartedAt.ToString("O");
				p["$startedTicks"].Value = entry.StartedAt.UtcTicks;
				p["$completedAt"].Value = entry.CompletedAt.ToString("O");
				p["$status"].Value = entry.Status.ToString();
				p["$triggerId"].Value = (object?)entry.TriggerId ?? DBNull.Value;
				p["$failedStep"].Value = (object?)entry.FailedStepName ?? DBNull.Value;
				p["$error"].Value = (object?)entry.ErrorMessage ?? DBNull.Value;
				p["$completionReason"].Value = (object?)entry.CompletionReason ?? DBNull.Value;
				p["$completedByStep"].Value = (object?)entry.CompletedByStep ?? DBNull.Value;
				p["$isIncomplete"].Value = entry.IsIncomplete ? 1 : 0;
				p["$cancellation"].Value = entry.Cancellation is null
					? DBNull.Value
					: JsonSerializer.Serialize(entry.Cancellation, s_jsonOptions);
				p["$hookCount"].Value = entry.HookExecutionCount;
				p["$retriedFrom"].Value = (object?)entry.RetriedFromRunId ?? DBNull.Value;
				p["$retryMode"].Value = (object?)entry.RetryMode ?? DBNull.Value;
				p["$parentExec"].Value = (object?)entry.ParentExecutionId ?? DBNull.Value;
				p["$parentStep"].Value = (object?)entry.ParentStepName ?? DBNull.Value;
				p["$rootExec"].Value = (object?)entry.RootExecutionId ?? DBNull.Value;
				p["$depth"].Value = entry.NestingDepth;
				// Materialized rather than recomputed in SQL so the C# classifier stays the single
				// definition of what an origin is. A change there bumps the schema and rebuilds.
				p["$origin"].Value = RunOriginClassifier.ToWireValue(RunOriginClassifier.Classify(entry.TriggeredBy));

				// Drop the document this folder used to own, if any, then index the new text.
				// Re-indexing an existing folder only happens on the duplicate-save path, since
				// run folders are otherwise write-once.
				findFolder.Value = entry.FolderPath;
				if (findFts.ExecuteScalar() is long staleRowId)
				{
					deleteRowId.Value = staleRowId;
					deleteFts.ExecuteNonQuery();
				}

				if (string.IsNullOrEmpty(searchText))
				{
					p["$ftsRowId"].Value = DBNull.Value;
				}
				else
				{
					insertContent.Value = searchText;
					insertRunId.Value = entry.RunId;
					p["$ftsRowId"].Value = Convert.ToInt64(insertFts.ExecuteScalar());
				}

				cmd.ExecuteNonQuery();
			}

			transaction.Commit();
		}
	}

	public void Upsert(RunIndex entry) => UpsertMany([entry]);

	/// <summary>Removes rows for the supplied folder paths, and their full-text documents.</summary>
	public void DeleteByFolderPaths(IEnumerable<string> folderPaths)
	{
		lock (_gate)
		{
			using var transaction = _connection.BeginTransaction();

			using var deleteFts = _connection.CreateCommand();
			deleteFts.Transaction = transaction;
			deleteFts.CommandText =
				"DELETE FROM runs_fts WHERE rowid IN (SELECT fts_rowid FROM runs WHERE folder_path = $folder AND fts_rowid IS NOT NULL);";
			var ftsFolder = deleteFts.Parameters.Add("$folder", SqliteType.Text);

			using var cmd = _connection.CreateCommand();
			cmd.Transaction = transaction;
			cmd.CommandText = "DELETE FROM runs WHERE folder_path = $folder;";
			var param = cmd.Parameters.Add("$folder", SqliteType.Text);

			foreach (var path in folderPaths)
			{
				ftsFolder.Value = path;
				deleteFts.ExecuteNonQuery();
				param.Value = path;
				cmd.ExecuteNonQuery();
			}

			transaction.Commit();
		}
	}

	/// <summary>Removes the row for a single run. Returns <see langword="true"/> when one existed.</summary>
	public bool DeleteRun(string orchestrationName, string runId)
	{
		lock (_gate)
		{
			using var transaction = _connection.BeginTransaction();

			using var deleteFts = _connection.CreateCommand();
			deleteFts.Transaction = transaction;
			deleteFts.CommandText = """
				DELETE FROM runs_fts WHERE rowid IN (
					SELECT fts_rowid FROM runs
					WHERE orchestration_name = $orch AND run_id = $runId AND fts_rowid IS NOT NULL);
				""";
			deleteFts.Parameters.AddWithValue("$orch", orchestrationName);
			deleteFts.Parameters.AddWithValue("$runId", runId);
			deleteFts.ExecuteNonQuery();

			using var cmd = _connection.CreateCommand();
			cmd.Transaction = transaction;
			cmd.CommandText = "DELETE FROM runs WHERE orchestration_name = $orch AND run_id = $runId;";
			cmd.Parameters.AddWithValue("$orch", orchestrationName);
			cmd.Parameters.AddWithValue("$runId", runId);
			var affected = cmd.ExecuteNonQuery();

			transaction.Commit();
			return affected > 0;
		}
	}

	// ── Reads ──

	/// <summary>Folder paths whose run content has not been read into the full-text index yet.</summary>
	public IReadOnlyList<string> GetFoldersPendingContentIndex(int limit)
	{
		lock (_gate)
		{
			var paths = new List<string>();
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = $"SELECT folder_path FROM runs WHERE fts_indexed = 0 LIMIT {limit};";
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				paths.Add(reader.GetString(0));
			return paths;
		}
	}

	/// <summary>Number of runs still waiting to have their content indexed.</summary>
	public int PendingContentIndexCount
	{
		get
		{
			lock (_gate)
			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = "SELECT COUNT(*) FROM runs WHERE fts_indexed = 0;";
				return Convert.ToInt32(cmd.ExecuteScalar());
			}
		}
	}

	/// <summary>
	/// Attaches full-text content to rows that already exist, marking them examined.
	/// </summary>
	/// <remarks>
	/// A <see langword="null"/> text still marks the row examined: a run with no output, or one
	/// whose file has since gone or become unreadable, must not be retried on every start.
	/// </remarks>
	public void SetSearchContent(IEnumerable<(string FolderPath, string RunId, string? SearchText)> entries)
	{
		lock (_gate)
		{
			using var transaction = _connection.BeginTransaction();

			using var findFts = _connection.CreateCommand();
			findFts.Transaction = transaction;
			findFts.CommandText = "SELECT fts_rowid FROM runs WHERE folder_path = $folder;";
			var findFolder = findFts.Parameters.Add("$folder", SqliteType.Text);

			using var deleteFts = _connection.CreateCommand();
			deleteFts.Transaction = transaction;
			deleteFts.CommandText = "DELETE FROM runs_fts WHERE rowid = $rid;";
			var deleteRowId = deleteFts.Parameters.Add("$rid", SqliteType.Integer);

			using var insertFts = _connection.CreateCommand();
			insertFts.Transaction = transaction;
			insertFts.CommandText =
				"INSERT INTO runs_fts (content, run_id) VALUES ($content, $runId); SELECT last_insert_rowid();";
			var insertContent = insertFts.Parameters.Add("$content", SqliteType.Text);
			var insertRunId = insertFts.Parameters.Add("$runId", SqliteType.Text);

			using var update = _connection.CreateCommand();
			update.Transaction = transaction;
			update.CommandText =
				"UPDATE runs SET fts_rowid = $rid, fts_indexed = 1 WHERE folder_path = $folder;";
			var updateRowId = update.Parameters.Add("$rid", SqliteType.Integer);
			var updateFolder = update.Parameters.Add("$folder", SqliteType.Text);

			foreach (var (folderPath, runId, searchText) in entries)
			{
				findFolder.Value = folderPath;
				if (findFts.ExecuteScalar() is long staleRowId)
				{
					deleteRowId.Value = staleRowId;
					deleteFts.ExecuteNonQuery();
				}

				if (string.IsNullOrEmpty(searchText))
				{
					updateRowId.Value = DBNull.Value;
				}
				else
				{
					insertContent.Value = searchText;
					insertRunId.Value = runId;
					updateRowId.Value = Convert.ToInt64(insertFts.ExecuteScalar());
				}

				updateFolder.Value = folderPath;
				update.ExecuteNonQuery();
			}

			transaction.Commit();
		}
	}

	/// <summary>Every folder path currently indexed. Used to reconcile against the filesystem.</summary>
	public HashSet<string> GetIndexedFolderPaths()
	{
		lock (_gate)
		{
			var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT folder_path FROM runs;";
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				paths.Add(reader.GetString(0));
			return paths;
		}
	}

	public int Count
	{
		get
		{
			lock (_gate)
			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = "SELECT COUNT(*) FROM runs;";
				return Convert.ToInt32(cmd.ExecuteScalar());
			}
		}
	}

	/// <summary>All runs, newest first.</summary>
	public IReadOnlyList<RunIndex> ListAll(int? limit = null) =>
		Query("SELECT * FROM runs" + StableOrder + LimitClause(limit), _ => { });

	/// <summary>Runs for one orchestration, newest first.</summary>
	public IReadOnlyList<RunIndex> ListByOrchestration(string orchestrationName, int? limit = null) =>
		Query(
			"SELECT * FROM runs WHERE orchestration_name = $orch" + StableOrder + LimitClause(limit),
			cmd => cmd.Parameters.AddWithValue("$orch", orchestrationName));

	/// <summary>Runs fired by one trigger, newest first.</summary>
	public IReadOnlyList<RunIndex> ListByTrigger(string triggerId, int? limit = null) =>
		Query(
			"SELECT * FROM runs WHERE trigger_id = $trigger" + StableOrder + LimitClause(limit),
			cmd => cmd.Parameters.AddWithValue("$trigger", triggerId));

	/// <summary>Finds a run by id across every orchestration. Case-insensitive, as callers expect.</summary>
	public RunIndex? FindByRunId(string runId) =>
		Query(
			"SELECT * FROM runs WHERE run_id = $runId COLLATE NOCASE" + StableOrder + " LIMIT 1;",
			cmd => cmd.Parameters.AddWithValue("$runId", runId))
		.FirstOrDefault();

	/// <summary>Finds a specific run. Case-sensitive on both keys, matching the previous behaviour.</summary>
	public RunIndex? FindRun(string orchestrationName, string runId) =>
		Query(
			"SELECT * FROM runs WHERE orchestration_name = $orch AND run_id = $runId LIMIT 1;",
			cmd =>
			{
				cmd.Parameters.AddWithValue("$orch", orchestrationName);
				cmd.Parameters.AddWithValue("$runId", runId);
			})
		.FirstOrDefault();

	/// <summary>
	/// Direct children of an execution, or every run in its subtree. Newest first, and matched
	/// case-insensitively, preserving the semantics the lineage callers were written against.
	/// </summary>
	public IReadOnlyList<RunIndex> FindChildRuns(
		string? parentExecutionId,
		string? rootExecutionId,
		ExecutionStatus? statusFilter,
		int? limit,
		int? offset)
	{
		var scope = !string.IsNullOrWhiteSpace(parentExecutionId) ? parentExecutionId : rootExecutionId;
		if (string.IsNullOrWhiteSpace(scope))
			return [];

		var column = !string.IsNullOrWhiteSpace(parentExecutionId) ? "parent_execution_id" : "root_execution_id";

		var sql = $"SELECT * FROM runs WHERE {column} = $scope COLLATE NOCASE";
		if (statusFilter is not null)
			sql += " AND status = $status";
		sql += StableOrder;

		// SQLite requires a LIMIT before OFFSET; -1 means "no limit".
		if (limit is not null || offset is not null)
			sql += $" LIMIT {limit?.ToString() ?? "-1"}";
		if (offset is not null)
			sql += $" OFFSET {offset.Value}";

		return Query(sql + ";", cmd =>
		{
			cmd.Parameters.AddWithValue("$scope", scope);
			if (statusFilter is not null)
				cmd.Parameters.AddWithValue("$status", statusFilter.Value.ToString());
		});
	}

	/// <summary>
	/// Runs matching <paramref name="query"/>, newest first, together with the size of the whole
	/// match set.
	/// </summary>
	/// <remarks>
	/// Filtering, ordering, counting and paging all happen in SQL. The endpoints used to pull
	/// every index row into memory and filter with LINQ, which cost one full materialization of
	/// the history — thousands of objects — per request, and made an honest <c>total</c>
	/// awkward enough that the search endpoint simply reported the size of the page instead.
	/// </remarks>
	/// <returns>
	/// The requested page, the total number of matching runs ignoring paging, and — when the query
	/// searched run content — the matching excerpt per folder path.
	/// </returns>
	public (IReadOnlyList<RunIndex> Rows, int Total, IReadOnlyDictionary<string, string>? Snippets) QueryPage(
		RunIndexQuery query, int offset, int limit)
	{
		lock (_gate)
		{
			PopulateIdFilters(
				(Bucket.Allow, query.RunIdAllowList),
				(Bucket.Also, query.AlsoMatchRunIds),
				(Bucket.Deny, query.RunIdDenyList));

			var predicate = BuildPredicate(query);

			using var countCmd = _connection.CreateCommand();
			countCmd.CommandText = $"SELECT COUNT(*) FROM runs{predicate.Where};";
			predicate.Bind(countCmd);
			var total = Convert.ToInt32(countCmd.ExecuteScalar());

			// Skip the page query entirely when the caller only wanted the count, or when the
			// offset is already past the end.
			if (limit <= 0 || offset >= total)
				return ([], total, null);

			var wantsSnippets = !string.IsNullOrEmpty(query.ContentMatch);

			using var pageCmd = _connection.CreateCommand();
			pageCmd.CommandText =
				$"SELECT runs.*{(wantsSnippets ? SnippetSelect : "")} FROM runs{predicate.Where}{StableOrder} "
				+ $"LIMIT {limit} OFFSET {Math.Max(0, offset)};";
			predicate.Bind(pageCmd);

			var rows = new List<RunIndex>();
			Dictionary<string, string>? snippets = wantsSnippets ? new(StringComparer.OrdinalIgnoreCase) : null;

			using var reader = pageCmd.ExecuteReader();
			while (reader.Read())
			{
				var row = Read(reader);
				rows.Add(row);

				if (snippets is null)
					continue;

				var ordinal = reader.GetOrdinal("match_snippet");
				if (!reader.IsDBNull(ordinal))
					snippets[row.FolderPath] = reader.GetString(ordinal);
			}

			return (rows, total, snippets);
		}
	}

	/// <summary>
	/// Maps run ids to their orchestration names. Used to label a child row with its parent's
	/// orchestration, for just the parents referenced by one page rather than the whole history.
	/// </summary>
	public Dictionary<string, string> GetOrchestrationNamesByRunIds(IReadOnlyCollection<string> runIds)
	{
		var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (runIds.Count == 0)
			return lookup;

		lock (_gate)
		{
			PopulateIdFilters((Bucket.Names, runIds));

			using var cmd = _connection.CreateCommand();
			cmd.CommandText =
				$"SELECT run_id, orchestration_name FROM runs WHERE run_id IN ({BucketQuery(Bucket.Names)});";
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				lookup[reader.GetString(0)] = reader.GetString(1);
			return lookup;
		}
	}

	// ── Filter plumbing ──

	/// <summary>
	/// Bucket ids within <see cref="FilterTable"/>. One table with a discriminator rather than
	/// four tables, so setup and teardown is a single statement each.
	/// </summary>
	private static class Bucket
	{
		public const int Allow = 0;
		public const int Also = 1;
		public const int Deny = 2;
		public const int Names = 3;
	}

	private const string FilterTable = "temp.run_id_filter";

	private static string BucketQuery(int bucket) =>
		$"SELECT run_id FROM {FilterTable} WHERE bucket = {bucket}";

	/// <summary>
	/// Loads sets of run ids into a temporary table so they can be joined against.
	/// </summary>
	/// <remarks>
	/// A temp table rather than a bound parameter per id: annotation-derived filters (favorites,
	/// tags) have no bound on how many runs they select, and SQLite caps the number of parameters
	/// in a single statement. The table is per-connection and lives in memory, and this path is
	/// only taken when the caller actually asked for such a filter.
	/// </remarks>
	private void PopulateIdFilters(params (int Bucket, IReadOnlyCollection<string>? Ids)[] sets)
	{
		Execute($"CREATE TEMP TABLE IF NOT EXISTS run_id_filter (bucket INTEGER NOT NULL, run_id TEXT NOT NULL, PRIMARY KEY (bucket, run_id));");

		using var transaction = _connection.BeginTransaction();

		using (var clear = _connection.CreateCommand())
		{
			clear.Transaction = transaction;
			clear.CommandText = $"DELETE FROM {FilterTable};";
			clear.ExecuteNonQuery();
		}

		using (var insert = _connection.CreateCommand())
		{
			insert.Transaction = transaction;
			insert.CommandText = $"INSERT OR IGNORE INTO {FilterTable} (bucket, run_id) VALUES ($bucket, $id);";
			var bucketParam = insert.Parameters.Add("$bucket", SqliteType.Integer);
			var idParam = insert.Parameters.Add("$id", SqliteType.Text);

			foreach (var (bucket, ids) in sets)
			{
				if (ids is null)
					continue;

				bucketParam.Value = bucket;
				foreach (var id in ids)
				{
					idParam.Value = id;
					insert.ExecuteNonQuery();
				}
			}
		}

		transaction.Commit();
	}

	private readonly record struct Predicate(string Where, Action<SqliteCommand> Bind);

	private static Predicate BuildPredicate(RunIndexQuery query)
	{
		var clauses = new List<string>();
		var parameters = new List<(string Name, object Value)>();

		if (query.Origins is { Count: > 0 } origins)
		{
			var names = origins.Select((_, i) => $"$origin{i}").ToList();
			clauses.Add($"origin IN ({string.Join(", ", names)})");
			parameters.AddRange(origins.Select((o, i) => ($"$origin{i}", (object)o)));
		}

		if (query.RootsOnly is { } rootsOnly)
		{
			// A run written before lineage tracking has NULL rather than an empty string, so both
			// have to count as "no parent".
			clauses.Add(rootsOnly
				? "(parent_execution_id IS NULL OR parent_execution_id = '')"
				: "(parent_execution_id IS NOT NULL AND parent_execution_id <> '')");
		}

		if (query.Statuses is { Count: > 0 } statuses)
		{
			var names = statuses.Select((_, i) => $"$status{i}").ToList();
			clauses.Add($"status COLLATE NOCASE IN ({string.Join(", ", names)})");
			parameters.AddRange(statuses.Select((s, i) => ($"$status{i}", (object)s)));
		}

		// AND-scoped allow list: the run must be one of these (e.g. tagged, favorited).
		if (query.RunIdAllowList is not null)
			clauses.Add($"run_id IN ({BucketQuery(Bucket.Allow)})");

		// AND-scoped deny list: the complement case, e.g. "not favorited".
		if (query.RunIdDenyList is { Count: > 0 })
			clauses.Add($"run_id NOT IN ({BucketQuery(Bucket.Deny)})");

		// The text-match group: a run qualifies if its name or id contains the query, if its
		// annotation matched (resolved by the caller, since annotations live on disk), or if its
		// indexed output matches. These are alternatives, not additional constraints.
		var alternatives = new List<string>();

		if (!string.IsNullOrEmpty(query.NameOrIdContains))
		{
			alternatives.Add(@"orchestration_name LIKE $text ESCAPE '\'");
			alternatives.Add(@"run_id LIKE $text ESCAPE '\'");
			parameters.Add(("$text", $"%{EscapeLike(query.NameOrIdContains)}%"));
		}

		if (query.AlsoMatchRunIds is { Count: > 0 })
			alternatives.Add($"run_id IN ({BucketQuery(Bucket.Also)})");

		if (!string.IsNullOrEmpty(query.ContentMatch))
		{
			alternatives.Add($"fts_rowid IN (SELECT rowid FROM runs_fts WHERE runs_fts MATCH {FtsParam})");
			parameters.Add((FtsParam, query.ContentMatch));
		}

		if (alternatives.Count > 0)
			clauses.Add($"({string.Join(" OR ", alternatives)})");

		var where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
		return new Predicate(where, cmd =>
		{
			foreach (var (name, value) in parameters)
				cmd.Parameters.AddWithValue(name, value);
		});
	}

	private const string FtsParam = "$fts";

	/// <summary>
	/// Correlated subquery that produces the matching excerpt for a row, or <c>NULL</c> when the
	/// row was matched by something other than its content.
	/// </summary>
	/// <remarks>
	/// Evaluated per returned row, and each evaluation is a rowid lookup into FTS5 rather than a
	/// scan, so the cost is bounded by the page size rather than by the number of matches.
	/// </remarks>
	private const string SnippetSelect = $"""
		, (SELECT snippet(runs_fts, 0, '<mark>', '</mark>', '…', 24)
		   FROM runs_fts
		   WHERE runs_fts.rowid = runs.fts_rowid AND runs_fts MATCH {FtsParam}) AS match_snippet
		""";

	/// <summary>Neutralizes LIKE wildcards so a query for "50%" does not match everything.</summary>
	private static string EscapeLike(string value) => value
		.Replace("\\", "\\\\", StringComparison.Ordinal)
		.Replace("%", "\\%", StringComparison.Ordinal)
		.Replace("_", "\\_", StringComparison.Ordinal);

	/// <summary>Per-orchestration run count and most recent start, computed in SQL.</summary>
	public IReadOnlyDictionary<string, OrchestrationRunStats> GetOrchestrationStats()
	{
		lock (_gate)
		{
			var stats = new Dictionary<string, OrchestrationRunStats>(StringComparer.OrdinalIgnoreCase);
			using var cmd = _connection.CreateCommand();
			cmd.CommandText =
				"SELECT orchestration_name, COUNT(*), MAX(started_at_ticks) FROM runs GROUP BY orchestration_name;";
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
			{
				var name = reader.GetString(0);
				var count = reader.GetInt32(1);
				var ticks = reader.GetInt64(2);
				stats[name] = new OrchestrationRunStats(count, new DateTimeOffset(ticks, TimeSpan.Zero));
			}
			return stats;
		}
	}

	private static string LimitClause(int? limit) => limit is null ? ";" : $" LIMIT {limit.Value};";

	private IReadOnlyList<RunIndex> Query(string sql, Action<SqliteCommand> bind)
	{
		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = sql;
			bind(cmd);

			var results = new List<RunIndex>();
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				results.Add(Read(reader));
			return results;
		}
	}

	private static RunIndex Read(SqliteDataReader reader) => new()
	{
		FolderPath = reader.GetString(reader.GetOrdinal("folder_path")),
		RunId = reader.GetString(reader.GetOrdinal("run_id")),
		OrchestrationName = reader.GetString(reader.GetOrdinal("orchestration_name")),
		OrchestrationVersion = reader.GetString(reader.GetOrdinal("orchestration_version")),
		TriggeredBy = reader.GetString(reader.GetOrdinal("triggered_by")),
		StartedAt = ParseOffset(reader.GetString(reader.GetOrdinal("started_at"))),
		CompletedAt = ParseOffset(reader.GetString(reader.GetOrdinal("completed_at"))),
		Status = Enum.TryParse<ExecutionStatus>(reader.GetString(reader.GetOrdinal("status")), out var s)
			? s
			: ExecutionStatus.Succeeded,
		TriggerId = GetNullableString(reader, "trigger_id"),
		FailedStepName = GetNullableString(reader, "failed_step_name"),
		ErrorMessage = GetNullableString(reader, "error_message"),
		CompletionReason = GetNullableString(reader, "completion_reason"),
		CompletedByStep = GetNullableString(reader, "completed_by_step"),
		IsIncomplete = reader.GetInt32(reader.GetOrdinal("is_incomplete")) != 0,
		Cancellation = DeserializeCancellation(GetNullableString(reader, "cancellation_json")),
		HookExecutionCount = reader.GetInt32(reader.GetOrdinal("hook_execution_count")),
		RetriedFromRunId = GetNullableString(reader, "retried_from_run_id"),
		RetryMode = GetNullableString(reader, "retry_mode"),
		ParentExecutionId = GetNullableString(reader, "parent_execution_id"),
		ParentStepName = GetNullableString(reader, "parent_step_name"),
		RootExecutionId = GetNullableString(reader, "root_execution_id"),
		NestingDepth = reader.GetInt32(reader.GetOrdinal("nesting_depth")),
	};

	private static string? GetNullableString(SqliteDataReader reader, string column)
	{
		var ordinal = reader.GetOrdinal(column);
		return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
	}

	private static DateTimeOffset ParseOffset(string value) =>
		DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
			? parsed
			: default;

	private static CancellationDetails? DeserializeCancellation(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return null;

		try { return JsonSerializer.Deserialize<CancellationDetails>(json, s_jsonOptions); }
		catch (JsonException) { return null; }
	}

	private void Execute(string sql)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
	};

	public void Dispose()
	{
		lock (_gate)
		{
			_connection.Dispose();
		}
		SqliteConnection.ClearAllPools();
	}

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Run index schema changed from v{Existing} to v{Current}; rebuilding from run.json")]
	private partial void LogSchemaReset(int existing, int current);

	[LoggerMessage(Level = LogLevel.Warning,
		Message = "Run index at {Path} is unusable and will be rebuilt from run.json")]
	private partial void LogIndexUnusable(Exception ex, string path);
}
