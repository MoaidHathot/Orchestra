using System.Text.Json;
using System.Threading.Channels;
using Orchestra.Engine;

namespace Orchestra.Host.Api;

/// <summary>
/// Represents an SSE event with type and JSON data.
/// </summary>
public record SseEvent(string Type, string Data);

/// <summary>
/// An IOrchestrationReporter that writes structured SSE events to multiple subscribers.
/// Supports late-joining subscribers by replaying accumulated events.
/// Each execution creates its own instance tied to a specific orchestration run.
/// </summary>
public class SseReporter : IOrchestrationReporter
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly object _lock = new();
	private readonly List<SseEvent> _accumulatedEvents = [];
	private readonly List<Channel<SseEvent>> _subscribers = [];
	private bool _isCompleted;

	/// <summary>
	/// Gets all accumulated events (for replay to late-joining subscribers).
	/// </summary>
	public IReadOnlyList<SseEvent> AccumulatedEvents
	{
		get
		{
			lock (_lock)
			{
				return _accumulatedEvents.ToList();
			}
		}
	}

	/// <summary>
	/// Whether the reporter has completed (orchestration finished).
	/// </summary>
	public bool IsCompleted
	{
		get
		{
			lock (_lock)
			{
				return _isCompleted;
			}
		}
	}

	/// <summary>
	/// Callback invoked when a step starts. Parameters: stepName
	/// </summary>
	public Action<string>? OnStepStarted { get; set; }

	/// <summary>
	/// Callback invoked when a step completes. Parameters: stepName
	/// </summary>
	public Action<string>? OnStepCompleted { get; set; }

	/// <summary>
	/// Creates a new subscriber channel that will receive future events.
	/// Call this to attach a new SSE client to the execution.
	/// </summary>
	/// <returns>A tuple of (accumulated events to replay, channel for future events)</returns>
	public (IReadOnlyList<SseEvent> Replay, ChannelReader<SseEvent> Future) Subscribe()
	{
		var channel = Channel.CreateUnbounded<SseEvent>(
			new UnboundedChannelOptions { SingleReader = true });

		lock (_lock)
		{
			if (_isCompleted)
			{
				// Already done - just return accumulated events and a completed channel
				channel.Writer.TryComplete();
				return (_accumulatedEvents.ToList(), channel.Reader);
			}

			_subscribers.Add(channel);
			return (_accumulatedEvents.ToList(), channel.Reader);
		}
	}

	/// <summary>
	/// Unsubscribes a channel (e.g., when client disconnects).
	/// </summary>
	public void Unsubscribe(ChannelReader<SseEvent> reader)
	{
		lock (_lock)
		{
			_subscribers.RemoveAll(ch => ch.Reader == reader);
		}
	}

	/// <summary>
	/// Legacy property for backward compatibility with existing code.
	/// Creates a new subscriber and returns its reader.
	/// </summary>
	public ChannelReader<SseEvent> Events
	{
		get
		{
			var (_, future) = Subscribe();
			return future;
		}
	}

	public void Complete()
	{
		lock (_lock)
		{
			_isCompleted = true;
			foreach (var channel in _subscribers)
			{
				channel.Writer.TryComplete();
			}
			_subscribers.Clear();
		}
	}

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

	/// <summary>
	/// Reports that a step was cancelled (not failed).
	/// </summary>
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
		OnStepCompleted?.Invoke(stepName);
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
			toolCalls = trace.ToolCalls.Select(tc => new
			{
				callId = tc.CallId,
				mcpServer = tc.McpServer,
				toolName = tc.ToolName,
				arguments = tc.Arguments,
				success = tc.Success,
				result = tc.Result,
				error = tc.Error,
				startedAt = tc.StartedAt?.ToString("o"),
				completedAt = tc.CompletedAt?.ToString("o")
			}).ToArray(),
			responseSegments = trace.ResponseSegments,
			finalResponse = trace.FinalResponse,
			outputHandlerResult = trace.OutputHandlerResult
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
		OnStepStarted?.Invoke(stepName);
	}

	public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations)
	{
		Write("loop-iteration", new { checkerStepName, targetStepName, iteration, maxIterations });
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

	/// <summary>
	/// Reports the final orchestration result.
	/// Not part of IOrchestrationReporter - called directly by the execution endpoint.
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

	/// <summary>
	/// Reports that the orchestration was cancelled.
	/// </summary>
	public void ReportOrchestrationCancelled()
	{
		Write("orchestration-cancelled", new { status = "Cancelled" });
	}

	/// <summary>
	/// Reports that the orchestration failed with an error.
	/// </summary>
	public void ReportOrchestrationError(string errorMessage)
	{
		Write("orchestration-error", new { status = "Failed", error = errorMessage });
	}

	/// <summary>
	/// Reports a status change for the orchestration (e.g., "Cancelling").
	/// </summary>
	public void ReportStatusChange(string status)
	{
		Write("status-changed", new { status });
	}

	private void Write(string eventType, object data)
	{
		var json = JsonSerializer.Serialize(data, s_jsonOptions);
		var evt = new SseEvent(eventType, json);

		lock (_lock)
		{
			if (_isCompleted)
				return;

			_accumulatedEvents.Add(evt);
			foreach (var channel in _subscribers)
			{
				channel.Writer.TryWrite(evt);
			}
		}
	}
}
