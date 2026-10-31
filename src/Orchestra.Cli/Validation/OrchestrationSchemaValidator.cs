using Shared = Orchestra.Host.Workspaces.OrchestrationSchemaValidator;

namespace Orchestra.Cli.Validation;

public static class OrchestrationSchemaValidator
{
	public const string SchemaFileName = Shared.SchemaFileName;
	public const int MaxReportedErrors = Shared.MaxReportedErrors;
	public const string UnknownPropertyHint = Shared.UnknownPropertyHint;
	public static string? LocateBundledSchema() => Shared.LocateBundledSchema();
	public static IReadOnlyList<string> ValidateFile(string orchestrationPath, string schemaPath) => Shared.ValidateFile(orchestrationPath, schemaPath);
	public static IReadOnlyList<string> ValidateJson(string json, string schemaPath) => Shared.ValidateJson(json, schemaPath);
	internal static IReadOnlyList<string> Summarise(Json.Schema.EvaluationResults results) => Shared.Summarise(results);
}
