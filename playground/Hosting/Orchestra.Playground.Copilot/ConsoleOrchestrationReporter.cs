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

	public void ReportStepCompleted(string stepName, AgentResult result)
	{
		if (result.ActualModel is not null)
		{
			Console.WriteLine($"  [{stepName}] Model used: {result.ActualModel}");
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

	public void ReportStepTrace(string stepName, StepExecutionTrace trace)
	{
		// Console reporter doesn't output trace details — the step output is already shown.
	}
}
