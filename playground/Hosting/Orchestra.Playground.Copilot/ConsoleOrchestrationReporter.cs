using Orchestra.Engine;

namespace Orchestra.Playground.Copilot;

public class ConsoleOrchestrationReporter : IOrchestrationReporter
{
	public void ReportSessionStarted(string requestedModel, string? selectedModel)
	{
		if (selectedModel is not null)
		{
			Console.WriteLine($"  Session started — requested: {requestedModel}, selected: {selectedModel}");
		}
		else
		{
			Console.WriteLine($"Creating Copilot session with model '{requestedModel}'...");
		}
	}

	public void ReportModelChange(string? previousModel, string newModel)
	{
		Console.WriteLine($"  Model changed: {previousModel} -> {newModel}");
	}

	public void ReportUsage(string stepName, string model, AgentUsage usage)
	{
		Console.WriteLine($"  [{stepName}] Tokens — in: {usage.InputTokens}, out: {usage.OutputTokens}, cache-read: {usage.CacheReadTokens}");
	}

	public void ReportContentDelta(string stepName, string chunk)
	{
		// Content deltas are streamed — don't print each chunk to avoid flooding the console.
		// The final content is reported via ReportStepOutput.
	}

	public void ReportReasoningDelta(string stepName, string chunk)
	{
		// Reasoning deltas are streamed — don't print each chunk to avoid flooding the console.
	}

	public void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer)
	{
		var server = mcpServer is not null ? $" (MCP: {mcpServer})" : "";
		Console.WriteLine($"  [{stepName}] Tool '{toolName}'{server} executing...");
		if (arguments is not null)
		{
			var preview = arguments.Length > 200 ? arguments[..200] + "..." : arguments;
			Console.WriteLine($"           Args: {preview}");
		}
	}

	public void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error)
	{
		if (success)
		{
			Console.WriteLine($"  [{stepName}] Tool '{toolName}' completed successfully.");
		}
		else
		{
			Console.Error.WriteLine($"  [{stepName}] Tool '{toolName}' failed: {error ?? "unknown error"}");
		}
	}

	public void ReportStepError(string stepName, string errorMessage)
	{
		Console.Error.WriteLine($"  [{stepName}] Error: {errorMessage}");
	}

	public void ReportStepCancelled(string stepName)
	{
		Console.WriteLine($"  [{stepName}] Cancelled");
	}

	public void ReportStepCompleted(string stepName, AgentResult result, OrchestrationStepType stepType)
	{
		if (result.ActualModel is not null)
		{
			Console.WriteLine($"  [{stepName}] Model used: {result.ActualModel}");
		}
		else
		{
			Console.WriteLine($"  [{stepName}] Completed ({stepType.ToString().ToLowerInvariant()})");
		}
	}

	public void ReportModelMismatch(ModelMismatchInfo mismatch)
	{
		Console.WriteLine();
		Console.WriteLine($"  ╔══════════════════════════════════════════════════════════════");
		Console.WriteLine($"  ║ MODEL MISMATCH DETECTED");
		Console.WriteLine($"  ╠══════════════════════════════════════════════════════════════");
		Console.WriteLine($"  ║ Configured model : {mismatch.ConfiguredModel}");
		Console.WriteLine($"  ║ Actual model used: {mismatch.ActualModel}");
		Console.WriteLine($"  ║");
		Console.WriteLine($"  ║ Step configuration:");
		Console.WriteLine($"  ║   System prompt mode: {mismatch.SystemPromptMode}");
		Console.WriteLine($"  ║   Reasoning level   : {mismatch.ReasoningLevel}");
		Console.WriteLine($"  ║   System prompt      : {mismatch.SystemPromptPreview}");
		Console.WriteLine($"  ║   MCP servers        : {(mismatch.McpServers is { Length: > 0 } ? string.Join(", ", mismatch.McpServers) : "(none)")}");
		Console.WriteLine($"  ║");

		if (mismatch.AvailableModels is { Count: > 0 })
		{
			Console.WriteLine($"  ║ Available models ({mismatch.AvailableModels.Count}):");
			foreach (var m in mismatch.AvailableModels)
			{
				var billing = m.BillingMultiplier is not null ? $" [{m.BillingMultiplier}x]" : "";
				var reasoning = m.ReasoningEfforts is { Length: > 0 }
					? $" reasoning:[{string.Join(",", m.ReasoningEfforts)}]"
					: "";
				Console.WriteLine($"  ║   - {m.Id,-40} {m.Name}{billing}{reasoning}");
			}
		}
		else
		{
			Console.WriteLine($"  ║ (Could not list available models)");
		}

		Console.WriteLine($"  ╚══════════════════════════════════════════════════════════════");
		Console.WriteLine();
	}

	public void ReportStepOutput(string stepName, string content)
	{
		Console.WriteLine();
		Console.WriteLine(content);
	}

	public void ReportStepStarted(string stepName)
	{
		Console.WriteLine($"  [{stepName}] Starting...");
	}

	public void ReportStepSkipped(string stepName, string reason)
	{
		Console.WriteLine($"  [{stepName}] Skipped: {reason}");
	}

	public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations)
	{
		Console.WriteLine($"  [{checkerStepName}] Loop iteration {iteration}/{maxIterations} — re-running '{targetStepName}' with feedback");
	}

	public void ReportStepRetry(string stepName, int attempt, int maxRetries, string error, TimeSpan delay)
	{
		Console.WriteLine($"  [{stepName}] Retrying ({attempt}/{maxRetries}) after error: {error}. Waiting {delay.TotalSeconds:F1}s...");
	}

	public void ReportCheckpointSaved(string runId, string stepName, int completedSteps, int totalSteps)
	{
		Console.WriteLine($"  [checkpoint] Saved after '{stepName}' ({completedSteps}/{totalSteps} steps complete) — run {runId}");
	}

	public void ReportSessionWarning(string warningType, string message)
	{
		Console.WriteLine($"  [WARNING] ({warningType}) {message}");
	}

	public void ReportSessionInfo(string infoType, string message)
	{
		Console.WriteLine($"  [INFO] ({infoType}) {message}");
	}

	public void ReportMcpServersLoaded(IReadOnlyList<McpServerStatusInfo> servers)
	{
		Console.WriteLine($"  [MCP] Servers loaded ({servers.Count}):");
		foreach (var server in servers)
		{
			var err = server.Error is not null ? $" — {server.Error}" : "";
			Console.WriteLine($"    - {server.Name}: {server.Status}{err}");
		}
	}

	public void ReportMcpServerStatusChanged(string serverName, string status)
	{
		Console.WriteLine($"  [MCP] Server '{serverName}' status changed: {status}");
	}

	public void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools)
	{
		var name = displayName ?? agentName;
		var toolList = tools is { Length: > 0 } ? string.Join(", ", tools) : "all";
		Console.WriteLine($"  [{stepName}] Subagent selected: {name} (tools: {toolList})");
	}

	public void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description)
	{
		var name = displayName ?? agentName;
		Console.WriteLine($"  [{stepName}] Subagent started: {name}");
		if (description is not null)
		{
			Console.WriteLine($"    Description: {description}");
		}
	}

	public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName)
	{
		var name = displayName ?? agentName;
		Console.WriteLine($"  [{stepName}] Subagent completed: {name}");
	}

	public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error)
	{
		var name = displayName ?? agentName;
		Console.Error.WriteLine($"  [{stepName}] Subagent failed: {name} - {error ?? "unknown error"}");
	}

	public void ReportSubagentDeselected(string stepName)
	{
		Console.WriteLine($"  [{stepName}] Returned to parent agent");
	}

	public void ReportStepTrace(string stepName, StepExecutionTrace trace)
	{
		// Console reporter doesn't output trace details — the step output is already shown.
	}

	public void ReportRunContext(RunContext context)
	{
		Console.WriteLine($"  [context] Run {context.RunId} — {context.OrchestrationName} v{context.OrchestrationVersion}");
		if (context.Parameters.Count > 0)
			Console.WriteLine($"    Parameters: {string.Join(", ", context.Parameters.Select(p => $"{p.Key}={p.Value}"))}");
		if (context.AccessedEnvironmentVariables.Count > 0)
			Console.WriteLine($"    Env vars accessed: {string.Join(", ", context.AccessedEnvironmentVariables.Keys)}");
	}

	public void ReportAuditLogEntry(string stepName, AuditLogEntry entry)
	{
		// No-op for console reporter
	}

	public void ReportStepStatusSet(string stepName, string status, string reason)
	{
		Console.WriteLine($"  [{stepName}] Status set to {status}: {reason}");
	}
}
