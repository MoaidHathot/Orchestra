using System.Text.Json;
using System.Threading.Channels;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>Observes successful, recognizable native todo writes without inferring plans from prose.</summary>
internal sealed class OpenCodeNativePlanCapture(string rootSession, IOpenCodeClient client, ChannelWriter<AgentEvent> writer)
{
	private readonly Dictionary<(string Session, string Call), string> _names = [];
	private readonly Dictionary<(string Session, string Call), JsonElement> _inputs = [];
	private readonly HashSet<string> _owned = [rootSession];
	private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Session, string Call), bool> _observed = new();
	private readonly object _gate = new();

	public async Task HandleAsync(OpenCodeServerEvent evt, CancellationToken token)
	{
		var data = evt.Properties;
		if (!data.TryGetProperty("sessionID", out var session) || !data.TryGetProperty("id", out var call)) return;
		var sid = session.GetString()!;
		var key = (sid, call.GetString()!);
		if (evt.Type == "session.tool.input.started" && data.TryGetProperty("name", out var name) && name.GetString() == "todowrite") _names[key] = "todowrite";
		if (!_names.TryGetValue(key, out var tool) || tool != "todowrite") return;
		if (evt.Type == "session.tool.called" && data.TryGetProperty("input", out var input)) _inputs[key] = input.Clone();
		if (evt.Type is not ("session.tool.success" or "session.tool.failed")) return;
		_names.Remove(key);
		if (!_inputs.Remove(key, out var arguments) || evt.Type == "session.tool.failed") return;
		if (!_owned.Contains(sid))
		{
			if (!await client.IsSessionWithinAsync(sid, rootSession, token).ConfigureAwait(false)) return;
			token.ThrowIfCancellationRequested();
			_owned.Add(sid);
		}
		lock (_gate) if (_observed.TryAdd(key, true)) Publish(sid, arguments);
	}

	public void Reconcile(IReadOnlyList<JsonElement> messages)
	{
		lock (_gate) ReconcileCore(messages);
	}

	private void ReconcileCore(IReadOnlyList<JsonElement> messages)
	{
		// Only the latest successful native write is the final state; replaying every old
		// write at each continuation would invent backwards revisions.
		JsonElement? last = null;
		foreach (var message in messages)
			if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
				foreach (var tool in content.EnumerateArray())
					if (tool.TryGetProperty("name", out var name) && name.GetString() == "todowrite"
						&& tool.TryGetProperty("state", out var state) && state.GetProperty("status").GetString() == "completed"
						&& state.TryGetProperty("input", out var input))
					{
						_observed.TryAdd((rootSession, tool.GetProperty("id").GetString()!), true);
						last = input.Clone();
					}
		if (last is { } value) Publish(rootSession, value);
	}

	private void Publish(string session, JsonElement input)
	{
		var plan = new AgentPlanSnapshot { Provider = "opencode", SessionId = session, Source = "native-todos", Title = "Native tasks",
			ActorId = session == rootSession ? null : session };
		try
		{
			if (!input.TryGetProperty("todos", out var todos) && !input.TryGetProperty("tasks", out todos)) throw new FormatException("Unrecognized native todo payload.");
			plan = plan with { Tasks = OpenCodePlanTool.ParseTasks(todos) };
		}
		catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { plan = plan with { CaptureError = ex.Message }; }
		writer.TryWrite(new() { Type = AgentEventType.PlanUpdated, Plan = plan });
	}
}
