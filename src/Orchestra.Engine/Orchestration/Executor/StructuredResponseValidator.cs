using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Orchestra.Engine;

/// <summary>Validates the provider's final JSON instead of trusting its formatting claim.</summary>
internal static class StructuredResponseValidator
{
	public static JsonSchema ParseSchema(JsonElement schema)
	{
		if (schema.ValueKind != JsonValueKind.Object)
			throw new ArgumentException("responseSchema must be a JSON Schema object.");
		return JsonSchema.FromText(schema.GetRawText());
	}

	public static string? Validate(JsonSchema schema, string content)
	{
		try
		{
			var instance = JsonNode.Parse(content);
			var result = schema.Evaluate(instance, new EvaluationOptions
			{
				OutputFormat = OutputFormat.Hierarchical,
				RequireFormatValidation = false,
			});
			if (result.IsValid)
				return null;

			var errors = new List<string>();
			CollectErrors(result, errors);
			return "JSON does not match responseSchema: " + string.Join("; ", errors.Take(10));
		}
		catch (JsonException ex)
		{
			return "Response is not valid JSON: " + ex.Message;
		}
		catch (Exception ex)
		{
			return "Response schema validation failed: " + ex.Message;
		}
	}

	private static void CollectErrors(EvaluationResults result, List<string> errors)
	{
		if (result.IsValid || errors.Count >= 10)
			return;
		if (result.Errors is { } details)
			foreach (var error in details.Values)
				errors.Add($"{result.InstanceLocation}: {error}");
		foreach (var child in result.Details)
			CollectErrors(child, errors);
	}
}
