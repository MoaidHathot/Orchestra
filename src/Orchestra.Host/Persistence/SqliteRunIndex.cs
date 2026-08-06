using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

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
	private const int SchemaVersion = 1;

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
				nesting_depth         INTEGER NOT NULL
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

	/// <summary>Inserts or replaces a batch of index rows in a single transaction.</summary>
	public void UpsertMany(IEnumerable<RunIndex> entries)
	{
		lock (_gate)
		{
			using var transaction = _connection.BeginTransaction();
			using var cmd = _connection.CreateCommand();
			cmd.Transaction = transaction;
			cmd.CommandText = """
				INSERT OR REPLACE INTO runs (
					folder_path, run_id, orchestration_name, orchestration_version, triggered_by,
					started_at, started_at_ticks, completed_at, status, trigger_id,
					failed_step_name, error_message, completion_reason, completed_by_step,
					is_incomplete, cancellation_json, hook_execution_count,
					retried_from_run_id, retry_mode,
					parent_execution_id, parent_step_name, root_execution_id, nesting_depth)
				VALUES (
					$folder, $runId, $orch, $version, $triggeredBy,
					$startedAt, $startedTicks, $completedAt, $status, $triggerId,
					$failedStep, $error, $completionReason, $completedByStep,
					$isIncomplete, $cancellation, $hookCount,
					$retriedFrom, $retryMode,
					$parentExec, $parentStep, $rootExec, $depth);
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

			foreach (var entry in entries)
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
				cmd.ExecuteNonQuery();
			}

			transaction.Commit();
		}
	}

	public void Upsert(RunIndex entry) => UpsertMany([entry]);

	/// <summary>Removes rows for the supplied folder paths.</summary>
	public void DeleteByFolderPaths(IEnumerable<string> folderPaths)
	{
		lock (_gate)
		{
			using var transaction = _connection.BeginTransaction();
			using var cmd = _connection.CreateCommand();
			cmd.Transaction = transaction;
			cmd.CommandText = "DELETE FROM runs WHERE folder_path = $folder;";
			var param = cmd.Parameters.Add("$folder", SqliteType.Text);

			foreach (var path in folderPaths)
			{
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
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "DELETE FROM runs WHERE orchestration_name = $orch AND run_id = $runId;";
			cmd.Parameters.AddWithValue("$orch", orchestrationName);
			cmd.Parameters.AddWithValue("$runId", runId);
			return cmd.ExecuteNonQuery() > 0;
		}
	}

	// ── Reads ──

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
		Query("SELECT * FROM runs ORDER BY started_at_ticks DESC" + LimitClause(limit), _ => { });

	/// <summary>Runs for one orchestration, newest first.</summary>
	public IReadOnlyList<RunIndex> ListByOrchestration(string orchestrationName, int? limit = null) =>
		Query(
			"SELECT * FROM runs WHERE orchestration_name = $orch ORDER BY started_at_ticks DESC" + LimitClause(limit),
			cmd => cmd.Parameters.AddWithValue("$orch", orchestrationName));

	/// <summary>Runs fired by one trigger, newest first.</summary>
	public IReadOnlyList<RunIndex> ListByTrigger(string triggerId, int? limit = null) =>
		Query(
			"SELECT * FROM runs WHERE trigger_id = $trigger ORDER BY started_at_ticks DESC" + LimitClause(limit),
			cmd => cmd.Parameters.AddWithValue("$trigger", triggerId));

	/// <summary>Finds a run by id across every orchestration. Case-insensitive, as callers expect.</summary>
	public RunIndex? FindByRunId(string runId) =>
		Query(
			"SELECT * FROM runs WHERE run_id = $runId COLLATE NOCASE ORDER BY started_at_ticks DESC LIMIT 1;",
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
		sql += " ORDER BY started_at_ticks DESC";

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
