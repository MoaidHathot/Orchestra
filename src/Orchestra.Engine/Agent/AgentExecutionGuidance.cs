namespace Orchestra.Engine;

/// <summary>Provider-neutral model-facing guidance; runtime controls remain provider-owned.</summary>
public static class AgentExecutionGuidance
{
	public static string Interaction(bool humanInput) => humanInput
		? "Complete the requested work. Ask the operator a question only when input is necessary to proceed."
		: "This is a non-interactive orchestration step. Complete the requested work using available tools, make reasonable assumptions, and verify the result. Do not stop at a plan, ask for permission to continue, or offer to do the work later. If genuinely blocked, report the blocker clearly.";

	public static string StructuredResponse(System.Text.Json.JsonElement? schema) => schema is { } value
		? "Return the final response as JSON matching this JSON Schema, with no Markdown fences or explanatory prose: " + value.GetRawText()
		: string.Empty;
}
