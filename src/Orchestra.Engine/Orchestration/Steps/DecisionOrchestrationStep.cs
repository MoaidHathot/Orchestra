using System.Text.Json.Serialization;

namespace Orchestra.Engine;

/// <summary>Classify or score shared evidence. Chat provider and model defaults do not apply.</summary>
public sealed class DecisionOrchestrationStep : OrchestrationStep
{
	public const string ProviderName = "copilot";
	public const string DefaultModel = "microsoft-decision-1";

	public string Model { get; init; } = DefaultModel;
	public required string Context { get; init; }
	public DecisionContextFormat ContextFormat { get; init; }
	public required DecisionQuestion[] Questions { get; init; }
}

public enum DecisionContextFormat { Text, Json }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BooleanQuestion), "boolean")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
public abstract record DecisionQuestion(string Name, string Instructions);

public sealed record BooleanQuestion(string Name, string Instructions, string? WhenTrue = null,
	string? WhenFalse = null, double Threshold = 0.5) : DecisionQuestion(Name, Instructions);

public sealed record ChoiceQuestion(string Name, string Instructions, DecisionOption[] Options) : DecisionQuestion(Name, Instructions);
public sealed record DecisionOption(string Value, string Description);

public sealed record ScoreQuestion(string Name, string Instructions, DecisionLevel[] Levels,
	DecisionRange? Range = null) : DecisionQuestion(Name, Instructions);
public sealed record DecisionLevel(string Label, string Description);
public sealed record DecisionRange(double Min, double Max);
