using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Built-in engine tool that allows the LLM to read the content of a file
/// previously saved via the <c>orchestra_save_file</c> tool.
///
/// Files are read from the orchestration's temp directory using the file path
/// returned by <c>orchestra_save_file</c>.
/// </summary>
public sealed class ReadFromFileTool : IEngineTool
{
	public string Name => "orchestra_read_file";

	public string Description =>
		"Read the content of a file from the orchestration's temp directory. " +
		"Use the file path returned by orchestra_save_file to retrieve the content. " +
		"This is useful for reading large or complex text that was saved by a previous " +
		"step or earlier in the current step.";

	public string ParametersSchema => """
		{
			"type": "object",
			"properties": {
				"filePath": {
					"type": "string",
					"description": "The file path to read (as returned by orchestra_save_file)."
				}
			},
			"required": ["filePath"]
		}
		""";

	public string Execute(string arguments, EngineToolContext context)
	{
		try
		{
			if (context.TempFileStore is null)
			{
				return "Error: Temp file storage is not available. The orchestration host may not have a data path configured.";
			}

			using var doc = JsonDocument.Parse(arguments);
			var root = doc.RootElement;

			var filePath = root.TryGetProperty("filePath", out var filePathProp)
				? filePathProp.GetString()
				: null;

			if (string.IsNullOrWhiteSpace(filePath))
			{
				return "Error: 'filePath' parameter is required and must be a non-empty string.";
			}

			var content = context.TempFileStore.ReadFile(filePath);

			return content;
		}
		catch (FileNotFoundException ex)
		{
			return $"Error: {ex.Message}";
		}
		catch (InvalidOperationException ex)
		{
			return $"Error: {ex.Message}";
		}
		catch (JsonException)
		{
			return "Invalid arguments. Expected JSON with a 'filePath' property.";
		}
	}
}
