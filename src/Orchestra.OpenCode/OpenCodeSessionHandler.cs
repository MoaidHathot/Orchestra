using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// Translates OpenCode <c>GET /event</c> bus events for a single session into engine-neutral
/// <see cref="AgentEvent"/>s and accumulates the final assistant content, model, and token
/// usage. Plays the role <c>CopilotSessionHandler</c> plays for the Copilot SDK. All OpenCode
/// wire-shape interpretation lives here so it can be unit-tested from JSON fixtures.
///
/// Threading: <see cref="Handle"/> is invoked sequentially from the agent's single SSE read
/// loop; internal state is not synchronized.
/// </summary>
internal sealed partial class OpenCodeSessionHandler
{
	private readonly string _sessionId;
	private readonly ChannelWriter<AgentEvent> _writer;
	private readonly IOrchestrationReporter _reporter;
	private readonly string _requestedModel;
	private readonly TaskCompletionSource _done;
	private readonly ILogger _logger;

	// Per-part latest full text (OpenCode resends the growing text each update; we emit the suffix delta).
	private readonly Dictionary<string, string> _textParts = [];
	private readonly List<string> _textPartOrder = [];
	private readonly Dictionary<string, string> _reasoningParts = [];
	private readonly HashSet<string> _toolStarted = [];
	private readonly HashSet<string> _toolCompleted = [];

	// Sub-agent invocations opened via the built-in "task" tool, keyed by the task tool-call id.
	// OpenCode runs each sub-agent in a separate child session whose events are filtered out in
	// Handle(), so the invocation's name/description captured at start is remembered here to
	// attribute the completion output (the sub-agent's visible result) to the right sub-agent.
	private readonly Dictionary<string, SubagentInvocation> _subagents = [];

	private sealed record SubagentInvocation(string? AgentName, string? Description);

	private string? _actualModel;
	private AgentUsage? _usage;

	public OpenCodeSessionHandler(
		string sessionId,
		ChannelWriter<AgentEvent> writer,
		IOrchestrationReporter reporter,
		string requestedModel,
		TaskCompletionSource done,
		ILogger logger)
	{
		_sessionId = sessionId;
		_writer = writer;
		_reporter = reporter;
		_requestedModel = requestedModel;
		_done = done;
		_logger = logger;
	}

	/// <summary>
	/// The assistant's final text, assembled from all text parts in arrival order. OpenCode
	/// streams growing per-part text, so the final value is the concatenation of each text
	/// part's latest content.
	/// </summary>
	public string? FinalContent => _textPartOrder.Count > 0
		? string.Concat(_textPartOrder.Select(id => _textParts[id]))
		: null;

	public string? ActualModel => _actualModel;
	public AgentUsage? Usage => _usage;

	public void Handle(OpenCodeServerEvent evt)
	{
		// Ignore events that belong to a different session on the same (possibly shared) instance.
		var sessionId = TryGetSessionId(evt);
		if (sessionId is not null && !string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
			return;

		switch (evt.Type)
		{
			case "message.part.updated":
				HandlePartUpdated(evt.Properties);
				break;
			case "message.updated":
				HandleMessageUpdated(evt.Properties);
				break;
			case "session.idle":
				_done.TrySetResult();
				break;
			case "session.error":
				HandleSessionError(evt.Properties);
				break;
			case "permission.updated":
				HandlePermissionRequested(evt.Properties);
				break;
			default:
				// server.connected, message.part.removed, session.updated, file.edited, lsp.*, … ignored.
				break;
		}
	}

	private void HandlePartUpdated(JsonElement properties)
	{
		if (!properties.TryGetProperty("part", out var part) || part.ValueKind != JsonValueKind.Object)
			return;

		var type = GetString(part, "type");
		switch (type)
		{
			case "text":
				EmitTextDelta(part, AgentEventType.MessageDelta, _textParts, _textPartOrder);
				break;
			case "reasoning":
				EmitTextDelta(part, AgentEventType.ReasoningDelta, _reasoningParts, order: null);
				break;
			case "tool":
				HandleToolPart(part);
				break;
			default:
				break; // step-start / step-finish / file / snapshot — no engine event needed
		}
	}

	private void EmitTextDelta(JsonElement part, AgentEventType eventType, Dictionary<string, string> store, List<string>? order)
	{
		var partId = GetString(part, "id") ?? GetString(part, "partID") ?? Guid.NewGuid().ToString("N");
		var text = GetString(part, "text") ?? string.Empty;

		var previous = store.TryGetValue(partId, out var p) ? p : string.Empty;
		if (order is not null && !store.ContainsKey(partId))
			order.Add(partId);
		store[partId] = text;

		// OpenCode resends the full growing text; emit only the newly-appended suffix.
		var delta = text.Length >= previous.Length && text.StartsWith(previous, StringComparison.Ordinal)
			? text[previous.Length..]
			: text;
		if (delta.Length == 0)
			return;

		Emit(new AgentEvent { Type = eventType, Content = delta });
	}

	private void HandleToolPart(JsonElement part)
	{
		var callId = GetString(part, "callID") ?? GetString(part, "id");
		var toolName = GetString(part, "tool");
		if (callId is null)
			return;

		var status = part.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object
			? GetString(state, "status")
			: null;

		// OpenCode delegates to a sub-agent via the built-in "task" tool; surface those as
		// Subagent* events (instead of generic tool events) for parity with the Copilot adapter.
		if (string.Equals(toolName, "task", StringComparison.OrdinalIgnoreCase))
		{
			HandleSubagentTask(callId, state, status);
			return;
		}

		switch (status)
		{
			case "running" or "pending":
				if (_toolStarted.Add(callId))
				{
					var args = state.TryGetProperty("input", out var input) ? RawJson(input) : null;
					Emit(new AgentEvent
					{
						Type = AgentEventType.ToolExecutionStart,
						ToolCallId = callId,
						ToolName = toolName,
						ToolArguments = args,
					});
				}
				break;
			case "completed" or "error":
				if (_toolCompleted.Add(callId))
				{
					var success = status == "completed";
					var output = state.TryGetProperty("output", out var o) ? o.GetString() ?? RawJson(o) : null;
					var error = state.TryGetProperty("error", out var e) ? (e.GetString() ?? RawJson(e)) : null;
					Emit(new AgentEvent
					{
						Type = AgentEventType.ToolExecutionComplete,
						ToolCallId = callId,
						ToolName = toolName,
						ToolSuccess = success,
						ToolResult = output,
						ToolError = success ? null : error,
					});
				}
				break;
		}
	}

	private void HandleSubagentTask(string callId, JsonElement state, string? status)
	{
		switch (status)
		{
			case "running" or "pending":
				if (_toolStarted.Add(callId))
				{
					var (name, description) = ExtractSubagentInfo(state);
					_subagents[callId] = new SubagentInvocation(name, description);
					Emit(new AgentEvent
					{
						Type = AgentEventType.SubagentStarted,
						ToolCallId = callId,
						SubagentName = name,
						SubagentDescription = description,
					});
				}
				break;
			case "completed" or "error":
				if (_toolCompleted.Add(callId))
				{
					// Prefer the name captured at start (the completed state often omits input).
					var invocation = _subagents.TryGetValue(callId, out var f) ? f : null;
					var name = invocation?.AgentName ?? ExtractSubagentInfo(state).Name;

					if (status == "completed")
					{
						// OpenCode runs the sub-agent in a child session whose streamed events are
						// filtered out (they would otherwise pollute the parent step's content), so
						// the task tool's completion output is the sub-agent's visible result. Emit it
						// as actor-attributed content so the Portal renders it inside the sub-agent's
						// card instead of showing "No output produced".
						var output = state.TryGetProperty("output", out var o) ? (o.GetString() ?? RawJson(o)) : null;
						if (!string.IsNullOrEmpty(output))
						{
							var actor = new ActorContext(name, name, callId, Depth: 1);
							Emit(new AgentEvent
							{
								Type = AgentEventType.MessageDelta,
								Content = output,
								ActorAgentName = actor.AgentName,
								ActorAgentDisplayName = actor.AgentDisplayName,
								ActorToolCallId = actor.ToolCallId,
								ActorDepth = actor.Depth,
							});
						}

						Emit(new AgentEvent
						{
							Type = AgentEventType.SubagentCompleted,
							ToolCallId = callId,
							SubagentName = name,
						});
					}
					else
					{
						Emit(new AgentEvent
						{
							Type = AgentEventType.SubagentFailed,
							ToolCallId = callId,
							SubagentName = name,
							ErrorMessage = state.TryGetProperty("error", out var e) ? (e.GetString() ?? RawJson(e)) : null,
						});
					}

					_subagents.Remove(callId);
				}
				break;
		}
	}

	/// <summary>
	/// Extracts the target sub-agent name and task description from an OpenCode <c>task</c> tool
	/// part's <c>state.input</c>. The agent-name field varies by OpenCode version; the description
	/// is the delegated instruction. Returns nulls when the state carries no input (e.g. some
	/// completion frames).
	/// </summary>
	private static (string? Name, string? Description) ExtractSubagentInfo(JsonElement state)
	{
		if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
			return (null, null);

		string? name = null;
		foreach (var key in (string[])["subagent_type", "subagentType", "agent", "agent_type", "name"])
		{
			var v = GetString(input, key);
			if (!string.IsNullOrWhiteSpace(v))
			{
				name = v;
				break;
			}
		}

		return (name, GetString(input, "description"));
	}

	private void HandleMessageUpdated(JsonElement properties)
	{
		if (!properties.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
			return;
		if (!string.Equals(GetString(info, "role"), "assistant", StringComparison.Ordinal))
			return;

		var providerId = GetString(info, "providerID");
		var modelId = GetString(info, "modelID");
		if (modelId is not null)
			_actualModel = providerId is not null ? $"{providerId}/{modelId}" : modelId;

		double? input = null, output = null, reasoning = null, cacheRead = null, cacheWrite = null;
		if (info.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
		{
			input = GetDouble(tokens, "input");
			output = GetDouble(tokens, "output");
			reasoning = GetDouble(tokens, "reasoning");
			if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
			{
				cacheRead = GetDouble(cache, "read");
				cacheWrite = GetDouble(cache, "write");
			}
		}

		var cost = GetDouble(info, "cost");
		if (input is null && output is null && cost is null)
			return; // not a completion-bearing update

		_usage = new AgentUsage
		{
			InputTokens = input,
			OutputTokens = output,
			ReasoningTokens = reasoning,
			CacheReadTokens = cacheRead,
			CacheWriteTokens = cacheWrite,
			Cost = cost,
		};
		Emit(new AgentEvent { Type = AgentEventType.Usage, Model = _actualModel, Usage = _usage });
	}

	private void HandleSessionError(JsonElement properties)
	{
		string? message = null;
		string? errorType = null;
		if (properties.TryGetProperty("error", out var error))
		{
			if (error.ValueKind == JsonValueKind.String)
			{
				message = error.GetString();
			}
			else if (error.ValueKind == JsonValueKind.Object)
			{
				errorType = GetString(error, "name") ?? GetString(error, "type");
				message = GetString(error, "message")
					?? (error.TryGetProperty("data", out var data) ? GetString(data, "message") : null)
					?? RawJson(error);
			}
		}
		message ??= "OpenCode session reported an error.";

		_reporter.ReportSessionWarning(errorType ?? "session_error", message);
		Emit(new AgentEvent { Type = AgentEventType.Error, ErrorMessage = message, DiagnosticType = errorType });

		var details = new AgentSessionErrorDetails
		{
			ErrorType = errorType,
			TransientUpstreamFailure = LooksTransient(errorType, message),
		};
		_done.TrySetException(new OpenCodeSessionFailedException(message, details));
	}

	private void HandlePermissionRequested(JsonElement properties)
	{
		// permission.updated carries the request either at the root or under a "permission" key.
		var perm = properties.TryGetProperty("permission", out var nested) && nested.ValueKind == JsonValueKind.Object
			? nested
			: properties;

		Emit(new AgentEvent
		{
			Type = AgentEventType.PermissionRequested,
			PermissionRequestId = GetString(perm, "id"),
			PermissionKind = GetString(perm, "type") ?? GetString(perm, "kind"),
			PermissionTarget = GetString(perm, "title") ?? GetString(perm, "pattern"),
		});
	}

	private void Emit(AgentEvent evt) => _writer.TryWrite(evt);

	private static bool LooksTransient(string? errorType, string message)
	{
		if (errorType is not null &&
			(errorType.Contains("rate", StringComparison.OrdinalIgnoreCase)
			 || errorType.Contains("overload", StringComparison.OrdinalIgnoreCase)
			 || errorType.Contains("unavailable", StringComparison.OrdinalIgnoreCase)))
			return true;

		return message.Contains(" 429", StringComparison.Ordinal)
			|| message.Contains(" 500", StringComparison.Ordinal)
			|| message.Contains(" 502", StringComparison.Ordinal)
			|| message.Contains(" 503", StringComparison.Ordinal)
			|| message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("overloaded", StringComparison.OrdinalIgnoreCase);
	}

	private static string? TryGetSessionId(OpenCodeServerEvent evt)
	{
		if (evt.Properties.ValueKind != JsonValueKind.Object)
			return null;
		var props = evt.Properties;
		if (props.TryGetProperty("sessionID", out var sid) && sid.ValueKind == JsonValueKind.String)
			return sid.GetString();
		if (props.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object)
			return GetString(part, "sessionID");
		if (props.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
			return GetString(info, "sessionID");
		if (props.TryGetProperty("permission", out var perm) && perm.ValueKind == JsonValueKind.Object)
			return GetString(perm, "sessionID");
		return null;
	}

	private static string? GetString(JsonElement obj, string name)
		=> obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
			? v.GetString()
			: null;

	private static double? GetDouble(JsonElement obj, string name)
		=> obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
			? v.GetDouble()
			: null;

	private static string RawJson(JsonElement element)
	{
		try { return element.GetRawText(); }
		catch { return string.Empty; }
	}
}
