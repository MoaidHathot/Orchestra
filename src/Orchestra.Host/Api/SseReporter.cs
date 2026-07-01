using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// Represents an SSE event with type, JSON data, and a monotonically increasing sequence
/// number. The sequence number is written as the SSE <c>id:</c> field so clients can
/// supply <c>Last-Event-Id</c> on reconnect and resume from the exact point they left off
/// (subject to the replay buffer still containing it).
/// </summary>
public record SseEvent(long Sequence, string Type, string Data);

/// <summary>
/// An IOrchestrationReporter that writes structured SSE events to multiple subscribers.
/// Supports late-joining subscribers via:
///   - Replaying accumulated events from a circular buffer.
///   - Serving an authoritative per-step state snapshot that survives buffer eviction.
///   - Honoring a <c>Last-Event-Id</c> cursor to resume after the most recently
///     consumed sequence number.
///
/// Each execution creates its own instance tied to a specific orchestration run.
/// Memory-bounded: uses a circular buffer for accumulated events (default 50,000)
/// and bounded channels for subscribers (default 5,000 capacity with DropOldest).
/// Limits subscribers to 50 max by default and implements IDisposable for cleanup.
/// All caps are configurable via <see cref="SseOptions"/>.
/// </summary>
public sealed partial class SseReporter : IOrchestrationReporter, IDisposable
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	private static readonly JsonSerializerOptions s_snapshotJsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>
	/// Default maximum number of events to keep in the circular buffer for replay.
	/// Used when the reporter is constructed without explicit <see cref="SseOptions"/>
	/// (e.g. in unit tests that call <c>new SseReporter()</c>).
	/// </summary>
	public const int MaxAccumulatedEvents = 50_000;

	/// <summary>
	/// Default maximum number of events that can be buffered per subscriber channel.
	/// </summary>
	public const int MaxChannelCapacity = 5_000;

	/// <summary>
	/// Default maximum number of concurrent subscribers.
	/// </summary>
	public const int MaxSubscribers = 50;

	/// <summary>
	/// Hard upper bound for per-step <see cref="StepStateSnapshot.Output"/>. Snapshot
	/// payloads must stay reasonable in size even when the step produced megabytes of
	/// content; full content is still available via the <c>step-output</c> replay event.
	/// </summary>
	public const int MaxSnapshotStepOutputLength = 64 * 1024;

	/// <summary>
	/// Hard upper bound for the number of audit entries retained per step in the
	/// snapshot. Beyond this, oldest entries are dropped to keep the snapshot bounded.
	/// </summary>
	public const int MaxSnapshotAuditEntriesPerStep = 200;

	/// <summary>
	/// Set of event types whose loss from the replay buffer materially degrades the
	/// UI's ability to render a "done" step (status + trace + output + audit). When
	/// eviction discards one of these types, the reporter logs a warning so operators
	/// can tune <see cref="SseOptions.MaxAccumulatedEvents"/>. The reporter's separate
	/// per-step snapshot is the safety net so the UI keeps working regardless.
	/// </summary>
	private static readonly HashSet<string> s_importantEventTypes = new(StringComparer.Ordinal)
	{
		"execution-started",
		"run-context",
		"status-changed",
		"step-started",
		"step-completed",
		"step-error",
		"step-cancelled",
		"step-skipped",
		"step-status-set",
		"step-retry",
		"step-trace",
		"step-output",
		"saved-file",
		"audit-log",
		"orchestration-done",
		"orchestration-cancelled",
		"orchestration-error",
	};

	private readonly Lock _lock = new();
	private readonly SseEvent[] _eventBuffer;
	private readonly int _maxAccumulatedEvents;
	private readonly int _maxChannelCapacity;
	private readonly int _maxSubscribers;
	private readonly TimeSpan _heartbeatInterval;
	private int _eventCount;
	private int _eventHead; // Index of the oldest event in the circular buffer
	private long _nextSequence; // Monotonically increasing event sequence number
	private long _firstAvailableSequence; // Sequence of the oldest event still in the buffer
	private readonly List<Channel<SseEvent>> _subscribers = [];
	private bool _isCompleted;
	private bool _disposed;
	private readonly DashboardEventBroadcaster? _dashboardBroadcaster;
	private readonly ILogger<SseReporter> _logger;

	// Authoritative state — survives circular-buffer eviction.
	private readonly Dictionary<string, MutableStepState> _stepStates = new(StringComparer.Ordinal);
	private string? _executionId;
	private string? _orchestrationId;
	private string? _orchestrationName;
	private DateTimeOffset? _startedAt;
	private string? _runStatus;
	private string? _triggeredBy;
	private Dictionary<string, string>? _parameters;
	private JsonElement? _runContext;

	/// <summary>
	/// Creates a new SSE reporter with default options.
	/// </summary>
	public SseReporter() : this(null, new SseOptions(), NullLogger<SseReporter>.Instance)
	{
	}

	/// <summary>
	/// Creates a new SSE reporter.
	/// </summary>
	/// <param name="dashboardBroadcaster">Optional dashboard broadcaster that receives a
	/// fan-out of HITL lifecycle events (<c>awaiting-input</c>, <c>input-received</c>,
	/// <c>input-timeout</c>) so the Portal can show pending counts/lists without subscribing
	/// to every execution stream. Null in unit tests that don't care about dashboard fan-out.</param>
	public SseReporter(DashboardEventBroadcaster? dashboardBroadcaster)
		: this(dashboardBroadcaster, new SseOptions(), NullLogger<SseReporter>.Instance)
	{
	}

	/// <summary>
	/// Creates a new SSE reporter with explicit options and logger. Used by
	/// <see cref="SseReporterFactory"/> when constructed via DI.
	/// </summary>
	public SseReporter(
		DashboardEventBroadcaster? dashboardBroadcaster,
		SseOptions options,
		ILogger<SseReporter> logger)
	{
		ArgumentNullException.ThrowIfNull(options);
		_dashboardBroadcaster = dashboardBroadcaster;
		_logger = logger ?? NullLogger<SseReporter>.Instance;
		_maxAccumulatedEvents = options.MaxAccumulatedEvents > 0 ? options.MaxAccumulatedEvents : MaxAccumulatedEvents;
		_maxChannelCapacity = options.MaxChannelCapacity > 0 ? options.MaxChannelCapacity : MaxChannelCapacity;
		_maxSubscribers = options.MaxSubscribers > 0 ? options.MaxSubscribers : MaxSubscribers;
		_heartbeatInterval = options.HeartbeatInterval > TimeSpan.Zero ? options.HeartbeatInterval : TimeSpan.FromSeconds(20);
		_eventBuffer = new SseEvent[_maxAccumulatedEvents];
	}

	/// <summary>
	/// Maximum number of events in the circular buffer for this instance (resolved from
	/// <see cref="SseOptions.MaxAccumulatedEvents"/>).
	/// </summary>
	public int InstanceMaxAccumulatedEvents => _maxAccumulatedEvents;

	/// <summary>
	/// Heartbeat interval configured for this instance.
	/// </summary>
	public TimeSpan HeartbeatInterval => _heartbeatInterval;

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
	/// Highest sequence number written to the reporter so far (0 if none yet).
	/// </summary>
	public long LastEventSequence
	{
		get
		{
			lock (_lock)
			{
				return _nextSequence;
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
	/// Callback invoked when the engine publishes a completed step record (via
	/// <see cref="IOrchestrationReporter.PublishStepRecord"/>). Parameters: canonical key
	/// (step name or <c>stepName:iteration-N</c>) + the record. Wired by
	/// <see cref="ChildOrchestrationLauncher"/> to populate
	/// <c>ActiveExecutionInfo.PartialStepRecords</c>, enabling the data-plane
	/// <c>get_orchestration_step</c> MCP tool to serve mid-run step content.
	/// </summary>
	public Action<string, StepRunRecord>? OnStepRecorded { get; set; }

	/// <summary>
	/// Result of a subscription, including the authoritative state snapshot taken atomically
	/// with the replay capture so clients have a complete picture even if events were evicted.
	/// </summary>
	public readonly record struct SubscriptionResult(
		ExecutionStateSnapshot Snapshot,
		IReadOnlyList<SseEvent> Replay,
		ChannelReader<SseEvent>? Future,
		bool ReplayTruncated);

	/// <summary>
	/// Subscribes a new client. Returns the authoritative snapshot, the replay of events
	/// the client has not yet seen, and a future-events channel (null if the subscriber cap
	/// has been reached).
	/// </summary>
	/// <param name="lastEventId">If provided, only events with a higher sequence number are
	/// returned in <see cref="SubscriptionResult.Replay"/>. If the requested cursor is older
	/// than the oldest event still in the circular buffer, <see cref="SubscriptionResult.ReplayTruncated"/>
	/// is true and the client should rely on the snapshot for authoritative state.</param>
	public SubscriptionResult SubscribeWithSnapshot(long? lastEventId = null)
	{
		var channel = Channel.CreateBounded<SseEvent>(
			new BoundedChannelOptions(_maxChannelCapacity)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.DropOldest,
			});

		lock (_lock)
		{
			var snapshot = GetCurrentSnapshotLocked();
			var (replay, truncated) = GetReplaySinceLocked(lastEventId);

			if (_isCompleted)
			{
				channel.Writer.TryComplete();
				return new SubscriptionResult(snapshot, replay, channel.Reader, truncated);
			}

			if (_subscribers.Count >= _maxSubscribers)
			{
				LogSubscriberLimitReached(_logger, _executionId ?? "(unknown)", _subscribers.Count, _maxSubscribers);
				channel.Writer.TryComplete();
				return new SubscriptionResult(snapshot, replay, null, truncated);
			}

			_subscribers.Add(channel);
			return new SubscriptionResult(snapshot, replay, channel.Reader, truncated);
		}
	}

	/// <summary>
	/// Legacy subscription overload returning (replay, future) without the snapshot.
	/// Retained for backward compatibility with existing callers and tests; new code
	/// should prefer <see cref="SubscribeWithSnapshot"/>.
	/// </summary>
	public (IReadOnlyList<SseEvent> Replay, ChannelReader<SseEvent>? Future) Subscribe()
	{
		var result = SubscribeWithSnapshot();
		return (result.Replay, result.Future);
	}

	/// <summary>
	/// Returns the current authoritative snapshot of orchestration + per-step state.
	/// Safe to call at any time, including after the reporter has completed.
	/// </summary>
	public ExecutionStateSnapshot GetCurrentSnapshot()
	{
		lock (_lock)
		{
			return GetCurrentSnapshotLocked();
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
		// Heartbeats use sequence 0 to indicate they are not retained / cannot be resumed from.
		var evt = new SseEvent(0, "heartbeat", "{}");

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
			_stepStates.Clear();
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
			reasoningTokens = usage.ReasoningTokens,
			totalNanoAiu = usage.TotalNanoAiu,
			timeToFirstTokenMs = usage.TimeToFirstTokenMs,
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

	// ── Actor-aware overloads (used by the Portal to distinguish sub-agents) ──

	public void ReportContentDelta(string stepName, string chunk, ActorContext actor)
	{
		Write("content-delta", new { stepName, chunk, actor = ActorPayload(actor) });
	}

	public void ReportReasoningDelta(string stepName, string chunk, ActorContext actor)
	{
		Write("reasoning-delta", new { stepName, chunk, actor = ActorPayload(actor) });
	}

	public void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer, ActorContext actor)
	{
		Write("tool-started", new { stepName, toolName, arguments, mcpServer, actor = ActorPayload(actor) });
	}

	public void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error, ActorContext actor)
	{
		Write("tool-completed", new { stepName, toolName, success, result, error, actor = ActorPayload(actor) });
	}

	/// <summary>
	/// Returns a JSON-friendly payload for an <see cref="ActorContext"/>, or null when the
	/// event was emitted by the main agent. Keeping main-agent payloads slim preserves
	/// backward compatibility for older consumers and avoids redundant per-event noise.
	/// </summary>
	private static object? ActorPayload(ActorContext actor)
	{
		if (actor.IsMain)
			return null;

		return new
		{
			agentName = actor.AgentName,
			displayName = actor.AgentDisplayName,
			toolCallId = actor.ToolCallId,
			depth = actor.Depth,
		};
	}

	public void ReportStepError(string stepName, string errorMessage)
	{
		// completedAt stamp: clients use this on replay to compute the actual elapsed time of
		// the step. Without it they default to Date.now() and the duration appears reset to ~0
		// each time the user opened the execution view.
		Write("step-error", new
		{
			stepName,
			error = errorMessage,
			completedAt = DateTimeOffset.UtcNow.ToString("o"),
		});
	}

	/// <summary>
	/// Reports a step error with structured details from the agent's session-error
	/// payload (ErrorType / StatusCode / ProviderCallId / Url / Stack). When
	/// <paramref name="errorDetails"/> is non-null the details are emitted as a nested
	/// <c>errorDetails</c> object on the <c>step-error</c> SSE event so the Portal and
	/// any other consumer can render them (e.g. the GitHub request id for support
	/// escalations).
	/// </summary>
	public void ReportStepError(string stepName, string errorMessage, AgentSessionErrorDetails? errorDetails)
	{
		if (errorDetails is null)
		{
			ReportStepError(stepName, errorMessage);
			return;
		}

		Write("step-error", new
		{
			stepName,
			error = errorMessage,
			completedAt = DateTimeOffset.UtcNow.ToString("o"),
			errorDetails = new
			{
				errorType = errorDetails.ErrorType,
				statusCode = errorDetails.StatusCode,
				providerCallId = errorDetails.ProviderCallId,
				url = errorDetails.Url,
				stack = errorDetails.Stack,
			},
		});
	}

	/// <summary>
	/// Reports that a step was cancelled (not failed).
	/// </summary>
	public void ReportStepCancelled(string stepName)
	{
		Write("step-cancelled", new
		{
			stepName,
			completedAt = DateTimeOffset.UtcNow.ToString("o"),
		});
	}

	public void ReportStepCompleted(string stepName, AgentResult result, OrchestrationStepType stepType)
	{
		Write("step-completed", new
		{
			stepName,
			stepType = stepType.ToString().ToLowerInvariant(),
			actualModel = result.ActualModel,
			selectedModel = result.SelectedModel,
			requestedModelInfo = result.RequestedModelInfo,
			selectedModelInfo = result.SelectedModelInfo,
			actualModelInfo = result.ActualModelInfo,
			contentPreview = result.Content.Length > 500
				? result.Content[..500] + "..."
				: result.Content,
			// Stamp completion time on the event so replayed events render the correct
			// elapsed duration. The client previously used Date.now() at replay time, which
			// reset the apparent duration every time the user opened the execution view.
			completedAt = DateTimeOffset.UtcNow.ToString("o"),
		});
		OnStepCompleted?.Invoke(stepName);
	}

	public void ReportStepTrace(string stepName, StepExecutionTrace trace)
	{
		Write("step-trace", new
		{
			stepName,
			parameters = trace.Parameters.Count > 0 ? trace.Parameters : null,
			dependencyOutputs = trace.DependencyOutputs.Count > 0 ? trace.DependencyOutputs : null,
			rawDependencyOutputs = trace.RawDependencyOutputs.Count > 0 ? trace.RawDependencyOutputs : null,
			accessibleStepData = trace.AccessibleStepData.Count > 0 ? trace.AccessibleStepData : null,
			command = trace.Command,
			commandArguments = trace.Command is not null || trace.Shell is not null || trace.CommandArguments.Count > 0 ? trace.CommandArguments : null,
			shell = trace.Shell,
			scriptSource = trace.ScriptSource,
			workingDirectory = trace.WorkingDirectory,
			environment = trace.Environment.Count > 0 ? trace.Environment : null,
			stdin = trace.Stdin,
			configuredProvider = trace.ConfiguredProvider,
			actualProvider = trace.ActualProvider,
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
				completedAt = tc.CompletedAt?.ToString("o"),
				actor = tc.ActorAgentName is null
					? null
					: (object)new
					{
						agentName = tc.ActorAgentName,
						displayName = tc.ActorAgentDisplayName,
						toolCallId = tc.ActorToolCallId,
						depth = tc.ActorDepth,
					},
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
		// Stamp the start time on the event itself so late-attaching SSE clients can compute
		// elapsed time correctly on replay. Without this, the client used Date.now() when
		// the event arrived (i.e. when the modal was opened) and the apparent duration reset
		// to zero every time the user reopened the execution view.
		Write("step-started", new
		{
			stepName,
			startedAt = DateTimeOffset.UtcNow.ToString("o"),
		});
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

	public void ReportSavedFile(string stepName, string filePath)
	{
		Write("saved-file", new { stepName, filePath });
	}

	/// <summary>
	/// Forwards engine-published step records to the <see cref="OnStepRecorded"/> callback
	/// (which <see cref="Triggers.ChildOrchestrationLauncher"/> uses to populate the active
	/// execution's <c>PartialStepRecords</c>). Intentionally does NOT write an SSE event:
	/// the per-step deltas are already streamed via <c>step-completed</c>, and the full
	/// record would be too large for SSE clients.
	/// </summary>
	public void PublishStepRecord(string key, StepRunRecord record)
	{
		OnStepRecorded?.Invoke(key, record);
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
		Write("subagent-started", new
		{
			stepName,
			toolCallId,
			agentName,
			displayName,
			description,
			startedAt = DateTimeOffset.UtcNow.ToString("o"),
		});
	}

	public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName)
	{
		Write("subagent-completed", new
		{
			stepName,
			toolCallId,
			agentName,
			displayName,
			completedAt = DateTimeOffset.UtcNow.ToString("o"),
		});
	}

	public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error)
	{
		Write("subagent-failed", new
		{
			stepName,
			toolCallId,
			agentName,
			displayName,
			error,
			completedAt = DateTimeOffset.UtcNow.ToString("o"),
		});
	}

	public void ReportSubagentDeselected(string stepName)
	{
		Write("subagent-deselected", new { stepName });
	}

	public void ReportAuditLogEntry(string stepName, AuditLogEntry entry)
	{
		Write("audit-log", new
		{
			stepName,
			sequence = entry.Sequence,
			timestamp = entry.Timestamp.ToString("o"),
			eventType = entry.EventType.ToString(),
			toolName = entry.ToolName,
			toolArguments = entry.ToolArguments,
			permissionDecision = entry.PermissionDecision,
			toolResult = entry.ToolResult,
			toolSuccess = entry.ToolSuccess,
			prompt = entry.Prompt,
			error = entry.Error,
			errorContext = entry.ErrorContext,
			errorHandling = entry.ErrorHandling,
			additionalContext = entry.AdditionalContext,
			sessionSource = entry.SessionSource,
			sessionEndReason = entry.SessionEndReason,
			autoModeRequestId = entry.AutoModeRequestId,
			autoModeErrorCode = entry.AutoModeErrorCode,
			autoModeResponse = entry.AutoModeResponse,
			notificationKind = entry.NotificationKind,
			notificationMessage = entry.NotificationMessage,
			// SDK 1.0.0 per-call permission audit fields. Surfaced alongside the existing
			// PermissionDecision so Portal can render a single PermissionCompleted card
			// with the decision, reason, and the request id that ties it back to the
			// matching PermissionRequested entry. PermissionToolCallId lets the Portal
			// link the gate entry to its originating tool call entry.
			permissionRequestId = entry.PermissionRequestId,
			permissionKind = entry.PermissionKind,
			permissionTarget = entry.PermissionTarget,
			permissionToolCallId = entry.PermissionToolCallId,
			permissionDecisionReason = entry.PermissionDecisionReason,
		});
	}

	public void ReportStepStatusSet(string stepName, string status, string reason)
	{
		Write("step-status-set", new { stepName, status, reason });
	}

	// ── Human-in-the-loop ──

	public void ReportAwaitingInput(PendingInputRecord record)
	{
		Write("awaiting-input", new
		{
			orchestrationName = record.OrchestrationName,
			runId = record.RunId,
			stepName = record.StepName,
			kind = record.Kind.ToString(),
			prompt = record.Prompt,
			choices = record.Choices.Length > 0 ? record.Choices : null,
			createdAt = record.CreatedAt.ToString("o"),
			expiresAt = record.ExpiresAt?.ToString("o"),
		});

		// Fan-out to the dashboard so the Portal can update its "Waiting Inputs" list
		// without subscribing to every per-execution SSE stream.
		_dashboardBroadcaster?.BroadcastAwaitingInput(
			record.OrchestrationName,
			record.RunId,
			record.StepName,
			record.Kind.ToString(),
			record.Prompt,
			record.Choices,
			record.CreatedAt,
			record.ExpiresAt);
	}

	public void ReportInputReceived(string orchestrationName, string runId, string stepName, UserInputResponse response)
	{
		Write("input-received", new
		{
			orchestrationName,
			runId,
			stepName,
			choice = response.Choice,
			reply = response.Reply,
			respondedBy = response.RespondedBy,
			respondedAt = response.RespondedAt.ToString("o"),
		});

		_dashboardBroadcaster?.BroadcastInputReceived(
			orchestrationName,
			runId,
			stepName,
			response.Choice,
			response.Reply,
			response.RespondedBy,
			response.RespondedAt);
	}

	public void ReportInputTimeout(string orchestrationName, string runId, string stepName, ApprovalTimeoutBehavior onTimeout)
	{
		Write("input-timeout", new
		{
			orchestrationName,
			runId,
			stepName,
			onTimeout = onTimeout.ToString(),
		});

		_dashboardBroadcaster?.BroadcastInputTimeout(
			orchestrationName,
			runId,
			stepName,
			onTimeout.ToString());
	}

	// ── Auto-mode + system notifications + quota (SDK 0.3.0 telemetry) ──

	public void ReportAutoModeSwitchRequested(string stepName, string requestId, string? errorCode)
	{
		Write("auto-mode-switch-requested", new { stepName, requestId, errorCode });
	}

	public void ReportAutoModeSwitchCompleted(string stepName, string requestId, string? response)
	{
		Write("auto-mode-switch-completed", new { stepName, requestId, response });
	}

	public void ReportSystemNotification(string stepName, string kind, string? message)
	{
		Write("system-notification", new { stepName, kind, message });
	}

	public void ReportQuotaSnapshot(string stepName, IReadOnlyDictionary<string, AgentQuotaSnapshot> snapshots)
	{
		Write("quota-snapshot", new
		{
			stepName,
			snapshots = snapshots.Select(kv => new
			{
				name = kv.Key,
				entitlementRequests = kv.Value.EntitlementRequests,
				usedRequests = kv.Value.UsedRequests,
				remainingPercentage = kv.Value.RemainingPercentage,
				overage = kv.Value.Overage,
				isUnlimitedEntitlement = kv.Value.IsUnlimitedEntitlement,
				usageAllowedWithExhaustedQuota = kv.Value.UsageAllowedWithExhaustedQuota,
				overageAllowedWithExhaustedQuota = kv.Value.OverageAllowedWithExhaustedQuota,
				resetDate = kv.Value.ResetDate?.ToString("o"),
			}).ToArray(),
		});
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

	public void ReportHookExecuted(HookExecutionRecord hookExecution)
	{
		Write("hook-executed", new
		{
			hookName = hookExecution.HookName,
			eventType = hookExecution.EventType.ToString(),
			source = hookExecution.Source.ToString(),
			status = hookExecution.Status.ToString(),
			startedAt = hookExecution.StartedAt.ToString("o"),
			completedAt = hookExecution.CompletedAt.ToString("o"),
			durationSeconds = Math.Round(hookExecution.Duration.TotalSeconds, 2),
			stepName = hookExecution.StepName,
			errorMessage = hookExecution.ErrorMessage,
			content = hookExecution.Content,
			failurePolicy = hookExecution.FailurePolicy.ToString(),
			actionType = hookExecution.ActionType.ToString(),
		});
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
				savedFiles = kv.Value.SavedFiles.Length > 0 ? kv.Value.SavedFiles : null,
			});

		Write("orchestration-done", new
		{
			status = orchestrationResult.Status.ToString(),
			completionReason = orchestrationResult.CompletionReason,
			completedByStep = orchestrationResult.CompletedByStep,
			isIncomplete = orchestrationResult.IsIncomplete,
			cancellation = orchestrationResult.Cancellation is { } cancel ? new
			{
				kind = cancel.Kind.ToString(),
				timeoutSeconds = cancel.TimeoutSeconds,
				source = cancel.Source,
				detail = cancel.Detail,
				reason = cancel.Reason,
				isTimeout = cancel.IsTimeout,
			} : null,
			savedFiles = orchestrationResult.SavedFiles.Length > 0 ? orchestrationResult.SavedFiles : null,
			results,
		});
	}

	/// <summary>
	/// Reports that the orchestration was cancelled.
	/// </summary>
	public void ReportOrchestrationCancelled()
	{
		Write("orchestration-cancelled", new { status = HostExecutionStatus.Cancelled });
	}

	/// <summary>
	/// Reports that the orchestration was cancelled with a structured cause (timeout, caller, etc).
	/// </summary>
	public void ReportOrchestrationCancelled(CancellationDetails cancellation)
	{
		Write("orchestration-cancelled", new
		{
			status = HostExecutionStatus.Cancelled,
			cancellation = new
			{
				kind = cancellation.Kind.ToString(),
				timeoutSeconds = cancellation.TimeoutSeconds,
				source = cancellation.Source,
				detail = cancellation.Detail,
				reason = cancellation.Reason,
				isTimeout = cancellation.IsTimeout,
				requestedAt = cancellation.RequestedAt,
				progress = cancellation.Progress is null ? null : new
				{
					totalSteps = cancellation.Progress.TotalSteps,
					stepsCompleted = cancellation.Progress.StepsCompleted,
					stepsCancelled = cancellation.Progress.StepsCancelled,
					stepsFailed = cancellation.Progress.StepsFailed,
					stepsSkippedOrNoAction = cancellation.Progress.StepsSkippedOrNoAction,
					stepsNotStarted = cancellation.Progress.StepsNotStarted,
					lastCompletedStep = cancellation.Progress.LastCompletedStep,
					lastCompletedAt = cancellation.Progress.LastCompletedAt,
					cancelledSteps = cancellation.Progress.CancelledSteps,
				},
			},
		});
	}

	/// <summary>
	/// Reports that the orchestration failed with an error.
	/// </summary>
	public void ReportOrchestrationError(string errorMessage)
	{
		Write("orchestration-error", new { status = HostExecutionStatus.Failed, error = errorMessage });
	}

	/// <summary>
	/// Reports a status change for the orchestration (e.g., <see cref="HostExecutionStatus.Cancelling"/>).
	/// </summary>
	public void ReportStatusChange(HostExecutionStatus status)
	{
		Write("status-changed", new { status });
	}

	/// <summary>
	/// Records execution-started metadata on the reporter. Called by
	/// <see cref="ExecutionApi"/> when the SSE <c>execution-started</c> frame is emitted
	/// outside the reporter (the reporter doesn't naturally see that event). The data is
	/// folded into the authoritative snapshot so attach clients see it without
	/// depending on the replay buffer.
	/// </summary>
	public void SetExecutionContext(
		string executionId,
		string? orchestrationId,
		string? orchestrationName,
		DateTimeOffset? startedAt,
		string? triggeredBy,
		Dictionary<string, string>? parameters)
	{
		lock (_lock)
		{
			_executionId = executionId;
			_orchestrationId = orchestrationId;
			_orchestrationName = orchestrationName;
			_startedAt = startedAt;
			_triggeredBy = triggeredBy;
			_parameters = parameters is null ? null : new Dictionary<string, string>(parameters);
			_runStatus ??= "Running";
		}
	}

	private void Write(string eventType, object data)
	{
		var json = JsonSerializer.Serialize(data, s_jsonOptions);

		lock (_lock)
		{
			if (_isCompleted || _disposed)
				return;

			var sequence = ++_nextSequence;
			var evt = new SseEvent(sequence, eventType, json);

			// Update authoritative state BEFORE evicting from the buffer so the snapshot
			// always reflects the latest event even if the buffer overwrites the oldest one.
			ApplyEventToStateLocked(eventType, json);

			// Add to circular buffer (oldest is overwritten on wrap)
			var writeIndex = (_eventHead + _eventCount) % _maxAccumulatedEvents;

			if (_eventCount < _maxAccumulatedEvents)
			{
				_eventBuffer[writeIndex] = evt;
				_eventCount++;
				if (_eventCount == 1)
					_firstAvailableSequence = sequence;
			}
			else
			{
				// Buffer is full — the slot we're about to overwrite holds the current oldest event.
				var evicted = _eventBuffer[_eventHead];
				if (evicted is not null && s_importantEventTypes.Contains(evicted.Type))
				{
					LogImportantEventEvicted(_logger, _executionId ?? "(unknown)", evicted.Type, evicted.Sequence, _maxAccumulatedEvents);
				}
				_eventBuffer[_eventHead] = evt;
				_eventHead = (_eventHead + 1) % _maxAccumulatedEvents;
				_firstAvailableSequence = _eventBuffer[_eventHead]!.Sequence;
			}

			foreach (var channel in _subscribers)
			{
				// TryWrite on bounded channel with DropOldest will always succeed.
				// We don't log per-event drops because that itself can spam under load —
				// the snapshot recovery path makes per-event drops survivable.
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
			var index = (_eventHead + i) % _maxAccumulatedEvents;
			result.Add(_eventBuffer[index]);
		}
		return result;
	}

	/// <summary>
	/// Returns events with sequence number greater than <paramref name="lastEventId"/>,
	/// or all accumulated events when null. Also reports whether the requested cursor
	/// fell off the buffer (truncated = client missed events that aren't in the buffer
	/// anymore; they must rely on the snapshot for state).
	/// </summary>
	private (List<SseEvent> Replay, bool Truncated) GetReplaySinceLocked(long? lastEventId)
	{
		if (lastEventId is null || _eventCount == 0)
		{
			return (GetAccumulatedEventsLocked(), false);
		}

		var cursor = lastEventId.Value;

		// If the requested cursor is older than the oldest event still in the buffer,
		// we can't faithfully resume — flag truncated.
		var truncated = cursor < _firstAvailableSequence - 1;

		var result = new List<SseEvent>();
		for (var i = 0; i < _eventCount; i++)
		{
			var index = (_eventHead + i) % _maxAccumulatedEvents;
			var evt = _eventBuffer[index];
			if (evt!.Sequence > cursor)
				result.Add(evt);
		}
		return (result, truncated);
	}

	private ExecutionStateSnapshot GetCurrentSnapshotLocked()
	{
		var steps = new Dictionary<string, StepStateSnapshot>(_stepStates.Count, StringComparer.Ordinal);
		foreach (var (name, mutable) in _stepStates)
		{
			steps[name] = mutable.ToImmutable();
		}

		return new ExecutionStateSnapshot
		{
			ExecutionId = _executionId,
			OrchestrationId = _orchestrationId,
			OrchestrationName = _orchestrationName,
			StartedAt = _startedAt,
			Status = _runStatus,
			TriggeredBy = _triggeredBy,
			Parameters = _parameters is null
				? null
				: new Dictionary<string, string>(_parameters),
			RunContext = _runContext,
			Steps = steps,
			LastEventSequence = _nextSequence,
			IsCompleted = _isCompleted,
		};
	}

	/// <summary>
	/// Updates the authoritative state for the given event. Must be called under _lock.
	/// Designed to be idempotent: applying the same event twice yields the same state.
	/// </summary>
	private void ApplyEventToStateLocked(string eventType, string json)
	{
		// Parse lazily — many event types we don't fold into the snapshot.
		// Importantly, we never throw out of this method (failures must not break Write()).
		try
		{
			switch (eventType)
			{
				case "run-context":
					{
						using var doc = JsonDocument.Parse(json);
						_runContext = doc.RootElement.Clone();
					}
					break;

				case "status-changed":
					{
						using var doc = JsonDocument.Parse(json);
						if (doc.RootElement.TryGetProperty("status", out var s))
							_runStatus = s.ValueKind == JsonValueKind.String ? s.GetString() : s.ToString();
					}
					break;

				case "step-started":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.Status = "running";
						state.StartedAt ??= TryGetDateTime(doc.RootElement, "startedAt") ?? DateTimeOffset.UtcNow;
					}
					break;

				case "step-completed":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.Status = "completed";
						state.CompletedAt = TryGetDateTime(doc.RootElement, "completedAt") ?? DateTimeOffset.UtcNow;
						state.ContentPreview = GetString(doc.RootElement, "contentPreview");
						state.ActualModel = GetString(doc.RootElement, "actualModel");
						state.SelectedModel = GetString(doc.RootElement, "selectedModel");
					}
					break;

				case "step-error":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.Status = "failed";
						state.Error = GetString(doc.RootElement, "error");
						state.CompletedAt = TryGetDateTime(doc.RootElement, "completedAt") ?? DateTimeOffset.UtcNow;
					}
					break;

				case "step-cancelled":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.Status = "cancelled";
						state.CompletedAt = TryGetDateTime(doc.RootElement, "completedAt") ?? DateTimeOffset.UtcNow;
					}
					break;

				case "step-skipped":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.Status = "skipped";
					}
					break;

				case "step-status-set":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var status = GetString(doc.RootElement, "status");
						if (status is null) break;
						var state = GetOrAddStep(stepName);
						state.Status = status.ToLowerInvariant();
					}
					break;

				case "step-retry":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.RetryCount++;
					}
					break;

				case "step-trace":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.Trace = doc.RootElement.Clone();
						state.ConfiguredProvider = GetString(doc.RootElement, "configuredProvider") ?? state.ConfiguredProvider;
						state.ActualProvider = GetString(doc.RootElement, "actualProvider") ?? state.ActualProvider;
					}
					break;

				case "step-output":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var content = GetString(doc.RootElement, "content");
						if (content is null) break;
						var state = GetOrAddStep(stepName);
						state.Output = content.Length > MaxSnapshotStepOutputLength
							? content[..MaxSnapshotStepOutputLength]
							: content;
					}
					break;

				case "saved-file":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						var filePath = GetString(doc.RootElement, "filePath");
						if (stepName is null || filePath is null) break;
						var state = GetOrAddStep(stepName);
						if (!state.SavedFiles.Contains(filePath))
							state.SavedFiles.Add(filePath);
					}
					break;

				case "audit-log":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.AuditEntries.Add(doc.RootElement.Clone());
						if (state.AuditEntries.Count > MaxSnapshotAuditEntriesPerStep)
							state.AuditEntries.RemoveAt(0);
					}
					break;

				case "usage":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.ActualModel ??= GetString(doc.RootElement, "model");
					}
					break;

				case "model-mismatch":
					{
						using var doc = JsonDocument.Parse(json);
						// model-mismatch doesn't carry a step name; ignore for snapshot.
					}
					break;

				case "subagent-started":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						state.ActiveSubagents++;
					}
					break;

				case "subagent-completed":
				case "subagent-failed":
					{
						using var doc = JsonDocument.Parse(json);
						var stepName = GetString(doc.RootElement, "stepName");
						if (stepName is null) break;
						var state = GetOrAddStep(stepName);
						if (state.ActiveSubagents > 0)
							state.ActiveSubagents--;
					}
					break;

				case "orchestration-done":
					{
						using var doc = JsonDocument.Parse(json);
						_runStatus = GetString(doc.RootElement, "status") ?? _runStatus;

						// Reconcile per-step statuses from the final result so steps that
						// haven't been individually reported land in the snapshot.
						if (doc.RootElement.TryGetProperty("results", out var results)
							&& results.ValueKind == JsonValueKind.Object)
						{
							foreach (var entry in results.EnumerateObject())
							{
								var stepName = entry.Name;
								if (string.IsNullOrEmpty(stepName)) continue;
								var state = GetOrAddStep(stepName);
								var status = GetString(entry.Value, "status");
								if (status is not null)
									state.Status = NormalizeFinalStatus(status);
								state.Error ??= GetString(entry.Value, "error");
								state.ContentPreview ??= GetString(entry.Value, "contentPreview");
							}
						}
					}
					break;

				case "orchestration-cancelled":
					_runStatus = "Cancelled";
					break;

				case "orchestration-error":
					_runStatus = "Failed";
					break;
			}
		}
		catch (JsonException)
		{
			// Be defensive: a malformed event must never break the reporter.
		}
	}

	private MutableStepState GetOrAddStep(string stepName)
	{
		if (!_stepStates.TryGetValue(stepName, out var state))
		{
			state = new MutableStepState { StepName = stepName, Status = "pending" };
			_stepStates[stepName] = state;
		}
		return state;
	}

	private static string? GetString(JsonElement el, string property)
	{
		return el.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
			? p.GetString()
			: null;
	}

	private static DateTimeOffset? TryGetDateTime(JsonElement el, string property)
	{
		if (!el.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.String)
			return null;
		var s = p.GetString();
		if (s is null) return null;
		return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
			out var parsed) ? parsed : null;
	}

	/// <summary>
	/// Maps engine ExecutionStatus strings to the lower-case status strings the UI uses.
	/// </summary>
	private static string NormalizeFinalStatus(string status) => status switch
	{
		"Succeeded" => "completed",
		"Failed" => "failed",
		"Cancelled" => "cancelled",
		"Skipped" => "skipped",
		"NoAction" => "noaction",
		"CompletedEarly" => "completed_early",
		_ => status.ToLowerInvariant(),
	};

	// ── Logging (LoggerMessage codegen) ──

	[LoggerMessage(
		EventId = 9101,
		Level = LogLevel.Warning,
		Message = "SSE replay buffer evicted important event '{EventType}' (seq {Sequence}) for execution '{ExecutionId}'. Consider raising Sse:MaxAccumulatedEvents (currently {MaxAccumulatedEvents}). Snapshot recovery keeps the UI correct but streaming history before this point is lost.")]
	private static partial void LogImportantEventEvicted(
		ILogger logger,
		string executionId,
		string eventType,
		long sequence,
		int maxAccumulatedEvents);

	[LoggerMessage(
		EventId = 9102,
		Level = LogLevel.Warning,
		Message = "SSE subscriber limit reached for execution '{ExecutionId}': {CurrentSubscribers}/{MaxSubscribers}. New subscriber received snapshot+replay but no future events.")]
	private static partial void LogSubscriberLimitReached(
		ILogger logger,
		string executionId,
		int currentSubscribers,
		int maxSubscribers);

	// ── Internal mutable state object for per-step bookkeeping ──

	private sealed class MutableStepState
	{
		public required string StepName { get; init; }
		public string Status { get; set; } = "pending";
		public DateTimeOffset? StartedAt { get; set; }
		public DateTimeOffset? CompletedAt { get; set; }
		public string? Error { get; set; }
		public string? Output { get; set; }
		public string? ContentPreview { get; set; }
		public JsonElement? Trace { get; set; }
		public List<string> SavedFiles { get; } = [];
		public List<JsonElement> AuditEntries { get; } = [];
		public string? RequestedModel { get; set; }
		public string? SelectedModel { get; set; }
		public string? ActualModel { get; set; }
		public string? ConfiguredProvider { get; set; }
		public string? ActualProvider { get; set; }
		public int ActiveSubagents { get; set; }
		public int RetryCount { get; set; }

		public StepStateSnapshot ToImmutable() => new()
		{
			StepName = StepName,
			Status = Status,
			StartedAt = StartedAt,
			CompletedAt = CompletedAt,
			Error = Error,
			Output = Output,
			ContentPreview = ContentPreview,
			Trace = Trace,
			SavedFiles = SavedFiles.ToArray(),
			AuditEntries = AuditEntries.ToArray(),
			RequestedModel = RequestedModel,
			SelectedModel = SelectedModel,
			ActualModel = ActualModel,
			ConfiguredProvider = ConfiguredProvider,
			ActualProvider = ActualProvider,
			ActiveSubagents = ActiveSubagents,
			RetryCount = RetryCount,
		};
	}
}
