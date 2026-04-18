using System.Text;
using Orchestra.Engine;

namespace Orchestra.Playground.Copilot.Terminal;

/// <summary>
/// An IOrchestrationReporter that tracks events for the Terminal UI.
/// Raises events that the TUI can subscribe to for live updates.
/// Also accumulates streaming content/reasoning deltas per step for real-time display.
/// </summary>
public class TerminalOrchestrationReporter : IOrchestrationReporter
{
	private readonly object _lock = new();
	private readonly List<ReporterEvent> _events = new();

	// Streaming delta accumulators (per step name)
	private readonly Dictionary<string, StringBuilder> _streamingContent = new();
	private readonly Dictionary<string, StringBuilder> _streamingReasoning = new();
	private string? _currentStreamingStep;
	private DateTime _lastDeltaTime;

	/// <summary>
	/// Event raised when any report is received.
	/// </summary>
	public event Action? OnUpdate;

	/// <summary>
	/// Event raised specifically when streaming content arrives. Allows the TUI
	/// to refresh at a higher frequency for the streaming view without flooding
	/// the general event list.
	/// </summary>
	public event Action? OnStreamingUpdate;

	/// <summary>
	/// Callback invoked when a step starts.
	/// </summary>
	public Action<string>? OnStepStarted { get; set; }

	/// <summary>
	/// Callback invoked when a step completes.
	/// </summary>
	public Action<string>? OnStepCompleted { get; set; }

	/// <summary>
	/// Gets a snapshot of all events.
	/// </summary>
	public IReadOnlyList<ReporterEvent> GetEvents()
	{
		lock (_lock)
		{
			return _events.ToList();
		}
	}

	/// <summary>
	/// Gets the accumulated streaming content for a specific step.
	/// Returns null if no content has been streamed for that step.
	/// </summary>
	public string? GetStreamingContent(string stepName)
	{
		lock (_lock)
		{
			return _streamingContent.TryGetValue(stepName, out var sb) ? sb.ToString() : null;
		}
	}

	/// <summary>
	/// Gets the accumulated streaming reasoning for a specific step.
	/// Returns null if no reasoning has been streamed for that step.
	/// </summary>
	public string? GetStreamingReasoning(string stepName)
	{
		lock (_lock)
		{
			return _streamingReasoning.TryGetValue(stepName, out var sb) ? sb.ToString() : null;
		}
	}

	/// <summary>
	/// Gets the name of the step that is currently receiving streaming content,
	/// or null if no step is currently streaming.
	/// </summary>
	public string? CurrentStreamingStep
	{
		get { lock (_lock) return _currentStreamingStep; }
	}

	/// <summary>
	/// Gets a snapshot of all step names that have accumulated streaming content.
	/// </summary>
	public IReadOnlyList<string> GetStreamingStepNames()
	{
		lock (_lock)
		{
			return _streamingContent.Keys.ToList();
		}
	}

	/// <summary>
	/// Returns the time of the most recent streaming delta, for UI throttling decisions.
	/// </summary>
	public DateTime LastDeltaTime
	{
		get { lock (_lock) return _lastDeltaTime; }
	}

	/// <summary>
	/// Clears all events and streaming accumulators.
	/// </summary>
	public void Clear()
	{
		lock (_lock)
		{
			_events.Clear();
			_streamingContent.Clear();
			_streamingReasoning.Clear();
			_currentStreamingStep = null;
		}
	}

	private void AddEvent(ReporterEvent evt)
	{
		lock (_lock)
		{
			_events.Add(evt);
			// Keep only last 100 events
			if (_events.Count > 100)
			{
				_events.RemoveAt(0);
			}
		}
		OnUpdate?.Invoke();
	}

	public void ReportSessionStarted(string requestedModel, string? selectedModel)
	{
		AddEvent(new ReporterEvent("session-started", $"Model: {selectedModel ?? requestedModel}"));
	}

	public void ReportModelChange(string? previousModel, string newModel)
	{
		AddEvent(new ReporterEvent("model-change", $"{previousModel} -> {newModel}"));
	}

	public void ReportUsage(string stepName, string model, AgentUsage usage)
	{
		AddEvent(new ReporterEvent("usage", $"[{stepName}] Tokens: in={usage.InputTokens}, out={usage.OutputTokens}"));
	}

	public void ReportContentDelta(string stepName, string chunk)
	{
		lock (_lock)
		{
			if (!_streamingContent.TryGetValue(stepName, out var sb))
			{
				sb = new StringBuilder();
				_streamingContent[stepName] = sb;
			}
			sb.Append(chunk);
			_currentStreamingStep = stepName;
			_lastDeltaTime = DateTime.Now;
		}
		// Don't add to the event list (would flood), but raise the streaming-specific event
		OnStreamingUpdate?.Invoke();
	}

	public void ReportReasoningDelta(string stepName, string chunk)
	{
		lock (_lock)
		{
			if (!_streamingReasoning.TryGetValue(stepName, out var sb))
			{
				sb = new StringBuilder();
				_streamingReasoning[stepName] = sb;
			}
			sb.Append(chunk);
			_currentStreamingStep = stepName;
			_lastDeltaTime = DateTime.Now;
		}
		// Don't add to the event list (would flood), but raise the streaming-specific event
		OnStreamingUpdate?.Invoke();
	}

	public void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer)
	{
		var server = mcpServer != null ? $" ({mcpServer})" : "";
		AddEvent(new ReporterEvent("tool-started", $"[{stepName}] {toolName}{server}"));
	}

	public void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error)
	{
		var status = success ? "completed" : $"failed: {error}";
		AddEvent(new ReporterEvent("tool-completed", $"[{stepName}] {toolName} {status}"));
	}

	public void ReportStepError(string stepName, string errorMessage)
	{
		AddEvent(new ReporterEvent("step-error", $"[{stepName}] {errorMessage}"));
	}

	public void ReportStepCancelled(string stepName)
	{
		AddEvent(new ReporterEvent("step-cancelled", $"[{stepName}] Cancelled"));
	}

	public void ReportStepCompleted(string stepName, AgentResult result, OrchestrationStepType stepType)
	{
		lock (_lock)
		{
			// Clear the current streaming step marker when the step finishes
			if (_currentStreamingStep == stepName)
			{
				_currentStreamingStep = null;
			}
		}
		var modelInfo = result.ActualModel is not null
			? $"model: {result.ActualModel}"
			: stepType.ToString().ToLowerInvariant();
		AddEvent(new ReporterEvent("step-completed", $"[{stepName}] Completed ({modelInfo})"));
		OnStepCompleted?.Invoke(stepName);
	}

	public void ReportModelMismatch(ModelMismatchInfo mismatch)
	{
		AddEvent(new ReporterEvent("model-mismatch", $"Configured: {mismatch.ConfiguredModel}, Actual: {mismatch.ActualModel}"));
	}

	public void ReportStepOutput(string stepName, string content)
	{
		var preview = content.Length > 100 ? content[..100] + "..." : content;
		AddEvent(new ReporterEvent("step-output", $"[{stepName}] {preview}"));
	}

	public void ReportStepStarted(string stepName)
	{
		AddEvent(new ReporterEvent("step-started", $"[{stepName}] Starting..."));
		OnStepStarted?.Invoke(stepName);
	}

	public void ReportStepSkipped(string stepName, string reason)
	{
		AddEvent(new ReporterEvent("step-skipped", $"[{stepName}] Skipped: {reason}"));
	}

	public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations)
	{
		AddEvent(new ReporterEvent("loop-iteration", $"[{checkerStepName}] Loop {iteration}/{maxIterations} -> {targetStepName}"));
	}

	public void ReportStepRetry(string stepName, int attempt, int maxRetries, string error, TimeSpan delay)
	{
		AddEvent(new ReporterEvent("step-retry", $"[{stepName}] Retry {attempt}/{maxRetries}: {error}. Waiting {delay.TotalSeconds:F1}s..."));
	}

	public void ReportCheckpointSaved(string runId, string stepName, int completedSteps, int totalSteps)
	{
		AddEvent(new ReporterEvent("checkpoint-saved", $"Checkpoint saved after '{stepName}' ({completedSteps}/{totalSteps}) — run {runId}"));
	}

	public void ReportSessionWarning(string warningType, string message)
	{
		AddEvent(new ReporterEvent("session-warning", $"[{warningType}] {message}"));
	}

	public void ReportSessionInfo(string infoType, string message)
	{
		AddEvent(new ReporterEvent("session-info", $"[{infoType}] {message}"));
	}

	public void ReportMcpServersLoaded(IReadOnlyList<McpServerStatusInfo> servers)
	{
		var summary = string.Join(", ", servers.Select(s =>
		{
			var err = s.Error is not null ? $" ({s.Error})" : "";
			return $"{s.Name}={s.Status}{err}";
		}));
		AddEvent(new ReporterEvent("mcp-servers-loaded", $"MCP servers: {summary}"));
	}

	public void ReportMcpServerStatusChanged(string serverName, string status)
	{
		AddEvent(new ReporterEvent("mcp-server-status-changed", $"MCP '{serverName}' -> {status}"));
	}

	public void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools)
	{
		var name = displayName ?? agentName;
		var toolList = tools != null ? string.Join(", ", tools) : "all";
		AddEvent(new ReporterEvent("subagent-selected", $"[{stepName}] Selected: {name} (tools: {toolList})"));
	}

	public void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description)
	{
		var name = displayName ?? agentName;
		AddEvent(new ReporterEvent("subagent-started", $"[{stepName}] Subagent started: {name}"));
	}

	public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName)
	{
		var name = displayName ?? agentName;
		AddEvent(new ReporterEvent("subagent-completed", $"[{stepName}] Subagent completed: {name}"));
	}

	public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error)
	{
		var name = displayName ?? agentName;
		AddEvent(new ReporterEvent("subagent-failed", $"[{stepName}] Subagent failed: {name} - {error}"));
	}

	public void ReportSubagentDeselected(string stepName)
	{
		AddEvent(new ReporterEvent("subagent-deselected", $"[{stepName}] Returned to parent agent"));
	}

	public void ReportStepTrace(string stepName, StepExecutionTrace trace)
	{
		// Don't log full traces to event list; they are available via the run record
	}

	public void ReportRunContext(RunContext context)
	{
		AddEvent(new ReporterEvent("run-context", $"Run {context.RunId} — {context.OrchestrationName} v{context.OrchestrationVersion}"));
	}

	public void ReportAuditLogEntry(string stepName, AuditLogEntry entry)
	{
		// No-op for terminal reporter
	}
}

/// <summary>
/// A single reporter event.
/// </summary>
public record ReporterEvent(string Type, string Message, DateTimeOffset Timestamp = default)
{
	public DateTimeOffset Timestamp { get; init; } = Timestamp == default ? DateTimeOffset.Now : Timestamp;
}
