using System.Collections.Concurrent;

namespace Orchestra.Engine;

/// <summary>
/// Manages temporary files for an orchestration run.
/// Files are stored under <c>{basePath}/temp/{orchestrationName}/{runId}/</c>.
/// Orchestra controls the lifecycle of these files; currently they are never deleted
/// automatically, but future policies may be added.
/// </summary>
public sealed class OrchestrationTempFileStore
{
	private readonly string _directory;
	private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _stepFiles = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Creates a temp file store for a specific orchestration run.
	/// The directory is created immediately on construction.
	/// </summary>
	/// <param name="basePath">The root data directory (e.g., <c>%LOCALAPPDATA%/OrchestraHost</c>).</param>
	/// <param name="orchestrationName">The name of the orchestration.</param>
	/// <param name="runId">The unique run identifier.</param>
	public OrchestrationTempFileStore(string basePath, string orchestrationName, string runId)
	{
		_directory = Path.Combine(basePath, "temp", SanitizePath(orchestrationName), SanitizePath(runId));
		System.IO.Directory.CreateDirectory(_directory);
	}

	/// <summary>
	/// Gets the full path to the temp directory for this orchestration run.
	/// </summary>
	public string TempDirectory => _directory;

	/// <summary>
	/// Saves string content to a file in the temp directory.
	/// Returns the full path to the saved file.
	/// </summary>
	/// <param name="content">The string content to save.</param>
	/// <param name="extension">Optional file extension (without dot). Defaults to "txt".</param>
	/// <returns>The full path to the saved file.</returns>
	public string SaveFile(string content, string? extension = null)
	{
		var ext = string.IsNullOrWhiteSpace(extension) ? "txt" : extension.TrimStart('.');
		var fileName = $"{Guid.NewGuid():N}.{ext}";
		var filePath = Path.Combine(_directory, fileName);

		File.WriteAllText(filePath, content);

		return filePath;
	}

	/// <summary>
	/// Saves string content to a file and registers it as belonging to the specified step.
	/// Returns the full path to the saved file.
	/// </summary>
	/// <param name="content">The string content to save.</param>
	/// <param name="stepName">The name of the step saving the file.</param>
	/// <param name="extension">Optional file extension (without dot). Defaults to "txt".</param>
	/// <returns>The full path to the saved file.</returns>
	public string SaveFile(string content, string stepName, string? extension = null)
	{
		var filePath = SaveFile(content, extension);
		RegisterFileForStep(stepName, filePath);
		return filePath;
	}

	/// <summary>
	/// Registers a file path as belonging to the specified step.
	/// Thread-safe for concurrent step execution.
	/// </summary>
	public void RegisterFileForStep(string stepName, string filePath)
	{
		var bag = _stepFiles.GetOrAdd(stepName, _ => []);
		bag.Add(filePath);
	}

	/// <summary>
	/// Gets all file paths saved by the specified step.
	/// Returns an empty array if no files were saved by the step.
	/// </summary>
	public string[] GetFilesForStep(string stepName)
	{
		return _stepFiles.TryGetValue(stepName, out var bag) ? [.. bag] : [];
	}

	/// <summary>
	/// Reads the content of a file from the temp directory.
	/// Accepts either a bare file name or a full path (which must be within the temp directory).
	/// </summary>
	/// <param name="fileNameOrPath">The file name or full path to read.</param>
	/// <returns>The file content as a string.</returns>
	/// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
	/// <exception cref="InvalidOperationException">Thrown when the path attempts traversal outside the temp directory.</exception>
	public string ReadFile(string fileNameOrPath)
	{
		string filePath;

		if (Path.IsPathRooted(fileNameOrPath))
		{
			// Full path provided — verify it's within the temp directory
			var fullPath = Path.GetFullPath(fileNameOrPath);
			var fullDir = Path.GetFullPath(_directory + Path.DirectorySeparatorChar);

			if (!fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					$"Invalid file path. The path must be within the orchestration temp directory.");
			}

			filePath = fullPath;
		}
		else
		{
			// Bare file name — prevent path traversal
			if (fileNameOrPath.Contains(".."))
			{
				throw new InvalidOperationException(
					$"Invalid file name '{fileNameOrPath}'. File names must not contain path traversal sequences.");
			}

			filePath = Path.Combine(_directory, fileNameOrPath);
		}

		if (!File.Exists(filePath))
		{
			throw new FileNotFoundException(
				$"File '{fileNameOrPath}' not found in the orchestration temp directory.", fileNameOrPath);
		}

		return File.ReadAllText(filePath);
	}

	/// <summary>
	/// Replaces invalid file system characters with underscores.
	/// </summary>
	private static string SanitizePath(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var sanitized = name.ToCharArray();

		for (var i = 0; i < sanitized.Length; i++)
		{
			if (Array.IndexOf(invalid, sanitized[i]) >= 0)
			{
				sanitized[i] = '_';
			}
		}

		return new string(sanitized);
	}
}
