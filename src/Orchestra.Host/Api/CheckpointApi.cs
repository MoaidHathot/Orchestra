using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for checkpoint management and orchestration resume.
/// </summary>
public static class CheckpointApi
{
	/// <summary>
	/// Maps checkpoint and resume endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapCheckpointApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		// GET /api/checkpoints - List all checkpoints
		endpoints.MapGet("/api/checkpoints", async (
			ICheckpointStore checkpointStore) =>
		{
			var checkpoints = await checkpointStore.ListCheckpointsAsync();
			return Results.Ok(checkpoints.Select(c => new
			{
				runId = c.RunId,
				orchestrationName = c.OrchestrationName,
				startedAt = c.StartedAt,
				checkpointedAt = c.CheckpointedAt,
				completedSteps = c.CompletedSteps.Count,
				completedStepNames = c.CompletedSteps.Keys.ToArray(),
				triggerId = c.TriggerId,
			}));
		});

		// GET /api/checkpoints/{orchestrationName} - List checkpoints for an orchestration
		endpoints.MapGet("/api/checkpoints/{orchestrationName}", async (
			string orchestrationName,
			ICheckpointStore checkpointStore) =>
		{
			var checkpoints = await checkpointStore.ListCheckpointsAsync(orchestrationName);
			return Results.Ok(checkpoints.Select(c => new
			{
				runId = c.RunId,
				orchestrationName = c.OrchestrationName,
				startedAt = c.StartedAt,
				checkpointedAt = c.CheckpointedAt,
				completedSteps = c.CompletedSteps.Count,
				completedStepNames = c.CompletedSteps.Keys.ToArray(),
				triggerId = c.TriggerId,
			}));
		});

		// GET /api/checkpoints/{orchestrationName}/{runId} - Get a specific checkpoint
		endpoints.MapGet("/api/checkpoints/{orchestrationName}/{runId}", async (
			string orchestrationName,
			string runId,
			ICheckpointStore checkpointStore) =>
		{
			var checkpoint = await checkpointStore.LoadCheckpointAsync(orchestrationName, runId);
			if (checkpoint is null)
			{
				return ProblemDetailsHelpers.NotFound(
					$"No checkpoint found for orchestration '{orchestrationName}', run '{runId}'.");
			}

			return Results.Ok(checkpoint);
		});

		// DELETE /api/checkpoints/{orchestrationName}/{runId} - Delete a checkpoint
		endpoints.MapDelete("/api/checkpoints/{orchestrationName}/{runId}", async (
			string orchestrationName,
			string runId,
			ICheckpointStore checkpointStore) =>
		{
			var checkpoint = await checkpointStore.LoadCheckpointAsync(orchestrationName, runId);
			if (checkpoint is null)
			{
				return ProblemDetailsHelpers.NotFound(
					$"No checkpoint found for orchestration '{orchestrationName}', run '{runId}'.");
			}

			await checkpointStore.DeleteCheckpointAsync(orchestrationName, runId);
			return Results.Ok(new { message = $"Checkpoint deleted for run '{runId}'." });
		});

		// GET /api/orchestrations/{id}/resume/{runId} - Resume from checkpoint (SSE)
		// NOTE: Must be GET for EventSource compatibility (SSE clients only support GET)
		endpoints.MapGet("/api/orchestrations/{id}/resume/{runId}", async (
			HttpContext httpContext,
			string id,
			string runId,
			OrchestrationRegistry registry,
			AgentBuilder agentBuilder,
			IScheduler scheduler,
			ILoggerFactory loggerFactory,
			ICheckpointStore checkpointStore,
			FileSystemRunStore runStore,
			OrchestrationHostOptions hostOptions,
			ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
		{
			var entry = registry.Get(id);
			if (entry is null)
			{
				httpContext.Response.StatusCode = 404;
				httpContext.Response.ContentType = "application/problem+json";
				await httpContext.Response.WriteAsJsonAsync(new
				{
					type = "https://tools.ietf.org/html/rfc7807",
					title = "Not Found",
					status = 404,
					detail = $"Orchestration '{id}' not found.",
					instance = httpContext.Request.Path.Value,
				});
				return;
			}

			var checkpoint = await checkpointStore.LoadCheckpointAsync(entry.Orchestration.Name, runId);
			if (checkpoint is null)
			{
				httpContext.Response.StatusCode = 404;
				httpContext.Response.ContentType = "application/problem+json";
				await httpContext.Response.WriteAsJsonAsync(new
				{
					type = "https://tools.ietf.org/html/rfc7807",
					title = "Not Found",
					status = 404,
					detail = $"No checkpoint found for orchestration '{entry.Orchestration.Name}', run '{runId}'.",
					instance = httpContext.Request.Path.Value,
				});
				return;
			}

			// Set up SSE response
			httpContext.Response.ContentType = "text/event-stream";
			httpContext.Response.Headers.CacheControl = "no-cache";
			httpContext.Response.Headers.Connection = "keep-alive";
			await httpContext.Response.Body.FlushAsync();

			var executionId = runId; // Reuse the original run ID for resume
			var reporter = new SseReporter();
			var cts = new CancellationTokenSource();

			activeExecutions[executionId] = cts;
			var executionInfo = new ActiveExecutionInfo
			{
				ExecutionId = executionId,
				OrchestrationId = id,
				OrchestrationName = entry.Orchestration.Name,
				StartedAt = checkpoint.StartedAt,
				TriggeredBy = "resume",
				CancellationTokenSource = cts,
				Reporter = reporter,
				Parameters = checkpoint.Parameters.Count > 0 ? checkpoint.Parameters : null,
				TotalSteps = entry.Orchestration.Steps.Length,
				CompletedSteps = checkpoint.CompletedSteps.Count,
			};
			activeExecutionInfos[executionId] = executionInfo;

			// Set up progress callbacks
			reporter.OnStepStarted = (stepName) =>
			{
				executionInfo.CurrentStep = stepName;
			};
			reporter.OnStepCompleted = (stepName) =>
			{
				executionInfo.CompletedSteps++;
				executionInfo.CurrentStep = null;
			};

			// Send resume-started event with checkpoint info
			await httpContext.Response.WriteAsync($"event: resume-started\n");
			await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new
			{
				executionId,
				resumedFrom = checkpoint.CheckpointedAt,
				completedSteps = checkpoint.CompletedSteps.Keys.ToArray(),
				remainingSteps = entry.Orchestration.Steps
					.Select(s => s.Name)
					.Where(n => !checkpoint.CompletedSteps.ContainsKey(n))
					.ToArray(),
			}, jsonOptions)}\n\n");
			await httpContext.Response.Body.FlushAsync();

			var executor = new OrchestrationExecutor(scheduler, agentBuilder, reporter, loggerFactory, runStore: runStore, checkpointStore: checkpointStore, dataPath: hostOptions.DataPath);
			var cancellationToken = cts.Token;

			// Execute resume in background
			var executionTask = Task.Run(async () =>
			{
				try
				{
					var result = await executor.ResumeAsync(
						entry.Orchestration,
						checkpoint,
						cancellationToken);

					if (cancellationToken.IsCancellationRequested)
					{
						reporter.ReportOrchestrationCancelled();
						executionInfo.Status = HostExecutionStatus.Cancelled;
						return;
					}

					foreach (var (stepName, stepResult) in result.StepResults)
					{
						if (stepResult.Status == ExecutionStatus.Succeeded)
							reporter.ReportStepOutput(stepName, stepResult.Content);
					}

					reporter.ReportOrchestrationDone(result);
					executionInfo.Status = HostExecutionStatus.Completed;
				}
				catch (OperationCanceledException)
				{
					reporter.ReportOrchestrationCancelled();
					executionInfo.Status = HostExecutionStatus.Cancelled;
				}
				catch (Exception ex)
				{
					reporter.ReportStepError("orchestration", ex.Message);
					reporter.ReportOrchestrationError(ex.Message);
					executionInfo.Status = HostExecutionStatus.Failed;
				}
				finally
				{
					reporter.Complete();
					_ = Task.Run(async () =>
					{
						await Task.Delay(TimeSpan.FromSeconds(5));
						activeExecutions.TryRemove(executionId, out _);
						activeExecutionInfos.TryRemove(executionId, out _);
						cts.Dispose();
					});
				}
			}, CancellationToken.None);

			// Subscribe and stream SSE events to this client
			var (replay, futureEvents) = reporter.Subscribe();
			var sseToken = httpContext.RequestAborted;

			// Replay any events that happened before we subscribed
			foreach (var evt in replay)
			{
				await httpContext.Response.WriteAsync($"event: {evt.Type}\n", sseToken);
				await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", sseToken);
			}
			await httpContext.Response.Body.FlushAsync(sseToken);

			// Stream future events until client disconnects OR orchestration completes
			if (futureEvents is not null)
			{
				try
				{
					await foreach (var evt in futureEvents.ReadAllAsync(sseToken))
					{
						await httpContext.Response.WriteAsync($"event: {evt.Type}\n", sseToken);
						await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", sseToken);
						await httpContext.Response.Body.FlushAsync(sseToken);
					}
				}
				catch (OperationCanceledException)
				{
					reporter.Unsubscribe(futureEvents);
				}
			}

			if (!sseToken.IsCancellationRequested)
			{
				await executionTask;
			}
		});

		return endpoints;
	}
}
