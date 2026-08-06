namespace Orchestra.Host.Export;

/// <summary>Shape of an exported run.</summary>
public enum RunExportFormat
{
	/// <summary>A single markdown file: the run's final content, or the richer saved artifact.</summary>
	Report,

	/// <summary>Everything: README, run record, definition snapshot, step payloads and saved artifacts.</summary>
	Bundle,

	/// <summary>Step payloads only, as clean JSON.</summary>
	Data,
}

/// <summary>Outcome of exporting a single run.</summary>
/// <param name="RunId">The exported run.</param>
/// <param name="OrchestrationName">Owning orchestration.</param>
/// <param name="Path">Directory (bundle/data) or file (report) that was written.</param>
/// <param name="FileCount">Number of files written.</param>
/// <param name="TotalBytes">Total bytes written.</param>
/// <param name="Warnings">
/// Non-fatal problems — a saved artifact that no longer exists, a step payload that was not
/// valid JSON. Surfaced rather than swallowed so an incomplete export is never mistaken for a
/// complete one.
/// </param>
public sealed record RunExportResult(
	string RunId,
	string OrchestrationName,
	string Path,
	int FileCount,
	long TotalBytes,
	IReadOnlyList<string> Warnings);
