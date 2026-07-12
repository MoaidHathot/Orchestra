using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// The action a Script step requested via its control file (the
/// <c>ORCHESTRA_CONTROL_FILE</c> channel).
/// </summary>
internal enum ScriptControlAction
{
	/// <summary>Set this step's terminal status (success / failed / no_action).</summary>
	SetStatus,

	/// <summary>Halt the whole orchestration (success / failed) — like <c>orchestra_complete</c>.</summary>
	Complete,
}

/// <summary>
/// Parsed contents of a Script step's control file — the deterministic, non-LLM equivalent of
/// the <c>orchestra_set_status</c> / <c>orchestra_complete</c> engine tools. The engine sets
/// <c>ORCHESTRA_CONTROL_FILE</c> to a temp path before launching the script; the script may
/// write a single JSON document:
/// <code>{ "action": "complete" | "set_status", "status": "success" | "failed" | "no_action", "reason": "..." }</code>
/// The engine reads it after the process exits (only on exit code 0) and maps it onto the
/// step's <see cref="ExecutionResult"/>.
/// </summary>
internal sealed record ScriptControlSignal(ScriptControlAction Action, ExecutionStatus Status, string? Reason)
{
	/// <summary>
	/// Attempts to parse a control-file payload. Returns <c>false</c> with a human-readable
	/// <paramref name="error"/> when the JSON is malformed, the action/status is unknown, or the
	/// action/status combination is invalid (e.g. <c>complete</c> + <c>no_action</c>).
	/// </summary>
	public static bool TryParse(string json, out ScriptControlSignal? signal, out string? error)
	{
		signal = null;
		error = null;

		if (string.IsNullOrWhiteSpace(json))
		{
			error = "control file is empty";
			return false;
		}

		string? actionRaw;
		string? statusRaw;
		string? reason;
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				error = "control file must contain a JSON object";
				return false;
			}

			actionRaw = root.TryGetProperty("action", out var a) ? a.GetString() : null;
			statusRaw = root.TryGetProperty("status", out var s) ? s.GetString() : null;
			reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
		}
		catch (JsonException ex)
		{
			error = $"invalid JSON: {ex.Message}";
			return false;
		}

		// action defaults to set_status when omitted (the most common, least destructive case).
		var action = (actionRaw ?? "set_status").Trim().ToLowerInvariant() switch
		{
			"set_status" or "set-status" or "setstatus" => (ScriptControlAction?)ScriptControlAction.SetStatus,
			"complete" => ScriptControlAction.Complete,
			_ => null,
		};
		if (action is null)
		{
			error = $"unknown action '{actionRaw}'. Expected 'complete' or 'set_status'.";
			return false;
		}

		var status = (statusRaw ?? string.Empty).Trim().ToLowerInvariant() switch
		{
			"success" or "succeeded" => (ExecutionStatus?)ExecutionStatus.Succeeded,
			"failed" or "failure" => ExecutionStatus.Failed,
			"no_action" or "no-action" or "noaction" => ExecutionStatus.NoAction,
			_ => null,
		};
		if (status is null)
		{
			error = $"unknown status '{statusRaw}'. Expected 'success', 'failed', or 'no_action'.";
			return false;
		}

		if (action == ScriptControlAction.Complete && status == ExecutionStatus.NoAction)
		{
			error = "action 'complete' does not support status 'no_action'; use 'success' or 'failed'.";
			return false;
		}

		signal = new ScriptControlSignal(action.Value, status.Value, reason);
		return true;
	}
}
