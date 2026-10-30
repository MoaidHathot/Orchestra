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
	private readonly AgentBuildConfig _config;
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
		_config = config;
		config.ExecutionPolicy?.Validate();
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
		// Per-step generated configuration lives in the run's temporary artifact folder.
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
		try
		{ return await RunCoreAsync(prompt, writer, cancellationToken).ConfigureAwait(false); }
		finally { writer.TryComplete(); }
	}

	private async Task<AgentResult> RunCoreAsync(string prompt, ChannelWriter<AgentEvent> writer, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(_model);
		var modelRef = OpenCodeModelRef.Parse(_model, _options.FallbackProvider);
		var continuation = new AgentContinuation(_config.ExecutionPolicy, _config.ResponseSchema);
		var planTool = _config.CapturePlans && _options.EngineToolBridgeEnabled ? new OpenCodePlanTool(writer) : null;

		// Each step uses a configured primary agent on a dedicated, capacity-bounded server.
		// Its config stays isolated from concurrent steps; its Location controls native tools.
		var systemPrompt = string.Join("\n\n", new[] { _systemPrompt, AgentExecutionGuidance.Interaction(_humanInput),
			AgentExecutionGuidance.StructuredResponse(_config.ResponseSchema), continuation.Instructions, planTool?.Instructions }.Where(s => !string.IsNullOrWhiteSpace(s)));
		var excludedTools = _humanInput ? _excludedTools : [.. _excludedTools, "question"];
		var plan = OpenCodeConfigBuilder.Build(modelRef, systemPrompt, _reasoningLevel, _subagents, _mcps, excludedTools, _options.FallbackProvider, _permissionPolicy);
		var stepConfig = new Dictionary<string, object>(plan.Config);
		if (_config.CapturePlans)
			stepConfig["experimental"] = new { todo = true };
		// Skills remain lazy. OpenCode never used Copilot's legacy skill-directory instruction fallback.
		var instructions = _config.InstructionDirectories ?? [];
		if (instructions.Length > 0)
			stepConfig["instructions"] = instructions.SelectMany(d => Directory.EnumerateFiles(d, "*.md",
				new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
				.Order(StringComparer.Ordinal).ToArray();
		if (_infiniteSession?.Enabled is { } compact)
			stepConfig["compaction"] = new { auto = compact };
		plan = plan with { Config = stepConfig };
		// Keep the runtime compatibility flag alongside the native compaction config.
		var disableAutoCompact = _infiniteSession?.Enabled == false;

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
			runAttempt: (ctx, ct) => RunDedicatedAsync(prompt, writer, modelRef, plan, disableAutoCompact, ctx.PriorSessionId, ctx.SessionIdBox, ct, continuation,
				_options.ResumeOnSwapEnabled && ctx.SwapAttempt < _options.SwapBudgetPerStep, planTool),
			writer: writer,
			providerClassifier: ClassifyHttpTransportFailure,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private static bool ClassifyHttpTransportFailure(Exception ex, out string reason)
	{
		reason = "transport_lost";
		return ex is HttpRequestException request && (request.StatusCode is null || (int)request.StatusCode >= 500);
	}

	/// <summary>Step name for swap reporting: the engine-tool step name, else the model.</summary>
	private string _stepName => _engineToolContext?.StepName ?? _model;

	/// <summary>
	/// Runs a step that needs per-step config on a dedicated OpenCode server: writes the
	/// generated opencode.json (agents, MCPs, and explicit skill paths) to the run's artifact folder,
	/// spawns the server at the step's working directory,
	/// and runs the turn (optionally through the per-step agent), with its own engine-tool bridge.
	/// </summary>
	private async Task<AgentResult> RunDedicatedAsync(string prompt, ChannelWriter<AgentEvent> writer, OpenCodeModelRef modelRef, OpenCodeStepPlan plan, bool disableAutoCompact, string? priorSessionId, SwapSessionIdBox sessionIdBox, CancellationToken cancellationToken, AgentContinuation continuation, bool allowResume, OpenCodePlanTool? planTool)
	{
		await using var slot = await _pool.AcquireDedicatedSlotAsync(cancellationToken).ConfigureAwait(false);
		var workspace = OpenCodeWorkspaceBuilder.Prepare(plan.HasConfig ? plan.Config : null, _workingDirectory, _skillDirectories, _runArtifactDirectory);
		var connectPlan = OpenCodeServerBootstrap.Resolve(_options);

		// Infinite sessions (Enabled=false) disable OpenCode's automatic context compaction.
		var extraEnv = disableAutoCompact
			? new Dictionary<string, string> { ["OPENCODE_DISABLE_AUTOCOMPACT"] = "true" }
			: null;

		var holder = new EngineToolContextHolder();
		OpenCodeEngineToolBridge? bridge = null;
		IReadOnlyCollection<IEngineTool> tools = planTool is null ? _engineTools : [.. _engineTools, planTool];
		var toolContext = _engineToolContext ?? (planTool is null ? null : new EngineToolContext { StepName = _stepName });
		// Reserve a pool capacity slot before spawning so dedicated servers are bounded by the run's
		// maxInstances cap and are counted in the pool snapshot (one instance / one session) while
		// this step runs; the slot is released in the finally when the server is torn down.
		var process = new OpenCodeServerProcess(connectPlan, _options, _clientFactory, _loggerFactory.CreateLogger<OpenCodeServerProcess>(), workspace.ConfigFilePath, workspace.WorkingDirectory, extraEnv);
		try
		{
			if (tools.Count > 0 && toolContext is not null && _options.EngineToolBridgeEnabled)
				bridge = await OpenCodeEngineToolBridge.StartAsync(holder, _options.Hostname, _loggerFactory, cancellationToken, tools).ConfigureAwait(false);

			await process.StartAsync(cancellationToken).ConfigureAwait(false);
			return await RunTurnAsync(process.Client, holder, bridge?.McpUrl, prompt, modelRef, plan.PrimaryAgentName, priorSessionId, sessionIdBox, writer, cancellationToken, continuation, allowResume, tools, toolContext, planTool).ConfigureAwait(false);
		}
		finally
		{
			holder.Clear();
			try
			{ await process.DisposeAsync().ConfigureAwait(false); }
			finally
			{
				try
				{ if (bridge is not null) await bridge.DisposeAsync().ConfigureAwait(false); }
				finally { workspace.Cleanup(_logger); }
			}
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
		CancellationToken cancellationToken,
		AgentContinuation? continuation,
		bool allowResume,
		IReadOnlyCollection<IEngineTool> tools,
		EngineToolContext? toolContext,
		OpenCodePlanTool? planTool)
	{
		string? sessionId = null;
		var completed = false;
		var retainForResume = false;
		try
		{
			try
			{ await client.EnsureReadyAsync(BuildModelDto(modelRef), agentName, _options.StartupTimeout, cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
			{ throw new OpenCodeSessionFailedException("Timed out loading the OpenCode model/agent catalog.", innerException: ex); }
			var hasEngineTools = tools.Count > 0 && toolContext is not null && _options.EngineToolBridgeEnabled;
			if (hasEngineTools)
			{
				holder.Set(tools, toolContext!);
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
				sessionId = await client.CreateSessionAsync(new OpenCodeCreateSessionRequest
				{
					Location = new { directory = _workingDirectory ?? Environment.CurrentDirectory },
					Model = BuildModelDto(modelRef),
					Agent = agentName,
				}, cancellationToken).ConfigureAwait(false);
				LogSessionCreated(sessionId, modelRef.ToString());
			}

			// Tell the swap loop this attempt's session id (for swap-event attribution + resume).
			sessionIdBox.Value = sessionId;
			planTool?.Bind(sessionId, isResume);
			await using var nativePlans = _config.CapturePlans ? new OpenCodeNativePlanCapture(sessionId, client, writer, ex =>
			{
				LogPlanCaptureFailed(ex);
				writer.TryWrite(new() { Type = AgentEventType.Warning, DiagnosticType = "plan_capture_failed", Content = ex.Message });
			}) : null;
			if (nativePlans is not null)
				writer.TryWrite(new AgentEvent { Type = AgentEventType.PlanCaptureStarted });
			if (isResume && nativePlans is not null)
				await CapturePlanMessagesAsync(client, nativePlans, writer).ConfigureAwait(false);

			var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var handler = new OpenCodeSessionHandler(sessionId, writer, _reporter, _model, failure, _logger);
			_reporter.ReportSessionStarted(_model, modelRef.ToString());
			writer.TryWrite(new AgentEvent { Type = AgentEventType.SessionStart, Model = modelRef.ToString() });

			// Wait for declared MCPs before prompting and fail if one did not connect. (OpenCode's API
			// exposes loaded MCP *names* only — not tool counts — so a connected-but-zero-tools
			// inline server is not observable here; global MCPs are covered by the engine's
			// provider-agnostic pre-LLM proxy probe.)
			await EmitMcpLoadStatusAsync(client, writer, cancellationToken).ConfigureAwait(false);

			// Subscribe BEFORE prompting so no events between session-create and send are missed.
			using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var pump = PumpEventsAsync(client, sessionId, handler, failure, ready, streamCts.Token, nativePlans);
			try
			{
				await AwaitOrFailureAsync(ready.Task.WaitAsync(_options.StartupTimeout, cancellationToken), failure.Task, cancellationToken).ConfigureAwait(false);
				var requestId = await AwaitOrFailureAsync(client.PromptAsync(sessionId, BuildPromptRequest(prompt, modelRef, agentName), cancellationToken), failure.Task, cancellationToken).ConfigureAwait(false);
				while (true)
				{
					var messages = await AwaitOrFailureAsync(client.WaitForMessagesAsync(sessionId, streamCts.Token), failure.Task, cancellationToken).ConfigureAwait(false);
					handler.ApplyMessages(messages, requestId);
					nativePlans?.Reconcile(messages);
					var finalContent = handler.FinalContent ?? string.Empty;
					if (continuation is null || continuation.TryComplete(finalContent, out finalContent))
					{
						completed = true;
						return new AgentResult { Content = finalContent, ActualModel = handler.ActualModel, SelectedModel = modelRef.ToString(), Usage = handler.Usage };
					}
					LogContinuing(sessionId, continuation.ContinuesUsed, continuation.LastContinuationReason!);
					requestId = await AwaitOrFailureAsync(client.PromptAsync(sessionId, BuildPromptRequest(continuation.NextPrompt, modelRef, agentName, includeAttachments: false), cancellationToken), failure.Task, cancellationToken).ConfigureAwait(false);
				}
			}
			catch (HttpRequestException ex) when (isResume && ex.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				LogResumeSessionMissing(sessionId, ex.Message);
				throw new OpenCodeClientUnhealthyException(sessionId, "resume_session_missing", ex.Message, innerException: ex);
			}
			finally
			{
				await streamCts.CancelAsync().ConfigureAwait(false);
				if (!completed)
					await CleanupSessionAsync(client, sessionId, abort: true).ConfigureAwait(false);
				if (nativePlans is not null)
				{
					await nativePlans.DrainAsync().ConfigureAwait(false);
					await CapturePlanMessagesAsync(client, nativePlans, writer).ConfigureAwait(false);
				}
				try
				{ await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
				catch (Exception ex) { LogSessionCleanupFailed(ex, sessionId); }
			}
		}
		catch (Exception ex) when (allowResume && (AgentSwapLoop.TryClassifyNeutral(ex, out _)
			|| ClassifyHttpTransportFailure(ex, out _)))
		{
			retainForResume = true;
			throw;
		}
		finally
		{
			// Preserve state only while an actual recovery attempt can consume it.
			if (sessionId is not null && (completed || !retainForResume || cancellationToken.IsCancellationRequested))
				await CleanupSessionAsync(client, sessionId, abort: false).ConfigureAwait(false);
		}
	}

	private static async Task AwaitOrFailureAsync(Task operation, Task failure, CancellationToken cancellationToken)
	{
		await Task.WhenAny(operation, failure).WaitAsync(cancellationToken).ConfigureAwait(false);
		if (failure.IsCompleted)
			await failure.ConfigureAwait(false);
		await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task CapturePlanMessagesAsync(IOpenCodeClient client, OpenCodeNativePlanCapture plans, ChannelWriter<AgentEvent> writer)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		foreach (var session in plans.Sessions)
		{
			try
			{
				var messages = await client.ReadMessagesAsync(session, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
				plans.Reconcile(messages, session);
			}
			catch (Exception ex)
			{
				LogPlanCaptureFailed(ex);
				writer.TryWrite(new() { Type = AgentEventType.Warning, DiagnosticType = "plan_capture_failed", Content = ex.Message });
				if (timeout.IsCancellationRequested)
					break;
			}
		}
	}

	private static async Task<T> AwaitOrFailureAsync<T>(Task<T> operation, Task failure, CancellationToken cancellationToken)
	{
		await AwaitOrFailureAsync((Task)operation, failure, cancellationToken).ConfigureAwait(false);
		return await operation.ConfigureAwait(false);
	}

	private async Task CleanupSessionAsync(IOpenCodeClient client, string sessionId, bool abort)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		try
		{
			var operation = abort ? client.AbortSessionAsync(sessionId, timeout.Token) : client.DeleteSessionAsync(sessionId, timeout.Token);
			await operation.WaitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (Exception ex) { LogSessionCleanupFailed(ex, sessionId); }
	}

	private async Task RegisterEngineToolMcpAsync(IOpenCodeClient client, string url, CancellationToken cancellationToken)
	{
		try
		{
			// Idempotent by name: OpenCode overwrites an existing entry, so re-registering each
			// step is harmless and keeps a pooled worker reusable across steps.
			await client.AddMcpAsync(OpenCodeEngineToolBridge.ServerName, new { type = "remote", url, disabled = false, oauth = false, codemode = false }, cancellationToken).ConfigureAwait(false);
			// Registration admits configuration before OpenCode has necessarily loaded the
			// remote tool catalog. Prompting during that window silently omits engine tools.
			var connected = await client.WaitForMcpNamesAsync([OpenCodeEngineToolBridge.ServerName], _options.StartupTimeout, cancellationToken).ConfigureAwait(false);
			if (!connected.Contains(OpenCodeEngineToolBridge.ServerName, StringComparer.Ordinal))
				throw new OpenCodeSessionFailedException("The required Orchestra engine-tool MCP server failed to load. No prompt was submitted.");
		}
		catch (Exception ex)
		{
			LogMcpRegisterError(ex, url);
			throw;
		}
	}

	/// <summary>
	/// Compares the step's declared MCP servers against the set OpenCode actually loaded
	/// (<c>GET /api/mcp</c>) and reports connection status before the prompt. Required servers
	/// must connect; a failed or timed-out status check cannot silently become a tool-less run.
	/// </summary>
	private async Task EmitMcpLoadStatusAsync(IOpenCodeClient client, ChannelWriter<AgentEvent> writer, CancellationToken cancellationToken)
	{
		if (_mcps.Length == 0)
		{
			return;
		}

		try
		{
			var loaded = await client.WaitForMcpNamesAsync(_mcps.Select(m => m.Name).ToArray(), _options.StartupTimeout, cancellationToken).ConfigureAwait(false);
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
			if (missing.Count > 0)
				throw new OpenCodeSessionFailedException($"Required MCP server(s) failed to load: {string.Join(", ", missing)}. No prompt was submitted.");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception ex)
		{
			LogMcpLoadProbeFailed(ex);
			throw;
		}
	}

	private async Task PumpEventsAsync(
		IOpenCodeClient client,
		string sessionId,
		OpenCodeSessionHandler handler,
		TaskCompletionSource done,
		TaskCompletionSource ready,
		CancellationToken cancellationToken,
		OpenCodeNativePlanCapture? plans)
	{
		var pending = new List<Task>();
		try
		{
			await foreach (var evt in client.SubscribeAsync(cancellationToken).ConfigureAwait(false))
			{
				if (evt.Type == "server.connected")
					ready.TrySetResult();
				handler.Handle(evt);
				plans?.Enqueue(evt);

				if (evt.Type == "permission.asked" && evt.Properties.TryGetProperty("sessionID", out var sid) && sid.ValueKind == JsonValueKind.String)
					pending.Add(ResolvePermissionAsync(client, sessionId, evt, done, cancellationToken));
				if (evt.Type == "form.created" && evt.Properties.TryGetProperty("form", out var form)
					&& form.TryGetProperty("sessionID", out var formSession) && formSession.ValueKind == JsonValueKind.String)
					pending.Add(ResolveFormAsync(client, sessionId, evt with { Properties = form.Clone() }, done, cancellationToken));
			}
			if (!cancellationToken.IsCancellationRequested)
				throw new IOException("OpenCode event stream ended unexpectedly.");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Stream closed on completion — expected.
		}
		catch (Exception ex)
		{
			// The event bus carries permissions/forms and live diagnostics. A lost stream must
			// fault the attempt rather than leave a native input request waiting forever.
			done.TrySetException(new OpenCodeClientUnhealthyException(
				sessionId, "event_stream_lost", probeDetails: ex.Message,
				message: $"OpenCode event stream for session '{sessionId}' was lost: {ex.Message}",
				innerException: ex));
		}
		finally
		{
			await Task.WhenAll(pending).ConfigureAwait(false);
		}
	}

	private OpenCodePromptRequest BuildPromptRequest(string prompt, OpenCodeModelRef modelRef, string? agentName, bool includeAttachments = true)
	{
		var parts = new List<OpenCodePartDto> { OpenCodePartDto.TextPart(prompt) };
		foreach (var attachment in includeAttachments ? _attachments : [])
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
			Model = BuildModelDto(modelRef),
			// When routing through a per-step agent, the agent's config carries the system
			// prompt + reasoning; otherwise send the system prompt inline.
			Agent = agentName,
			System = agentName is null ? _systemPrompt : null,
			Parts = parts,
		};
	}

	private OpenCodeModelDto BuildModelDto(OpenCodeModelRef model)
	{
		var parts = model.ModelId.Split('#', 2);
		return new OpenCodeModelDto
		{
			ProviderId = model.ProviderId,
			ModelId = parts[0],
			Variant = _reasoningLevel?.ToString().ToLowerInvariant() ?? (parts.Length > 1 ? parts[1] : null)
		};
	}

	private async Task ResolvePermissionAsync(IOpenCodeClient client, string sessionId, OpenCodeServerEvent evt, TaskCompletionSource done, CancellationToken cancellationToken)
	{
		try
		{
			var targetSession = evt.Properties.GetProperty("sessionID").GetString()!;
			if (!await client.IsSessionWithinAsync(targetSession, sessionId, cancellationToken).ConfigureAwait(false))
				return;
			var (permissionId, kind, target) = ParsePermission(evt.Properties);
			if (permissionId is null)
				return;

			var response = await DecidePermissionAsync(kind, target, cancellationToken).ConfigureAwait(false);
			await client.RespondPermissionAsync(targetSession, permissionId, response, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			LogPermissionError(ex, sessionId);
			done.TrySetException(ex);
		}
	}

	private async Task ResolveFormAsync(IOpenCodeClient client, string sessionId, OpenCodeServerEvent evt, TaskCompletionSource done, CancellationToken cancellationToken)
	{
		try
		{
			var targetSession = evt.Properties.GetProperty("sessionID").GetString()!;
			if (!await client.IsSessionWithinAsync(targetSession, sessionId, cancellationToken).ConfigureAwait(false))
				return;
			sessionId = targetSession;
			var id = evt.Properties.TryGetProperty("id", out var value) ? value.GetString() : evt.Properties.GetProperty("formID").GetString();
			if (id is null)
				return;
			if (!_humanInput || _engineToolContext is null)
			{
				await client.ReplyFormAsync(sessionId, id, null, cancellationToken).ConfigureAwait(false);
				return;
			}
			await _permissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var form = await client.GetFormAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
				var answer = await OpenCodeFormAnswers.ReadAsync(form, _engineToolContext, cancellationToken).ConfigureAwait(false);
				await client.ReplyFormAsync(sessionId, id, answer, cancellationToken).ConfigureAwait(false);
			}
			finally { _permissionGate.Release(); }
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex) { done.TrySetException(ex); }
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
		return (Get("id"), Get("action") ?? Get("type") ?? Get("kind"),
			perm.TryGetProperty("resources", out var resources) ? string.Join("\n", resources.EnumerateArray().Select(r => r.GetString())) : Get("title") ?? Get("pattern"));
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

	[LoggerMessage(EventId = 224, Level = LogLevel.Warning, Message = "OpenCode: required MCP load-status check failed before prompt submission")]
	private partial void LogMcpLoadProbeFailed(Exception ex);

	[LoggerMessage(EventId = 225, Level = LogLevel.Information, Message = "OpenCode: resuming session {SessionId} for model {Model} after a swap")]
	private partial void LogSessionResumed(string sessionId, string model);

	[LoggerMessage(EventId = 226, Level = LogLevel.Warning, Message = "OpenCode: resume of session {SessionId} failed ({Reason}); cold-restarting on a new session")]
	private partial void LogResumeSessionMissing(string sessionId, string reason);

	[LoggerMessage(EventId = 227, Level = LogLevel.Warning, Message = "OpenCode session cleanup failed for {SessionId}")]
	private partial void LogSessionCleanupFailed(Exception ex, string sessionId);

	[LoggerMessage(EventId = 228, Level = LogLevel.Information, Message = "OpenCode session {SessionId} continuation {Continuation}: {Reason}")]
	private partial void LogContinuing(string sessionId, int continuation, string reason);

	[LoggerMessage(EventId = 229, Level = LogLevel.Warning, Message = "OpenCode native plan observation failed")]
	private partial void LogPlanCaptureFailed(Exception exception);
}
