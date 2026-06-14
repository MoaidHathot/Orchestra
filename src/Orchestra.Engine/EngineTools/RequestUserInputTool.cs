using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Engine tool that lets the LLM pause inside a Prompt step to ask the human a question.
/// The tool blocks the agent's tool-call until the user responds via the host's HumanInput
/// API; the response is returned as the tool result so the agent can naturally continue
/// the conversation with the answer in hand.
/// <para>
/// Unlike <see cref="SetStatusTool"/> and <see cref="CompleteTool"/> the wait does NOT
/// tear down the agent session — the prompt step stays in <see cref="ExecutionStatus.Running"/>
/// while the tool is awaiting. This preserves the agent's conversation state so the
/// answer can drive subsequent reasoning. Host crashes during the wait surface as
/// <see cref="CancellationCauseKind.HostShutdownDuringWait"/> and authors retry from the
/// previous step's checkpoint.
/// </para>
/// <para>
/// Opt-in: this tool is only registered for a Prompt step when its
/// <see cref="PromptOrchestrationStep.EnableTools"/> array (or the orchestration's
/// <see cref="Orchestration.DefaultEnableTools"/>) contains <c>"request_user_input"</c>.
/// Existing pipelines see no behavior change.
/// </para>
/// </summary>
public sealed class RequestUserInputTool : IEngineTool
{
	/// <summary>
	/// Canonical opt-in name (matches the suffix of the tool name without the prefix).
	/// Listed in <see cref="PromptOrchestrationStep.EnableTools"/> to enable the tool.
	/// </summary>
	public const string OptInName = "request_user_input";

	public string Name => "orchestra_request_user_input";

	public string Description =>
		"Pause execution and ask the human user for input. The orchestration will block on this " +
		"call until the user responds (via the Orchestra UI, CLI, or API). The user's reply is " +
		"returned as your tool result, and you can then continue with the answer in hand. Use " +
		"this only when you genuinely need a clarification or decision from the human — for " +
		"unambiguous tasks, just complete the work. Provide a clear, focused 'prompt' describing " +
		"what you need; optionally supply 'choices' to constrain the answer (e.g. " +
		"['approve', 'reject']).";

	public string ParametersSchema => """
		{
			"type": "object",
			"properties": {
				"prompt": {
					"type": "string",
					"description": "The question or request to display to the human user."
				},
				"choices": {
					"type": "array",
					"items": { "type": "string" },
					"description": "Optional list of allowed responses. When provided, the user's choice will be one of these values; otherwise any free-form reply is accepted."
				}
			},
			"required": ["prompt"]
		}
		""";

	public string Execute(string arguments, EngineToolContext context)
	{
		// Block-style synchronous Execute: we wait on the async call. Engine tools run
		// inside the agent's tool-call dispatch which is itself async, so the SDK's
		// downstream orchestrator handles this OK — but we still want to respect any
		// linked cancellation token via the StepCompletionCts wired by the executor.
		var cancellationToken = context.StepCompletionCts?.Token ?? CancellationToken.None;
		try
		{
			return ExecuteAsync(arguments, context, cancellationToken).GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
			return "Wait for user input was cancelled before a response arrived.";
		}
	}

	internal async Task<string> ExecuteAsync(string arguments, EngineToolContext context, CancellationToken cancellationToken)
	{
		string? prompt = null;
		string[] choices = [];
		try
		{
			using var doc = JsonDocument.Parse(arguments);
			var root = doc.RootElement;
			prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() : null;
			if (root.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array)
			{
				choices = c.EnumerateArray()
					.Select(e => e.GetString())
					.Where(s => !string.IsNullOrWhiteSpace(s))
					.Select(s => s!)
					.ToArray();
			}
		}
		catch (JsonException)
		{
			return "Invalid arguments. Expected JSON with 'prompt' (string) and optional 'choices' (string array).";
		}

		if (string.IsNullOrWhiteSpace(prompt))
		{
			return "Missing 'prompt' argument. Provide a non-empty prompt describing what you need.";
		}

		var stepName = context.StepName;
		var orchestrationName = context.OrchestrationName;
		var runId = context.RunId;

		if (string.IsNullOrEmpty(stepName) || string.IsNullOrEmpty(orchestrationName) || string.IsNullOrEmpty(runId))
		{
			return "Tool is not available in this context (missing run identity). Cannot request user input.";
		}

		// Shared HITL primitive: persist a pending record, fire awaiting-input notifications,
		// then block until the host completes the wait. Cancellation propagates to Execute's
		// catch (returns the cancellation message); the agent session is NOT torn down.
		var response = await context
			.RequestHumanInputAsync(prompt, choices, PendingInputKind.EngineTool, cancellationToken)
			.ConfigureAwait(false);

		return response?.ResolveContent()
			?? "Tool is not available in this context (missing run identity). Cannot request user input.";
	}
}
