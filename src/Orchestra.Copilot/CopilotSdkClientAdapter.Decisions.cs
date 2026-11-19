using System.Text.Json;
using GitHub.Copilot;
using Orchestra.Engine;
using Rpc = GitHub.Copilot.Rpc;

namespace Orchestra.Copilot;

// decisions.ask and its wire types are experimental in SDK 1.0.34. Keep them inside the adapter.
#pragma warning disable GHCP001
internal sealed partial class CopilotSdkClientAdapter
{
	public async Task<DecisionResult> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
	{
		Rpc.DecisionContext context = request.Context.ValueKind == JsonValueKind.String
			? new Rpc.DecisionContextText { Text = request.Context.GetString()! }
			: new Rpc.DecisionContextJson { Value = request.Context };
		try
		{
			// Also bound the remote operation if the caller stops awaiting the RPC. The SDK permits 250–30000 ms.
			var timeoutMs = request.TimeoutSeconds is > 0 and < 30 ? request.TimeoutSeconds.Value * 1000 : 30_000;
			var result = await _client.Rpc.Decisions.AskAsync(context, request.Questions.Select(ToSdkQuestion).ToArray(),
				model: request.Model, timeoutMs: timeoutMs, cancellationToken: cancellationToken).ConfigureAwait(false);
			return ReadDecisionResult(request, result);
		}
		catch (IOException ex) when (ex.InnerException is RemoteRpcException { ErrorData: { ValueKind: JsonValueKind.Object } data } &&
			data.TryGetProperty("kind", out _))
		{
			// SDK wraps remote JSON-RPC failures in IOException (including non-transport errors).
			var detail = data.Deserialize<Rpc.DecisionErrorData>()!;
			var violations = detail.Violations is { Count: > 0 } list ? ": " + string.Join(", ", list.Select(v => $"{v.Path} ({v.Rule})")) : "";
			var message = $"{detail.Kind}: {ex.InnerException.Message}{violations}";
			throw detail.Kind.Value switch
			{
				"timeout" => new TimeoutException(message, ex),
				"invalidRequest" or "tooLarge" => new ArgumentException(message, ex),
				_ => new InvalidOperationException(message, ex),
			};
		}
	}

	private static Rpc.DecisionQuestion ToSdkQuestion(DecisionQuestion q) => q switch
	{
		BooleanQuestion b => new Rpc.DecisionQuestionBoolean { Name = b.Name, Instructions = b.Instructions, WhenTrue = b.WhenTrue, WhenFalse = b.WhenFalse },
		ChoiceQuestion c => new Rpc.DecisionQuestionChoice { Name = c.Name, Instructions = c.Instructions,
			Options = c.Options.Select(o => new Rpc.DecisionOption { Value = o.Value, Description = o.Description }).ToArray() },
		ScoreQuestion s => new Rpc.DecisionQuestionScore { Name = s.Name, Instructions = s.Instructions,
			Levels = s.Levels.Select(l => new Rpc.DecisionLevel { Label = l.Label, Description = l.Description }).ToArray() },
		_ => throw new ArgumentException($"Unsupported decision question: {q.GetType().Name}."),
	};

	internal static DecisionResult ReadDecisionResult(DecisionRequest request, Rpc.DecisionsAskResult result)
	{
		if (result.Answers.Count != request.Questions.Count)
			throw new InvalidDataException("The decision model did not return one answer per question.");
		var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
		for (var i = 0; i < result.Answers.Count; i++)
		{
			// The SDK guarantees request order. Check identity and kind before applying a question's threshold/range.
			var question = request.Questions[i];
			answers.Add(question.Name, (result.Answers[i], question) switch
			{
				(Rpc.DecisionAnswerRefused r, _) when r.Name == question.Name => new RefusedAnswer(),
				(Rpc.DecisionAnswerBoolean b, BooleanQuestion q) when b.Name == q.Name && b.Probability is >= 0 and <= 1
					=> new BooleanAnswer(b.Probability, q.Threshold),
				(Rpc.DecisionAnswerChoice c, ChoiceQuestion q) when c.Name == q.Name && q.Options.Any(o => o.Value == c.Value)
					=> new ChoiceAnswer(c.Value, c.Tied, c.Confidence, c.VendorConfidence, c.Probabilities.ToDictionary(o => o.Value, o => o.Probability)),
				(Rpc.DecisionAnswerScore s, ScoreQuestion q) when s.Name == q.Name && s.Score >= 0 && s.Score <= q.Levels.Length - 1 && q.Levels.Any(l => l.Label == s.Level)
					=> new ScoreAnswer(s.Level, s.Score, Scale(s.Score, q), s.Confidence, s.VendorConfidence, s.Probabilities.ToDictionary(l => l.Label, l => l.Probability)),
				_ => throw new InvalidDataException($"Unexpected decision answer for question '{question.Name}'."),
			});
		}
		return new DecisionResult(result.Model, result.UpstreamModel, result.LatencyMs, answers, result.Usage is not { } u ? null : new TokenUsage
		{
			InputTokens = checked((int)u.InputTokens), OutputTokens = checked((int)u.OutputTokens),
			CacheReadTokens = checked((int)(u.CachedInputTokens ?? 0)), CacheWriteTokens = checked((int)(u.CacheWriteTokens ?? 0)),
			Duration = result.LatencyMs / 1000d,
		});
	}

	private static double Scale(double score, ScoreQuestion question)
	{
		if (question.Range is not { } range) return score;
		var fraction = score / (question.Levels.Length - 1);
		return double.Lerp(range.Min, range.Max, fraction);
	}
}
#pragma warning restore GHCP001
