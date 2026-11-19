using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestra.Engine;

/// <summary>A run-scoped provider of sessionless, batched decisions.</summary>
public interface IDecisionProvider
{
	Task<DecisionResult> AskAsync(DecisionRequest request, CancellationToken cancellationToken);
}

/// <param name="Context">A string, an object or an array of objects.</param>
/// <param name="Model">The requested decision catalog ID.</param>
/// <param name="Questions">Named questions, in request order.</param>
/// <param name="TimeoutSeconds">Effective step deadline; providers can impose a shorter service limit.</param>
public sealed record DecisionRequest(string Model, JsonElement Context, IReadOnlyList<DecisionQuestion> Questions, int? TimeoutSeconds = null);

public sealed record DecisionResult(string Model, string? UpstreamModel, long LatencyMs,
	IReadOnlyDictionary<string, DecisionAnswer> Answers, TokenUsage? Usage);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BooleanAnswer), "boolean")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
[JsonDerivedType(typeof(RefusedAnswer), "refused")]
public abstract record DecisionAnswer;

public sealed record BooleanAnswer(double Probability, double Threshold) : DecisionAnswer
{
	public bool Value => Probability >= Threshold;
}

public sealed record ChoiceAnswer(string Value, bool Tied, double Confidence, double? VendorConfidence,
	IReadOnlyDictionary<string, double> Probabilities) : DecisionAnswer;

public sealed record ScoreAnswer(string Level, double Score, double Value, double Confidence, double? VendorConfidence,
	IReadOnlyDictionary<string, double> Probabilities) : DecisionAnswer;

public sealed record RefusedAnswer : DecisionAnswer;
