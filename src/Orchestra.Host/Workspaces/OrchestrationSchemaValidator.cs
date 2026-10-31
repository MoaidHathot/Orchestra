using System.Text.Json.Nodes;
using Json.Schema;
using Orchestra.Engine;

namespace Orchestra.Host.Workspaces;

/// <summary>
/// Validates an orchestration file against the bundled <c>orchestration.schema.json</c>
/// (JSON Schema 2020-12). Complements the parser: the parser ignores unknown keys, so a typo
/// such as <c>maxConcurency</c> or <c>onStepFailure: halt</c> only surfaces here.
/// </summary>
public static class OrchestrationSchemaValidator
{
	/// <summary>File name of the orchestration schema inside the bundled <c>schemas</c> directory.</summary>
	public const string SchemaFileName = "orchestration.schema.json";

	/// <summary>Most schema errors reported for one file; the rest are summarised.</summary>
	public const int MaxReportedErrors = 25;

	/// <summary>Appended once whenever a property was rejected as unknown.</summary>
	public const string UnknownPropertyHint = "hint: check the spelling and that the property belongs to this step type. A valid property is also reported as unknown when another property of the same object failed validation - fix those first.";

	/// <summary>The bundled schema next to the CLI binaries, or null when the install lacks it.</summary>
	public static string? LocateBundledSchema()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "schemas", SchemaFileName);
		return File.Exists(path) ? path : null;
	}

	/// <summary>
	/// Validates the orchestration at <paramref name="orchestrationPath"/> (JSON or YAML) against
	/// the schema at <paramref name="schemaPath"/>. Returns one line per violation, formatted as
	/// <c>{json-pointer}: {message}</c>, ordered by location; empty when the document conforms.
	/// </summary>
	public static IReadOnlyList<string> ValidateFile(string orchestrationPath, string schemaPath)
	{
		var raw = File.ReadAllText(orchestrationPath);
		var json = OrchestrationParser.IsYamlFile(orchestrationPath) ? OrchestrationParser.ConvertYamlToJson(raw) : raw;
		return ValidateJson(json, schemaPath);
	}

	/// <summary>Validates a JSON document (already converted from YAML if needed).</summary>
	public static IReadOnlyList<string> ValidateJson(string json, string schemaPath)
	{
		var schema = JsonSchema.FromFile(schemaPath);
		JsonNode? instance;
		try
		{
			instance = JsonNode.Parse(json);
		}
		catch (System.Text.Json.JsonException ex)
		{
			return [$"/: not valid JSON - {ex.Message}"];
		}

		var results = schema.Evaluate(instance, new EvaluationOptions
		{
			OutputFormat = OutputFormat.Hierarchical,
			RequireFormatValidation = false,
		});

		if (results.IsValid)
			return [];

		return Summarise(results);
	}

	/// <summary>
	/// Flattens the evaluator's tree into user-facing lines. Only invalid subtrees are walked (a
	/// valid <c>oneOf</c> branch's siblings are irrelevant), <c>if</c> nodes are skipped (a failing
	/// <c>if</c> selects the <c>else</c> branch; it is not an error in the document), duplicates
	/// are merged, and deeper locations come first because they are the specific cause while the
	/// object-level "unexpected property" is the consequence.
	/// </summary>
	public static IReadOnlyList<string> Summarise(EvaluationResults results)
	{
		var lines = new List<(string Location, string Message)>();
		Collect(results, lines);

		var distinct = lines
			.Distinct()
			.OrderByDescending(l => l.Location.Count(c => c == '/'))
			.ThenBy(l => l.Location, StringComparer.Ordinal)
			.Select(l => $"{(l.Location.Length == 0 ? "/" : l.Location)}: {l.Message}")
			.ToList();

		if (distinct.Count > MaxReportedErrors)
		{
			var extra = distinct.Count - MaxReportedErrors;
			distinct = distinct.Take(MaxReportedErrors).ToList();
			distinct.Add($"... and {extra} more schema error{(extra == 1 ? "" : "s")}");
		}

		if (distinct.Any(l => l.Contains("unknown or misplaced property", StringComparison.Ordinal)))
			distinct.Add(UnknownPropertyHint);

		return distinct;
	}

	private static void Collect(EvaluationResults node, List<(string Location, string Message)> into)
	{
		if (node.IsValid)
			return;

		var path = node.EvaluationPath.ToString();
		if (path.EndsWith("/if", StringComparison.Ordinal))
			return;

		if (node.HasErrors && node.Errors is { Count: > 0 })
		{
			var location = node.InstanceLocation.ToString();
			foreach (var (keyword, message) in node.Errors)
				into.Add((location, Describe(keyword, message, location)));
		}

		foreach (var child in node.Details)
			Collect(child, into);
	}

	/// <summary>
	/// Turns the evaluator's generic wording into something an author can act on. The
	/// <c>false</c>-schema message is what <c>additionalProperties: false</c> and
	/// <c>unevaluatedProperties: false</c> both produce, i.e. "this key is not allowed here".
	/// </summary>
	private static string Describe(string keyword, string message, string location)
	{
		if (keyword is "additionalProperties" or "unevaluatedProperties" || message.Contains("false schema", StringComparison.OrdinalIgnoreCase))
		{
			var property = location.LastIndexOf('/') is var slash and >= 0 ? location[(slash + 1)..] : location;
			return $"unknown or misplaced property '{property}'";
		}

		return message;
	}
}
