using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Built-in engine tool that allows the LLM to explicitly signal the execution status
/// of a prompt step. The LLM calls this tool to mark the step as 'success', 'failed',
/// or 'no_action' with a reason describing the outcome.
/// </summary>
public sealed class SetStatusTool : IEngineTool
{
	public string Name => "orchestra_set_status";

	public string Description =>
		"Signal the final execution status of this step. Call this tool with 'success' to " +
		"confirm the task was completed successfully, 'failed' if you cannot accomplish " +
		"the task (e.g., required tools are unavailable, input is invalid, or the task is " +
		"impossible), or 'no_action' if there is nothing to do (e.g., no items to process, " +
		"all items already handled). When 'no_action' is used, all downstream steps that " +
		"depend on this step will be skipped. Provide a reason describing the outcome.";

	public string ParametersSchema => """
		{
			"type": "object",
			"properties": {
				"status": {
					"type": "string",
					"enum": ["success", "failed", "no_action"],
					"description": "The execution status to set: 'success', 'failed', or 'no_action' (nothing to do, skip downstream steps)."
				},
				"reason": {
					"type": "string",
					"description": "A clear explanation of the outcome — why the task succeeded, failed, or requires no action."
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

			if (string.Equals(status, "no_action", StringComparison.OrdinalIgnoreCase))
			{
				context.SetStatus(ExecutionStatus.NoAction, reason ?? "No action needed");
				return "Status set to no_action. The step will complete and all downstream dependent steps will be skipped.";
			}

			return $"Unknown status '{status}'. Supported values are 'success', 'failed', and 'no_action'.";
		}
		catch (JsonException)
		{
			return "Invalid arguments. Expected JSON with 'status' and 'reason' properties.";
		}
	}
}
