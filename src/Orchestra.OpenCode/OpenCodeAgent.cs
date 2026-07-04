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
	private readonly IOpenCodeClientFactory _clientFactory;
	private readonly ILoggerFactory _loggerFactory;
	private readonly string _model;
	private readonly string? _systemPrompt;
	private readonly ReasoningLevel? _reasoningLevel;
	private readonly Subagent[] _subagents;
	private readonly Mcp[] _mcps;
	private readonly string? _workingDirectory;
	private readonly string[] _skillDirectories;
	private readonly string[] _excludedTools;
	private readonly InfiniteSessionConfig? _infiniteSession;
	private readonly string? _runArtifactDirectory;
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
		IOpenCodeClientFactory clientFactory,
		ILoggerFactory loggerFactory,
		AgentBuildConfig config)
	{
		_pool = pool;
		_options = options;
		_clientFactory = clientFactory;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<OpenCodeAgent>();
		_model = config.Model;
		_systemPrompt = config.SystemPrompt;
		_reasoningLevel = config.ReasoningLevel;
		_subagents = config.Subagents;
		_mcps = config.Mcps;
		_workingDirectory = config.WorkingDirectory;
		_skillDirectories = config.SkillDirectories;
		_excludedTools = config.ExcludedTools;
		_infiniteSession = config.InfiniteSessionConfig;
		// Per-step config files + skill staging live in the run's artifact (temp) folder.
		_runArtifactDirectory = config.EngineToolCtx?.TempFileStore?.TempDirectory;
		_engineTools = config.EngineTools;
		_engineToolContext = config.EngineToolCtx;
		_attachments = config.Attachments;
		_permissionPolicy = config.PermissionPolicy;
		_humanInput = config.HumanInput;
		_reporter = config.Reporter;
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

		// OpenCode applies reasoning, sub-agents, and MCP servers via its *spawn-time* config
		// (runtime config patches don't register usable agents), and skills/working-directory via
		// the server's cwd. So any step that needs per-step config runs on its own dedicated
		// server spawned with a generated opencode.json; plain text-prompt steps use the pool.
		var plan = OpenCodeConfigBuilder.Build(modelRef, _systemPrompt, _reasoningLevel, _subagents, _mcps, _excludedTools, _options.FallbackProvider);
		// Auto-compaction is disabled via a spawn-time env var, and excluded tools / skills /
		// working directory all need a dedicated server too.
		var disableAutoCompact = _infiniteSession?.Enabled == false;
		var needsDedicated = plan.HasConfig
			|| !string.IsNullOrWhiteSpace(_workingDirectory)
			|| _skillDirectories.Length > 0
			|| disableAutoCompact;

		// On a transport-class failure the shared loop retries on a fresh worker. OpenCode
		// persists sessions in its data dir (shared across server processes), so a swap can
		// resume the prior session — re-prompting its id to preserve tool-call progress — or
		// cold-restart on a new session when resume is disabled or the session is unreachable.
		var swapLoop = new AgentSwapLoop(
			new SwapPolicy(_options.SwapBudgetPerStep, ResumeEnabled: _options.ResumeOnSwapEnabled),
			_reporter,
			_stepName,
			_loggerFactory.CreateLogger<AgentSwapLoop>());

		return await swapLoop.RunAsync(
			runAttempt: (ctx, ct) => needsDedicated
				? RunDedicatedAsync(prompt, writer, modelRef, plan, disableAutoCompact, ctx.PriorSessionId, ctx.SessionIdBox, ct)
				: RunPooledAsync(prompt, writer, modelRef, ctx.PriorSessionId, ctx.SessionIdBox, ct),
			writer: writer,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Step name for swap reporting: the engine-tool step name, else the model.</summary>
	private string _stepName => _engineToolContext?.StepName ?? _model;

	/// <summary>Runs a plain step on a pooled, shared OpenCode worker.</summary>
	private async Task<AgentResult> RunPooledAsync(string prompt, ChannelWriter<AgentEvent> writer, OpenCodeModelRef modelRef, string? priorSessionId, SwapSessionIdBox sessionIdBox, CancellationToken cancellationToken)
	{
		var lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await RunTurnAsync(lease.Client, lease.ContextHolder, lease.EngineToolMcpUrl, prompt, modelRef, agentName: null, priorSessionId, sessionIdBox, writer, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			lease.ContextHolder.Clear();
			await lease.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Runs a step that needs per-step config on a dedicated OpenCode server: writes the
	/// generated opencode.json (agents + MCP servers) to the run's artifact folder, stages any
	/// skill directories under the server's working directory, spawns the server pointed at both,
	/// and runs the turn (optionally through the per-step agent), with its own engine-tool bridge.
	/// </summary>
	private async Task<AgentResult> RunDedicatedAsync(string prompt, ChannelWriter<AgentEvent> writer, OpenCodeModelRef modelRef, OpenCodeStepPlan plan, bool disableAutoCompact, string? priorSessionId, SwapSessionIdBox sessionIdBox, CancellationToken cancellationToken)
	{
		var workspace = OpenCodeWorkspaceBuilder.Prepare(plan.HasConfig ? plan.Config : null, _workingDirectory, _skillDirectories, _runArtifactDirectory);
		var connectPlan = OpenCodeServerBootstrap.Resolve(_options);

		// Infinite sessions (Enabled=false) disable OpenCode's automatic context compaction.
		var extraEnv = disableAutoCompact
			? new Dictionary<string, string> { ["OPENCODE_DISABLE_AUTOCOMPACT"] = "true" }
			: null;

		var holder = new EngineToolContextHolder();
		OpenCodeEngineToolBridge? bridge = null;
		// Reserve a pool capacity slot before spawning so dedicated servers are bounded by the run's
		// maxInstances cap and are counted in the pool snapshot (one instance / one session) while
		// this step runs; the slot is released in the finally when the server is torn down.
		var slot = await _pool.AcquireDedicatedSlotAsync(cancellationToken).ConfigureAwait(false);
		var process = new OpenCodeServerProcess(connectPlan, _options, _clientFactory, _loggerFactory.CreateLogger<OpenCodeServerProcess>(), workspace.ConfigFilePath, workspace.WorkingDirectory, extraEnv);
		try
		{
			if (_engineTools.Count > 0 && _engineToolContext is not null && _options.EngineToolBridgeEnabled)
				bridge = await OpenCodeEngineToolBridge.StartAsync(holder, _options.Hostname, _loggerFactory, cancellationToken).ConfigureAwait(false);

			await process.StartAsync(cancellationToken).ConfigureAwait(false);
			return await RunTurnAsync(process.Client, holder, bridge?.McpUrl, prompt, modelRef, plan.PrimaryAgentName, priorSessionId, sessionIdBox, writer, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			holder.Clear();
			await process.DisposeAsync().ConfigureAwait(false);
			if (bridge is not null)
				await bridge.DisposeAsync().ConfigureAwait(false);
			workspace.Cleanup(_logger);
			await slot.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Shared per-turn logic: bind engine tools, create or resume the session, stream events, send
	/// the prompt (optionally routed through a named agent), and return the accumulated result.
	/// On a resume attempt (<paramref name="priorSessionId"/> set) the prior session is re-prompted;
	/// if it can't be reached, a <c>resume_session_missing</c> signal forces the swap loop to
	/// cold-restart. The session is deleted only when the turn completes, so a failed attempt's
	/// session survives for the next swap to resume.
	/// </summary>
	private async Task<AgentResult> RunTurnAsync(
		IOpenCodeClient client,
		EngineToolContextHolder holder,
		string? engineToolMcpUrl,
		string prompt,
		OpenCodeModelRef modelRef,
		string? agentName,
		string? priorSessionId,
		SwapSessionIdBox sessionIdBox,
		ChannelWriter<AgentEvent> writer,
		CancellationToken cancellationToken)
	{
		string? sessionId = null;
		var completed = false;
		try
		{
			var hasEngineTools = _engineTools.Count > 0 && _engineToolContext is not null && _options.EngineToolBridgeEnabled;
			if (hasEngineTools)
			{
				holder.Set(_engineTools, _engineToolContext!);
				if (engineToolMcpUrl is not null)
					await RegisterEngineToolMcpAsync(client, engineToolMcpUrl, cancellationToken).ConfigureAwait(false);
			}

			var isResume = !string.IsNullOrEmpty(priorSessionId);
			if (isResume)
			{
				// Resume the prior session (OpenCode persists it across server processes).
				sessionId = priorSessionId!;
				LogSessionResumed(sessionId, modelRef.ToString());
			}
			else
			{
				sessionId = await client.CreateSessionAsync(title: null, cancellationToken).ConfigureAwait(false);
				LogSessionCreated(sessionId, modelRef.ToString());
			}

			// Tell the swap loop this attempt's session id (for swap-event attribution + resume).
			sessionIdBox.Value = sessionId;

			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var handler = new OpenCodeSessionHandler(sessionId, writer, _reporter, _model, done, _logger);
			_reporter.ReportSessionStarted(_model, modelRef.ToString());
			writer.TryWrite(new AgentEvent { Type = AgentEventType.SessionStart, Model = modelRef.ToString() });

			// MCP load-status fail-fast: emit a McpServersLoaded event marking any declared MCP
			// that did not load on the server as Failed. The executor's post-turn check then fails
			// the step rather than letting the LLM run without its required tools. (OpenCode's API
			// exposes loaded MCP *names* only — not tool counts — so a connected-but-zero-tools
			// inline server is not observable here; global MCPs are covered by the engine's
			// provider-agnostic pre-LLM proxy probe.)
			await EmitMcpLoadStatusAsync(client, writer, cancellationToken).ConfigureAwait(false);

			// Subscribe BEFORE prompting so no events between session-create and send are missed.
			using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var pump = PumpEventsAsync(client, sessionId, handler, done, streamCts.Token);

			var request = BuildPromptRequest(prompt, modelRef, agentName);
			try
			{
				await client.PromptAsync(sessionId, request, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				streamCts.Cancel();
				try { await pump.ConfigureAwait(false); } catch { /* pump cancellation */ }

				if (isResume)
				{
					// The resume target is unreachable (e.g. a fresh dedicated server that doesn't
					// have this session). Signal resume_session_missing so the swap loop cold-restarts.
					LogResumeSessionMissing(sessionId, ex.Message);
					throw new OpenCodeClientUnhealthyException(
						sessionId, "resume_session_missing", probeDetails: ex.Message,
						message: $"Resume of OpenCode session '{sessionId}' failed; falling back to a new session.",
						innerException: ex);
				}

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

			completed = true;
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
			// Delete the session only when the turn completed. A failed attempt's session is left
			// in place so the next swap can resume it; orphans from a final failure are pruned by
			// OpenCode's own session pruning.
			if (sessionId is not null && completed)
				await client.DeleteSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
		}
	}

	private async Task RegisterEngineToolMcpAsync(IOpenCodeClient client, string url, CancellationToken cancellationToken)
	{
		try
		{
			// Idempotent by name: OpenCode overwrites an existing entry, so re-registering each
			// step is harmless and keeps a pooled worker reusable across steps.
			await client.AddMcpAsync("orchestra-engine-tools", new { type = "remote", url, enabled = true }, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			LogMcpRegisterError(ex, url);
		}
	}

	/// <summary>
	/// Compares the step's declared MCP servers against the set OpenCode actually loaded
	/// (<c>GET /mcp</c>) and emits a <see cref="AgentEventType.McpServersLoaded"/> event marking
	/// each as Connected (present) or Failed (declared but absent). The engine's post-turn check
	/// then fails the step when a required server failed to load. A probe failure is non-fatal —
	/// the step proceeds and the engine's other MCP safety nets still apply.
	/// </summary>
	private async Task EmitMcpLoadStatusAsync(IOpenCodeClient client, ChannelWriter<AgentEvent> writer, CancellationToken cancellationToken)
	{
		if (_mcps.Length == 0)
		{
			return;
		}

		try
		{
			var loaded = await client.ListMcpNamesAsync(cancellationToken).ConfigureAwait(false);
			var loadedSet = new HashSet<string>(loaded, StringComparer.OrdinalIgnoreCase);

			var statuses = _mcps
				.Select(m => new McpServerStatusInfo(
					Name: m.Name,
					Status: loadedSet.Contains(m.Name) ? "Connected" : "Failed",
					Source: "opencode"))
				.ToList();

			var missing = statuses.Where(s => s.Status == "Failed").Select(s => s.Name).ToList();
			if (missing.Count > 0)
			{
				LogMcpServersMissing(string.Join(", ", missing));
			}

			_reporter.ReportMcpServersLoaded(statuses);
			writer.TryWrite(new AgentEvent { Type = AgentEventType.McpServersLoaded, McpServerStatuses = statuses });
		}
		catch (Exception ex)
		{
			LogMcpLoadProbeFailed(ex);
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

	private OpenCodePromptRequest BuildPromptRequest(string prompt, OpenCodeModelRef modelRef, string? agentName)
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
			// When routing through a per-step agent, the agent's config carries the system
			// prompt + reasoning; otherwise send the system prompt inline.
			Agent = agentName,
			System = agentName is null ? _systemPrompt : null,
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

	[LoggerMessage(EventId = 223, Level = LogLevel.Warning, Message = "OpenCode: declared MCP server(s) did not load: {Servers}")]
	private partial void LogMcpServersMissing(string servers);

	[LoggerMessage(EventId = 224, Level = LogLevel.Debug, Message = "OpenCode: MCP load-status probe failed; proceeding without it")]
	private partial void LogMcpLoadProbeFailed(Exception ex);

	[LoggerMessage(EventId = 225, Level = LogLevel.Information, Message = "OpenCode: resuming session {SessionId} for model {Model} after a swap")]
	private partial void LogSessionResumed(string sessionId, string model);

	[LoggerMessage(EventId = 226, Level = LogLevel.Warning, Message = "OpenCode: resume of session {SessionId} failed ({Reason}); cold-restarting on a new session")]
	private partial void LogResumeSessionMissing(string sessionId, string reason);
}
