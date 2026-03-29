using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Built-in engine tool that allows the LLM to halt the entire orchestration immediately.
/// When called, all pending and running steps will be cancelled and the orchestration
/// will complete with the specified status.
///
/// Use this tool when a step determines that the orchestration should not continue —
/// either because there is nothing to do or because a critical issue was detected.
/// </summary>
public sealed class CompleteTool : IEngineTool
{
	public string Name => "orchestra_complete";

	public string Description =>
		"Halt the entire orchestration immediately. Call this tool when you determine that " +
		"the orchestration should stop — either because there is nothing to process (use " +
		"'success' status) or because a critical issue was detected (use 'failed' status). " +
		"All remaining steps will be cancelled. Provide a reason explaining why the " +
		"orchestration should stop.";

	public string ParametersSchema => """
		{
			"type": "object",
			"properties": {
				"status": {
					"type": "string",
					"enum": ["success", "failed"],
					"description": "The final orchestration status: 'success' if the orchestration completed its purpose (e.g., nothing to do), or 'failed' if a critical issue was detected."
				},
				"reason": {
					"type": "string",
					"description": "A clear explanation of why the orchestration should stop."
				}
			},
			"required": ["status", "reason"]
		}
		""";

	public string Execute(string arguments, EngineToolContext context)
	{
		try
		{
			using var doc = JsonDocument.Parse(arguments);
			var root = doc.RootElement;

			var status = root.TryGetProperty("status", out var statusProp)
				? statusProp.GetString()
				: null;

			var reason = root.TryGetProperty("reason", out var reasonProp)
				? reasonProp.GetString()
				: null;

			if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
			{
				context.CompleteOrchestration(ExecutionStatus.Succeeded, reason ?? "Orchestration completed early by LLM");
				context.SetStatus(ExecutionStatus.Succeeded, reason ?? "Step completed (orchestration halted)");
				return "Orchestration will be completed with success status. All remaining steps will be cancelled.";
			}

			if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
			{
				context.CompleteOrchestration(ExecutionStatus.Failed, reason ?? "Orchestration halted by LLM");
				context.SetStatus(ExecutionStatus.Failed, reason ?? "Step failed (orchestration halted)");
				return "Orchestration will be completed with failed status. All remaining steps will be cancelled.";
			}

			return $"Unknown status '{status}'. Supported values are 'success' and 'failed'.";
		}
		catch (JsonException)
		{
			return "Invalid arguments. Expected JSON with 'status' and 'reason' properties.";
		}
	}
}
