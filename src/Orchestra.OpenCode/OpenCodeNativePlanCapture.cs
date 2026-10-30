using System.Text.Json;
using System.Threading.Channels;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>Observes successful, recognizable native todo writes without inferring plans from prose.</summary>
internal sealed class OpenCodeNativePlanCapture : IAsyncDisposable
{
	private readonly string _rootSession;
	private readonly IOpenCodeClient _client;
	private readonly ChannelWriter<AgentEvent> _writer;
	private readonly Action<Exception>? _diagnostic;
	private readonly Dictionary<(string Session, string Call), string> _names = [];
	private readonly Dictionary<(string Session, string Call), JsonElement> _inputs = [];
	private readonly HashSet<string> _owned;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Session, string Call), bool> _observed = new();
	private readonly Dictionary<string, string> _latestCalls = new(StringComparer.Ordinal);
	private readonly object _gate = new();
	private readonly Channel<OpenCodeServerEvent> _events = Channel.CreateUnbounded<OpenCodeServerEvent>(new() { SingleReader = true });
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _worker;
	private int _disposed;

	public OpenCodeNativePlanCapture(string rootSession, IOpenCodeClient client, ChannelWriter<AgentEvent> writer, Action<Exception>? diagnostic = null)
	{
		_rootSession = rootSession;
		_client = client;
		_writer = writer;
		_diagnostic = diagnostic;
		_owned = [rootSession];
		_worker = RunAsync();
	}

	/// <summary>Transport callbacks only queue data; ownership checks run on a separate serial reader.</summary>
	public void Enqueue(OpenCodeServerEvent evt)
	{
		if (evt.Properties.ValueKind != JsonValueKind.Object)
			return;
		if (evt.Type is "session.tool.input.started" or "session.tool.called" or "session.tool.success" or "session.tool.failed")
			_events.Writer.TryWrite(evt with { Properties = evt.Properties.Clone() });
	}

	private async Task RunAsync()
	{
		try
		{
			await foreach (var evt in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
			{
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
				timeout.CancelAfter(TimeSpan.FromSeconds(2));
				try
				{ await HandleAsync(evt, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
				catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
				catch (Exception ex) { _diagnostic?.Invoke(ex); }
			}
		}
		catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
	}

	public async Task DrainAsync()
	{
		_events.Writer.TryComplete();
		try
		{ await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
		catch (TimeoutException ex) { _diagnostic?.Invoke(ex); await _stop.CancelAsync().ConfigureAwait(false); }
		await _worker.ConfigureAwait(false);
	}

	public IReadOnlyList<string> Sessions
	{
		get { lock (_gate) return _owned.ToArray(); }
	}

	public async Task HandleAsync(OpenCodeServerEvent evt, CancellationToken token)
	{
		var data = evt.Properties;
		var sid = Text(data, "sessionID");
		var id = Text(data, "id");
		if (sid is null || id is null)
			return;
		var key = (sid, id);
		if (evt.Type == "session.tool.input.started" && Text(data, "name") == "todowrite")
			_names[key] = "todowrite";
		if (!_names.TryGetValue(key, out var tool) || tool != "todowrite")
			return;
		if (evt.Type == "session.tool.called" && data.TryGetProperty("input", out var input))
			_inputs[key] = input.Clone();
		if (evt.Type is not ("session.tool.success" or "session.tool.failed"))
			return;
		_names.Remove(key);
		if (!_inputs.Remove(key, out var arguments) || evt.Type == "session.tool.failed" || HasErrorMetadata(data))
			return;
		bool owned;
		lock (_gate)
			owned = _owned.Contains(sid);
		if (!owned)
		{
			if (!await _client.IsSessionWithinAsync(sid, _rootSession, token).ConfigureAwait(false))
				return;
			token.ThrowIfCancellationRequested();
			lock (_gate)
				_owned.Add(sid);
		}
		lock (_gate)
		{
			if (_observed.TryAdd(key, true))
			{
				_latestCalls[sid] = id;
				Publish(sid, arguments);
			}
		}
	}

	public void Reconcile(IReadOnlyList<JsonElement> messages, string? sessionId = null)
	{
		lock (_gate)
		{
			var session = sessionId ?? _rootSession;
			if (_owned.Contains(session))
				ReconcileCore(session, messages);
		}
	}

	private void ReconcileCore(string session, IReadOnlyList<JsonElement> messages)
	{
		// Only the latest successful native write is the final state; replaying every old
		// write at each continuation would invent backwards revisions.
		JsonElement? last = null;
		string? lastId = null;
		var containsCurrent = !_latestCalls.TryGetValue(session, out var current);
		foreach (var message in messages)
			if (Text(message, "type") == "assistant" && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
				foreach (var tool in content.EnumerateArray())
					if (Text(tool, "type") == "tool" && Text(tool, "name") == "todowrite" && Text(tool, "id") is { } id
						&& tool.TryGetProperty("state", out var state) && Text(state, "status") == "completed" && !HasErrorMetadata(state)
						&& state.TryGetProperty("input", out var input))
					{
						_observed.TryAdd((session, id), true);
						containsCurrent |= id == current;
						lastId = id;
						last = input.Clone();
					}
		if (last is { } value && containsCurrent)
		{
			_latestCalls[session] = lastId!;
			Publish(session, value);
		}
	}

	private void Publish(string session, JsonElement input)
	{
		var plan = new AgentPlanSnapshot
		{
			Provider = "opencode",
			SessionId = session,
			Source = "native-todos",
			Title = "Native tasks",
			ActorId = session == _rootSession ? null : session
		};
		try
		{
			if (!input.TryGetProperty("todos", out var todos) && !input.TryGetProperty("tasks", out todos))
				throw new FormatException("Unrecognized native todo payload.");
			plan = plan with { Tasks = OpenCodePlanTool.ParseTasks(todos) };
		}
		catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { plan = plan with { CaptureError = ex.Message }; }
		_writer.TryWrite(new() { Type = AgentEventType.PlanUpdated, Plan = plan });
	}

	private static string? Text(JsonElement value, string property)
		=> value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

	private static bool HasErrorMetadata(JsonElement value)
		=> value.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
			&& metadata.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True;

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		await DrainAsync().ConfigureAwait(false);
		_stop.Dispose();
	}
}
