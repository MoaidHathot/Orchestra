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
public record StepParseContext(string? BaseDirectory);
