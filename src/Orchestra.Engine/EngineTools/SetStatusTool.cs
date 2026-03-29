using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Built-in engine tool that allows the LLM to explicitly signal the execution status
/// of a prompt step. The LLM calls this tool to mark the step as either 'success' or
/// 'failed' with a reason describing the outcome.
/// </summary>
public sealed class SetStatusTool : IEngineTool
{
	public string Name => "orchestra_set_status";

	public string Description =>
		"Signal the final execution status of this step. Call this tool with 'success' to " +
		"confirm the task was completed successfully, or 'failed' if you cannot accomplish " +
		"the task (e.g., required tools are unavailable, input is invalid, or the task is " +
		"impossible). Provide a reason describing the outcome.";

	public string ParametersSchema => """
		{
			"type": "object",
			"properties": {
				"status": {
					"type": "string",
					"enum": ["success", "failed"],
					"description": "The execution status to set: 'success' or 'failed'."
				},
				"reason": {
					"type": "string",
					"description": "A clear explanation of the outcome — why the task succeeded or failed."
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
				context.SetStatus(ExecutionStatus.Succeeded, reason ?? "Step marked as succeeded by LLM");
				return "Status set to success. The step will be marked as succeeded upon completion.";
			}

			if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
			{
				context.SetStatus(ExecutionStatus.Failed, reason ?? "Step marked as failed by LLM");
				return "Status set to failed. The step will be marked as failed upon completion.";
			}

			return $"Unknown status '{status}'. Supported values are 'success' and 'failed'.";
		}
		catch (JsonException)
		{
			return "Invalid arguments. Expected JSON with 'status' and 'reason' properties.";
		}
	}
}
