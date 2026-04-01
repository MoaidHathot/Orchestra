using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Built-in engine tool that allows the LLM to save string content to a temporary file.
/// The file is stored in the orchestration's temp directory and the full file path is
/// returned so it can be passed to other steps or used with the <c>orchestra_read_file</c> tool.
///
/// This is useful for passing large or complex text (e.g., multi-line JSON, code blocks)
/// between steps without embedding it directly in prompts or outputs.
/// </summary>
public sealed class SaveToFileTool : IEngineTool
{
	public string Name => "orchestra_save_file";

	public string Description =>
		"Save text content to a temporary file managed by the orchestration engine. " +
		"Returns the full file path that can be used to retrieve the content later via " +
		"orchestra_read_file, or passed to other steps. Use this when you need to " +
		"pass large or complex text (e.g., multi-line JSON, code, structured data) " +
		"to other steps. The file is stored in the orchestration's temp directory.";

	public string ParametersSchema => """
		{
			"type": "object",
			"properties": {
				"content": {
					"type": "string",
					"description": "The text content to save to the file."
				},
				"extension": {
					"type": "string",
					"description": "Optional file extension without the dot (e.g., 'json', 'xml', 'md'). Defaults to 'txt'."
				}
			},
			"required": ["content"]
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

			var content = root.TryGetProperty("content", out var contentProp)
				? contentProp.GetString()
				: null;

			if (content is null)
			{
				return "Error: 'content' parameter is required and must be a string.";
			}

			var extension = root.TryGetProperty("extension", out var extProp)
				? extProp.GetString()
				: null;

			var filePath = context.TempFileStore.SaveFile(content, extension);

			return $"File saved successfully. File path: {filePath}";
		}
		catch (JsonException)
		{
			return "Invalid arguments. Expected JSON with a 'content' property and optional 'extension' property.";
		}
	}
}
