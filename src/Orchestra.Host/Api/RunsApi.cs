using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for run history and active executions.
/// </summary>
public static partial class RunsApi
{
	/// <summary>
	/// Maps run management endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapRunsApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		// History endpoints
		var historyGroup = endpoints.MapGroup("/api/history");

		// GET /api/history - Get recent executions (lightweight summaries)
		// Optional filters:
		//   ?origins=manual,scheduler,loop,webhook,mcp,orchestration,retry,resume
		//   ?roots=true|false        (true = roots only, false = children only, omitted = no scope filter)
		//   ?statuses=Running,Succeeded,Failed,Cancelled
		historyGroup.MapGet("", async (
			FileSystemRunStore runStore,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			int? limit,
			string? origins,
			bool? roots,
			string? statuses) =>
		{
			var requestedLimit = limit ?? 15;
			var filters = HistoryFilterParser.Parse(origins, roots, statuses);

			// Build a runId -> orchestrationName lookup so child rows can surface the parent's
			// orchestration name even when the parent is outside the response window. The lookup
			// covers BOTH active and stored runs because a child can be launched while the parent
			// is still running.
			var allSummariesForLookup = await runStore.GetRunSummariesAsync();
			var runIdToOrchName = BuildRunIdLookup(allSummariesForLookup, activeExecutionInfos);

			// Get running orchestrations (these should appear at the top).
			// Filter out completed/cancelled/failed executions that are still in the dictionary
			// during the cleanup grace period — they should show up as completed history entries instead.
			var runningRuns = activeExecutionInfos.Values
				.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
				.Where(e => !filters.HasAnyFilter || HistoryFilterParser.Matches(e, filters))
				.OrderByDescending(e => e.StartedAt)
				.Select(e => ProjectActiveRow(e, runIdToOrchName))
				.ToList();

			// Get completed runs from store, applying filters server-side. We pull all summaries
			// (already in memory from the lookup-build step) and filter+take the requested count.
			var remainingLimit = Math.Max(0, requestedLimit - runningRuns.Count);
			IEnumerable<RunIndex> filteredCompleted = filters.HasAnyFilter
				? allSummariesForLookup.Where(s => HistoryFilterParser.Matches(s, filters))
				: allSummariesForLookup;

			var completedRuns = filteredCompleted
				.Take(remainingLimit)
				.Select(s => ProjectCompletedRow(s, runIdToOrchName));

			// Combine: running first, then completed
			var allRuns = runningRuns
				.Concat(completedRuns)
				.Take(requestedLimit)
				.ToList();

			return Results.Json(new
			{
				count = allRuns.Count,
				runs = allRuns
			}, jsonOptions);
		});

		// GET /api/history/all - Get all executions (paginated)
		// Same filter semantics as /api/history.
		historyGroup.MapGet("/all", async (
			FileSystemRunStore runStore,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			int? limit,
			int? offset,
			string? origins,
			bool? roots,
			string? statuses) =>
		{
			var requestedOffset = offset ?? 0;
			var requestedLimit = limit ?? 300;
			var filters = HistoryFilterParser.Parse(origins, roots, statuses);

			var allSummariesForLookup = await runStore.GetRunSummariesAsync();
			var runIdToOrchName = BuildRunIdLookup(allSummariesForLookup, activeExecutionInfos);

			// Get running orchestrations (filter out completed/cancelled/failed during cleanup grace period)
			var runningRuns = activeExecutionInfos.Values
				.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
				.Where(e => !filters.HasAnyFilter || HistoryFilterParser.Matches(e, filters))
				.OrderByDescending(e => e.StartedAt)
				.Select(e => ProjectActiveRow(e, runIdToOrchName))
				.ToList();

			var runningCount = runningRuns.Count;

			var completedFiltered = filters.HasAnyFilter
				? allSummariesForLookup.Where(s => HistoryFilterParser.Matches(s, filters)).ToList()
				: [.. allSummariesForLookup];
			var completedTotal = completedFiltered.Count;
			var totalAll = runningCount + completedTotal;

			// Calculate which items to return based on offset
			var allItems = new List<object>();

			if (requestedOffset < runningCount)
			{
				var runningToTake = runningRuns.Skip(requestedOffset).Take(requestedLimit);
				allItems.AddRange(runningToTake);

				var remaining = requestedLimit - allItems.Count;
				if (remaining > 0)
				{
					var completedItems = completedFiltered
						.Take(remaining)
						.Select(s => ProjectCompletedRow(s, runIdToOrchName));
					allItems.AddRange(completedItems);
				}
			}
			else
			{
				var completedOffset = requestedOffset - runningCount;
				var completedItems = completedFiltered
					.Skip(completedOffset)
					.Take(requestedLimit)
					.Select(s => ProjectCompletedRow(s, runIdToOrchName));
				allItems.AddRange(completedItems);
			}

			return Results.Json(new
			{
				total = totalAll,
				offset = requestedOffset,
				limit = requestedLimit,
				count = allItems.Count,
				runs = allItems
			}, jsonOptions);
		});

		// GET /api/history/search - Search across ALL stored executions by name or runId
		// Same filter semantics as /api/history.
		historyGroup.MapGet("/search", async (
			FileSystemRunStore runStore,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			string? query,
			int? limit,
			string? origins,
			bool? roots,
			string? statuses) =>
		{
			var searchQuery = query?.Trim() ?? "";
			var requestedLimit = limit ?? 300;
			var filters = HistoryFilterParser.Parse(origins, roots, statuses);

			if (string.IsNullOrEmpty(searchQuery))
				return Results.Json(new { total = 0, count = 0, runs = Array.Empty<object>() }, jsonOptions);

			var allSummaries = await runStore.GetRunSummariesAsync();
			var runIdToOrchName = BuildRunIdLookup(allSummaries, activeExecutionInfos);

			// Search across active executions (filter out completed/cancelled/failed during cleanup grace period)
			var matchingActive = activeExecutionInfos.Values
				.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
				.Where(e => !filters.HasAnyFilter || HistoryFilterParser.Matches(e, filters))
				.Where(e => e.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
					|| e.ExecutionId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(e => e.StartedAt)
				.Select(e => ProjectActiveRow(e, runIdToOrchName))
				.Cast<object>()
				.ToList();

			// Search across ALL completed runs in the index
			var matchingCompleted = allSummaries
				.Where(s => !filters.HasAnyFilter || HistoryFilterParser.Matches(s, filters))
				.Where(s => s.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
					|| s.RunId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
				.Take(requestedLimit)
				.Select(s => ProjectCompletedRow(s, runIdToOrchName))
				.Cast<object>()
				.ToList();

			var allResults = matchingActive.Concat(matchingCompleted).Take(requestedLimit).ToList();

			return Results.Json(new
			{
				total = allResults.Count,
				count = allResults.Count,
				runs = allResults
			}, jsonOptions);
		});

		// GET /api/history/{orchestrationName}/{runId} - Get full execution details
		historyGroup.MapGet("/{orchestrationName}/{runId}", async (string orchestrationName, string runId, FileSystemRunStore runStore) =>
		{
			var record = await runStore.GetRunAsync(orchestrationName, runId);
			if (record is null)
				return ProblemDetailsHelpers.NotFound($"Run '{runId}' not found.");

			// Look up the folder path from the run index
			var summaries = await runStore.GetRunSummariesAsync(orchestrationName);
			var matchingIndex = summaries.FirstOrDefault(s => s.RunId == runId);

			return Results.Json(new
			{
				runId = record.RunId,
				orchestrationName = record.OrchestrationName,
				version = record.OrchestrationVersion,
				triggeredBy = record.TriggeredBy,
				startedAt = record.StartedAt.ToString("o"),
				completedAt = record.CompletedAt.ToString("o"),
				durationSeconds = Math.Round(record.Duration.TotalSeconds, 2),
				status = record.Status.ToString(),
				completionReason = record.CompletionReason,
				completedByStep = record.CompletedByStep,
				cancellation = MapCancellation(record.Cancellation),
				isIncomplete = record.IsIncomplete,
				retriedFromRunId = record.RetriedFromRunId,
				retryMode = record.RetryMode,
				parentExecutionId = record.ParentExecutionId,
				parentStepName = record.ParentStepName,
				rootExecutionId = record.RootExecutionId,
				nestingDepth = record.NestingDepth,
				parameters = record.Parameters,
				finalContent = record.FinalContent,
				savedFiles = record.SavedFiles.Length > 0 ? record.SavedFiles : null,
				totalUsage = record.TotalUsage is { } tu ? new
				{
					inputTokens = tu.InputTokens,
					outputTokens = tu.OutputTokens,
					totalTokens = tu.TotalTokens,
					cacheReadTokens = tu.CacheReadTokens,
					cacheWriteTokens = tu.CacheWriteTokens,
					cost = tu.Cost,
					duration = tu.Duration,
				} : null,
				context = record.Context is { } ctx ? new
				{
					runId = ctx.RunId,
					orchestrationName = ctx.OrchestrationName,
					orchestrationVersion = ctx.OrchestrationVersion,
					startedAt = ctx.StartedAt.ToString("o"),
					triggeredBy = ctx.TriggeredBy,
					triggerId = ctx.TriggerId,
					parameters = ctx.Parameters.Count > 0 ? ctx.Parameters : null,
					variables = ctx.Variables.Count > 0 ? ctx.Variables : null,
					resolvedVariables = ctx.ResolvedVariables.Count > 0 ? ctx.ResolvedVariables : null,
					accessedEnvironmentVariables = ctx.AccessedEnvironmentVariables.Count > 0 ? ctx.AccessedEnvironmentVariables : null,
					dataDirectory = matchingIndex?.FolderPath ?? ctx.DataDirectory,
				} : null,
				hookExecutions = record.HookExecutions.Count > 0
					? record.HookExecutions.Select(h => new
					{
						hookName = h.HookName,
						eventType = h.EventType.ToString(),
						source = h.Source.ToString(),
						status = h.Status.ToString(),
						startedAt = h.StartedAt.ToString("o"),
						completedAt = h.CompletedAt.ToString("o"),
						durationSeconds = Math.Round(h.Duration.TotalSeconds, 2),
						stepName = h.StepName,
						errorMessage = h.ErrorMessage,
						content = h.Content,
						failurePolicy = h.FailurePolicy.ToString(),
						actionType = h.ActionType.ToString(),
					}).ToArray()
					: null,
				steps = record.StepRecords.Select(kv => new
				{
					name = kv.Key,
					status = kv.Value.Status.ToString(),
					startedAt = kv.Value.StartedAt.ToString("o"),
					completedAt = kv.Value.CompletedAt.ToString("o"),
					durationSeconds = Math.Round(kv.Value.Duration.TotalSeconds, 2),
					content = kv.Value.Content,
					rawContent = kv.Value.RawContent,
					promptSent = kv.Value.PromptSent,
					actualModel = kv.Value.ActualModel,
					selectedModel = kv.Value.SelectedModel,
					requestedModelInfo = kv.Value.RequestedModelInfo,
					selectedModelInfo = kv.Value.SelectedModelInfo,
					actualModelInfo = kv.Value.ActualModelInfo,
					savedFiles = kv.Value.SavedFiles.Length > 0 ? kv.Value.SavedFiles : null,
					usage = kv.Value.Usage is { } u ? new
					{
						inputTokens = u.InputTokens,
						outputTokens = u.OutputTokens,
						totalTokens = u.TotalTokens,
						cacheReadTokens = u.CacheReadTokens,
						cacheWriteTokens = u.CacheWriteTokens,
						cost = u.Cost,
						duration = u.Duration,
					} : null,
					errorMessage = kv.Value.ErrorMessage,
					errorCategory = kv.Value.ErrorCategory?.ToString(),
					retryHistory = kv.Value.RetryHistory is { Count: > 0 } rh ? rh.Select(r => new
					{
						attempt = r.Attempt,
						error = r.Error,
						attemptedAt = r.AttemptedAt.ToString("o"),
						delaySeconds = r.DelaySeconds,
						errorCategory = r.ErrorCategory?.ToString(),
					}).ToArray() : null,
					trace = kv.Value.Trace is { } t ? new
					{
						parameters = t.Parameters.Count > 0 ? t.Parameters : kv.Value.Parameters.Count > 0 ? kv.Value.Parameters : null,
						dependencyOutputs = t.DependencyOutputs.Count > 0 ? t.DependencyOutputs : null,
						rawDependencyOutputs = t.RawDependencyOutputs.Count > 0 ? t.RawDependencyOutputs : kv.Value.RawDependencyOutputs.Count > 0 ? kv.Value.RawDependencyOutputs : null,
						accessibleStepData = t.AccessibleStepData.Count > 0 ? t.AccessibleStepData : null,
						command = t.Command,
						commandArguments = t.Command is not null || t.Shell is not null || t.CommandArguments.Count > 0 ? t.CommandArguments : null,
						shell = t.Shell,
						scriptSource = t.ScriptSource,
						workingDirectory = t.WorkingDirectory,
						environment = t.Environment.Count > 0 ? t.Environment : null,
						stdin = t.Stdin,
						systemPrompt = t.SystemPrompt,
						userPromptRaw = t.UserPromptRaw,
						userPromptProcessed = t.UserPromptProcessed,
						reasoning = t.Reasoning,
						toolCalls = t.ToolCalls.Select(tc => new
						{
							callId = tc.CallId,
							mcpServer = tc.McpServer,
							toolName = tc.ToolName,
							arguments = tc.Arguments,
							success = tc.Success,
							result = tc.Result,
							error = tc.Error,
							startedAt = tc.StartedAt?.ToString("o"),
							completedAt = tc.CompletedAt?.ToString("o"),
							durationMs = tc.StartedAt.HasValue && tc.CompletedAt.HasValue
								? Math.Round((tc.CompletedAt.Value - tc.StartedAt.Value).TotalMilliseconds, 1)
								: (double?)null,
						}).ToArray(),
						responseSegments = t.ResponseSegments,
						finalResponse = t.FinalResponse,
						outputHandlerResult = t.OutputHandlerResult,
						mcpServers = t.McpServers.Count > 0 ? t.McpServers : null,
						warnings = t.Warnings.Count > 0 ? t.Warnings : null,
						conversationHistory = t.ConversationHistory.Count > 0 ? t.ConversationHistory.Select(m => new
						{
							role = m.Role,
							content = m.Content,
							toolCallId = m.ToolCallId,
							toolName = m.ToolName,
							timestamp = m.Timestamp.ToString("o"),
						}).ToArray() : null,
					} : null,
					// When the step invoked another orchestration, surface the child run's
					// executionId/name/status so consumers (Portal, external API clients) can
					// render parent → child navigation. Null on non-orchestration steps and
					// omitted from JSON by the ignore-null serializer policy.
					childExecutionId = kv.Value.ChildExecutionId,
					childOrchestrationName = kv.Value.ChildOrchestrationName,
					childStatus = kv.Value.ChildStatus?.ToString().ToLowerInvariant(),
				}).ToArray(),
				allStepRecords = record.AllStepRecords.Count != record.StepRecords.Count
					? record.AllStepRecords
						.Where(kv => !record.StepRecords.ContainsKey(kv.Key))
						.Select(kv => new
						{
							key = kv.Key,
							name = kv.Value.StepName,
							status = kv.Value.Status.ToString(),
							startedAt = kv.Value.StartedAt.ToString("o"),
						completedAt = kv.Value.CompletedAt.ToString("o"),
						durationSeconds = Math.Round(kv.Value.Duration.TotalSeconds, 2),
						content = kv.Value.Content,
						loopIteration = kv.Value.LoopIteration,
						savedFiles = kv.Value.SavedFiles.Length > 0 ? kv.Value.SavedFiles : null,
						errorMessage = kv.Value.ErrorMessage,
						childExecutionId = kv.Value.ChildExecutionId,
						childOrchestrationName = kv.Value.ChildOrchestrationName,
						childStatus = kv.Value.ChildStatus?.ToString().ToLowerInvariant(),
					}).ToArray()
					: null,
			}, jsonOptions);
		});

		// DELETE /api/history/{orchestrationName}/{runId} - Delete a specific execution
		historyGroup.MapDelete("/{orchestrationName}/{runId}", async (string orchestrationName, string runId, FileSystemRunStore runStore) =>
		{
			var deleted = await runStore.DeleteRunAsync(orchestrationName, runId);
			if (!deleted)
				return ProblemDetailsHelpers.NotFound($"Run '{runId}' not found.");

			return Results.Ok(new { deleted = true, runId, orchestrationName });
		});

		// Active executions endpoints
		var activeGroup = endpoints.MapGroup("/api/active");

		// GET /api/active - Get all active (running) orchestrations
		activeGroup.MapGet("", (
			TriggerManager triggerManager,
			OrchestrationRegistry registry,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
		{
			// Combine manual executions and trigger-based executions
			var activeList = new List<object>();

			// Add executions that are still running (filter out completed/cancelled/failed)
			foreach (var info in activeExecutionInfos.Values)
			{
				if (info.Status is HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed)
					continue;

				activeList.Add(new
				{
					executionId = info.ExecutionId,
					orchestrationId = info.OrchestrationId,
					orchestrationName = info.OrchestrationName,
					startedAt = info.StartedAt,
					triggeredBy = info.TriggeredBy,
					source = "manual",
					status = info.Status,
					parameters = info.Parameters,
					totalSteps = info.TotalSteps,
					completedSteps = info.CompletedSteps,
					currentStep = info.CurrentStep,
					// Surface lineage so Portal and external clients can follow active
					// parent → child chains without needing a separate per-active lookup.
					// Null for top-level executions (NestingMetadata is null in that case).
					parentExecutionId = info.NestingMetadata?.ParentExecutionId,
					parentStepName = info.NestingMetadata?.ParentStepName,
					rootExecutionId = info.NestingMetadata?.RootExecutionId,
					nestingDepth = info.NestingMetadata?.Depth,
				});
			}

			// Add trigger-based running executions
			var runningTriggers = triggerManager.GetAllTriggers()
				.Where(t => t.Status == TriggerStatus.Running && !string.IsNullOrEmpty(t.ActiveExecutionId));

			foreach (var trigger in runningTriggers)
			{
				// Capture into local to avoid race with concurrent null-assignment
				var activeExecId = trigger.ActiveExecutionId;
				if (activeExecId is null) continue;

				// Avoid duplicates if somehow tracked in both
				if (!activeExecutionInfos.ContainsKey(activeExecId))
				{
				var triggerType = trigger.Config switch
				{
					SchedulerTriggerConfig => "scheduler",
					LoopTriggerConfig => "loop",
					WebhookTriggerConfig => "webhook",
					ManualTriggerConfig => "manual",
					_ => "trigger"
				};

					// Resolve name: trigger metadata -> registry -> fallback
					var orchName = trigger.OrchestrationName
						?? registry.Get(trigger.Id)?.Orchestration.Name
						?? "Unknown";

					activeList.Add(new
					{
						executionId = activeExecId,
						orchestrationId = trigger.Id,
						orchestrationName = orchName,
						startedAt = trigger.LastFireTime,
						triggeredBy = triggerType,
						source = "trigger"
					});
				}
			}

			// Add pending/waiting triggers
			var pendingTriggers = triggerManager.GetAllTriggers()
				.Where(t => t.Config.Enabled && t.Status == TriggerStatus.Waiting &&
					(t.NextFireTime.HasValue || t.Config is WebhookTriggerConfig));

			var pending = pendingTriggers.Select(t =>
			{
				var orch = registry.Get(t.Id);
				var stepCount = orch?.Orchestration?.Steps?.Length ?? 0;

				// Resolve name: trigger metadata -> registry -> fallback
				var orchName = t.OrchestrationName
					?? orch?.Orchestration.Name
					?? "Unknown";

				return new
				{
					orchestrationId = t.Id,
					orchestrationName = orchName,
					orchestrationDescription = t.OrchestrationDescription,
					stepCount,
					nextFireTime = t.NextFireTime,
					lastFireTime = t.LastFireTime,
					lastExecutionId = t.LastExecutionId,
					runCount = t.RunCount,
					status = t.Status.ToString().ToLowerInvariant(),
				triggerType = t.Config switch
				{
					SchedulerTriggerConfig => "scheduler",
					LoopTriggerConfig => "loop",
					WebhookTriggerConfig => "webhook",
					ManualTriggerConfig => "manual",
					_ => "trigger"
				},
				triggeredBy = t.Config switch
				{
					SchedulerTriggerConfig => "scheduler",
					LoopTriggerConfig => "loop",
					WebhookTriggerConfig => "webhook",
					ManualTriggerConfig => "manual",
					_ => "trigger"
				},
					source = "pending",
					webhookUrl = t.Config is WebhookTriggerConfig ? $"/api/webhooks/{t.Id}" : null,
				};
			}).ToList();

			return Results.Json(new
			{
				running = activeList,
				pending,
				totalRunning = activeList.Count,
				totalPending = pending.Count
			}, jsonOptions);
		});

		// POST /api/active/{executionId}/cancel - Cancel a running execution
		activeGroup.MapPost("/{executionId}/cancel", (HttpContext httpContext, string executionId) =>
		{
			var activeExecutionInfos = httpContext.RequestServices
				.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();
			var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
			if (activeExecutionInfos.TryGetValue(executionId, out var info))
			{
				info.Status = HostExecutionStatus.Cancelling;
				if (info.Reporter is SseReporter sseReporter)
					sseReporter.ReportStatusChange(HostExecutionStatus.Cancelling);

				// Attribute the cancel before triggering it so the engine's probe records a
				// precise CancellationDetails on the run record instead of a generic "caller".
				// Use ??= so explicit overrides (e.g. HostShutdown from TriggerManager) win.
				info.CancellationCauseOverride ??= new CancellationDetails
				{
					Kind = CancellationCauseKind.External,
					Source = "caller",
					Detail = "REST /api/active/{id}/cancel",
					RequestedAt = DateTimeOffset.UtcNow,
				};

				var logger = loggerFactory.CreateLogger(typeof(RunsApi));
				LogRunCancelRequested(logger, executionId, "rest-api", "REST /api/active/{id}/cancel");

				info.CancellationTokenSource.Cancel();
				return Results.Ok(new { cancelled = true, executionId, status = HostExecutionStatus.Cancelling });
			}
			return ProblemDetailsHelpers.NotFound($"No active execution with ID '{executionId}'.");
		});

		return endpoints;
	}

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Run cancel requested: executionId={ExecutionId}, source={Source}, detail={Detail}")]
	private static partial void LogRunCancelRequested(ILogger logger, string executionId, string source, string? detail);

	/// <summary>
	/// Projects a <see cref="CancellationDetails"/> into a stable JSON shape for API responses.
	/// Returns <c>null</c> when <paramref name="details"/> is <c>null</c> so non-cancelled runs
	/// emit no <c>cancellation</c> field at all.
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

	/// <summary>
	/// Builds a one-shot <c>runId -> orchestrationName</c> lookup that combines stored
	/// <see cref="RunIndex"/> entries and currently-running <see cref="ActiveExecutionInfo"/>
	/// records. Used to resolve <c>parentOrchestrationName</c> when projecting child rows.
	/// </summary>
	/// <remarks>
	/// On a collision (a run id appears both in the active set and in the persisted index)
	/// the active set wins because the active record is authoritative for what is currently
	/// running. Lookups are case-insensitive to mirror <c>FindRunByIdAsync</c>.
	/// </remarks>
	private static Dictionary<string, string> BuildRunIdLookup(
		IEnumerable<RunIndex> summaries,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos)
	{
		var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var s in summaries)
			lookup[s.RunId] = s.OrchestrationName;
		foreach (var (id, info) in activeExecutionInfos)
			lookup[id] = info.OrchestrationName;
		return lookup;
	}

	/// <summary>
	/// Projects an <see cref="ActiveExecutionInfo"/> (a still-running execution) into the
	/// JSON shape expected by the history list endpoints. Includes the lineage and origin
	/// fields that the portal needs to render badges/icons for child and retry runs.
	/// </summary>
	private static object ProjectActiveRow(
		ActiveExecutionInfo e,
		IReadOnlyDictionary<string, string> runIdToOrchName)
	{
		var nesting = e.NestingMetadata;
		var parentExecutionId = nesting?.ParentExecutionId;
		string? parentOrchName = null;
		if (parentExecutionId is not null && runIdToOrchName.TryGetValue(parentExecutionId, out var name))
			parentOrchName = name;

		return new
		{
			runId = e.ExecutionId,
			executionId = e.ExecutionId,
			orchestrationId = e.OrchestrationId,
			orchestrationName = e.OrchestrationName,
			version = "1.0.0",
			triggeredBy = e.TriggeredBy,
			origin = RunOriginClassifier.ToWireValue(RunOriginClassifier.Classify(e.TriggeredBy)),
			startedAt = e.StartedAt.ToString("o"),
			completedAt = (string?)null,
			durationSeconds = Math.Round((DateTimeOffset.UtcNow - e.StartedAt).TotalSeconds, 2),
			status = e.Status,
			isActive = true,
			isIncomplete = false,
			parameters = e.Parameters,
			// Lineage (running runs do not yet have retry metadata)
			retriedFromRunId = (string?)null,
			retryMode = (string?)null,
			parentExecutionId,
			parentStepName = nesting?.ParentStepName,
			parentOrchestrationName = parentOrchName,
			rootExecutionId = nesting?.RootExecutionId,
			nestingDepth = nesting?.Depth ?? 0,
		};
	}

	/// <summary>
	/// Projects a stored <see cref="RunIndex"/> (a completed/failed/cancelled run) into the
	/// JSON shape expected by the history list endpoints.
	/// </summary>
	private static object ProjectCompletedRow(
		RunIndex s,
		IReadOnlyDictionary<string, string> runIdToOrchName)
	{
		string? parentOrchName = null;
		if (s.ParentExecutionId is not null && runIdToOrchName.TryGetValue(s.ParentExecutionId, out var name))
			parentOrchName = name;

		return new
		{
			runId = s.RunId,
			executionId = (string?)null,
			orchestrationId = (string?)null,
			orchestrationName = s.OrchestrationName,
			version = s.OrchestrationVersion,
			triggeredBy = s.TriggeredBy,
			origin = RunOriginClassifier.ToWireValue(RunOriginClassifier.Classify(s.TriggeredBy)),
			startedAt = s.StartedAt.ToString("o"),
			completedAt = s.CompletedAt.ToString("o"),
			durationSeconds = Math.Round(s.Duration.TotalSeconds, 2),
			status = s.Status.ToString(),
			completionReason = s.CompletionReason,
			completedByStep = s.CompletedByStep,
			cancellation = MapCancellation(s.Cancellation),
			hookExecutionCount = s.HookExecutionCount,
			isActive = false,
			isIncomplete = s.IsIncomplete,
			// Lineage
			retriedFromRunId = s.RetriedFromRunId,
			retryMode = s.RetryMode,
			parentExecutionId = s.ParentExecutionId,
			parentStepName = s.ParentStepName,
			parentOrchestrationName = parentOrchName,
			rootExecutionId = s.RootExecutionId,
			nestingDepth = s.NestingDepth,
		};
	}
}
