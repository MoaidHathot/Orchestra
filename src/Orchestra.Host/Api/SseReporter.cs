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
/// 
/// Memory-bounded: uses a circular buffer for accumulated events (max 10,000)
/// and bounded channels for subscribers (1,000 capacity with DropOldest).
/// Limits subscribers to 50 max and implements IDisposable for cleanup.
/// </summary>
public sealed class SseReporter : IOrchestrationReporter, IDisposable
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>
	/// Maximum number of events to keep in the circular buffer for replay.
	/// </summary>
	public const int MaxAccumulatedEvents = 10_000;

	/// <summary>
	/// Maximum number of events that can be buffered per subscriber channel.
	/// </summary>
	public const int MaxChannelCapacity = 1_000;

	/// <summary>
	/// Maximum number of concurrent subscribers.
	/// </summary>
	public const int MaxSubscribers = 50;

	private readonly object _lock = new();
	private readonly SseEvent[] _eventBuffer = new SseEvent[MaxAccumulatedEvents];
	private int _eventCount;
	private int _eventHead; // Index of the oldest event in the circular buffer
	private readonly List<Channel<SseEvent>> _subscribers = [];
	private bool _isCompleted;
	private bool _disposed;

	/// <summary>
	/// Gets all accumulated events (for replay to late-joining subscribers).
	/// Returns events in chronological order from the circular buffer.
	/// </summary>
	public IReadOnlyList<SseEvent> AccumulatedEvents
	{
		get
		{
			lock (_lock)
			{
				return GetAccumulatedEventsLocked();
			}
		}
	}

	/// <summary>
	/// Gets the total number of accumulated events (may be less than total written if buffer wrapped).
	/// </summary>
	public int AccumulatedEventCount
	{
		get
		{
			lock (_lock)
			{
				return _eventCount;
			}
		}
	}

	/// <summary>
	/// Gets the current number of active subscribers.
	/// </summary>
	public int SubscriberCount
	{
		get
		{
			lock (_lock)
			{
				return _subscribers.Count;
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
	/// Returns null for Future if the maximum subscriber limit has been reached.
	/// </summary>
	/// <returns>A tuple of (accumulated events to replay, channel for future events)</returns>
	public (IReadOnlyList<SseEvent> Replay, ChannelReader<SseEvent>? Future) Subscribe()
	{
		var channel = Channel.CreateBounded<SseEvent>(
			new BoundedChannelOptions(MaxChannelCapacity)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.DropOldest,
			});

		lock (_lock)
		{
			var replay = GetAccumulatedEventsLocked();

			if (_isCompleted)
			{
				// Already done - just return accumulated events and a completed channel
				channel.Writer.TryComplete();
				return (replay, channel.Reader);
			}

			if (_subscribers.Count >= MaxSubscribers)
			{
				// Too many subscribers - return replay but no future channel
				channel.Writer.TryComplete();
				return (replay, null);
			}

			_subscribers.Add(channel);
			return (replay, channel.Reader);
		}
	}

	/// <summary>
	/// Unsubscribes a channel (e.g., when client disconnects).
	/// </summary>
	public void Unsubscribe(ChannelReader<SseEvent>? reader)
	{
		if (reader is null) return;

		lock (_lock)
		{
			for (var i = _subscribers.Count - 1; i >= 0; i--)
			{
				if (_subscribers[i].Reader == reader)
				{
					_subscribers[i].Writer.TryComplete();
					_subscribers.RemoveAt(i);
					break;
				}
			}
		}
	}

	/// <summary>
	/// Sends a heartbeat/keepalive event to all subscribers.
	/// Call this periodically from the SSE streaming loop.
	/// </summary>
	public void SendHeartbeat()
	{
		var evt = new SseEvent("heartbeat", "{}");

		lock (_lock)
		{
			if (_isCompleted) return;

			// Do NOT add heartbeats to the accumulator — they are ephemeral
			foreach (var channel in _subscribers)
			{
				channel.Writer.TryWrite(evt);
			}
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
			return future ?? Channel.CreateBounded<SseEvent>(1).Reader;
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

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;

		lock (_lock)
		{
			_isCompleted = true;
			foreach (var channel in _subscribers)
			{
				channel.Writer.TryComplete();
			}
			_subscribers.Clear();

			// Clear buffer references so events can be GC'd
			Array.Clear(_eventBuffer);
			_eventCount = 0;
			_eventHead = 0;
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
			outputHandlerResult = trace.OutputHandlerResult,
			mcpServers = trace.McpServers.Count > 0 ? trace.McpServers : null,
			warnings = trace.Warnings.Count > 0 ? trace.Warnings : null,
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
			completionReason = orchestrationResult.CompletionReason,
			completedByStep = orchestrationResult.CompletedByStep,
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
			if (_isCompleted || _disposed)
				return;

			// Add to circular buffer
			var writeIndex = (_eventHead + _eventCount) % MaxAccumulatedEvents;
			_eventBuffer[writeIndex] = evt;

			if (_eventCount < MaxAccumulatedEvents)
			{
				_eventCount++;
			}
			else
			{
				// Buffer is full — advance head (oldest event is discarded)
				_eventHead = (_eventHead + 1) % MaxAccumulatedEvents;
			}

			foreach (var channel in _subscribers)
			{
				// TryWrite on bounded channel with DropOldest will always succeed
				channel.Writer.TryWrite(evt);
			}
		}
	}

	/// <summary>
	/// Gets accumulated events in chronological order. Must be called under _lock.
	/// </summary>
	private List<SseEvent> GetAccumulatedEventsLocked()
	{
		var result = new List<SseEvent>(_eventCount);
		for (var i = 0; i < _eventCount; i++)
		{
			var index = (_eventHead + i) % MaxAccumulatedEvents;
			result.Add(_eventBuffer[index]);
		}
		return result;
	}
}
