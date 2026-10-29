namespace Orchestra.Engine;

/// <summary>
/// Bounded continuation for providers without a native autopilot loop. A completion marker is
/// accepted only from the root session's final response, never from tool or child-session text.
/// </summary>
public sealed class AgentContinuation
{
	public const string CompletionMarker = "<orchestra:task-complete>";
	private readonly AgentExecutionPolicy? _policy;
	private readonly Json.Schema.JsonSchema? _responseContract;
	private int _continues;
	public AgentContinuation(AgentExecutionPolicy? policy, System.Text.Json.JsonElement? responseSchema = null)
	{
		policy?.Validate();
		_policy = policy;
		if (responseSchema is { } schema)
			_responseContract = StructuredResponseValidator.ParseSchema(schema);
	}
	public bool Enabled => _policy?.Mode == AgentExecutionMode.Autopilot;
	public int ContinuesUsed => _continues;
	public string? LastContinuationReason { get; private set; }
	public string Instructions => Enabled
		? $"Complete the current task and verify the result. When all requested work is complete, append {CompletionMarker} on its own line after your final response. Do not append it when blocked or when work remains. This marker is an internal completion signal and is removed before the response reaches the workflow."
		: string.Empty;
	public const string ContinuePrompt = "Continue the original task from the existing session context. Finish any remaining work and verify the outcome. If the task is complete, return the final response with the required completion marker.";
	public string NextPrompt { get; private set; } = ContinuePrompt;

	public bool TryComplete(string response, out string content)
	{
		content = response;
		if (!Enabled)
			return true;
		var trimmed = response.TrimEnd();
		var completed = trimmed == CompletionMarker || trimmed.EndsWith("\n" + CompletionMarker, StringComparison.Ordinal);
		if (completed)
		{
			content = trimmed[..^CompletionMarker.Length].TrimEnd();
			var validation = _responseContract is null ? null : StructuredResponseValidator.Validate(_responseContract, content);
			if (validation is null)
				return true;
			LastContinuationReason = validation;
			NextPrompt = "The work is complete, but the final response violated responseSchema. " + validation +
				"\nCorrect only the response format using the results already obtained; do not repeat file changes or other completed work. Return JSON matching the original schema, then append " + CompletionMarker + " on its own line.";
		}
		else
		{
			LastContinuationReason = "The root response did not signal task completion.";
			NextPrompt = ContinuePrompt;
		}
		if (_continues >= _policy!.MaxContinues)
			throw new InvalidOperationException($"Autopilot exhausted maxContinues ({_policy.MaxContinues}). {LastContinuationReason}");
		_continues++;
		return false;
	}
}
