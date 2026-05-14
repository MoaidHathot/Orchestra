using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Orchestra.Host.Api;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
using Orchestra.Host.Persistence;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Services;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.McpServer;

/// <summary>
/// MCP tools for the Orchestra data plane.
/// Provides orchestration discovery and invocation capabilities to external AI agents.
/// </summary>
[McpServerToolType]
public sealed partial class DataPlaneTools
{
	[McpServerTool(Name = "list_orchestrations"), Description(
		"Lists orchestrations registered in Orchestra. " +
		"Returns orchestration IDs, names, descriptions, parameters, and input schemas. " +
		"Use the returned information to understand what orchestrations are available and what inputs they require before invoking them.")]
	public static string ListOrchestrations(
		OrchestrationRegistry registry,
		OrchestrationTagStore tagStore,
		[Description("Optional comma-separated tags to filter orchestrations. Only orchestrations matching ALL specified tags are returned.")] string? tags = null,
		[Description("Optional name pattern to filter orchestrations. Matches against orchestration name (case-insensitive, substring match).")] string? namePattern = null)
	{
		var entries = registry.GetAll().AsEnumerable();

		// Filter by tags
		if (!string.IsNullOrWhiteSpace(tags))
		{
			var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			entries = entries.Where(e =>
			{
				var effectiveTags = tagStore.GetEffectiveTags(e.Id, e.Orchestration.Tags);
				return tagList.All(t => effectiveTags.Contains(t, StringComparer.OrdinalIgnoreCase));
			});
		}

		// Filter by name pattern
		if (!string.IsNullOrWhiteSpace(namePattern))
		{
			entries = entries.Where(e =>
				e.Orchestration.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase));
		}

		var result = entries.Select(e =>
		{
			var o = e.Orchestration;
			var parameterNames = o.Steps.SelectMany(s => s.Parameters).Distinct().ToArray();

			return new
			{
				id = e.Id,
				name = o.Name,
				description = o.Description,
				version = o.Version,
				tags = tagStore.GetEffectiveTags(e.Id, o.Tags),
				parameters = parameterNames,
				inputs = o.Inputs?.ToDictionary(
					kvp => kvp.Key,
					kvp => new
					{
						type = kvp.Value.Type.ToString().ToLowerInvariant(),
						description = kvp.Value.Description,
						required = kvp.Value.Required,
						@default = kvp.Value.Default,
						@enum = kvp.Value.Enum.Length > 0 ? kvp.Value.Enum : null,
						multiline = kvp.Value.Multiline ? true : (bool?)null,
					}),
				stepCount = o.Steps.Length,
			};
		}).ToArray();

		return JsonSerializer.Serialize(new { count = result.Length, orchestrations = result }, s_jsonOptions);
	}

	[McpServerTool(Name = "invoke_orchestration"), Description(
		"Invokes an orchestration by its ID with the specified parameters. " +
		"By default, returns immediately with an execution ID (async mode). " +
		"Use mode='sync' to block until the orchestration completes (with optional timeout). " +
		"Use get_orchestration_status to check the result of async invocations.")]
	public static async Task<string> InvokeOrchestration(
		IChildOrchestrationLauncher launcher,
		IHttpContextAccessor httpContextAccessor,
		McpServerOptions mcpServerOptions,
		[Description("The orchestration ID to invoke.")] string orchestrationId,
		[Description("JSON object with parameter key-value pairs. All values must be strings.")] string? parameters = null,
		[Description("Execution mode: 'async' (default, returns immediately with execution ID) or 'sync' (blocks until completion).")] string mode = "async",
		[Description("Maximum seconds to wait in sync mode. Default: 300 (5 minutes). Ignored in async mode.")] int timeoutSeconds = 300,
		[Description("Optional metadata JSON object with key-value pairs for tracking (e.g., correlation IDs, ticket numbers).")] string? metadata = null,
		[Description("Parent execution ID for nested invocations. Set automatically when called from within an orchestration.")] string? parentExecutionId = null,
		CancellationToken cancellationToken = default)
	{
		// Parse parameters
		Dictionary<string, string>? parsedParams = null;
		if (!string.IsNullOrWhiteSpace(parameters))
		{
			try
			{
				parsedParams = JsonSerializer.Deserialize<Dictionary<string, string>>(parameters, s_jsonOptions);
			}
			catch (JsonException ex)
			{
				return JsonSerializer.Serialize(new { error = $"Invalid parameters JSON: {ex.Message}" }, s_jsonOptions);
			}
		}

		// Parse metadata (parse failures are non-fatal)
		Dictionary<string, string>? parsedMetadata = null;
		if (!string.IsNullOrWhiteSpace(metadata))
		{
			try
			{
				parsedMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadata, s_jsonOptions);
			}
			catch (JsonException) { /* non-fatal */ }
		}

		var isSync = string.Equals(mode, "sync", StringComparison.OrdinalIgnoreCase);

		// Sync-timeout vs transport-timeout sanity check.
		// The host applies a default transport timeout (DefaultOrchestraInvokeTimeoutSeconds)
		// to the MCP request to /mcp/data when the caller's orchestration `mcps[]` entry does
		// not override it. If the caller asks for a sync `timeoutSeconds` that is larger than
		// that default, the transport layer would abort this request long before the engine's
		// own sync-invoke timeout fires, producing a generic "cancelled by caller" record
		// even though the child made real progress (the classic 30-minute cliff).
		// We can't see the caller's per-mcps[] override here, but we CAN see the host default,
		// which is the most common source of this mismatch.
		var transportTimeoutSeconds = mcpServerOptions.DefaultOrchestraInvokeTimeoutSeconds;
		if (isSync
			&& transportTimeoutSeconds > 0
			&& timeoutSeconds > transportTimeoutSeconds - 60)
		{
			var minimumSafe = timeoutSeconds + 60;
			return JsonSerializer.Serialize(new
			{
				error = "timeout-mismatch",
				message =
					$"Requested sync timeoutSeconds ({timeoutSeconds}) exceeds the configured MCP transport timeout " +
					$"({transportTimeoutSeconds}) by more than the 60s safety margin. The child run would be aborted " +
					$"by the MCP transport long before the engine's sync-invoke timeout fires, producing a generic " +
					$"\"cancelled by caller\" record. To fix: either lower timeoutSeconds to <= {transportTimeoutSeconds - 60}, " +
					$"or set `mcps[].timeoutSeconds` on the calling orchestration to at least {minimumSafe}, or set the " +
					$"host's `mcpServer.defaultOrchestraInvokeTimeoutSeconds` to 0 to disable the transport timeout entirely " +
					$"(server-side timeouts remain authoritative).",
				transportTimeoutSeconds,
				requestedSyncTimeoutSeconds = timeoutSeconds,
				minimumSafeTransportTimeoutSeconds = minimumSafe,
			}, s_jsonOptions);
		}

		// Auto-populate parentExecutionId from headers stamped by the engine when this MCP
		// tool was reached from inside an orchestration's prompt step. The LLM cannot pass
		// its own execution ID (it doesn't know it), so the engine's PromptExecutor +
		// McpManager set X-Orchestra-Parent-* headers on outbound connections to /mcp/data.
		// An explicit parentExecutionId argument from the caller still wins.
		var parentStepName = (string?)null;
		var parentOrchestrationName = (string?)null;
		if (string.IsNullOrWhiteSpace(parentExecutionId)
			&& httpContextAccessor.HttpContext is { } httpContext)
		{
			if (httpContext.Request.Headers.TryGetValue(OrchestraHeaders.ParentExecutionId, out var headerExecId)
				&& !string.IsNullOrWhiteSpace(headerExecId))
			{
				parentExecutionId = headerExecId.ToString();
			}
			if (httpContext.Request.Headers.TryGetValue(OrchestraHeaders.ParentStepName, out var headerStepName)
				&& !string.IsNullOrWhiteSpace(headerStepName))
			{
				parentStepName = headerStepName.ToString();
			}
			if (httpContext.Request.Headers.TryGetValue(OrchestraHeaders.ParentOrchestrationName, out var headerOrchName)
				&& !string.IsNullOrWhiteSpace(headerOrchName))
			{
				parentOrchestrationName = headerOrchName.ToString();
			}
		}

		ParentExecutionContext? parentContext = null;
		if (!string.IsNullOrWhiteSpace(parentExecutionId))
		{
			// Depth/root are filled in by the launcher from the live active-executions table.
			parentContext = new ParentExecutionContext
			{
				ParentExecutionId = parentExecutionId!,
				ParentStepName = parentStepName,
			};
		}

		// Triggered-by string identifies the chain on the persisted run record. Use the
		// orchestration name from headers when present so historical views can render
		// "child of <orchestration>:<runId>" without cross-referencing.
		var triggeredBy = parentExecutionId is not null
			? (parentOrchestrationName is not null
				? $"orchestration:{parentOrchestrationName}:{parentExecutionId}"
				: $"orchestration:{parentExecutionId}")
			: "mcp";

		var request = new ChildLaunchRequest
		{
			OrchestrationId = orchestrationId,
			Parameters = parsedParams,
			Mode = isSync ? ChildLaunchMode.Sync : ChildLaunchMode.Async,
			TimeoutSeconds = isSync ? timeoutSeconds : null,
			TriggeredBy = triggeredBy,
			ParentContext = parentContext,
			UserMetadata = parsedMetadata,
		};

		ChildOrchestrationHandle handle;
		try
		{
			handle = await launcher.LaunchAsync(request, cancellationToken);
		}
		catch (ChildOrchestrationLaunchException ex)
		{
			return JsonSerializer.Serialize(new { error = ex.Message }, s_jsonOptions);
		}

		if (!isSync)
		{
			return JsonSerializer.Serialize(new
			{
				executionId = handle.ExecutionId,
				orchestrationId = handle.OrchestrationId,
				orchestrationName = handle.OrchestrationName,
				mode = "async",
				status = "started",
				message = "Orchestration started. Use get_orchestration_status to check progress.",
				metadata = parsedMetadata,
			}, s_jsonOptions);
		}

		// Sync: await the run to completion. The launcher handles cleanup; we just translate
		// the result into the historical JSON response shape that external MCP clients expect.
		var result = await handle.Completion;

		if (result.TimedOut)
		{
			var timeoutCancellation = result.OrchestrationResult?.Cancellation;
			return JsonSerializer.Serialize(new
			{
				executionId = handle.ExecutionId,
				orchestrationId = handle.OrchestrationId,
				mode = "sync",
				status = "timeout",
				error = result.ErrorMessage ?? $"Orchestration did not complete within {timeoutSeconds} seconds.",
				cancellation = MapCancellation(timeoutCancellation),
			}, s_jsonOptions);
		}

		if (result.OrchestrationResult is null)
		{
			// Engine threw before producing a result
			return JsonSerializer.Serialize(new
			{
				executionId = handle.ExecutionId,
				orchestrationId = handle.OrchestrationId,
				mode = "sync",
				status = "error",
				error = result.ErrorMessage ?? "Orchestration ended without producing a result.",
			}, s_jsonOptions);
		}

		var orch = result.OrchestrationResult;
		return JsonSerializer.Serialize(new
		{
			executionId = handle.ExecutionId,
			orchestrationId = handle.OrchestrationId,
			orchestrationName = handle.OrchestrationName,
			mode = "sync",
			status = orch.Status.ToString().ToLowerInvariant(),
			completionReason = orch.CompletionReason,
			cancellation = MapCancellation(orch.Cancellation),
			stepResults = orch.StepResults.ToDictionary(
				kvp => kvp.Key,
				kvp => new
				{
					status = kvp.Value.Status.ToString().ToLowerInvariant(),
					content = TruncateContent(kvp.Value.Content, 4000),
				}),
			summary = TruncateContent(result.FinalContent, 8000),
			metadata = parsedMetadata,
		}, s_jsonOptions);
	}

	[McpServerTool(Name = "get_orchestration_status"), Description(
		"Gets the status and result of an orchestration execution by its execution ID. " +
		"Use this to check the progress of async invocations or to retrieve results after completion.")]
	public static async Task<string> GetOrchestrationStatus(
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		FileSystemRunStore runStore,
		[Description("The execution ID returned by invoke_orchestration.")] string executionId)
	{
		// Check active executions first
		if (activeExecutionInfos.TryGetValue(executionId, out var info))
		{
			return JsonSerializer.Serialize(new
			{
				executionId = info.ExecutionId,
				orchestrationId = info.OrchestrationId,
				orchestrationName = info.OrchestrationName,
				status = info.Status.ToString().ToLowerInvariant(),
				startedAt = info.StartedAt,
				triggeredBy = info.TriggeredBy,
				totalSteps = info.TotalSteps,
				completedSteps = info.CompletedSteps,
				currentStep = info.CurrentStep,
				parameters = info.Parameters,
				nesting = info.NestingMetadata is not null ? new
				{
					parentExecutionId = info.NestingMetadata.ParentExecutionId,
					rootExecutionId = info.NestingMetadata.RootExecutionId,
					depth = info.NestingMetadata.Depth,
				} : null,
			}, s_jsonOptions);
		}

		// Check completed runs via the run index
		var runIndex = await runStore.FindRunByIdAsync(executionId);
		if (runIndex is not null)
		{
			// Load the full run record for step details
			var run = await runStore.GetRunAsync(runIndex.OrchestrationName, runIndex.RunId);
			if (run is not null)
			{
				return JsonSerializer.Serialize(new
				{
					executionId = run.RunId,
					orchestrationName = run.OrchestrationName,
					status = run.Status.ToString().ToLowerInvariant(),
					startedAt = run.StartedAt,
					completedAt = run.CompletedAt,
					triggeredBy = run.TriggeredBy,
					parameters = run.Parameters,
					stepResults = run.StepRecords.ToDictionary(
						kvp => kvp.Key,
						kvp => new
						{
							status = kvp.Value.Status.ToString().ToLowerInvariant(),
							content = TruncateContent(kvp.Value.Content, 2000),
							errorMessage = kvp.Value.ErrorMessage,
						}),
					summary = TruncateContent(run.FinalContent, 4000),
					nesting = run.ParentExecutionId is not null || run.NestingDepth > 0 ? new
					{
						parentExecutionId = run.ParentExecutionId,
						parentStepName = run.ParentStepName,
						rootExecutionId = run.RootExecutionId,
						depth = run.NestingDepth,
					} : null,
				}, s_jsonOptions);
			}

			// Fall back to index-level summary
			return JsonSerializer.Serialize(new
			{
				executionId = runIndex.RunId,
				orchestrationName = runIndex.OrchestrationName,
				status = runIndex.Status.ToString().ToLowerInvariant(),
				startedAt = runIndex.StartedAt,
				completedAt = runIndex.CompletedAt,
				triggeredBy = runIndex.TriggeredBy,
				error = runIndex.ErrorMessage,
			}, s_jsonOptions);
		}

		return JsonSerializer.Serialize(new
		{
			error = $"No execution found with ID '{executionId}'. It may have expired or never existed."
		}, s_jsonOptions);
	}

	[McpServerTool(Name = "cancel_orchestration"), Description(
		"Cancels a running orchestration execution. " +
		"Only active (in-progress) executions can be cancelled. " +
		"Optionally pass a `reason` so the run record carries the cause (visible in /api/history and MCP responses).")]
	public static string CancelOrchestration(
		ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		ILoggerFactory loggerFactory,
		[Description("The execution ID to cancel.")] string executionId,
		[Description("Optional reason for the cancellation. Persisted on the run record's cancellation.detail field so consumers can distinguish why it was cancelled.")] string? reason = null)
	{
		if (!activeExecutions.TryGetValue(executionId, out var cts))
		{
			return JsonSerializer.Serialize(new
			{
				error = $"No active execution found with ID '{executionId}'."
			}, s_jsonOptions);
		}

		if (cts.IsCancellationRequested)
		{
			return JsonSerializer.Serialize(new
			{
				executionId,
				status = "already_cancelling",
				message = "Cancellation was already requested for this execution."
			}, s_jsonOptions);
		}

		// Attribute the cancel before triggering it so the engine's probe records a precise
		// CancellationDetails on the run record. Use ??= so explicit overrides set elsewhere
		// (e.g. HostShutdown from TriggerManager) win.
		var detail = string.IsNullOrWhiteSpace(reason)
			? "mcp:cancel_orchestration"
			: $"mcp:cancel_orchestration: {reason}";

		if (activeExecutionInfos.TryGetValue(executionId, out var info))
		{
			info.CancellationCauseOverride ??= new CancellationDetails
			{
				Kind = CancellationCauseKind.External,
				Source = "caller",
				Detail = detail,
				RequestedAt = DateTimeOffset.UtcNow,
			};
			info.Status = HostExecutionStatus.Cancelled;
		}

		var logger = loggerFactory.CreateLogger(typeof(DataPlaneTools));
		LogRunCancelRequested(logger, executionId, "mcp:cancel_orchestration", detail);

		cts.Cancel();

		return JsonSerializer.Serialize(new
		{
			executionId,
			status = "cancelling",
			message = "Cancellation requested. The orchestration will stop at the next safe point.",
			reason,
		}, s_jsonOptions);
	}

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Run cancel requested: executionId={ExecutionId}, source={Source}, detail={Detail}")]
	private static partial void LogRunCancelRequested(ILogger logger, string executionId, string source, string? detail);

	[McpServerTool(Name = "list_pending_inputs"), Description(
		"Lists orchestration runs currently awaiting human input (Approval steps and " +
		"orchestra_request_user_input tool calls). " +
		"Returns the orchestrationName, runId, stepName, kind (Approval or EngineTool), " +
		"prompt, choices (when constrained), and timestamps. " +
		"Use this to discover runs that need a response, then call respond_to_input to " +
		"unblock them.")]
	public static async Task<string> ListPendingInputs(
		IPendingInputStore pendingInputStore,
		[Description("Optional orchestration name to filter pending records by.")] string? orchestrationName = null)
	{
		var records = await pendingInputStore.ListAsync(orchestrationName);
		return JsonSerializer.Serialize(new
		{
			pending = records.Select(r => new
			{
				orchestrationName = r.OrchestrationName,
				runId = r.RunId,
				stepName = r.StepName,
				kind = r.Kind.ToString(),
				prompt = r.Prompt,
				choices = r.Choices.Length > 0 ? r.Choices : null,
				createdAt = r.CreatedAt,
				expiresAt = r.ExpiresAt,
			}).ToArray(),
			count = records.Count,
		}, s_jsonOptions);
	}

	[McpServerTool(Name = "respond_to_input"), Description(
		"Submits a response to a pending human-input wait, unblocking the orchestration. " +
		"Either 'choice' (must match one of the declared choices when present) or 'reply' " +
		"(free-form text) is required; both may be supplied (reply wins as the step's " +
		"output content). Returns 404 if no active wait exists for the run/step (the run " +
		"may have moved on, the host may have restarted, or the step may not yet be " +
		"executing). For long-lived approval gates that survive host restarts, the wait " +
		"is preserved across restarts; for engine-tool waits the agent session is volatile " +
		"and cannot be re-attached.")]
	public static string RespondToInput(
		IPendingInputStore pendingInputStore,
		IHumanInputWaiter humanInputWaiter,
		[Description("The orchestration name (matches the 'name' field of the registered orchestration).")] string orchestrationName,
		[Description("The run ID returned by invoke_orchestration or visible via list_pending_inputs.")] string runId,
		[Description("The step name awaiting input.")] string stepName,
		[Description("Optional constrained choice value. When the wait declared a 'choices' array, this must be one of the allowed values (case-insensitive).")] string? choice = null,
		[Description("Optional free-form reply text. Wins over 'choice' as the step's output content when both are supplied.")] string? reply = null,
		[Description("Optional identifier of the responder (persisted on the run record for audit).")] string? respondedBy = null)
	{
		if (string.IsNullOrEmpty(choice) && string.IsNullOrEmpty(reply))
		{
			return JsonSerializer.Serialize(new
			{
				error = "Either 'choice' or 'reply' (or both) is required."
			}, s_jsonOptions);
		}

		var pending = pendingInputStore.GetAsync(orchestrationName, runId, stepName).GetAwaiter().GetResult();
		if (pending is null)
		{
			return JsonSerializer.Serialize(new
			{
				error = $"No pending input record for orchestration '{orchestrationName}', run '{runId}', step '{stepName}'."
			}, s_jsonOptions);
		}

		if (pending.Choices.Length > 0 && !string.IsNullOrEmpty(choice)
			&& !pending.Choices.Any(c => string.Equals(c, choice, StringComparison.OrdinalIgnoreCase)))
		{
			return JsonSerializer.Serialize(new
			{
				error = $"Choice '{choice}' is not one of the allowed values: [{string.Join(", ", pending.Choices)}]."
			}, s_jsonOptions);
		}

		var response = new UserInputResponse
		{
			Choice = choice,
			Reply = reply,
			RespondedBy = respondedBy,
			RespondedAt = DateTimeOffset.UtcNow,
		};

		var completed = humanInputWaiter.TryComplete(orchestrationName, runId, stepName, response);
		if (!completed)
		{
			return JsonSerializer.Serialize(new
			{
				error = $"No active wait found for run '{runId}' step '{stepName}'. The run may have moved on, the host may have restarted (engine-tool waits don't survive restarts), or the step may not yet be executing."
			}, s_jsonOptions);
		}

		return JsonSerializer.Serialize(new
		{
			accepted = true,
			orchestrationName,
			runId,
			stepName,
			respondedAt = response.RespondedAt,
		}, s_jsonOptions);
	}

	private static string? TruncateContent(string? content, int maxLength)
	{
		if (content is null) return null;
		if (content.Length <= maxLength) return content;
		return content[..maxLength] + "... (truncated)";
	}

	/// <summary>
	/// Projects a <see cref="CancellationDetails"/> into the JSON shape returned by the MCP
	/// data-plane tools. Returns <c>null</c> when <paramref name="details"/> is <c>null</c>.
	/// </summary>
	private static object? MapCancellation(CancellationDetails? details)
	{
		if (details is null)
		{
			return null;
		}

		return new
		{
			kind = details.Kind.ToString(),
			timeoutSeconds = details.TimeoutSeconds,
			source = details.Source,
			detail = details.Detail,
			reason = details.Reason,
			isTimeout = details.IsTimeout,
			requestedAt = details.RequestedAt,
			progress = details.Progress is null ? null : new
			{
				totalSteps = details.Progress.TotalSteps,
				stepsCompleted = details.Progress.StepsCompleted,
				stepsCancelled = details.Progress.StepsCancelled,
				stepsFailed = details.Progress.StepsFailed,
				stepsSkippedOrNoAction = details.Progress.StepsSkippedOrNoAction,
				stepsNotStarted = details.Progress.StepsNotStarted,
				lastCompletedStep = details.Progress.LastCompletedStep,
				lastCompletedAt = details.Progress.LastCompletedAt,
				cancelledSteps = details.Progress.CancelledSteps,
			},
		};
	}

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
	};
}
