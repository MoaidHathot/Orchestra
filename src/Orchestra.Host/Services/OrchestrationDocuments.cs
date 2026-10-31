using System.Text.Json;
using Orchestra.Engine;

namespace Orchestra.Host.Services;

/// <summary>Shared document parsing for browser previews and workspace authoring.</summary>
public static class OrchestrationDocuments
{
	public static string Normalize(string content)
	{
		if (string.IsNullOrWhiteSpace(content))
			throw new ArgumentException("Orchestration content is required.");
		return content.TrimStart().StartsWith('{') ? content : OrchestrationParser.ConvertYamlToJson(content);
	}

	public static DocumentPreview Preview(string content, string? sourcePath = null)
	{
		var json = Normalize(content);
		var orchestration = sourcePath is null
			? OrchestrationParser.ParseOrchestrationMetadataOnly(json)
			: OrchestrationParser.ParseOrchestrationFileMetadataOnly(sourcePath);
		var errors = new List<string>();
		try { new OrchestrationScheduler().Schedule(orchestration); }
		catch (InvalidOperationException ex) { errors.Add(ex.Message); }
		errors.AddRange(TemplateExpressionValidator.ValidateOrchestration(orchestration).Errors.Select(e => e.Message));
		using var document = JsonDocument.Parse(json);
		return new(json, document.RootElement.Clone(), errors.Count == 0, errors);
	}
}

public sealed record DocumentPreview(string Json, JsonElement Definition, bool Valid, IReadOnlyList<string> Errors);
