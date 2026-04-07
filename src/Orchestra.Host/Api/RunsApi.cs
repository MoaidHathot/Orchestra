using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for run history and active executions.
/// </summary>
public static class RunsApi
{
	/// <summary>
	/// Maps run management endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapRunsApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		// History endpoints
		var historyGroup = endpoints.MapGroup("/api/history");

		// GET /api/history - Get recent executions (lightweight summaries)
		historyGroup.MapGet("", async (
			FileSystemRunStore runStore,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			int? limit) =>
		{
			var requestedLimit = limit ?? 15;

		// Get running orchestrations (these should appear at the top).
		// Filter out completed/cancelled/failed executions that are still in the dictionary
		// during the cleanup grace period — they should show up as completed history entries instead.
		var runningRuns = activeExecutionInfos.Values
			.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
			.OrderByDescending(e => e.StartedAt)
			.Select(e => new
			{
				runId = e.ExecutionId,
				executionId = e.ExecutionId,
				orchestrationId = e.OrchestrationId,
				orchestrationName = e.OrchestrationName,
				version = "1.0.0",
				triggeredBy = e.TriggeredBy,
				startedAt = e.StartedAt.ToString("o"),
				completedAt = (string?)null,
				durationSeconds = Math.Round((DateTimeOffset.UtcNow - e.StartedAt).TotalSeconds, 2),
				status = e.Status,
				isActive = true,
				parameters = e.Parameters
			})
			.ToList();

			// Get completed runs from store
			var remainingLimit = Math.Max(0, requestedLimit - runningRuns.Count);
			var completedSummaries = await runStore.GetRunSummariesAsync(remainingLimit);
			var completedRuns = completedSummaries.Select(s => new
			{
				runId = s.RunId,
				executionId = (string?)null,
				orchestrationName = s.OrchestrationName,
				version = s.OrchestrationVersion,
				triggeredBy = s.TriggeredBy,
				startedAt = s.StartedAt.ToString("o"),
				completedAt = s.CompletedAt.ToString("o"),
				durationSeconds = Math.Round(s.Duration.TotalSeconds, 2),
				status = s.Status.ToString(),
				completionReason = s.CompletionReason,
				completedByStep = s.CompletedByStep,
				isActive = false,
				isIncomplete = s.IsIncomplete
			});

			// Combine: running first, then completed
			var allRuns = runningRuns
				.Cast<object>()
				.Concat(completedRuns.Cast<object>())
				.Take(requestedLimit)
				.ToList();

			return Results.Json(new
			{
				count = allRuns.Count,
				runs = allRuns
			}, jsonOptions);
		});

		// GET /api/history/all - Get all executions (paginated)
		historyGroup.MapGet("/all", async (
			FileSystemRunStore runStore,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			int? limit,
			int? offset) =>
		{
			var requestedOffset = offset ?? 0;
			var requestedLimit = limit ?? 300;

			// Get running orchestrations (filter out completed/cancelled/failed during cleanup grace period)
			var runningRuns = activeExecutionInfos.Values
				.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
				.OrderByDescending(e => e.StartedAt)
				.Select(e => new
				{
					runId = e.ExecutionId,
					executionId = e.ExecutionId,
					orchestrationName = e.OrchestrationName,
					version = "1.0.0",
					triggeredBy = e.TriggeredBy,
					startedAt = e.StartedAt.ToString("o"),
					completedAt = (string?)null,
					durationSeconds = Math.Round((DateTimeOffset.UtcNow - e.StartedAt).TotalSeconds, 2),
					status = e.Status,
					isActive = true
				})
				.ToList();

			var runningCount = runningRuns.Count;

			// Get all completed runs from store
			var completedSummaries = await runStore.GetRunSummariesAsync();
			var completedTotal = completedSummaries.Count;
			var totalAll = runningCount + completedTotal;

			// Calculate which items to return based on offset
			var allItems = new List<object>();

			if (requestedOffset < runningCount)
			{
				var runningToTake = runningRuns.Skip(requestedOffset).Take(requestedLimit);
				allItems.AddRange(runningToTake.Cast<object>());

				var remaining = requestedLimit - allItems.Count;
				if (remaining > 0)
				{
				var completedItems = completedSummaries.Take(remaining).Select(s => new
				{
					runId = s.RunId,
					executionId = (string?)null,
					orchestrationName = s.OrchestrationName,
					version = s.OrchestrationVersion,
					triggeredBy = s.TriggeredBy,
					startedAt = s.StartedAt.ToString("o"),
					completedAt = s.CompletedAt.ToString("o"),
					durationSeconds = Math.Round(s.Duration.TotalSeconds, 2),
					status = s.Status.ToString(),
					completionReason = s.CompletionReason,
					completedByStep = s.CompletedByStep,
					isActive = false,
					isIncomplete = s.IsIncomplete
				});
				allItems.AddRange(completedItems.Cast<object>());
			}
		}
		else
		{
			var completedOffset = requestedOffset - runningCount;
			var completedItems = completedSummaries.Skip(completedOffset).Take(requestedLimit).Select(s => new
			{
				runId = s.RunId,
				executionId = (string?)null,
				orchestrationName = s.OrchestrationName,
				version = s.OrchestrationVersion,
				triggeredBy = s.TriggeredBy,
				startedAt = s.StartedAt.ToString("o"),
				completedAt = s.CompletedAt.ToString("o"),
				durationSeconds = Math.Round(s.Duration.TotalSeconds, 2),
				status = s.Status.ToString(),
				completionReason = s.CompletionReason,
				completedByStep = s.CompletedByStep,
				isActive = false,
				isIncomplete = s.IsIncomplete
			});
			allItems.AddRange(completedItems.Cast<object>());
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
		historyGroup.MapGet("/search", async (
			FileSystemRunStore runStore,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			string? query,
			int? limit) =>
		{
			var searchQuery = query?.Trim() ?? "";
			var requestedLimit = limit ?? 300;

			if (string.IsNullOrEmpty(searchQuery))
				return Results.Json(new { total = 0, count = 0, runs = Array.Empty<object>() }, jsonOptions);

			var lowerQuery = searchQuery.ToLowerInvariant();

			// Search across active executions (filter out completed/cancelled/failed during cleanup grace period)
			var matchingActive = activeExecutionInfos.Values
				.Where(e => e.Status is not (HostExecutionStatus.Completed or HostExecutionStatus.Cancelled or HostExecutionStatus.Failed))
				.Where(e => e.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
					|| e.ExecutionId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(e => e.StartedAt)
				.Select(e => new
				{
					runId = e.ExecutionId,
					executionId = e.ExecutionId,
					orchestrationId = e.OrchestrationId,
					orchestrationName = e.OrchestrationName,
					version = "1.0.0",
					triggeredBy = e.TriggeredBy,
					startedAt = e.StartedAt.ToString("o"),
					completedAt = (string?)null,
					durationSeconds = Math.Round((DateTimeOffset.UtcNow - e.StartedAt).TotalSeconds, 2),
					status = e.Status,
					isActive = true,
					isIncomplete = false
				})
				.Cast<object>()
				.ToList();

			// Search across ALL completed runs in the index
			var allSummaries = await runStore.GetRunSummariesAsync();
			var matchingCompleted = allSummaries
				.Where(s => s.OrchestrationName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
					|| s.RunId.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
				.Take(requestedLimit)
				.Select(s => new
				{
					runId = s.RunId,
					executionId = (string?)null,
					orchestrationId = (string?)null,
					orchestrationName = s.OrchestrationName,
					version = s.OrchestrationVersion,
					triggeredBy = s.TriggeredBy,
					startedAt = s.StartedAt.ToString("o"),
					completedAt = s.CompletedAt.ToString("o"),
					durationSeconds = Math.Round(s.Duration.TotalSeconds, 2),
					status = s.Status.ToString(),
					completionReason = s.CompletionReason,
					completedByStep = s.CompletedByStep,
					isActive = false,
					isIncomplete = s.IsIncomplete
				})
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
				isIncomplete = record.IsIncomplete,
				parameters = record.Parameters,
				finalContent = record.FinalContent,
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
					usage = kv.Value.Usage is { } u ? new
					{
						inputTokens = u.InputTokens,
						outputTokens = u.OutputTokens,
						totalTokens = u.TotalTokens
					} : null,
					errorMessage = kv.Value.ErrorMessage,
					trace = kv.Value.Trace is { } t ? new
					{
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
							completedAt = tc.CompletedAt?.ToString("o")
						}).ToArray(),
						responseSegments = t.ResponseSegments,
						finalResponse = t.FinalResponse,
						outputHandlerResult = t.OutputHandlerResult,
						mcpServers = t.McpServers.Count > 0 ? t.McpServers : null,
						warnings = t.Warnings.Count > 0 ? t.Warnings : null,
					} : null
				}).ToArray()
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
					currentStep = info.CurrentStep
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
			});

			return Results.Json(new
			{
				running = activeList,
				pending,
				totalRunning = activeList.Count,
				totalPending = pending.Count()
			}, jsonOptions);
		});

		// POST /api/active/{executionId}/cancel - Cancel a running execution
		activeGroup.MapPost("/{executionId}/cancel", (
			string executionId,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
		{
			if (activeExecutionInfos.TryGetValue(executionId, out var info))
			{
				info.Status = HostExecutionStatus.Cancelling;
				if (info.Reporter is SseReporter sseReporter)
					sseReporter.ReportStatusChange(HostExecutionStatus.Cancelling);
				info.CancellationTokenSource.Cancel();
				return Results.Ok(new { cancelled = true, executionId, status = HostExecutionStatus.Cancelling });
			}
			return ProblemDetailsHelpers.NotFound($"No active execution with ID '{executionId}'.");
		});

		return endpoints;
	}
}
