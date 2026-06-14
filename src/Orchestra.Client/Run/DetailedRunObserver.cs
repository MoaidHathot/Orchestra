using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Orchestra.Client.Run;

/// <summary>
/// A richer real-time observer (enabled by <c>--detailed</c>) that sits between the compact
/// default and the <c>--verbose</c> firehose. It delegates the standard step/HITL/terminal
/// rendering to <see cref="ConsoleRunObserver"/> and additionally surfaces a curated set of
/// otherwise-hidden events as they happen — the actually-selected model, MCP/tool calls,
/// sub-agent activity, retries, and saved files — while still suppressing high-frequency noise
/// (content/reasoning deltas, traces, usage/quota snapshots, heartbeats).
/// </summary>
public sealed class DetailedRunObserver : IRunObserver
{
	private readonly IAnsiConsole _console;
	private readonly ConsoleRunObserver _compact;

	public DetailedRunObserver(IAnsiConsole console, ILogger<DetailedRunObserver> logger, ConsoleRunObserver compact)
	{
		_console = console;
		_compact = compact;
	}

	public void OnExecutionStarted(string executionId) => _compact.OnExecutionStarted(executionId);
	public void OnRunContext(string orchestrationName, string runId) => _compact.OnRunContext(orchestrationName, runId);
	public void OnStepStarted(string stepName) => _compact.OnStepStarted(stepName);
	public void OnStepCompleted(string stepName) => _compact.OnStepCompleted(stepName);
	public void OnStepError(string stepName, string error) => _compact.OnStepError(stepName, error);
	public void OnStepCancelled(string stepName) => _compact.OnStepCancelled(stepName);
	public void OnStepSkipped(string stepName, string reason) => _compact.OnStepSkipped(stepName, reason);
	public void OnAwaitingInput(AwaitingInputInfo info) => _compact.OnAwaitingInput(info);
	public void OnInputReceived(string stepName, string? choice, string? reply, string? respondedBy)
		=> _compact.OnInputReceived(stepName, choice, reply, respondedBy);
	public void OnInputTimeout(string stepName, string onTimeout) => _compact.OnInputTimeout(stepName, onTimeout);
	public void OnOrchestrationDone(string status) => _compact.OnOrchestrationDone(status);
	public void OnOrchestrationCancelled(string? reason) => _compact.OnOrchestrationCancelled(reason);
	public void OnOrchestrationError(string error) => _compact.OnOrchestrationError(error);
	public void OnStreamInterrupted(string? reason) => _compact.OnStreamInterrupted(reason);

	public void OnUnknownEvent(string eventType, JsonElement payload)
	{
		switch (eventType)
		{
			case "session-started":
			{
				var model = Str(payload, "selectedModel") ?? Str(payload, "requestedModel");
				if (model is not null)
				{
					Detail("grey", $"model: [white]{Markup.Escape(model)}[/]");
				}
				break;
			}
			case "model-change":
			{
				var prev = Str(payload, "previousModel");
				var next = Str(payload, "newModel");
				Detail("yellow", $"model changed: [grey]{Markup.Escape(prev ?? "?")}[/] \u2192 [white]{Markup.Escape(next ?? "?")}[/]");
				break;
			}
			case "tool-started":
			{
				var tool = Str(payload, "toolName") ?? "tool";
				var mcp = Str(payload, "mcpServer");
				var via = mcp is null ? string.Empty : $" [dim]@{Markup.Escape(mcp)}[/]";
				Detail("grey", $"\u2699 {Markup.Escape(tool)}{via}");
				break;
			}
			case "tool-completed":
			{
				var tool = Str(payload, "toolName") ?? "tool";
				var ok = Bool(payload, "success") ?? true;
				var err = Str(payload, "error");
				var mark = ok ? "[green]\u2713[/]" : "[red]\u2717[/]";
				var tail = !ok && err is not null ? $" [red]{Markup.Escape(Truncate(err, 100))}[/]" : string.Empty;
				Detail("grey", $"{mark} {Markup.Escape(tool)}{tail}");
				break;
			}
			case "mcp-servers-loaded":
			{
				if (payload.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
				{
					var names = servers.EnumerateArray()
						.Select(s => Str(s, "name"))
						.Where(n => !string.IsNullOrEmpty(n))
						.ToArray();
					if (names.Length > 0)
					{
						Detail("grey", $"MCP servers: [white]{Markup.Escape(string.Join(", ", names!))}[/]");
					}
				}
				break;
			}
			case "mcp-server-status-changed":
			{
				var server = Str(payload, "serverName");
				var status = Str(payload, "status");
				if (server is not null)
				{
					Detail("grey", $"MCP [white]{Markup.Escape(server)}[/]: {Markup.Escape(status ?? "?")}");
				}
				break;
			}
			case "subagent-started":
			{
				var name = Str(payload, "displayName") ?? Str(payload, "agentName") ?? "subagent";
				Detail("grey", $"\u21b3 subagent [white]{Markup.Escape(name)}[/]");
				break;
			}
			case "subagent-completed":
			{
				var name = Str(payload, "displayName") ?? Str(payload, "agentName") ?? "subagent";
				Detail("grey", $"[green]\u2713[/] subagent {Markup.Escape(name)}");
				break;
			}
			case "subagent-failed":
			{
				var name = Str(payload, "displayName") ?? Str(payload, "agentName") ?? "subagent";
				var err = Str(payload, "error");
				var tail = err is not null ? $": [red]{Markup.Escape(Truncate(err, 100))}[/]" : string.Empty;
				Detail("grey", $"[red]\u2717[/] subagent {Markup.Escape(name)}{tail}");
				break;
			}
			case "step-retry":
			{
				var step = Str(payload, "stepName") ?? "step";
				var attempt = Str(payload, "attempt") ?? Num(payload, "attempt");
				var max = Str(payload, "maxRetries") ?? Num(payload, "maxRetries");
				Detail("yellow", $"retry [cyan]{Markup.Escape(step)}[/] [dim](attempt {attempt}/{max})[/]");
				break;
			}
			case "loop-iteration":
			{
				var iter = Num(payload, "iteration");
				var max = Num(payload, "maxIterations");
				Detail("grey", $"loop iteration [white]{iter}/{max}[/]");
				break;
			}
			case "saved-file":
			{
				var path = Str(payload, "filePath");
				if (path is not null)
				{
					Detail("grey", $"saved file: [white]{Markup.Escape(path)}[/]");
				}
				break;
			}
			// Everything else (content/reasoning deltas, traces, usage, quota, heartbeats,
			// audit logs, status flips, etc.) stays hidden at this level.
			default:
				break;
		}
	}

	private void Detail(string color, string markup)
		=> _console.MarkupLine($"    [{color}]\u00b7[/] {markup}");

	private static string? Str(JsonElement payload, string name)
		=> payload.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
			? el.GetString()
			: null;

	private static string? Num(JsonElement payload, string name)
		=> payload.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
			? el.ToString()
			: null;

	private static bool? Bool(JsonElement payload, string name)
		=> payload.TryGetProperty(name, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
			? el.GetBoolean()
			: null;

	private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\u2026";
}
