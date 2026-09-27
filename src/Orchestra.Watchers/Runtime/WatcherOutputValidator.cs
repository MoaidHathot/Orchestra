using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Orchestra.Watchers.Runtime;

/// <summary>One schema violation in a fetcher's output.</summary>
/// <param name="JsonPointer">Location in the output document (RFC 6901), <c>/</c> for the root.</param>
/// <param name="Keyword">The schema keyword that failed (<c>type</c>, <c>additionalProperties</c>, ...), or <c>protocolVersion</c> for a version-negotiation error.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record WatcherValidationError(string JsonPointer, string Keyword, string Message)
{
	public override string ToString() => $"{JsonPointer}: {Message}";
}

/// <summary>
/// Validates fetcher stdout against the embedded <c>fetcher-output</c> schema for the protocol
/// version the document declares. Version negotiation: a missing <c>protocolVersion</c> means the
/// version Orchestra sent (v1); <c>"1"</c> validates against v1; anything else is reported as
/// <c>unsupported protocolVersion</c> without attempting schema validation.
/// </summary>
public sealed class WatcherOutputValidator
{
	/// <summary>The only protocol version this build speaks.</summary>
	public const string SupportedProtocolVersion = "1";

	private const string OutputSchemaResource = "Orchestra.Watchers.Schemas.v1.fetcher-output.schema.json";
	private const string InputSchemaResource = "Orchestra.Watchers.Schemas.v1.fetcher-input.schema.json";

	private static readonly Lazy<JsonSchema> s_outputSchema = new(() => JsonSchema.FromText(ReadEmbeddedSchema(OutputSchemaResource)));

	/// <summary>Raw text of the embedded v1 output schema (for drift tests and the CLI).</summary>
	public static string OutputSchemaText => ReadEmbeddedSchema(OutputSchemaResource);

	/// <summary>Raw text of the embedded v1 input schema (for drift tests and the CLI).</summary>
	public static string InputSchemaText => ReadEmbeddedSchema(InputSchemaResource);

	/// <summary>
	/// Validates <paramref name="output"/>. Returns <see langword="true"/> when the document is a
	/// well-formed v1 fetcher response; otherwise <paramref name="errors"/> lists every violation.
	/// </summary>
	public bool TryValidate(JsonNode? output, out IReadOnlyList<WatcherValidationError> errors)
	{
		if (output is not JsonObject obj)
		{
			errors = [new WatcherValidationError("/", "type", "fetcher output must be a JSON object.")];
			return false;
		}

		var versionNode = obj["protocolVersion"];
		if (versionNode is not null)
		{
			var version = versionNode is JsonValue v && v.TryGetValue<string>(out var text) ? text : versionNode.ToJsonString();
			if (version != SupportedProtocolVersion)
			{
				errors = [new WatcherValidationError("/protocolVersion", "protocolVersion", $"unsupported protocolVersion: {version} (this build supports \"{SupportedProtocolVersion}\").")];
				return false;
			}
		}

		var results = s_outputSchema.Value.Evaluate(obj, new EvaluationOptions
		{
			OutputFormat = OutputFormat.Hierarchical,
			RequireFormatValidation = false,
		});

		if (results.IsValid)
		{
			errors = [];
			return true;
		}

		var collected = new List<WatcherValidationError>();
		Collect(results, collected);
		if (collected.Count == 0)
			collected.Add(new WatcherValidationError("/", "schema", "fetcher output does not match the v1 schema."));

		errors = collected;
		return false;
	}

	private static void Collect(EvaluationResults node, List<WatcherValidationError> into)
	{
		if (node.IsValid)
			return;

		if (node.HasErrors && node.Errors is { Count: > 0 })
		{
			var location = node.InstanceLocation.ToString();
			if (location.Length == 0)
				location = "/";

			foreach (var (keyword, message) in node.Errors)
				into.Add(new WatcherValidationError(location, keyword, Describe(keyword, message)));
		}

		foreach (var child in node.Details)
			Collect(child, into);
	}

	private static string Describe(string keyword, string message)
	{
		// additionalProperties: false surfaces as the "false schema" failure on the offending property.
		if (keyword is "additionalProperties" or "false" || message.Contains("false schema", StringComparison.OrdinalIgnoreCase))
			return "property is not allowed here (v1 output accepts protocolVersion, items, cursor and diagnostics).";

		return message;
	}

	private static string ReadEmbeddedSchema(string resourceName)
	{
		using var stream = typeof(WatcherOutputValidator).GetTypeInfo().Assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Embedded schema '{resourceName}' is missing from the Orchestra.Watchers assembly.");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}
}
