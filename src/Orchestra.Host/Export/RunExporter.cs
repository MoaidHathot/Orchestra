using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Persistence;

namespace Orchestra.Host.Export;

/// <summary>
/// Exports a stored run into a self-contained, portable form.
/// </summary>
/// <remarks>
/// <para>
/// A run's artifacts live in <b>two</b> roots, which is the whole reason this class exists:
/// </para>
/// <list type="bullet">
/// <item><c>{dataPath}/executions/{orch}/{folder}/</c> — the run record and per-step projections.</item>
/// <item><c>{dataPath}/temp/{orch}/{runId}/</c> — files written via <c>orchestra_save_file</c>.</item>
/// </list>
/// <para>
/// The second is frequently where the actual deliverable lives: a step that produces a large
/// document typically writes it to a file and returns only a short summary inline, so copying
/// the run folder alone yields the summary and loses the document. Every format below pulls
/// the temp-store artifacts in.
/// </para>
/// </remarks>
public sealed partial class RunExporter
{
	private readonly string _dataPath;
	private readonly FileSystemRunStore _runStore;
	private readonly RunAnnotationStore _annotations;
	private readonly ILogger<RunExporter> _logger;

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
	};

	private static readonly UTF8Encoding s_utf8NoBom = new(false);

	public RunExporter(
		string dataPath,
		FileSystemRunStore runStore,
		RunAnnotationStore annotations,
		ILogger<RunExporter> logger)
	{
		_dataPath = dataPath;
		_runStore = runStore;
		_annotations = annotations;
		_logger = logger;
	}

	// ── Public API ──

	/// <summary>Exports one run into <paramref name="targetDirectory"/>.</summary>
	public async Task<RunExportResult> ExportAsync(
		string orchestrationName,
		string runId,
		RunExportFormat format,
		string targetDirectory,
		CancellationToken cancellationToken = default)
	{
		var record = await _runStore.GetRunAsync(orchestrationName, runId, cancellationToken)
			?? throw new FileNotFoundException($"Run '{runId}' not found for orchestration '{orchestrationName}'.");

		var summaries = await _runStore.GetRunSummariesAsync(orchestrationName, limit: null, cancellationToken);
		var index = summaries.FirstOrDefault(s => string.Equals(s.RunId, runId, StringComparison.OrdinalIgnoreCase));

		var warnings = new List<string>();
		var written = new List<string>();

		if (format == RunExportFormat.Report)
		{
			var path = await ExportReportAsync(record, index, targetDirectory, warnings, cancellationToken);
			return Result(record, path, [path], warnings);
		}

		var exportDir = Path.Combine(targetDirectory, ExportFolderName(record));
		Directory.CreateDirectory(exportDir);

		if (format == RunExportFormat.Data)
		{
			written.AddRange(await WriteStepPayloadsAsync(record, exportDir, warnings, cancellationToken));
			return Result(record, exportDir, written, warnings);
		}

		// Bundle: everything.
		written.AddRange(await WriteBundleAsync(record, index, exportDir, warnings, cancellationToken));
		return Result(record, exportDir, written, warnings);
	}

	/// <summary>
	/// Exports one run into a zip archive. Used by the REST endpoint, which cannot write to the
	/// caller's filesystem.
	/// </summary>
	public async Task<(byte[] Content, string FileName, string ContentType)> ExportToArchiveAsync(
		string orchestrationName,
		string runId,
		RunExportFormat format,
		CancellationToken cancellationToken = default)
	{
		var staging = Path.Combine(Path.GetTempPath(), $"orchestra-export-{Guid.NewGuid():N}");
		Directory.CreateDirectory(staging);
		try
		{
			var result = await ExportAsync(orchestrationName, runId, format, staging, cancellationToken);

			if (format == RunExportFormat.Report)
			{
				var bytes = await File.ReadAllBytesAsync(result.Path, cancellationToken);
				return (bytes, Path.GetFileName(result.Path), "text/markdown");
			}

			var zipPath = Path.Combine(staging, "export.zip");
			ZipFile.CreateFromDirectory(result.Path, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
			var zipBytes = await File.ReadAllBytesAsync(zipPath, cancellationToken);
			return (zipBytes, $"{Path.GetFileName(result.Path)}.zip", "application/zip");
		}
		finally
		{
			TryDeleteDirectory(staging);
		}
	}

	/// <summary>
	/// Compresses an already-exported directory in place, replacing it with a sibling
	/// <c>.zip</c>. Used by the CLI's <c>--zip</c> flag.
	/// </summary>
	public static string CompressExport(string exportDirectory)
	{
		var zipPath = exportDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";
		if (File.Exists(zipPath))
			File.Delete(zipPath);

		ZipFile.CreateFromDirectory(exportDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
		TryDeleteDirectory(exportDirectory);
		return zipPath;
	}

	/// <summary>
	/// Resolves the runs matching a bulk selector. At least one of
	/// <paramref name="favoritesOnly"/> or <paramref name="tags"/> must be supplied.
	/// </summary>
	public async Task<IReadOnlyList<(string OrchestrationName, string RunId)>> SelectRunsAsync(
		bool favoritesOnly,
		IReadOnlyCollection<string>? tags,
		CancellationToken cancellationToken = default)
	{
		var summaries = await _runStore.GetRunSummariesAsync(limit: null, cancellationToken);
		var wanted = tags is { Count: > 0 }
			? new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase)
			: null;

		var selected = new List<(string, string)>();
		foreach (var summary in summaries)
		{
			var annotation = _annotations.Get(summary.RunId);
			if (annotation is null)
				continue;

			if (favoritesOnly && !annotation.Favorite)
				continue;

			// OR semantics, matching the history tag filter.
			if (wanted is not null && !annotation.Tags.Any(wanted.Contains))
				continue;

			selected.Add((summary.OrchestrationName, summary.RunId));
		}

		return selected;
	}

	// ── Formats ──

	private async Task<string> ExportReportAsync(
		OrchestrationRunRecord record,
		RunIndex? index,
		string targetDirectory,
		List<string> warnings,
		CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(targetDirectory);
		var path = Path.Combine(targetDirectory, $"{ExportFolderName(record)}.md");

		// Prefer the richest markdown available: a saved .md artifact usually holds the full
		// document while the inline content is a summary of it.
		var artifacts = ResolveArtifacts(record, warnings);
		var bestMarkdown = artifacts
			.Where(a => string.Equals(Path.GetExtension(a.SourcePath), ".md", StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(a => a.Length)
			.FirstOrDefault();

		if (bestMarkdown is not null && bestMarkdown.Length > record.FinalContent.Length)
		{
			File.Copy(bestMarkdown.SourcePath, path, overwrite: true);
			return path;
		}

		var resultMd = index is null ? null : Path.Combine(index.FolderPath, "result.md");
		if (string.IsNullOrWhiteSpace(record.FinalContent) && resultMd is not null && File.Exists(resultMd))
		{
			File.Copy(resultMd, path, overwrite: true);
			return path;
		}

		await WriteTextAsync(path, record.FinalContent, cancellationToken);
		return path;
	}

	private async Task<IReadOnlyList<string>> WriteBundleAsync(
		OrchestrationRunRecord record,
		RunIndex? index,
		string exportDir,
		List<string> warnings,
		CancellationToken cancellationToken)
	{
		var written = new List<string>();

		// run.json — serialized from the loaded record so the export is well-formed even if the
		// on-disk file is unreadable for some reason.
		var runJsonPath = Path.Combine(exportDir, "run.json");
		await WriteTextAsync(runJsonPath, JsonSerializer.Serialize(record, s_jsonOptions), cancellationToken);
		written.Add(runJsonPath);

		// orchestration.json — the definition as it was at execution time.
		if (index is not null)
		{
			var source = Path.Combine(index.FolderPath, "orchestration.json");
			if (File.Exists(source))
			{
				var dest = Path.Combine(exportDir, "orchestration.json");
				File.Copy(source, dest, overwrite: true);
				written.Add(dest);
			}
			else
			{
				warnings.Add("orchestration.json was not present in the run folder.");
			}
		}

		written.AddRange(await WriteStepPayloadsAsync(record, exportDir, warnings, cancellationToken));
		written.AddRange(await WriteArtifactsAsync(record, exportDir, warnings, cancellationToken));

		if (!string.IsNullOrWhiteSpace(record.FinalContent))
		{
			var resultPath = Path.Combine(exportDir, "result.md");
			await WriteTextAsync(resultPath, record.FinalContent, cancellationToken);
			written.Add(resultPath);
		}

		// README last so it can describe everything above.
		var readmePath = Path.Combine(exportDir, "README.md");
		await WriteTextAsync(readmePath, BuildReadme(record, index, written, warnings), cancellationToken);
		written.Add(readmePath);

		return written;
	}

	private async Task<IReadOnlyList<string>> WriteStepPayloadsAsync(
		OrchestrationRunRecord record,
		string exportDir,
		List<string> warnings,
		CancellationToken cancellationToken)
	{
		var stepsDir = Path.Combine(exportDir, "steps");
		Directory.CreateDirectory(stepsDir);

		var written = new List<string>();
		foreach (var (stepName, step) in AllSteps(record))
		{
			var payload = ResolveStepPayload(step, warnings);
			if (payload is null)
				continue;

			var (content, isJson) = payload.Value;
			var path = Path.Combine(stepsDir, $"{Sanitize(stepName)}.{(isJson ? "json" : "txt")}");
			await WriteTextAsync(path, content, cancellationToken);
			written.Add(path);
		}

		return written;
	}

	private async Task<IReadOnlyList<string>> WriteArtifactsAsync(
		OrchestrationRunRecord record,
		string exportDir,
		List<string> warnings,
		CancellationToken cancellationToken)
	{
		var artifacts = ResolveArtifacts(record, warnings);
		if (artifacts.Count == 0)
			return [];

		var filesDir = Path.Combine(exportDir, "files");
		Directory.CreateDirectory(filesDir);

		var written = new List<string>();
		var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var artifact in artifacts)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// The stored name is a GUID; rename to the producing step so the bundle is legible.
			var extension = Path.GetExtension(artifact.SourcePath);
			var baseName = Sanitize(artifact.StepName ?? "_unattributed");
			var name = baseName + extension;
			var counter = 2;
			while (!usedNames.Add(name))
				name = $"{baseName}-{counter++}{extension}";

			var dest = Path.Combine(filesDir, name);
			File.Copy(artifact.SourcePath, dest, overwrite: true);
			written.Add(dest);
		}

		await Task.CompletedTask;
		return written;
	}

	// ── Resolution helpers ──

	private sealed record ResolvedArtifact(string SourcePath, string? StepName, long Length);

	/// <summary>
	/// Locates every saved artifact for a run: the paths recorded on the run, plus anything else
	/// sitting in the run's temp directory.
	/// </summary>
	/// <remarks>
	/// Recorded paths are absolute and were valid on the machine that produced the run, so they
	/// are checked first and the temp directory is used as the fallback. Sweeping the directory
	/// also catches artifacts whose step never recorded them.
	/// </remarks>
	private List<ResolvedArtifact> ResolveArtifacts(OrchestrationRunRecord record, List<string> warnings)
	{
		var tempDir = TempDirectoryFor(record);
		var byPath = new Dictionary<string, ResolvedArtifact>(StringComparer.OrdinalIgnoreCase);

		void Add(string recordedPath, string? stepName)
		{
			var resolved = ResolveArtifactPath(recordedPath, tempDir);
			if (resolved is null)
			{
				warnings.Add($"Saved file '{Path.GetFileName(recordedPath)}'"
					+ (stepName is null ? "" : $" (step '{stepName}')")
					+ " was recorded but no longer exists on disk.");
				return;
			}

			// First writer wins, but a named step beats an unattributed sweep.
			if (byPath.TryGetValue(resolved, out var existing) && existing.StepName is not null)
				return;

			byPath[resolved] = new ResolvedArtifact(resolved, stepName, new FileInfo(resolved).Length);
		}

		foreach (var (stepName, step) in AllSteps(record))
		{
			foreach (var file in step.SavedFiles)
				Add(file, stepName);
		}

		foreach (var file in record.SavedFiles)
			Add(file, null);

		// Sweep the temp directory for anything not already accounted for.
		if (Directory.Exists(tempDir))
		{
			foreach (var file in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
			{
				if (!byPath.ContainsKey(file))
					byPath[file] = new ResolvedArtifact(file, null, new FileInfo(file).Length);
			}
		}

		return [.. byPath.Values.OrderBy(a => a.StepName ?? "\uffff", StringComparer.Ordinal)];
	}

	private static string? ResolveArtifactPath(string recordedPath, string tempDir)
	{
		if (File.Exists(recordedPath))
			return Path.GetFullPath(recordedPath);

		// The data directory may have moved since the run; try the same file name under the
		// current temp directory.
		var candidate = Path.Combine(tempDir, Path.GetFileName(recordedPath));
		return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
	}

	private string TempDirectoryFor(OrchestrationRunRecord record) =>
		Path.Combine(_dataPath, "temp", Sanitize(record.OrchestrationName), Sanitize(record.RunId));

	/// <summary>
	/// Chooses the payload to export for a step and reports whether it is JSON.
	/// </summary>
	/// <remarks>
	/// Steps commonly wrap JSON in a markdown code fence, or emit prose around it. The fence is
	/// stripped and the result validated so downstream tooling gets parseable files; anything
	/// that is not JSON is preserved verbatim as <c>.txt</c> rather than silently mangled.
	/// </remarks>
	private static (string Content, bool IsJson)? ResolveStepPayload(StepRunRecord step, List<string> warnings)
	{
		var raw = !string.IsNullOrWhiteSpace(step.RawContent) ? step.RawContent : step.Content;
		if (string.IsNullOrWhiteSpace(raw))
			return null;

		var cleaned = StripCodeFence(raw);
		if (cleaned is not null && IsValidJson(cleaned))
			return (cleaned, true);

		if (cleaned is not null)
			warnings.Add($"Step '{step.StepName}' looked like JSON but did not parse; exported as text.");

		return (raw, false);
	}

	/// <summary>Extracts a JSON document from a fenced block or surrounding prose.</summary>
	private static string? StripCodeFence(string raw)
	{
		var text = raw.Trim();

		if (text.StartsWith("```", StringComparison.Ordinal))
		{
			var firstNewline = text.IndexOf('\n');
			var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
			if (firstNewline > 0 && lastFence > firstNewline)
				text = text[(firstNewline + 1)..lastFence].Trim();
		}

		if (text.Length == 0)
			return null;

		if (text[0] is '{' or '[')
			return text;

		var start = text.IndexOfAny(['{', '[']);
		if (start < 0)
			return null;

		var end = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
		return end > start ? text[start..(end + 1)].Trim() : null;
	}

	private static bool IsValidJson(string candidate)
	{
		try
		{
			using var _ = JsonDocument.Parse(candidate);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	/// <summary>
	/// Every step in the run: the canonical records plus loop iterations that only appear in
	/// <see cref="OrchestrationRunRecord.AllStepRecords"/>.
	/// </summary>
	private static IEnumerable<(string Name, StepRunRecord Step)> AllSteps(OrchestrationRunRecord record)
	{
		foreach (var (name, step) in record.StepRecords)
			yield return (name, step);

		foreach (var (name, step) in record.AllStepRecords)
		{
			if (!record.StepRecords.ContainsKey(name))
				yield return (name, step);
		}
	}

	// ── README ──

	private string BuildReadme(
		OrchestrationRunRecord record,
		RunIndex? index,
		IReadOnlyList<string> written,
		IReadOnlyList<string> warnings)
	{
		var annotation = _annotations.Get(record.RunId);
		var sb = new StringBuilder();

		var heading = annotation?.Title is { Length: > 0 } title ? title : record.OrchestrationName;
		sb.AppendLine(CultureInfo.InvariantCulture, $"# {heading}");
		sb.AppendLine();

		if (annotation?.Title is { Length: > 0 })
			sb.AppendLine(CultureInfo.InvariantCulture, $"**Orchestration:** {record.OrchestrationName}  ");

		sb.AppendLine(CultureInfo.InvariantCulture, $"**Run ID:** `{record.RunId}`  ");
		sb.AppendLine(CultureInfo.InvariantCulture, $"**Status:** {record.Status}  ");
		sb.AppendLine(CultureInfo.InvariantCulture, $"**Started:** {record.StartedAt:u}  ");
		sb.AppendLine(CultureInfo.InvariantCulture, $"**Completed:** {record.CompletedAt:u}  ");
		sb.AppendLine(CultureInfo.InvariantCulture, $"**Duration:** {FormatDuration(record.Duration)}  ");
		sb.AppendLine(CultureInfo.InvariantCulture, $"**Triggered by:** {record.TriggeredBy}  ");
		sb.AppendLine(CultureInfo.InvariantCulture, $"**Exported:** {DateTimeOffset.UtcNow:u}");
		sb.AppendLine();

		// A run that did not succeed is the most important thing to say about the export, so it
		// goes above everything else.
		if (record.Status != ExecutionStatus.Succeeded || record.IsIncomplete)
		{
			sb.AppendLine("> [!WARNING]");
			sb.AppendLine(CultureInfo.InvariantCulture,
				$"> This run ended as **{record.Status}**{(record.IsIncomplete ? " and is marked incomplete" : "")}.");
			sb.AppendLine("> Its outputs may be partial. Check the step table before relying on anything here.");
			if (record.Cancellation?.CallerReason is { Length: > 0 } reason)
				sb.AppendLine(CultureInfo.InvariantCulture, $"> Cancellation reason: {reason}");
			sb.AppendLine();
		}

		if (annotation is not null)
		{
			sb.AppendLine("## Notes");
			sb.AppendLine();
			if (annotation.Favorite)
				sb.AppendLine("- Marked as a favorite");
			if (annotation.Tags.Length > 0)
				sb.AppendLine(CultureInfo.InvariantCulture, $"- Tags: {string.Join(", ", annotation.Tags)}");
			if (annotation.Note is { Length: > 0 } note)
			{
				sb.AppendLine();
				sb.AppendLine(note);
			}
			sb.AppendLine();
		}

		// Steps
		var steps = AllSteps(record).OrderBy(s => s.Step.StartedAt).ToList();
		if (steps.Count > 0)
		{
			sb.AppendLine("## Steps");
			sb.AppendLine();
			sb.AppendLine("| Step | Status | Duration | Error |");
			sb.AppendLine("|---|---|--:|---|");
			foreach (var (name, step) in steps)
			{
				var error = step.ErrorMessage is { Length: > 0 } e
					? e.Replace("|", "\\|").Replace("\n", " ").Trim()
					: "";
				if (error.Length > 120)
					error = error[..117] + "...";
				sb.AppendLine(CultureInfo.InvariantCulture,
					$"| {name} | {step.Status} | {FormatDuration(step.Duration)} | {error} |");
			}
			sb.AppendLine();

			var problems = steps.Where(s => s.Step.Status is ExecutionStatus.Failed or ExecutionStatus.Cancelled).ToList();
			if (problems.Count > 0)
			{
				sb.AppendLine(CultureInfo.InvariantCulture,
					$"**{problems.Count} step(s) did not complete:** {string.Join(", ", problems.Select(p => p.Name))}");
				sb.AppendLine();
			}
		}

		if (record.Parameters.Count > 0)
		{
			sb.AppendLine("## Parameters");
			sb.AppendLine();
			sb.AppendLine("| Name | Value |");
			sb.AppendLine("|---|---|");
			foreach (var (key, value) in record.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
			{
				var shown = value.Replace("|", "\\|").Replace("\n", " ");
				if (shown.Length > 200)
					shown = shown[..197] + "...";
				sb.AppendLine(CultureInfo.InvariantCulture, $"| {key} | {shown} |");
			}
			sb.AppendLine();
		}

		if (record.TotalUsage is { } usage)
		{
			sb.AppendLine("## Token usage");
			sb.AppendLine();
			sb.AppendLine(CultureInfo.InvariantCulture, $"- Input: {usage.InputTokens:N0}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"- Output: {usage.OutputTokens:N0}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"- Total: {usage.TotalTokens:N0}");
			sb.AppendLine();
		}

		sb.AppendLine("## Contents");
		sb.AppendLine();
		sb.AppendLine("| File | Purpose |");
		sb.AppendLine("|---|---|");
		sb.AppendLine("| `run.json` | Full run record |");
		sb.AppendLine("| `orchestration.json` | Orchestration definition as it was at execution time |");
		sb.AppendLine("| `steps/` | Per-step payloads; JSON where the step emitted JSON |");
		sb.AppendLine("| `files/` | Artifacts saved via `orchestra_save_file`, named by producing step |");
		sb.AppendLine("| `result.md` | Final content |");
		sb.AppendLine();
		sb.AppendLine(CultureInfo.InvariantCulture, $"{written.Count} file(s) exported.");
		sb.AppendLine();
		sb.AppendLine("Files under `files/` are pulled from the run's temp store, which is separate from");
		sb.AppendLine("the execution folder. They are often the run's real deliverable — a step that");
		sb.AppendLine("produces a large document usually saves it and returns only a summary inline.");
		sb.AppendLine();

		if (warnings.Count > 0)
		{
			sb.AppendLine("## Export warnings");
			sb.AppendLine();
			foreach (var warning in warnings)
				sb.AppendLine(CultureInfo.InvariantCulture, $"- {warning}");
			sb.AppendLine();
		}

		sb.AppendLine("## Provenance");
		sb.AppendLine();
		if (index is not null)
			sb.AppendLine(CultureInfo.InvariantCulture, $"- Source run folder: `{index.FolderPath}`");
		sb.AppendLine(CultureInfo.InvariantCulture, $"- Source temp folder: `{TempDirectoryFor(record)}`");
		if (record.ParentExecutionId is { Length: > 0 } parent)
			sb.AppendLine(CultureInfo.InvariantCulture, $"- Parent execution: `{parent}` (step `{record.ParentStepName}`)");
		if (record.RetriedFromRunId is { Length: > 0 } retried)
			sb.AppendLine(CultureInfo.InvariantCulture, $"- Retried from run: `{retried}` (mode `{record.RetryMode}`)");

		return sb.ToString();
	}

	// ── Utilities ──

	private static RunExportResult Result(
		OrchestrationRunRecord record,
		string path,
		IReadOnlyList<string> written,
		IReadOnlyList<string> warnings)
	{
		long total = 0;
		foreach (var file in written)
		{
			if (File.Exists(file))
				total += new FileInfo(file).Length;
		}

		return new RunExportResult(record.RunId, record.OrchestrationName, path, written.Count, total, warnings);
	}

	private static string ExportFolderName(OrchestrationRunRecord record) =>
		$"{Sanitize(record.OrchestrationName)}_{Sanitize(record.RunId)}_{record.StartedAt:yyyyMMdd-HHmmss}";

	private static string FormatDuration(TimeSpan duration) =>
		duration.TotalHours >= 1
			? $"{(int)duration.TotalHours}h {duration.Minutes}m"
			: duration.TotalMinutes >= 1
				? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
				: $"{duration.TotalSeconds:0.0}s";

	private static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
	{
		var normalized = content.Replace("\r\n", "\n");
		await File.WriteAllTextAsync(path, normalized, s_utf8NoBom, cancellationToken);
	}

	private static string Sanitize(string value)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var chars = value.ToCharArray();
		for (var i = 0; i < chars.Length; i++)
		{
			if (Array.IndexOf(invalid, chars[i]) >= 0)
				chars[i] = '_';
		}
		return new string(chars);
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch
		{
			// Best-effort cleanup of a staging directory; never fail an export over it.
		}
	}
}
