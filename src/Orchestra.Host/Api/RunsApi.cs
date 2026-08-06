using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Export;
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
			[FromServices] RunAnnotationStore annotations,
			int? limit,
			string? origins,
			bool? roots,
			string? statuses,
			bool? favorites,
			string? tags) =>
		{
			var requestedLimit = Math.Max(0, limit ?? 15);
			var filters = HistoryFilterParser.Parse(origins, roots, statuses, favorites, tags);

			// Running executions sort above completed ones and are held in memory, so they are
			// filtered here and the store is asked only for the shortfall.
			var runningRuns = SelectActiveRuns(activeExecutionInfos, filters, annotations)
				.Take(requestedLimit)
				.ToList();

			var remainingLimit = Math.Max(0, requestedLimit - runningRuns.Count);
			var (completedRuns, _) = await runStore.QueryRunsAsync(
				filters.ToIndexQuery(annotations), offset: 0, limit: remainingLimit);

			var runIdToOrchName = await BuildParentLookupAsync(
				runStore, activeExecutionInfos, runningRuns, completedRuns);

			var allRuns = runningRuns
				.Select(e => ProjectActiveRow(e, runIdToOrchName, annotations.Get(e.ExecutionId)))
				.Concat(completedRuns.Select(s => ProjectCompletedRow(s, runIdToOrchName, annotations.Get(s.RunId))))
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
			[FromServices] RunAnnotationStore annotations,
			int? limit,
			int? offset,
			string? origins,
			bool? roots,
			string? statuses,
			bool? favorites,
			string? tags) =>
		{
			var requestedOffset = Math.Max(0, offset ?? 0);
			var requestedLimit = Math.Max(0, limit ?? 300);
			var filters = HistoryFilterParser.Parse(origins, roots, statuses, favorites, tags);

			// The response is one virtual list: every running run, then every completed run. The
			// running segment is served from memory; the completed segment is paged in SQL with
			// its offset shifted by however much of the running segment the caller already has.
			var runningRuns = SelectActiveRuns(activeExecutionInfos, filters, annotations).ToList();
			var runningCount = runningRuns.Count;

			var runningPage = runningRuns.Skip(requestedOffset).Take(requestedLimit).ToList();

			var (completedPage, completedTotal) = await runStore.QueryRunsAsync(
				filters.ToIndexQuery(annotations),
				offset: Math.Max(0, requestedOffset - runningCount),
				limit: requestedLimit - runningPage.Count);

			var runIdToOrchName = await BuildParentLookupAsync(
				runStore, activeExecutionInfos, runningPage, completedPage);

			var allItems = runningPage
				.Select(e => ProjectActiveRow(e, runIdToOrchName, annotations.Get(e.ExecutionId)))
				.Concat(completedPage.Select(s => ProjectCompletedRow(s, runIdToOrchName, annotations.Get(s.RunId))))
				.ToList();

			return Results.Json(new
			{
				total = runningCount + completedTotal,
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
			[FromServices] RunAnnotationStore annotations,
			string? query,
			int? limit,
			int? offset,
			string? origins,
			bool? roots,
			string? statuses,
			bool? favorites,
			string? tags) =>
		{
			var searchQuery = query?.Trim() ?? "";
			var requestedOffset = Math.Max(0, offset ?? 0);
			var requestedLimit = Math.Max(0, limit ?? 300);
			var filters = HistoryFilterParser.Parse(origins, roots, statuses, favorites, tags);

			if (string.IsNullOrEmpty(searchQuery))
				return Results.Json(new { total = 0, offset = requestedOffset, limit = requestedLimit, count = 0, runs = Array.Empty<object>() }, jsonOptions);

			var matchingActive = SelectActiveRuns(activeExecutionInfos, filters, annotations)
				.Where(e => e.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
					|| e.ExecutionId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
					|| HistoryFilterParser.MatchesAnnotationText(annotations.Get(e.ExecutionId), searchQuery))
				.ToList();

			var activePage = matchingActive.Skip(requestedOffset).Take(requestedLimit).ToList();

			var (completedPage, completedTotal) = await runStore.QueryRunsAsync(
				filters.ToIndexQuery(annotations, searchQuery),
				offset: Math.Max(0, requestedOffset - matchingActive.Count),
				limit: requestedLimit - activePage.Count);

			var runIdToOrchName = await BuildParentLookupAsync(
				runStore, activeExecutionInfos, activePage, completedPage);

			var allResults = activePage
				.Select(e => ProjectActiveRow(e, runIdToOrchName, annotations.Get(e.ExecutionId)))
				.Concat(completedPage.Select(s => ProjectCompletedRow(s, runIdToOrchName, annotations.Get(s.RunId))))
				.ToList();

			return Results.Json(new
			{
				// The size of the whole match set, not of the page: a client paging through
				// results has no other way to know that more exist.
				total = matchingActive.Count + completedTotal,
				offset = requestedOffset,
				limit = requestedLimit,
				count = allResults.Count,
				runs = allResults
			}, jsonOptions);
		});

		// GET /api/history/{orchestrationName}/{runId} - Get full execution details
		historyGroup.MapGet("/{orchestrationName}/{runId}", async (string orchestrationName, string runId, FileSystemRunStore runStore, [FromServices] RunAnnotationStore annotations) =>
		{
			var record = await runStore.GetRunAsync(orchestrationName, runId);
			if (record is null)
				return ProblemDetailsHelpers.NotFound($"Run '{runId}' not found.");

			// Look up the folder path from the run index
			var summaries = await runStore.GetRunSummariesAsync(orchestrationName);
			var matchingIndex = summaries.FirstOrDefault(s => s.RunId == runId);
			var annotation = annotations.Get(runId);

			return Results.Json(new
			{
				runId = record.RunId,
				annotation = ProjectAnnotation(runId, annotation, orphaned: false),
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
					configuredProvider = kv.Value.ConfiguredProvider,
					actualProvider = kv.Value.ActualProvider,
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
		// Favorited runs require ?force=true: a favorite is an explicit "keep this" signal, so
		// deleting one is made deliberate rather than incidental.
		historyGroup.MapDelete("/{orchestrationName}/{runId}", async (
			string orchestrationName,
			string runId,
			FileSystemRunStore runStore,
			[FromServices] RunAnnotationStore annotations,
			bool? force) =>
		{
			if (annotations.Get(runId)?.Favorite == true && force != true)
			{
				return ProblemDetailsHelpers.BadRequest(
					$"Run '{runId}' is marked as a favorite. Use --force (CLI) or ?force=true (API) to delete it.");
			}

			var deleted = await runStore.DeleteRunAsync(orchestrationName, runId);
			if (!deleted)
				return ProblemDetailsHelpers.NotFound($"Run '{runId}' not found.");

			return Results.Ok(new { deleted = true, runId, orchestrationName });
		});

		// GET /api/history/{orchestrationName}/{runId}/export?format=bundle|report|data
		//
		// Always streams: HTTP cannot write into the caller's filesystem. `report` returns the
		// markdown directly; the other formats return a zip. The CLI writes directories instead.
		historyGroup.MapGet("/{orchestrationName}/{runId}/export", async (
			string orchestrationName,
			string runId,
			string? format,
			[FromServices] RunExporter exporter,
			CancellationToken cancellationToken) =>
		{
			if (!TryParseExportFormat(format, out var parsed))
				return ProblemDetailsHelpers.BadRequest($"Unknown export format '{format}'. Use report, bundle, or data.");

			try
			{
				var (content, fileName, contentType) =
					await exporter.ExportToArchiveAsync(orchestrationName, runId, parsed, cancellationToken);
				return Results.File(content, contentType, fileName);
			}
			catch (FileNotFoundException)
			{
				return ProblemDetailsHelpers.NotFound($"Run '{runId}' not found.");
			}
		});

		// ── Run annotations (favorite / title / tags / note) ──
		//
		// Annotations live in their own store rather than on the run record: run records are
		// immutable and re-saving one would duplicate its index entry. They are keyed by run id
		// and merged into history projections at read time.

		// GET /api/history/annotations - Every annotation plus tag usage counts
		historyGroup.MapGet("/annotations", async (
			FileSystemRunStore runStore,
			[FromServices] RunAnnotationStore annotations,
			bool? orphans) =>
		{
			var all = annotations.GetAll();

			// An annotation is orphaned when its run is no longer in the store. They are reported
			// rather than silently deleted, so a partially-loaded index can never destroy curation.
			var summaries = await runStore.GetRunSummariesAsync();
			var liveRunIds = new HashSet<string>(summaries.Select(s => s.RunId), StringComparer.OrdinalIgnoreCase);
			var orphanIds = annotations.FindOrphans(liveRunIds);
			var orphanSet = new HashSet<string>(orphanIds, StringComparer.OrdinalIgnoreCase);

			var items = all
				.Where(kvp => orphans != true || orphanSet.Contains(kvp.Key))
				.OrderByDescending(kvp => kvp.Value.AnnotatedAt)
				.Select(kvp => ProjectAnnotation(kvp.Key, kvp.Value, orphanSet.Contains(kvp.Key)))
				.ToList();

			var tagCounts = annotations.GetAllTagsWithCounts()
				.OrderByDescending(kvp => kvp.Value)
				.ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
				.Select(kvp => new { tag = kvp.Key, count = kvp.Value })
				.ToList();

			return Results.Json(new
			{
				count = items.Count,
				orphanCount = orphanIds.Count,
				annotations = items,
				tags = tagCounts,
			}, jsonOptions);
		});

		// POST /api/history/annotations/prune - Drop annotations whose run no longer exists
		historyGroup.MapPost("/annotations/prune", async (
			FileSystemRunStore runStore,
			[FromServices] RunAnnotationStore annotations) =>
		{
			var summaries = await runStore.GetRunSummariesAsync();
			var liveRunIds = new HashSet<string>(summaries.Select(s => s.RunId), StringComparer.OrdinalIgnoreCase);
			var orphans = annotations.FindOrphans(liveRunIds);
			var pruned = annotations.RemoveMany(orphans);

			return Results.Ok(new { pruned, runIds = orphans });
		});

		// GET /api/history/{orchestrationName}/{runId}/annotation
		historyGroup.MapGet("/{orchestrationName}/{runId}/annotation", (
			string orchestrationName,
			string runId,
			[FromServices] RunAnnotationStore annotations) =>
		{
			var annotation = annotations.Get(runId);
			return annotation is null
				? ProblemDetailsHelpers.NotFound($"Run '{runId}' has no annotation.")
				: Results.Json(ProjectAnnotation(runId, annotation, orphaned: false), jsonOptions);
		});

		// PUT /api/history/{orchestrationName}/{runId}/annotation - Replace
		historyGroup.MapPut("/{orchestrationName}/{runId}/annotation", async (
			string orchestrationName,
			string runId,
			AnnotationRequest? body,
			FileSystemRunStore runStore,
			[FromServices] RunAnnotationStore annotations) =>
		{
			if (await runStore.GetRunAsync(orchestrationName, runId) is null)
				return ProblemDetailsHelpers.NotFound($"Run '{runId}' not found.");

			var saved = annotations.Set(runId, new RunAnnotation
			{
				Favorite = body?.Favorite ?? false,
				Title = body?.Title,
				Tags = body?.Tags ?? [],
				Note = body?.Note,
				OrchestrationName = orchestrationName,
				AnnotatedAt = DateTimeOffset.UtcNow,
			});

			return Results.Json(ProjectAnnotation(runId, saved, orphaned: false), jsonOptions);
		});

		// PATCH /api/history/{orchestrationName}/{runId}/annotation - Partial update
		// Omitted fields are left untouched, so setting a title cannot clear tags.
		historyGroup.MapPatch("/{orchestrationName}/{runId}/annotation", async (
			string orchestrationName,
			string runId,
			AnnotationRequest? body,
			FileSystemRunStore runStore,
			[FromServices] RunAnnotationStore annotations) =>
		{
			if (await runStore.GetRunAsync(orchestrationName, runId) is null)
				return ProblemDetailsHelpers.NotFound($"Run '{runId}' not found.");

			var saved = annotations.Patch(
				runId,
				favorite: body?.Favorite,
				title: body?.Title,
				tags: body?.Tags,
				note: body?.Note,
				orchestrationName: orchestrationName);

			return Results.Json(ProjectAnnotation(runId, saved, orphaned: false), jsonOptions);
		});

		// DELETE /api/history/{orchestrationName}/{runId}/annotation
		historyGroup.MapDelete("/{orchestrationName}/{runId}/annotation", (
			string orchestrationName,
			string runId,
			[FromServices] RunAnnotationStore annotations) =>
		{
			var removed = annotations.Remove(runId, orchestrationName);
			return removed
				? Results.Ok(new { removed = true, runId })
				: ProblemDetailsHelpers.NotFound($"Run '{runId}' has no annotation.");
		});

		// POST /api/history/{orchestrationName}/{runId}/favorite
		historyGroup.MapPost("/{orchestrationName}/{runId}/favorite", async (
			string orchestrationName,
			string runId,
			FileSystemRunStore runStore,
			[FromServices] RunAnnotationStore annotations) =>
		{
			if (await runStore.GetRunAsync(orchestrationName, runId) is null)
				return ProblemDetailsHelpers.NotFound($"Run '{runId}' not found.");

			var saved = annotations.Patch(runId, favorite: true, orchestrationName: orchestrationName);
			return Results.Json(ProjectAnnotation(runId, saved, orphaned: false), jsonOptions);
		});

		// DELETE /api/history/{orchestrationName}/{runId}/favorite
		historyGroup.MapDelete("/{orchestrationName}/{runId}/favorite", (
			string orchestrationName,
			string runId,
			[FromServices] RunAnnotationStore annotations) =>
		{
			var saved = annotations.Patch(runId, favorite: false, orchestrationName: orchestrationName);
			return Results.Json(ProjectAnnotation(runId, saved, orphaned: false), jsonOptions);
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
		//
		// Optional JSON body:
		//   { "reason": "<free-text>", "source": "<client-type-label>" }
		//
		// Both fields are optional and untrusted; they are stored verbatim on the run record
		// for diagnostics, not used for authorization. The endpoint also captures the
		// authenticated principal name, remote IP, and User-Agent automatically so a run
		// record always identifies "who" cancelled it (best-effort: anonymous unauthenticated
		// calls record null identity).
		activeGroup.MapPost("/{executionId}/cancel", async (HttpContext httpContext, string executionId) =>
		{
			var activeExecutionInfos = httpContext.RequestServices
				.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>();
			var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
			var logger = loggerFactory.CreateLogger(typeof(RunsApi));

			// Parse the optional body. Empty/whitespace/missing body is fine — historical
			// clients post `null`, and we keep them working unchanged. We probe for a JSON
			// content type rather than ContentLength because chunked-transfer requests under
			// some HTTP stacks (notably TestServer) leave ContentLength null even when a body
			// is present.
			CancelRequestBody? body = null;
			if (httpContext.Request.HasJsonContentType())
			{
				try
				{
					body = await httpContext.Request.ReadFromJsonAsync<CancelRequestBody>(jsonOptions);
				}
				catch (JsonException ex)
				{
					LogCancelBodyParseFailed(logger, executionId, ex.Message);
					return ProblemDetailsHelpers.BadRequest("Cancel request body is not valid JSON.");
				}
			}

			var callerReason = NormalizeOrNull(body?.Reason);
			var callerSource = NormalizeOrNull(body?.Source);

			if (activeExecutionInfos.TryGetValue(executionId, out var info))
			{
				info.Status = HostExecutionStatus.Cancelling;
				if (info.Reporter is SseReporter sseReporter)
					sseReporter.ReportStatusChange(HostExecutionStatus.Cancelling);

				// Capture caller identity from the HTTP context. Best-effort: any/all of these
				// may be null on anonymous or non-HTTP-piped requests; we never throw.
				var callerIdentity = NormalizeOrNull(httpContext.User?.Identity?.Name);
				var callerAddress = httpContext.Connection?.RemoteIpAddress?.ToString();
				var callerUserAgent = httpContext.Request.Headers.UserAgent.ToString();
				if (string.IsNullOrWhiteSpace(callerUserAgent)) callerUserAgent = null;

				// Attribute the cancel before triggering it so the engine's probe records a
				// precise CancellationDetails on the run record instead of a generic "caller".
				// Use ??= so explicit overrides (e.g. HostShutdown from TriggerManager) win.
				info.CancellationCauseOverride ??= new CancellationDetails
				{
					Kind = CancellationCauseKind.External,
					Source = "caller",
					Detail = "REST /api/active/{id}/cancel",
					RequestedAt = DateTimeOffset.UtcNow,
					CallerReason = callerReason,
					CallerSource = callerSource,
					CallerIdentity = callerIdentity,
					CallerAddress = callerAddress,
					CallerUserAgent = callerUserAgent,
				};

				LogRunCancelRequested(
					logger,
					executionId,
					callerSource ?? "rest-api",
					"REST /api/active/{id}/cancel",
					callerReason,
					callerIdentity,
					callerAddress);

				info.CancellationTokenSource.Cancel();
				return Results.Ok(new { cancelled = true, executionId, status = HostExecutionStatus.Cancelling });
			}
			return ProblemDetailsHelpers.NotFound($"No active execution with ID '{executionId}'.");
		});

		return endpoints;
	}

	/// <summary>
	/// Optional body shape for <c>POST /api/active/{id}/cancel</c>. Both fields are caller-supplied
	/// diagnostics, never used for authorization. Whitespace-only values are treated as null.
	/// </summary>
	/// <remarks>
	/// Property names are pinned with <see cref="JsonPropertyNameAttribute"/> so deserialization
	/// works the same whether the host's <see cref="JsonSerializerOptions.PropertyNamingPolicy"/>
	/// is camelCase, PascalCase, or null. Without this, positional-record parameter binding can
	/// silently miss values when the configured naming policy doesn't match.
	/// </remarks>
	private sealed record CancelRequestBody(
		[property: System.Text.Json.Serialization.JsonPropertyName("reason")] string? Reason,
		[property: System.Text.Json.Serialization.JsonPropertyName("source")] string? Source);

	private static string? NormalizeOrNull(string? value)
		=> string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Run cancel requested: executionId={ExecutionId}, source={Source}, detail={Detail}, callerReason={CallerReason}, callerIdentity={CallerIdentity}, callerAddress={CallerAddress}")]
	private static partial void LogRunCancelRequested(
		ILogger logger,
		string executionId,
		string source,
		string? detail,
		string? callerReason,
		string? callerIdentity,
		string? callerAddress);

	[LoggerMessage(Level = LogLevel.Warning,
		Message = "Run cancel body parse failed: executionId={ExecutionId}, error={Error}")]
	private static partial void LogCancelBodyParseFailed(ILogger logger, string executionId, string error);

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
			callerReason = details.CallerReason,
			callerSource = details.CallerSource,
			callerIdentity = details.CallerIdentity,
			callerAddress = details.CallerAddress,
			callerUserAgent = details.CallerUserAgent,
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
	/// <summary>
	/// The running executions that pass <paramref name="filters"/>, newest first.
	/// </summary>
	/// <remarks>
	/// Executions that have finished but are still in the dictionary during the cleanup grace
	/// period are dropped here — they belong in the completed segment, and counting them in both
	/// would double up rows and inflate totals.
	/// </remarks>
	private static IEnumerable<ActiveExecutionInfo> SelectActiveRuns(
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		HistoryFilters filters,
		RunAnnotationStore annotations) =>
		activeExecutionInfos.Values
			.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
			.Where(e => !filters.HasAnyFilter || HistoryFilterParser.Matches(e, filters, annotations.Get(e.ExecutionId)))
			.OrderByDescending(e => e.StartedAt)
			.ThenBy(e => e.ExecutionId, StringComparer.Ordinal);

	/// <summary>
	/// Builds the runId → orchestrationName lookup needed to label the child rows on one page
	/// with their parent's orchestration.
	/// </summary>
	/// <remarks>
	/// Scoped to the parents this page actually references. The previous implementation built the
	/// lookup from every run in the index on every request, which grew without bound while the
	/// number of entries a page can use stays capped by the page size.
	/// <para>
	/// On a collision (a run id appears both in the active set and in the persisted index) the
	/// active set wins, because the active record is authoritative for what is currently running.
	/// </para>
	/// </remarks>
	private static async Task<Dictionary<string, string>> BuildParentLookupAsync(
		FileSystemRunStore runStore,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		IEnumerable<ActiveExecutionInfo> activeRows,
		IEnumerable<RunIndex> completedRows)
	{
		var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var e in activeRows)
		{
			if (e.NestingMetadata?.ParentExecutionId is { Length: > 0 } parentId)
				needed.Add(parentId);
		}

		foreach (var s in completedRows)
		{
			if (s.ParentExecutionId is { Length: > 0 } parentId)
				needed.Add(parentId);
		}

		var lookup = needed.Count == 0
			? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			: await runStore.GetOrchestrationNamesByRunIdsAsync(needed);

		foreach (var id in needed)
		{
			if (activeExecutionInfos.TryGetValue(id, out var info))
				lookup[id] = info.OrchestrationName;
		}

		return lookup;
	}

	/// <summary>
	/// Parses the <c>?format=</c> export query parameter. Defaults to
	/// <see cref="RunExportFormat.Bundle"/> when absent.
	/// </summary>
	private static bool TryParseExportFormat(string? value, out RunExportFormat format)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			format = RunExportFormat.Bundle;
			return true;
		}

		return Enum.TryParse(value, ignoreCase: true, out format) && Enum.IsDefined(format);
	}

	/// <summary>
	/// Request body for annotation writes. Every field is optional: on <c>PUT</c> an omitted
	/// field is cleared, on <c>PATCH</c> an omitted field is left untouched.
	/// </summary>
	public sealed class AnnotationRequest
	{
		public bool? Favorite { get; set; }
		public string? Title { get; set; }
		public string[]? Tags { get; set; }
		public string? Note { get; set; }
	}

	/// <summary>
	/// Projects an annotation onto the wire. A <see langword="null"/> annotation — the run was
	/// annotated down to nothing — is reported as a cleared annotation rather than as absent,
	/// so clients get a consistent shape back from every write.
	/// </summary>
	private static object ProjectAnnotation(string runId, RunAnnotation? annotation, bool orphaned) => new
	{
		runId,
		orchestrationName = annotation?.OrchestrationName,
		favorite = annotation?.Favorite ?? false,
		title = annotation?.Title,
		tags = annotation?.Tags ?? [],
		note = annotation?.Note,
		annotatedAt = annotation?.AnnotatedAt.ToString("o"),
		orphaned,
	};

	/// <summary>
	/// Projects an <see cref="ActiveExecutionInfo"/> (a still-running execution) into the
	/// JSON shape expected by the history list endpoints. Includes the lineage and origin
	/// fields that the portal needs to render badges/icons for child and retry runs.
	/// </summary>
	private static object ProjectActiveRow(
		ActiveExecutionInfo e,
		IReadOnlyDictionary<string, string> runIdToOrchName,
		RunAnnotation? annotation = null)
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
			// User curation; absent annotation reads as an unfavorited, untitled, untagged run.
			favorite = annotation?.Favorite ?? false,
			title = annotation?.Title,
			tags = annotation?.Tags ?? [],
			note = annotation?.Note,
		};
	}

	/// <summary>
	/// Projects a stored <see cref="RunIndex"/> (a completed/failed/cancelled run) into the
	/// JSON shape expected by the history list endpoints.
	/// </summary>
	private static object ProjectCompletedRow(
		RunIndex s,
		IReadOnlyDictionary<string, string> runIdToOrchName,
		RunAnnotation? annotation = null)
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
			// User curation; absent annotation reads as an unfavorited, untitled, untagged run.
			favorite = annotation?.Favorite ?? false,
			title = annotation?.Title,
			tags = annotation?.Tags ?? [],
			note = annotation?.Note,
		};
	}
}
