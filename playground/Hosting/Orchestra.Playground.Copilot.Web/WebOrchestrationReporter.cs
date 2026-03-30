using System.Text.Json;
using System.Threading.Channels;
using Orchestra.Engine;

namespace Orchestra.Playground.Copilot.Web;

/// <summary>
/// An IOrchestrationReporter that writes structured SSE events to a channel.
/// Each execution creates its own instance tied to a specific SSE response stream.
/// </summary>
public class WebOrchestrationReporter : IOrchestrationReporter
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly Channel<SseEvent> _channel = Channel.CreateUnbounded<SseEvent>(
		new UnboundedChannelOptions { SingleReader = true });

	public ChannelReader<SseEvent> Events => _channel.Reader;

	public void Complete() => _channel.Writer.TryComplete();

	public void ReportSessionStarted(string requestedModel, string? selectedModel)
	{
		Write("session-started", new { requestedModel, selectedModel });
	}

	public void ReportModelChange(string? previousModel, string newModel)
	{
		Write("model-change", new { previousModel, newModel });
	}

	public void ReportUsage(string stepName, string model, AgentUsage usage)
	{
		Write("usage", new
		{
			stepName,
			model,
			inputTokens = usage.InputTokens,
			outputTokens = usage.OutputTokens,
			cacheReadTokens = usage.CacheReadTokens,
			cacheWriteTokens = usage.CacheWriteTokens,
			cost = usage.Cost,
			duration = usage.Duration,
		});
	}

	public void ReportContentDelta(string stepName, string chunk)
	{
		Write("content-delta", new { stepName, chunk });
	}

	public void ReportReasoningDelta(string stepName, string chunk)
	{
		Write("reasoning-delta", new { stepName, chunk });
	}

	public void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer)
	{
		Write("tool-started", new { stepName, toolName, arguments, mcpServer });
	}

	public void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error)
	{
		Write("tool-completed", new { stepName, toolName, success, result, error });
	}

	public void ReportStepError(string stepName, string errorMessage)
	{
		Write("step-error", new { stepName, error = errorMessage });
	}

	public void ReportStepCancelled(string stepName)
	{
		Write("step-cancelled", new { stepName });
	}

	public void ReportStepCompleted(string stepName, AgentResult result)
	{
		Write("step-completed", new
		{
			stepName,
			actualModel = result.ActualModel,
			selectedModel = result.SelectedModel,
			contentPreview = result.Content.Length > 500
				? result.Content[..500] + "..."
				: result.Content,
		});
	}

	public void ReportModelMismatch(ModelMismatchInfo mismatch)
	{
		Write("model-mismatch", new
		{
			configuredModel = mismatch.ConfiguredModel,
			actualModel = mismatch.ActualModel,
			systemPromptMode = mismatch.SystemPromptMode,
			reasoningLevel = mismatch.ReasoningLevel,
		});
	}

	public void ReportStepOutput(string stepName, string content)
	{
		Write("step-output", new { stepName, content });
	}

	public void ReportStepSkipped(string stepName, string reason)
	{
		Write("step-skipped", new { stepName, reason });
	}

	public void ReportStepStarted(string stepName)
	{
		Write("step-started", new { stepName });
	}

	public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations)
	{
		Write("loop-iteration", new { checkerStepName, targetStepName, iteration, maxIterations });
	}

	public void ReportStepRetry(string stepName, int attempt, int maxRetries, string error, TimeSpan delay)
	{
		Write("step-retry", new { stepName, attempt, maxRetries, error, delaySeconds = delay.TotalSeconds });
	}

	public void ReportCheckpointSaved(string runId, string stepName, int completedSteps, int totalSteps)
	{
		Write("checkpoint-saved", new { runId, stepName, completedSteps, totalSteps });
	}

	public void ReportSessionWarning(string warningType, string message)
	{
		Write("session-warning", new { warningType, message });
	}

	public void ReportSessionInfo(string infoType, string message)
	{
		Write("session-info", new { infoType, message });
	}

	public void ReportMcpServersLoaded(IReadOnlyList<McpServerStatusInfo> servers)
	{
		Write("mcp-servers-loaded", new
		{
			servers = servers.Select(s => new
			{
				name = s.Name,
				status = s.Status,
				source = s.Source,
				error = s.Error,
			}).ToArray(),
		});
	}

	public void ReportMcpServerStatusChanged(string serverName, string status)
	{
		Write("mcp-server-status-changed", new { serverName, status });
	}

	public void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools)
	{
		Write("subagent-selected", new { stepName, agentName, displayName, tools });
	}

	public void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description)
	{
		Write("subagent-started", new { stepName, toolCallId, agentName, displayName, description });
	}

	public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName)
	{
		Write("subagent-completed", new { stepName, toolCallId, agentName, displayName });
	}

	public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error)
	{
		Write("subagent-failed", new { stepName, toolCallId, agentName, displayName, error });
	}

	public void ReportSubagentDeselected(string stepName)
	{
		Write("subagent-deselected", new { stepName });
	}

	public void ReportRunContext(RunContext context)
	{
		Write("run-context", new
		{
			runId = context.RunId,
			orchestrationName = context.OrchestrationName,
			orchestrationVersion = context.OrchestrationVersion,
			startedAt = context.StartedAt.ToString("o"),
			triggeredBy = context.TriggeredBy,
			triggerId = context.TriggerId,
			parameters = context.Parameters.Count > 0 ? context.Parameters : null,
			variables = context.Variables.Count > 0 ? context.Variables : null,
			resolvedVariables = context.ResolvedVariables.Count > 0 ? context.ResolvedVariables : null,
			accessedEnvironmentVariables = context.AccessedEnvironmentVariables.Count > 0 ? context.AccessedEnvironmentVariables : null,
			dataDirectory = context.DataDirectory,
		});
	}

	public void ReportStepTrace(string stepName, StepExecutionTrace trace)
	{
		Write("step-trace", new
		{
			stepName,
			systemPrompt = trace.SystemPrompt,
			userPromptRaw = trace.UserPromptRaw,
			userPromptProcessed = trace.UserPromptProcessed,
			reasoning = trace.Reasoning,
			toolCalls = trace.ToolCalls?.Select(tc => new
			{
				tc.ToolName,
				tc.McpServer,
				tc.Arguments,
				tc.Result,
				tc.Error,
				tc.StartedAt,
				tc.CompletedAt,
			}),
			responseSegments = trace.ResponseSegments,
			finalResponse = trace.FinalResponse,
			outputHandlerResult = trace.OutputHandlerResult,
			mcpServers = trace.McpServers.Count > 0 ? trace.McpServers : null,
			warnings = trace.Warnings.Count > 0 ? trace.Warnings : null,
		});
	}

	/// <summary>
	/// Reports the final orchestration result.
	/// Not part of IOrchestrationReporter — called directly by the execution endpoint.
	/// </summary>
	public void ReportOrchestrationDone(OrchestrationResult orchestrationResult)
	{
		var results = orchestrationResult.StepResults.ToDictionary(
			kv => kv.Key,
			kv => new
			{
				status = kv.Value.Status.ToString(),
				contentPreview = kv.Value.Content.Length > 1000
					? kv.Value.Content[..1000] + "..."
					: kv.Value.Content,
				error = kv.Value.ErrorMessage,
			});

		Write("orchestration-done", new
		{
			status = orchestrationResult.Status.ToString(),
			results,
		});
	}

	private void Write(string eventType, object data)
	{
		var json = JsonSerializer.Serialize(data, s_jsonOptions);
		_channel.Writer.TryWrite(new SseEvent(eventType, json));
	}
}

public record SseEvent(string Type, string Data);
