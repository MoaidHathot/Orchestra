using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

public partial class PromptExecutor : Executor<PromptOrchestrationStep>
{
	private readonly AgentBuilder _agentBuilder;
	private readonly IOrchestrationReporter _reporter;
	private readonly IPromptFormatter _formatter;
	private readonly EngineToolRegistry _engineToolRegistry;
	private readonly IMcpResolver? _mcpResolver;
	private readonly IPendingInputStore _pendingInputStore;
	private readonly IHumanInputWaiter _humanInputWaiter;
	private readonly string? _serverUrl;
	private readonly ILogger<PromptExecutor> _logger;
	private readonly int _maxAgentSwapAttempts;
	private readonly RequestUserInputTool _requestUserInputTool = new();

	/// <summary>
	/// Default budget for executor-level CLI-swap retries when the underlying agent
	/// surfaces an <see cref="AgentSessionErrorDetails.ExhaustedCliRetries"/> failure
	/// that the in-agent swap loop did not (or could not) recover from. One extra
	/// attempt is enough in practice: a fresh CLI process re-rolls upstream provider
	/// routing / connection pool and typically clears the upstream blip.
	/// </summary>
	public const int DefaultMaxAgentSwapAttempts = 1;

	public PromptExecutor(
		AgentBuilder agentBuilder,
		IOrchestrationReporter reporter,
		IPromptFormatter formatter,
		ILogger<PromptExecutor> logger,
		EngineToolRegistry? engineToolRegistry = null,
		IMcpResolver? mcpResolver = null,
		IPendingInputStore? pendingInputStore = null,
		IHumanInputWaiter? humanInputWaiter = null,
		string? serverUrl = null,
		int maxAgentSwapAttempts = DefaultMaxAgentSwapAttempts)
	{
		_agentBuilder = agentBuilder;
		_reporter = reporter;
		_formatter = formatter;
		_engineToolRegistry = engineToolRegistry ?? EngineToolRegistry.CreateDefault();
		_mcpResolver = mcpResolver;
		_pendingInputStore = pendingInputStore ?? NullPendingInputStore.Instance;
		_humanInputWaiter = humanInputWaiter ?? NullHumanInputWaiter.Instance;
		_serverUrl = serverUrl;
		_logger = logger;
		_maxAgentSwapAttempts = Math.Max(0, maxAgentSwapAttempts);
	}

	/// <summary>
	/// Executes the prompt step with an executor-level swap-retry safety net. If the
	/// inner attempt fails with <see cref="AgentSessionErrorDetails.ExhaustedCliRetries"/>
	/// set (CLI's own "Failed to get response from the AI model; retried N times" error),
	/// we re-run the entire step against a fresh agent instance, up to
	/// <see cref="DefaultMaxAgentSwapAttempts"/> extra attempts.
	///
	/// This is a belt-and-suspenders complement to <c>CopilotAgent</c>'s in-process
	/// swap loop. The in-agent loop only fires when the failure is observed inside
	/// the same <c>RunSessionAsync</c> call; if the error surfaces via a code path
	/// that bypasses the in-agent classifier (e.g. when the SDK reports the failure
	/// but the swap classifier returns false for any reason), this executor-level
	/// fallback still recovers the step instead of failing the orchestration outright.
	/// </summary>
	public override async Task<ExecutionResult> ExecuteAsync(
		PromptOrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		var attempt = 0;
		while (true)
		{
			var result = await ExecuteOnceAsync(step, context, cancellationToken).ConfigureAwait(false);

			// Only retry on a Failed result whose details flag the CLI exhaustion pattern.
			// Cancelled / NoAction / Succeeded short-circuit immediately.
			//
			// CRITICAL: also short-circuit when the LLM already declared a terminal status
			// via orchestra_set_status (CapturedStatusOverride is set). Re-running the
			// prompt would discard the LLM's decision and could flip a declared success
			// into a swap-induced failure — exactly the failure mode that motivated this
			// guard. The captured override is honored unconditionally: success/no_action
			// short-circuits with a synthesized result; LLM-declared failures propagate
			// as-is (no retry, no override synthesis).
			if (result.CapturedStatusOverride is ExecutionStatus.Succeeded or ExecutionStatus.NoAction
				&& result.Status == ExecutionStatus.Failed)
			{
				LogExecutorSkipRetryLlmTerminalStatus(step.Name, result.CapturedStatusOverride.ToString()!, result.ErrorMessage ?? "(no message)");
				return SynthesizeResultFromCapturedOverride(result);
			}

			if (result.Status != ExecutionStatus.Failed
				|| result.ErrorDetails?.ExhaustedCliRetries != true
				|| attempt >= _maxAgentSwapAttempts
				|| cancellationToken.IsCancellationRequested)
			{
				if (attempt > 0 && result.Status == ExecutionStatus.Failed
					&& result.ErrorDetails?.ExhaustedCliRetries == true)
				{
					LogExecutorSwapBudgetExhausted(step.Name, attempt, _maxAgentSwapAttempts, result.ErrorMessage ?? "(no message)");
				}
				return result;
			}

			attempt++;
			LogExecutorSwapTriggered(step.Name, attempt, _maxAgentSwapAttempts, result.ErrorMessage ?? "(no message)");
		}
	}

	/// <summary>
	/// Constructs a non-Failed <see cref="ExecutionResult"/> from a Failed result whose
	/// <see cref="ExecutionResult.CapturedStatusOverride"/> is <see cref="ExecutionStatus.Succeeded"/>
	/// or <see cref="ExecutionStatus.NoAction"/>. Used when a post-set_status transport
	/// failure would otherwise discard the LLM's declared outcome.
	///
	/// Note: the synthesized result carries an empty content because the original Failed
	/// result didn't capture the LLM's response body — but that's strictly better than
	/// failing a step the LLM already finished. Future work could plumb the captured
	/// content through ExecuteOnceAsync's catch as well.
	/// </summary>
	private static ExecutionResult SynthesizeResultFromCapturedOverride(ExecutionResult failedResult)
	{
		var status = failedResult.CapturedStatusOverride!.Value;
		return new ExecutionResult
		{
			Content = string.Empty,
			Status = status,
			ErrorMessage = null,
			RawDependencyOutputs = failedResult.RawDependencyOutputs,
			PromptSent = failedResult.PromptSent,
			ActualModel = failedResult.ActualModel,
			SelectedModel = failedResult.SelectedModel,
			RequestedModelInfo = failedResult.RequestedModelInfo,
			SelectedModelInfo = failedResult.SelectedModelInfo,
			ActualModelInfo = failedResult.ActualModelInfo,
			Trace = failedResult.Trace,
			SavedFiles = failedResult.SavedFiles,
			RetryHistory = failedResult.RetryHistory,
			CapturedStatusOverride = status,
		};
	}

	private async Task<ExecutionResult> ExecuteOnceAsync(
		PromptOrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		// Capture raw dependency outputs before building the prompt
		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);

		// Get the raw user prompt before input handler processing
		var userPromptRaw = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);
		userPromptRaw = TemplateResolver.Resolve(userPromptRaw, context.Parameters, context, step.DependsOn, step);

		// Create event processor to handle agent events and collect trace data
		var eventProcessor = new AgentEventProcessor(_reporter, step.Name);

		// Resolve template expressions in MCP configurations (param, env, vars, orchestration,
		// AND — for `mcps[].timeoutSeconds` only — step-output references such as
		// {{validate-inputs.output.controllerMcpTimeoutSeconds}}) before building diagnostics
		// or the agent config, so resolved values are visible.
		var resolvedMcps = step.Mcps
			.Select(m => TemplateResolver.ResolveStaticMcp(m, context.Parameters, context, step.DependsOn, step))
			.ToArray();

		// Replace globally shared MCPs with remote proxy endpoints, and stamp parent-execution
		// headers on remote MCPs that target Orchestra's own server endpoints. The headers let
		// /mcp/data tool handlers (e.g. invoke_orchestration) auto-populate parentExecutionId
		// for nested invocations — restoring run lineage that was previously lost when an LLM
		// agent recursively invoked orchestrations through MCP.
		if (_mcpResolver is not null)
		{
			var parentAnnotation = new ParentExecutionAnnotation
			{
				ExecutionId = context.OrchestrationInfo.RunId,
				OrchestrationName = context.OrchestrationInfo.Name,
				StepName = step.Name,
				RootExecutionId = context.RootExecutionId,
			};
			resolvedMcps = _mcpResolver.Resolve(resolvedMcps, parentAnnotation);
		}
		var resolvedSubagents = ResolveSubagentMcps(step.Subagents, context, step.DependsOn, step);

		// Build MCP server descriptions for trace diagnostics (using resolved values)
		var mcpServerDescriptions = BuildMcpServerDescriptions(resolvedMcps);

		// Resolve static template expressions in model and prompt fields.
		// These fields support param, env, vars, orchestration expressions (not step outputs
		// for model; full resolution including step outputs for prompts).
		var resolvedModel = TemplateResolver.ResolveStatic(
			step.Model ?? context.DefaultModel ?? throw new InvalidOperationException(
				$"Step '{step.Name}' has no 'model' and the orchestration has no 'defaultModel'. " +
				$"Either specify 'model' on the step or set 'defaultModel' at the orchestration level."),
			context.Parameters, context);
		var resolvedSystemPrompt = TemplateResolver.Resolve(step.SystemPrompt, context.Parameters, context, step.DependsOn, step);
		var resolvedOutputHandlerPrompt = step.OutputHandlerPrompt is not null
			? TemplateResolver.Resolve(step.OutputHandlerPrompt, context.Parameters, context, step.DependsOn, step)
			: null;

		// Create a fresh engine tool context for this execution
		var enabledOptInTools = ResolveEnabledOptInTools(step, context);
		var respondUrlBuilder = _serverUrl is null
			? (Func<string, string, string, string?>?)null
			: (orchestrationName, runId, stepName) =>
				$"{_serverUrl.TrimEnd('/')}/api/orchestrations/{Uri.EscapeDataString(orchestrationName)}/runs/{Uri.EscapeDataString(runId)}/respond?step={Uri.EscapeDataString(stepName)}";

		var engineToolCtx = new EngineToolContext
		{
			TempFileStore = context.TempFileStore,
			StepName = step.Name,
			Reporter = _reporter,
			OrchestrationName = context.OrchestrationInfo.Name,
			RunId = context.OrchestrationInfo.RunId,
			HumanInputWaiter = _humanInputWaiter,
			PendingInputStore = _pendingInputStore,
			RespondUrlBuilder = respondUrlBuilder,
			EnabledOptInTools = enabledOptInTools,
			OnAwaitingInput = context.OnAwaitingInput,
			OnInputResolved = context.OnInputResolved,
		};
		var engineTools = BuildEngineToolsForStep(enabledOptInTools);

		// Create a CTS that engine tools (e.g., set_status) can cancel to signal
		// that the step is done and the agent should stop immediately.
		using var stepCompletionCts = new CancellationTokenSource();
		engineToolCtx.StepCompletionCts = stepCompletionCts;
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stepCompletionCts.Token);
		string? userPrompt = null;

		try
		{
			// Build the user prompt, incorporating dependency outputs and parameters
			userPrompt = BuildUserPrompt(step, context);

			// Log step MCPs for debugging
			LogStepMcps(step.Name, step.Mcps.Length, string.Join(", ", step.Mcps.Select(m => m.Name)));
			LogStepMcpNames(step.Name, string.Join(", ", step.McpNames));

			// Build and run the agent using an immutable config snapshot (thread-safe)
			var config = new AgentBuildConfig
			{
				Model = resolvedModel,
				SystemPrompt = resolvedSystemPrompt,
				Mcps = resolvedMcps,
				Subagents = resolvedSubagents,
				ReasoningLevel = step.ReasoningLevel,
				SystemPromptMode = step.SystemPromptMode ?? context.DefaultSystemPromptMode,
				Reporter = _reporter,
				EngineTools = engineTools,
				EngineToolCtx = engineToolCtx,
				SkillDirectories = step.SkillDirectories
					.Select(dir => TemplateResolver.Resolve(dir, context.Parameters, context, step.DependsOn, step))
					.ToArray(),
				SystemPromptSections = step.SystemPromptSections,
				InfiniteSessionConfig = step.InfiniteSessions,
				Attachments = ResolveAttachments(step.Attachments, context, step),
			};

			var agent = await _agentBuilder
				.BuildAgentAsync(config, cancellationToken);

			var task = agent.SendAsync(userPrompt, linkedCts.Token);

			// Process all agent events, collecting trace data
			await eventProcessor.ProcessEventsAsync(task, linkedCts.Token);

			var result = await task.GetResultAsync();

			// Check if any required MCP servers failed to start.
			// When MCP servers fail, the LLM runs without the expected tools and produces
			// unreliable output. Fail the step early with a clear error rather than
			// propagating the LLM's confused response as a "success."
			var failedMcpServers = eventProcessor.GetFailedMcpServers();
			if (failedMcpServers.Count > 0 && resolvedMcps.Length > 0)
			{
				var requiredFailed = failedMcpServers
					.Where(f => resolvedMcps.Any(m => string.Equals(m.Name, f, StringComparison.OrdinalIgnoreCase)))
					.ToList();

				if (requiredFailed.Count > 0)
				{
					var serverList = string.Join(", ", requiredFailed);
					var errorMessage = $"Required MCP server(s) failed to start: {serverList}. The step cannot execute without these tools.";
					var mcpFailTrace = eventProcessor.BuildPartialTrace(resolvedSystemPrompt, userPromptRaw, mcpServerDescriptions);
					_reporter.ReportStepTrace(step.Name, mcpFailTrace);
					_reporter.ReportStepError(step.Name, errorMessage);
					LogMcpServersFailed(step.Name, serverList);
					return ExecutionResult.Failed(errorMessage, rawDependencyOutputs, trace: mcpFailTrace, errorCategory: StepErrorCategory.McpFailure, savedFiles: context.TempFileStore?.GetFilesForStep(step.Name));
				}
			}

			// Report model and usage metadata if available
			// Note: step-completed event is now emitted centrally by OrchestrationExecutor
			// after this method returns, so we only report usage here.
			if (result.Usage is not null && result.ActualModel is not null)
			{
				_reporter.ReportUsage(step.Name, result.ActualModel, result.Usage);
			}

			var content = result.Content;
			string? rawContent = null;
			string? outputHandlerResult = null;

			// Apply output handler if specified
			if (resolvedOutputHandlerPrompt is not null)
			{
				rawContent = content;
				var handlerResult = await RunHandlerAsync(resolvedOutputHandlerPrompt, content, resolvedModel, step.Name, cancellationToken);
				content = handlerResult.Content;
				outputHandlerResult = handlerResult.FellBackToOriginal
					? $"[OUTPUT HANDLER FALLBACK] Original content used — handler returned empty/whitespace. Original: {rawContent}"
					: content;
			}

			// Convert usage to our TokenUsage type
			TokenUsage? tokenUsage = null;
			if (result.Usage is not null)
			{
				tokenUsage = new TokenUsage
				{
					InputTokens = (int)(result.Usage.InputTokens ?? 0),
					OutputTokens = (int)(result.Usage.OutputTokens ?? 0),
					CacheReadTokens = (int)(result.Usage.CacheReadTokens ?? 0),
					CacheWriteTokens = (int)(result.Usage.CacheWriteTokens ?? 0),
					Cost = result.Usage.Cost,
					Duration = result.Usage.Duration,
				};
			}

			// Build the execution trace from collected data
			var trace = eventProcessor.BuildTrace(
				resolvedSystemPrompt,
				userPromptRaw,
				userPrompt,
				rawContent ?? result.Content,
				outputHandlerResult,
				mcpServerDescriptions);

			// Report the step trace for live trace viewing
			_reporter.ReportStepTrace(step.Name, trace);

			// Check if an engine tool overrode the status (e.g., LLM called orchestra_set_status)
			if (engineToolCtx.HasStatusOverride && engineToolCtx.StatusOverride == ExecutionStatus.Failed)
			{
				var reason = engineToolCtx.StatusReason ?? "Step marked as failed by LLM";
				LogEngineToolStatusOverride(step.Name, reason);
				_reporter.ReportStepError(step.Name, reason);
				return WithOrchestrationComplete(ExecutionResult.Failed(
					reason,
					rawDependencyOutputs,
					userPrompt,
					result.ActualModel,
					trace,
					selectedModel: result.SelectedModel,
					requestedModelInfo: result.RequestedModelInfo,
					selectedModelInfo: result.SelectedModelInfo,
					actualModelInfo: result.ActualModelInfo,
					savedFiles: context.TempFileStore?.GetFilesForStep(step.Name)), engineToolCtx, step.Name);
			}

			if (engineToolCtx.HasStatusOverride && engineToolCtx.StatusOverride == ExecutionStatus.NoAction)
			{
				var reason = engineToolCtx.StatusReason ?? "No action needed";
				LogEngineToolNoActionOverride(step.Name, reason);
				return WithOrchestrationComplete(ExecutionResult.NoAction(
					reason,
					rawDependencyOutputs,
					userPrompt,
					result.ActualModel,
					tokenUsage,
					trace,
					selectedModel: result.SelectedModel,
					requestedModelInfo: result.RequestedModelInfo,
					selectedModelInfo: result.SelectedModelInfo,
					actualModelInfo: result.ActualModelInfo,
					savedFiles: context.TempFileStore?.GetFilesForStep(step.Name)), engineToolCtx, step.Name);
			}

			if (engineToolCtx.HasStatusOverride && engineToolCtx.StatusOverride == ExecutionStatus.Succeeded)
			{
				var reason = engineToolCtx.StatusReason ?? "Step marked as succeeded by LLM";
				LogEngineToolSuccessOverride(step.Name, reason);
			}

			return WithOrchestrationComplete(ExecutionResult.Succeeded(
				content,
				rawContent,
				rawDependencyOutputs,
				userPrompt,
				result.ActualModel,
				tokenUsage,
				trace,
				selectedModel: result.SelectedModel,
				requestedModelInfo: result.RequestedModelInfo,
				selectedModelInfo: result.SelectedModelInfo,
				actualModelInfo: result.ActualModelInfo,
				savedFiles: context.TempFileStore?.GetFilesForStep(step.Name)), engineToolCtx, step.Name);
		}
		catch (OperationCanceledException) when (engineToolCtx.StepCompletionRequested && !cancellationToken.IsCancellationRequested)
		{
			// The agent was cancelled because an engine tool (e.g., set_status) signaled
			// that the step is complete. Build a partial trace and use the status override.
			var trace = eventProcessor.BuildPartialTrace(resolvedSystemPrompt, userPromptRaw, mcpServerDescriptions);
			_reporter.ReportStepTrace(step.Name, trace);

			if (engineToolCtx.StatusOverride == ExecutionStatus.Failed)
			{
				var reason = engineToolCtx.StatusReason ?? "Step marked as failed by LLM";
				LogEngineToolStatusOverride(step.Name, reason);
				_reporter.ReportStepError(step.Name, reason);
				return WithOrchestrationComplete(ExecutionResult.Failed(
					reason, rawDependencyOutputs, trace: trace, savedFiles: context.TempFileStore?.GetFilesForStep(step.Name)), engineToolCtx, step.Name);
			}

			if (engineToolCtx.StatusOverride == ExecutionStatus.NoAction)
			{
				var reason = engineToolCtx.StatusReason ?? "No action needed";
				LogEngineToolNoActionOverride(step.Name, reason);
				return WithOrchestrationComplete(ExecutionResult.NoAction(
					reason, rawDependencyOutputs, trace: trace, savedFiles: context.TempFileStore?.GetFilesForStep(step.Name)), engineToolCtx, step.Name);
			}

			// Default: treat as succeeded
			var successReason = engineToolCtx.StatusReason ?? "Step completed by LLM";
			LogEngineToolSuccessOverride(step.Name, successReason);
			return WithOrchestrationComplete(ExecutionResult.Succeeded(
				successReason, rawDependencyOutputs: rawDependencyOutputs, trace: trace, savedFiles: context.TempFileStore?.GetFilesForStep(step.Name)), engineToolCtx, step.Name);
		}
		catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
		{
			// Let the caller decide whether cancellation means timeout, external cancel,
			// or host shutdown, while preserving the trace collected before cancellation.
			var trace = eventProcessor.BuildPartialTrace(
				resolvedSystemPrompt,
				userPromptRaw,
				mcpServerDescriptions,
				userPrompt);
			_reporter.ReportStepTrace(step.Name, trace);

			var partialResult = new ExecutionResult
			{
				Content = string.Empty,
				Status = ExecutionStatus.Cancelled,
				ErrorMessage = "Cancelled",
				RawDependencyOutputs = rawDependencyOutputs,
				PromptSent = userPrompt,
				Trace = trace,
				SavedFiles = context.TempFileStore?.GetFilesForStep(step.Name) ?? [],
			};

			throw new StepExecutionCanceledException("Prompt execution was cancelled.", partialResult, ex, cancellationToken);
		}
		catch (Exception ex)
		{
			// Build partial trace even on failure
			var trace = eventProcessor.BuildPartialTrace(resolvedSystemPrompt, userPromptRaw, mcpServerDescriptions);

			// Report the partial trace for live trace viewing
			_reporter.ReportStepTrace(step.Name, trace);

			// Detect agent-client-unhealthy faults via the marker interface so the engine
			// can categorize the failure as ClientUnhealthy and the executor's retry loop
			// can short-circuit (retries on a dead client are guaranteed to fail).
			// Also capture structured agent-session-error details (HTTP status, request id,
			// upstream URL, stack) via the IAgentSessionFailedException marker so they
			// land in run.json instead of being collapsed into ex.Message.
			// Walk inner exceptions in case a wrapper (AggregateException, etc.) hides them.
			var category = StepErrorCategory.ModelError;
			AgentSessionErrorDetails? errorDetails = null;
			for (var probe = ex; probe is not null; probe = probe.InnerException!)
			{
				if (probe is IAgentClientUnhealthyException unhealthy)
				{
					category = StepErrorCategory.ClientUnhealthy;
					LogStepFailedClientUnhealthy(step.Name, unhealthy.TriggeringSessionId, unhealthy.TriggeringFailureReason);
				}
				if (probe is IAgentSessionFailedException sessionFailed && sessionFailed.Details is not null && errorDetails is null)
				{
					// Capture the first details payload we encounter; outer wrappers
					// (AggregateException, etc.) typically don't carry one of their own.
					errorDetails = sessionFailed.Details;
				}
				if (probe.InnerException is null) break;
			}

			// Fallback exhaustion detection: when the CLI surfaces "Failed to get response
			// from the AI model; retried N times" via a path that did NOT mint an
			// IAgentSessionFailedException with the flag set (e.g. raw SDK exception from
			// SendAsync, or a wrapped/re-throw that lost the structured payload), we still
			// want the executor-level swap loop above to kick in. Synthesize a minimal
			// details record with ExhaustedCliRetries=true so ExecuteAsync's outer loop can
			// detect it the same way it detects the structured form. The message-pattern
			// check is intentionally lenient — same shape as
			// CopilotSessionHandler.LooksLikeCliExhaustedRetries — so the two stay in sync.
			if (errorDetails?.ExhaustedCliRetries != true && LooksLikeCliExhaustedRetriesMessage(ex.Message))
			{
				errorDetails = (errorDetails ?? new AgentSessionErrorDetails()) with { ExhaustedCliRetries = true };
			}

			_reporter.ReportStepError(step.Name, ex.Message, errorDetails);

			// Capture any terminal status the LLM declared via orchestra_set_status BEFORE
			// the transport failure. The executor-level swap-retry loop (ExecuteAsync) uses
			// this to skip retrying a step that the LLM already finished — otherwise a
			// post-success transport error would burn the retry budget and potentially
			// flip a declared success into a swap-induced failure.
			var failedResult = ExecutionResult.Failed(
				ex.Message,
				rawDependencyOutputs,
				trace: trace,
				errorCategory: category,
				savedFiles: context.TempFileStore?.GetFilesForStep(step.Name),
				errorDetails: errorDetails);

			if (!engineToolCtx.HasStatusOverride)
				return failedResult;

			return new ExecutionResult
			{
				Content = failedResult.Content,
				Status = failedResult.Status,
				ErrorMessage = failedResult.ErrorMessage,
				RawContent = failedResult.RawContent,
				RawDependencyOutputs = failedResult.RawDependencyOutputs,
				PromptSent = failedResult.PromptSent,
				ActualModel = failedResult.ActualModel,
				SelectedModel = failedResult.SelectedModel,
				RequestedModelInfo = failedResult.RequestedModelInfo,
				SelectedModelInfo = failedResult.SelectedModelInfo,
				ActualModelInfo = failedResult.ActualModelInfo,
				Usage = failedResult.Usage,
				Trace = failedResult.Trace,
				SavedFiles = failedResult.SavedFiles,
				RetryHistory = failedResult.RetryHistory,
				ErrorCategory = failedResult.ErrorCategory,
				ErrorDetails = failedResult.ErrorDetails,
				CapturedStatusOverride = engineToolCtx.StatusOverride,
				ChildOrchestrationInfo = failedResult.ChildOrchestrationInfo,
				OrchestrationCompleteRequested = failedResult.OrchestrationCompleteRequested,
				OrchestrationCompleteStatus = failedResult.OrchestrationCompleteStatus,
				OrchestrationCompleteStepName = failedResult.OrchestrationCompleteStepName,
				OrchestrationCompleteReason = failedResult.OrchestrationCompleteReason,
			};
		}
	}

	/// <summary>
	/// Detects the bundled Copilot CLI's "I exhausted my internal retries" error
	/// message shape so the executor-level swap loop can recognise it even when the
	/// failure surfaces via a non-structured exception path (i.e. without an
	/// <see cref="IAgentSessionFailedException"/> carrying
	/// <see cref="AgentSessionErrorDetails.ExhaustedCliRetries"/>). The pattern is
	/// intentionally identical to <c>CopilotSessionHandler.LooksLikeCliExhaustedRetries</c>
	/// so both detection sites stay in sync; duplicated here only because Orchestra.Engine
	/// must not take a build-time dependency on Orchestra.Copilot.
	/// </summary>
	internal static bool LooksLikeCliExhaustedRetriesMessage(string? message)
	{
		if (string.IsNullOrEmpty(message))
			return false;

		if (System.Text.RegularExpressions.Regex.IsMatch(
				message,
				@"retried\s+\d+\s+times",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase))
		{
			return true;
		}

		return message.Contains("Failed to get response from the AI model", StringComparison.OrdinalIgnoreCase);
	}

	private string BuildUserPrompt(PromptOrchestrationStep step, OrchestrationExecutionContext context)
	{
		var userPrompt = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);

		// Resolve {{stepName.output}} and {{stepName.rawOutput}} template expressions inline.
		// This uses the same TemplateResolver as Command/Http/Transform steps, with a fallback
		// to TryGetResult for steps not listed in DependsOn (e.g. transitive dependencies).
		userPrompt = TemplateResolver.Resolve(userPrompt, context.Parameters, context, step.DependsOn, step);

		var dependencyOutputsDict = context.GetDependencyOutputs(step.DependsOn);
		var dependencyOutputs = _formatter.FormatDependencyOutputs(dependencyOutputsDict);
		var loopFeedback = context.ConsumeLoopFeedback(step.Name);

		return _formatter.BuildUserPrompt(userPrompt, dependencyOutputs, loopFeedback, step.InputHandlerPrompt);
	}

	/// <summary>
	/// Resolves the effective set of opt-in tool names for a step. Step-level
	/// <see cref="PromptOrchestrationStep.EnableTools"/> wins over orchestration-level
	/// <see cref="OrchestrationExecutionContext.DefaultEnableTools"/>. Empty array means
	/// no opt-in tools (always-on tools still apply).
	/// </summary>
	private static IReadOnlyCollection<string> ResolveEnabledOptInTools(PromptOrchestrationStep step, OrchestrationExecutionContext context)
	{
		var source = step.EnableTools ?? context.DefaultEnableTools;
		if (source.Length == 0)
			return Array.Empty<string>();

		return new HashSet<string>(source, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Builds the engine tool collection for a step. Always-on tools come from the
	/// registry; opt-in tools (currently <c>request_user_input</c>) are appended only
	/// when the step's <c>enableTools</c> set lists them.
	/// </summary>
	private IReadOnlyCollection<IEngineTool> BuildEngineToolsForStep(IReadOnlyCollection<string> enabledOptInTools)
	{
		var alwaysOn = _engineToolRegistry.GetAll();
		if (enabledOptInTools.Count == 0)
			return alwaysOn;

		var combined = new List<IEngineTool>(alwaysOn);

		if (enabledOptInTools.Contains(RequestUserInputTool.OptInName))
		{
			// Avoid duplicate registration if a custom registry already includes the tool.
			if (!combined.Any(t => string.Equals(t.Name, _requestUserInputTool.Name, StringComparison.OrdinalIgnoreCase)))
			{
				combined.Add(_requestUserInputTool);
			}
		}

		return combined;
	}

	private static string InjectParameters(string prompt, string[] parameterNames, Dictionary<string, string> parameters)
	{
		if (parameterNames.Length == 0 || parameters.Count == 0)
			return prompt;

		var result = prompt;
		foreach (var name in parameterNames)
		{
			if (parameters.TryGetValue(name, out var value))
			{
				result = result.Replace($"{{{{{name}}}}}", value);
			}
		}

		return result;
	}

	private static List<string> BuildMcpServerDescriptions(Mcp[] mcps)
	{
		var descriptions = new List<string>(mcps.Length);
		foreach (var mcp in mcps)
		{
			var desc = mcp switch
			{
				LocalMcp local => $"{mcp.Name} (local: {local.Command} {string.Join(" ", local.Arguments)})",
				RemoteMcp remote => $"{mcp.Name} (remote: {remote.Endpoint})",
				_ => mcp.Name,
			};
			descriptions.Add(desc);
		}
		return descriptions;
	}

	/// <summary>
	/// Creates copies of subagents with their MCP configurations resolved using
	/// static/orchestration-level template expressions (param, env, vars, orchestration),
	/// and — for <c>mcps[].timeoutSeconds</c> only — step-output references such as
	/// <c>{{validate-inputs.output.foo}}</c> against the parent step's dependencies.
	/// Returns the original array if no subagents have MCPs to resolve.
	/// </summary>
	private static Subagent[] ResolveSubagentMcps(
		Subagent[] subagents,
		OrchestrationExecutionContext context,
		string[] parentDependsOn,
		OrchestrationStep parentStep)
	{
		if (subagents.Length == 0)
			return subagents;

		// Check if any subagent has MCPs — if not, skip cloning entirely
		if (!subagents.Any(s => s.Mcps.Length > 0))
			return subagents;

		return subagents.Select(s =>
		{
			if (s.Mcps.Length == 0)
				return s;

			// Clone the subagent with resolved MCP configurations
			var resolved = new Subagent
			{
				Name = s.Name,
				DisplayName = s.DisplayName,
				Description = s.Description,
				Prompt = s.Prompt,
				Tools = s.Tools,
				Infer = s.Infer,
			};
			resolved.Mcps = s.Mcps
				.Select(m => TemplateResolver.ResolveStaticMcp(m, context.Parameters, context, parentDependsOn, parentStep))
				.ToArray();
			return resolved;
		}).ToArray();
	}

	private static ImageAttachment[] ResolveAttachments(
		ImageAttachment[] attachments,
		OrchestrationExecutionContext context,
		PromptOrchestrationStep step)
	{
		if (attachments.Length == 0)
			return attachments;

		return attachments.Select(a => a switch
		{
			FileImageAttachment file => new FileImageAttachment
			{
				Path = TemplateResolver.Resolve(file.Path, context.Parameters, context, step.DependsOn, step),
				DisplayName = file.DisplayName,
			},
			BlobImageAttachment blob => new BlobImageAttachment
			{
				Data = TemplateResolver.Resolve(blob.Data, context.Parameters, context, step.DependsOn, step),
				MimeType = blob.MimeType,
				DisplayName = blob.DisplayName,
			},
			_ => a,
		}).ToArray();
	}

	private async Task<OutputHandlerResult> RunHandlerAsync(
		string handlerPrompt,
		string content,
		string model,
		string stepName,
		CancellationToken cancellationToken)
	{
		try
		{
			var systemPrompt = _formatter.BuildTransformationSystemPrompt(handlerPrompt);

			var config = new AgentBuildConfig
			{
				Model = model,
				SystemPrompt = systemPrompt,
				SystemPromptMode = SystemPromptMode.Replace,
				Mcps = [],
				Reporter = _reporter,
			};

			var agent = await _agentBuilder
				.BuildAgentAsync(config, cancellationToken);

			var wrappedContent = _formatter.WrapContentForTransformation(content);

			var task = agent.SendAsync(wrappedContent, cancellationToken);

			// Process output handler events so usage/trace data is captured
			var handlerEventProcessor = new AgentEventProcessor(_reporter, $"{stepName}:output-handler");
			await handlerEventProcessor.ProcessEventsAsync(task, cancellationToken);

			var result = await task.GetResultAsync();

			// Report usage for the output handler call separately
			if (result.Usage is not null && result.ActualModel is not null)
			{
				_reporter.ReportUsage($"{stepName}:output-handler", result.ActualModel, result.Usage);
			}

			// Guard against empty output handler response — fall back to original content
			if (string.IsNullOrWhiteSpace(result.Content))
			{
				LogOutputHandlerEmptyResponse(stepName);
				return new OutputHandlerResult(content, FellBackToOriginal: true);
			}

			return new OutputHandlerResult(result.Content, FellBackToOriginal: false);
		}
		catch (OperationCanceledException)
		{
			throw; // Propagate cancellation
		}
		catch (Exception ex)
		{
			// Output handler failure should not lose the primary step output.
			// Log the error and fall back to the raw (unprocessed) content.
			LogOutputHandlerFailed(ex);
			return new OutputHandlerResult(content, FellBackToOriginal: true);
		}
	}

	/// <summary>
	/// Result from the output handler, including whether a fallback to the original content was used.
	/// </summary>
	private sealed record OutputHandlerResult(string Content, bool FellBackToOriginal);

	/// <summary>
	/// Copies orchestration-complete flags from the engine tool context onto the execution result.
	/// If the LLM called orchestra_complete, the returned result will carry the signal
	/// so the orchestration executor can halt all remaining steps.
	/// </summary>
	private static ExecutionResult WithOrchestrationComplete(ExecutionResult result, EngineToolContext ctx, string stepName)
	{
		var savedFiles = ctx.TempFileStore?.GetFilesForStep(stepName) ?? result.SavedFiles;
		var capturedOverride = ctx.HasStatusOverride ? ctx.StatusOverride : result.CapturedStatusOverride;

		if (!ctx.OrchestrationCompleteRequested)
			return WithSavedFiles(result, savedFiles, capturedOverride);

		return new ExecutionResult
		{
			Content = result.Content,
			Status = result.Status,
			ErrorMessage = result.ErrorMessage,
			RawContent = result.RawContent,
			RawDependencyOutputs = result.RawDependencyOutputs,
			PromptSent = result.PromptSent,
			ActualModel = result.ActualModel,
			SelectedModel = result.SelectedModel,
			RequestedModelInfo = result.RequestedModelInfo,
			SelectedModelInfo = result.SelectedModelInfo,
			ActualModelInfo = result.ActualModelInfo,
			Usage = result.Usage,
			Trace = result.Trace,
			SavedFiles = savedFiles,
			RetryHistory = result.RetryHistory,
			ErrorCategory = result.ErrorCategory,
			CapturedStatusOverride = capturedOverride,
			OrchestrationCompleteRequested = true,
			OrchestrationCompleteStatus = ctx.OrchestrationCompleteStatus,
			OrchestrationCompleteReason = ctx.OrchestrationCompleteReason,
			OrchestrationCompleteStepName = stepName,
		};
	}

	private static ExecutionResult WithSavedFiles(ExecutionResult result, string[] savedFiles, ExecutionStatus? capturedStatusOverride = null) => new()
	{
		Content = result.Content,
		Status = result.Status,
		ErrorMessage = result.ErrorMessage,
		RawContent = result.RawContent,
		RawDependencyOutputs = result.RawDependencyOutputs,
		PromptSent = result.PromptSent,
		ActualModel = result.ActualModel,
		SelectedModel = result.SelectedModel,
		RequestedModelInfo = result.RequestedModelInfo,
		SelectedModelInfo = result.SelectedModelInfo,
		ActualModelInfo = result.ActualModelInfo,
		Usage = result.Usage,
		Trace = result.Trace,
		SavedFiles = savedFiles,
		RetryHistory = result.RetryHistory,
		ErrorCategory = result.ErrorCategory,
		ErrorDetails = result.ErrorDetails,
		CapturedStatusOverride = capturedStatusOverride ?? result.CapturedStatusOverride,
		OrchestrationCompleteRequested = result.OrchestrationCompleteRequested,
		OrchestrationCompleteStatus = result.OrchestrationCompleteStatus,
		OrchestrationCompleteReason = result.OrchestrationCompleteReason,
		OrchestrationCompleteStepName = result.OrchestrationCompleteStepName,
	};

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Debug,
		Message = "Step '{StepName}' has {McpCount} MCPs: [{McpNames}]")]
	private partial void LogStepMcps(string stepName, int mcpCount, string mcpNames);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Debug,
		Message = "Step '{StepName}' McpNames configuration: [{McpNames}]")]
	private partial void LogStepMcpNames(string stepName, string mcpNames);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' status overridden to failed by engine tool: {Reason}")]
	private partial void LogEngineToolStatusOverride(string stepName, string reason);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' explicitly marked as succeeded by engine tool: {Reason}")]
	private partial void LogEngineToolSuccessOverride(string stepName, string reason);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' marked as no_action by engine tool: {Reason}")]
	private partial void LogEngineToolNoActionOverride(string stepName, string reason);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' failed because required MCP server(s) did not start: {Servers}")]
	private partial void LogMcpServersFailed(string stepName, string servers);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Warning,
		Message = "Output handler failed, falling back to raw content")]
	private partial void LogOutputHandlerFailed(Exception ex);

	[LoggerMessage(
		EventId = 8,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' output handler returned empty/whitespace response, falling back to original content")]
	private partial void LogOutputHandlerEmptyResponse(string stepName);

	[LoggerMessage(
		EventId = 9,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' failed because the agent client is unhealthy (triggered by session '{TriggeringSessionId}': {TriggeringFailureReason}). Categorized as ClientUnhealthy; retries will be skipped.")]
	private partial void LogStepFailedClientUnhealthy(string stepName, string triggeringSessionId, string triggeringFailureReason);

	[LoggerMessage(
		EventId = 10,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' agent CLI exhausted internal retries — re-running the step on a fresh agent (executor-level swap attempt {Attempt}/{Budget}). Last error: {LastError}")]
	private partial void LogExecutorSwapTriggered(string stepName, int attempt, int budget, string lastError);

	[LoggerMessage(
		EventId = 11,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' executor-level swap budget exhausted after {Attempt}/{Budget} extra attempt(s); failing the step. Last error: {LastError}")]
	private partial void LogExecutorSwapBudgetExhausted(string stepName, int attempt, int budget, string lastError);

	[LoggerMessage(
		EventId = 12,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' agent surfaced a transport error AFTER the LLM declared a terminal status via orchestra_set_status ({CapturedStatus}); skipping executor-level swap retry and honoring the LLM's declared outcome. Suppressed error: {SuppressedError}")]
	private partial void LogExecutorSkipRetryLlmTerminalStatus(string stepName, string capturedStatus, string suppressedError);

	#endregion
}
