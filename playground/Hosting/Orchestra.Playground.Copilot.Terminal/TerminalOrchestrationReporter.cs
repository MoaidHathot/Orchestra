using Orchestra.Engine;

namespace Orchestra.Playground.Copilot.Terminal;

/// <summary>
/// An IOrchestrationReporter that tracks events for the Terminal UI.
/// Raises events that the TUI can subscribe to for live updates.
/// </summary>
public class TerminalOrchestrationReporter : IOrchestrationReporter
{
	private readonly object _lock = new();
	private readonly List<ReporterEvent> _events = new();

	/// <summary>
	/// Event raised when any report is received.
	/// </summary>
	public event Action? OnUpdate;

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
	/// Clears all events.
	/// </summary>
	public void Clear()
	{
		lock (_lock)
		{
			_events.Clear();
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
		// Don't log individual deltas to avoid flooding
	}

	public void ReportReasoningDelta(string stepName, string chunk)
	{
		// Don't log individual deltas
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

	public void ReportStepCompleted(string stepName, AgentResult result)
	{
		AddEvent(new ReporterEvent("step-completed", $"[{stepName}] Completed (model: {result.ActualModel})"));
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

	public void ReportStepTrace(string stepName, StepExecutionTrace trace)
	{
		// Don't log full traces
	}
}

/// <summary>
/// A single reporter event.
/// </summary>
public record ReporterEvent(string Type, string Message, DateTimeOffset Timestamp = default)
{
	public DateTimeOffset Timestamp { get; init; } = Timestamp == default ? DateTimeOffset.Now : Timestamp;
}
