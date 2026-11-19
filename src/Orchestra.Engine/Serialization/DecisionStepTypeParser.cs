using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Orchestra.Engine;

public sealed partial class DecisionStepTypeParser : IStepTypeParser
{
	private static readonly JsonSerializerOptions s_questionOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		AllowOutOfOrderMetadataProperties = true,
		RespectRequiredConstructorParameters = true,
		RespectNullableAnnotations = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};

	public string TypeName => "Decision";

	public OrchestrationStep Parse(JsonElement root, StepParseContext context)
	{
		var fields = StepBaseFields.Parse(root);
		JsonException Error(string message) => new($"Decision step '{fields.Name}': {message}");
		string ReadText(string key, string? fallback = null)
		{
			if (!root.TryGetProperty(key, out var property))
				return fallback ?? throw Error($"'{key}' is required.");
			if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
				throw Error($"'{key}' must be a non-empty string.");
			return property.GetString()!;
		}

		if (ReadText("provider", DecisionOrchestrationStep.ProviderName) != DecisionOrchestrationStep.ProviderName)
			throw Error("only the 'copilot' provider supports Decision steps.");
		var format = ReadText("contextFormat", "text") switch
		{
			"text" => DecisionContextFormat.Text,
			"json" => DecisionContextFormat.Json,
			_ => throw Error("contextFormat must be 'text' or 'json'."),
		};
		if (!root.TryGetProperty("questions", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32)
			throw Error("questions must contain 1–32 named questions.");
		DecisionQuestion[] questions;
		try
		{
			questions = array.Deserialize<DecisionQuestion[]>(s_questionOptions)!;
		}
		catch (Exception ex) when (ex is JsonException or NotSupportedException)
		{
			throw new JsonException($"Decision step '{fields.Name}': invalid questions: {ex.Message}", ex);
		}
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Template output lookup is case-insensitive.
		foreach (var q in questions)
		{
			if (q is null || !QuestionName().IsMatch(q.Name) || !names.Add(q.Name))
				throw Error("question names must be unique (ignoring case) and use only letters, digits, '_' or '-'.");
			if (string.IsNullOrWhiteSpace(q.Instructions))
				throw Error($"'{q.Name}' needs non-empty instructions.");
			switch (q)
			{
				case BooleanQuestion b when !double.IsFinite(b.Threshold) || b.Threshold is < 0 or > 1:
					throw Error($"'{q.Name}' threshold must be between 0 and 1.");
				case ChoiceQuestion c when c.Options.Length is < 2 or > 10 ||
					c.Options.Any(o => o is null || !OptionId().IsMatch(o.Value) || string.IsNullOrWhiteSpace(o.Description)) ||
					c.Options.DistinctBy(o => o.Value).Count() != c.Options.Length:
					throw Error($"'{q.Name}' needs 2–10 uniquely identified options with descriptions (values: 1–64 ASCII letters, digits or ._:/@-).");
				case ScoreQuestion s when s.Levels.Length is < 2 or > 10 ||
					s.Levels.Any(l => l is null || string.IsNullOrWhiteSpace(l.Label) || string.IsNullOrWhiteSpace(l.Description)) ||
					s.Levels.DistinctBy(l => l.Label).Count() != s.Levels.Length:
					throw Error($"'{q.Name}' needs 2–10 uniquely labeled levels with descriptions, lowest first.");
				case ScoreQuestion { Range: { } r } when !double.IsFinite(r.Min) || !double.IsFinite(r.Max) || r.Min >= r.Max:
					throw Error($"'{q.Name}' range must have finite min < max.");
			}
		}

		return new DecisionOrchestrationStep
		{
			Type = OrchestrationStepType.Decision,
			Name = fields.Name,
			DependsOn = fields.DependsOn,
			Enabled = fields.Enabled,
			When = fields.When,
			TimeoutSeconds = fields.TimeoutSeconds,
			Retry = fields.Retry,
			Parameters = StepBaseFields.ParseParameterNames(root),
			Model = ReadText("model", DecisionOrchestrationStep.DefaultModel),
			Context = ReadText("context"),
			ContextFormat = format,
			Questions = questions,
		};
	}

	[GeneratedRegex(@"\A[A-Za-z0-9_-]+\z")]
	private static partial Regex QuestionName();
	[GeneratedRegex(@"\A[A-Za-z0-9._:/@-]{1,64}\z")]
	private static partial Regex OptionId();
}
