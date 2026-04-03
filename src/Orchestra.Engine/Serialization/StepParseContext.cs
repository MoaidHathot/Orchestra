namespace Orchestra.Engine;

/// <summary>
/// Context passed to step type parsers during deserialization.
/// Provides information needed for resolving file-based references (e.g., prompt files).
/// </summary>
/// <param name="BaseDirectory">
/// The base directory for resolving relative file paths in step definitions.
/// When parsing from a file, this is the directory containing the orchestration JSON.
/// When parsing from a raw string, this defaults to the current working directory.
/// Null when no directory context is available.
/// </param>
/// <param name="MetadataOnly">
/// When true, file-based references (e.g., systemPromptFile) are not read from disk.
/// Instead, a placeholder value is used. This allows metadata-only parsing to succeed
/// even when prompt files are unreachable (e.g., paths contain template expressions).
/// </param>
/// <param name="Variables">
/// Orchestration-level variables extracted from the JSON before step deserialization.
/// Used to resolve <c>{{vars.*}}</c> expressions in file paths (e.g., systemPromptFile)
/// at parse time, since template resolution normally only happens at execution time.
/// </param>
public record StepParseContext(
	string? BaseDirectory,
	bool MetadataOnly = false,
	IReadOnlyDictionary<string, string>? Variables = null);
