using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// Drives one prompt turn against an OpenCode server: lease a worker, create a session,
/// subscribe to the event bus, send the prompt, translate events to <see cref="AgentEvent"/>s
/// via <see cref="OpenCodeSessionHandler"/>, resolve permission requests per policy, and return
/// the accumulated <see cref="AgentResult"/>. Mirrors <c>CopilotAgent</c>.
/// </summary>
internal sealed partial class OpenCodeAgent : IAgent
{
	private readonly OpenCodeServerPool _pool;
	private readonly OpenCodeAgentPoolOptions _options;
	private readonly string _model;
	private readonly string? _systemPrompt;
	private readonly IReadOnlyCollection<IEngineTool> _engineTools;
	private readonly EngineToolContext? _engineToolContext;
	private readonly ImageAttachment[] _attachments;
	private readonly PermissionPolicy? _permissionPolicy;
	private readonly bool _humanInput;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger _logger;
	private readonly SemaphoreSlim _permissionGate = new(1, 1);

	public OpenCodeAgent(
		OpenCodeServerPool pool,
		OpenCodeAgentPoolOptions options,
		AgentBuildConfig config,
		ILogger logger)
	{
		_pool = pool;
		_options = options;
		_model = config.Model;
		_systemPrompt = config.SystemPrompt;
		_engineTools = config.EngineTools;
		_engineToolContext = config.EngineToolCtx;
		_attachments = config.Attachments;
		_permissionPolicy = config.PermissionPolicy;
		_humanInput = config.HumanInput;
		_reporter = config.Reporter;
		_logger = logger;
	}

	public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var resultTask = RunAsync(prompt, channel.Writer, cancellationToken);
		return new AgentTask(channel.Reader, resultTask);
	}

	private async Task<AgentResult> RunAsync(string prompt, ChannelWriter<AgentEvent> writer, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(_model);
		var modelRef = OpenCodeModelRef.Parse(_model, _options.FallbackProvider);

		IOpenCodeServerLease? lease = null;
		string? sessionId = null;
		try
		{
			lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
			var client = lease.Client;

			// Bind this step's engine tools so the loopback MCP bridge routes orchestra_* calls
			// to the right EngineToolContext, then register the bridge with this OpenCode instance.
			var hasEngineTools = _engineTools.Count > 0 && _engineToolContext is not null && _options.EngineToolBridgeEnabled;
			if (hasEngineTools)
			{
				lease.ContextHolder.Set(_engineTools, _engineToolContext!);
				await RegisterEngineToolMcpAsync(client, lease, cancellationToken).ConfigureAwait(false);
			}

			sessionId = await client.CreateSessionAsync(title: null, cancellationToken).ConfigureAwait(false);
			LogSessionCreated(sessionId, modelRef.ToString());

			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var handler = new OpenCodeSessionHandler(sessionId, writer, _reporter, _model, done, _logger);
			_reporter.ReportSessionStarted(_model, modelRef.ToString());
			writer.TryWrite(new AgentEvent { Type = AgentEventType.SessionStart, Model = modelRef.ToString() });

			// Subscribe BEFORE prompting so no events between session-create and send are missed.
			using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var pump = PumpEventsAsync(client, sessionId, handler, done, streamCts.Token);

			var request = BuildPromptRequest(prompt, modelRef);
			try
			{
				await client.PromptAsync(sessionId, request, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				throw new OpenCodeSessionFailedException($"OpenCode prompt failed: {ex.Message}", innerException: ex);
			}

			// Cancellation (including engine-tool RequestStepCompletion, which the executor links
			// into this token) aborts the in-flight turn.
			await using var ctReg = cancellationToken.Register(() =>
			{
				_ = client.AbortSessionAsync(sessionId, CancellationToken.None);
				done.TrySetCanceled(cancellationToken);
			});

			try
			{
				await done.Task.ConfigureAwait(false);
			}
			finally
			{
				streamCts.Cancel();
				try { await pump.ConfigureAwait(false); } catch { /* pump cancellation */ }
			}

			return new AgentResult
			{
				Content = handler.FinalContent ?? string.Empty,
				ActualModel = handler.ActualModel,
				SelectedModel = modelRef.ToString(),
				Usage = handler.Usage,
			};
		}
		finally
		{
			if (lease is not null)
			{
				if (sessionId is not null)
					await lease.Client.DeleteSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
				lease.ContextHolder.Clear();
				await lease.DisposeAsync().ConfigureAwait(false);
			}
			writer.TryComplete();
		}
	}

	private async Task RegisterEngineToolMcpAsync(IOpenCodeClient client, IOpenCodeServerLease lease, CancellationToken cancellationToken)
	{
		if (lease.EngineToolMcpUrl is not { } url)
			return;
		try
		{
			// Idempotent by name: OpenCode overwrites an existing entry, so re-registering each
			// step is harmless and keeps the worker reusable across steps.
			await client.AddMcpAsync("orchestra-engine-tools", new { type = "remote", url, enabled = true }, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			LogMcpRegisterError(ex, url);
		}
	}

	private async Task PumpEventsAsync(
		IOpenCodeClient client,
		string sessionId,
		OpenCodeSessionHandler handler,
		TaskCompletionSource done,
		CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var evt in client.SubscribeAsync(cancellationToken).ConfigureAwait(false))
			{
				handler.Handle(evt);

				if (evt.Type == "permission.updated")
					_ = ResolvePermissionAsync(client, sessionId, evt, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			// Stream closed on completion — expected.
		}
		catch (Exception ex)
		{
			// The event bus is our only completion signal; a broken stream must fault the turn
			// rather than hang until the step timeout.
			done.TrySetException(new OpenCodeClientUnhealthyException(
				sessionId, "event_stream_lost", probeDetails: ex.Message,
				message: $"OpenCode event stream for session '{sessionId}' was lost: {ex.Message}",
				innerException: ex));
		}
	}

	private OpenCodePromptRequest BuildPromptRequest(string prompt, OpenCodeModelRef modelRef)
	{
		var parts = new List<OpenCodePartDto> { OpenCodePartDto.TextPart(prompt) };
		foreach (var attachment in _attachments)
		{
			if (attachment is FileImageAttachment file && !string.IsNullOrWhiteSpace(file.Path))
			{
				parts.Add(new OpenCodePartDto
				{
					Type = "file",
					Filename = file.DisplayName ?? Path.GetFileName(file.Path),
					Url = new Uri(Path.GetFullPath(file.Path)).AbsoluteUri,
				});
			}
			else if (attachment is BlobImageAttachment blob)
			{
				parts.Add(new OpenCodePartDto
				{
					Type = "file",
					Mime = blob.MimeType,
					Filename = blob.DisplayName,
					Url = $"data:{blob.MimeType};base64,{blob.Data}",
				});
			}
		}

		return new OpenCodePromptRequest
		{
			Model = new OpenCodeModelDto { ProviderId = modelRef.ProviderId, ModelId = modelRef.ModelId },
			System = _systemPrompt,
			Parts = parts,
		};
	}

	private async Task ResolvePermissionAsync(IOpenCodeClient client, string sessionId, OpenCodeServerEvent evt, CancellationToken cancellationToken)
	{
		try
		{
			var (permissionId, kind, target) = ParsePermission(evt.Properties);
			if (permissionId is null)
				return;

			var response = await DecidePermissionAsync(kind, target, cancellationToken).ConfigureAwait(false);
			await client.RespondPermissionAsync(sessionId, permissionId, response, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			LogPermissionError(ex, sessionId);
		}
	}

	private async Task<string> DecidePermissionAsync(string? kind, string? target, CancellationToken cancellationToken)
	{
		var policy = _permissionPolicy;
		if (policy is null || policy.Mode == PermissionMode.ApproveAll)
			return "once";

		if (policy.Mode == PermissionMode.DenyList)
			return IsDeniedByPolicy(kind, target, policy.Deny) ? "reject" : "once";

		// RequireHumanApproval
		if (_engineToolContext is null)
			return "reject";

		await _permissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var prompt = $"The OpenCode agent is requesting permission to {kind ?? "act"}{(target is null ? string.Empty : $": {target}")}.\n" +
				"Reply 'approve' to allow this action once, or provide a reason to deny it.";
			var response = await _engineToolContext.RequestHumanInputAsync(
				prompt, choices: ["approve", "deny"], PendingInputKind.Permission, cancellationToken).ConfigureAwait(false);

			return IsApproval(response) ? "once" : "reject";
		}
		finally
		{
			_permissionGate.Release();
		}
	}

	private static bool IsApproval(UserInputResponse? response)
	{
		if (response is null)
			return false;
		if (!string.IsNullOrWhiteSpace(response.Choice))
			return response.Choice.Trim().Equals("approve", StringComparison.OrdinalIgnoreCase);
		var content = response.ResolveContent().Trim();
		return content.Equals("approve", StringComparison.OrdinalIgnoreCase)
			|| content.Equals("yes", StringComparison.OrdinalIgnoreCase);
	}

	private static (string? Id, string? Kind, string? Target) ParsePermission(JsonElement properties)
	{
		var perm = properties.TryGetProperty("permission", out var nested) && nested.ValueKind == JsonValueKind.Object
			? nested
			: properties;
		if (perm.ValueKind != JsonValueKind.Object)
			return (null, null, null);

		string? Get(string n) => perm.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
		return (Get("id"), Get("type") ?? Get("kind"), Get("title") ?? Get("pattern"));
	}

	internal static bool IsDeniedByPolicy(string? kind, string? target, string[] deny)
	{
		foreach (var pattern in deny)
		{
			if ((kind is not null && GlobMatches(pattern, kind)) || (target is not null && GlobMatches(pattern, target)))
				return true;
		}
		return false;
	}

	private static bool GlobMatches(string pattern, string value)
	{
		if (string.IsNullOrEmpty(pattern))
			return false;
		if (!pattern.Contains('*') && !pattern.Contains('?'))
			return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
		var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
		return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
	}

	[LoggerMessage(EventId = 220, Level = LogLevel.Debug, Message = "OpenCode: created session {SessionId} for model {Model}")]
	private partial void LogSessionCreated(string sessionId, string model);

	[LoggerMessage(EventId = 221, Level = LogLevel.Warning, Message = "OpenCode: error resolving permission for session {SessionId}")]
	private partial void LogPermissionError(Exception ex, string sessionId);

	[LoggerMessage(EventId = 222, Level = LogLevel.Warning, Message = "OpenCode: failed to register engine-tool MCP bridge at {Url}")]
	private partial void LogMcpRegisterError(Exception ex, string url);
}
