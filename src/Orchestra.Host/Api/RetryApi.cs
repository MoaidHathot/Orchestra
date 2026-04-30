using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Services;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for retrying historical orchestration executions.
/// Provides three retry modes:
/// <list type="bullet">
///   <item><description><c>failed</c>: only re-runs steps that did not succeed.</description></item>
///   <item><description><c>all</c>: re-runs the entire orchestration with the original parameters.</description></item>
///   <item><description><c>from-step</c>: re-runs the named step plus all downstream dependents.</description></item>
/// </list>
/// All endpoints stream Server-Sent Events using the same vocabulary as
/// <see cref="ExecutionApi"/> so the existing Portal modal works unchanged.
/// </summary>
public static partial class RetryApi
{
	/// <summary>
	/// Maps the retry endpoint(s).
	/// </summary>
	public static IEndpointRouteBuilder MapRetryApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		// GET /api/history/{orchestrationName}/{runId}/retry?mode=failed|all|from-step&step=<name>
		// SSE — must be GET for EventSource compatibility.
		endpoints.MapGet("/api/history/{orchestrationName}/{runId}/retry", async (
			HttpContext httpContext,
			string orchestrationName,
			string runId,
			OrchestrationRegistry registry,
			AgentBuilder agentBuilder,
			IScheduler scheduler,
			ILoggerFactory loggerFactory,
			ICheckpointStore checkpointStore,
			FileSystemRunStore runStore,
			OrchestrationHostOptions hostOptions,
			EngineToolRegistry engineToolRegistry,
			McpManager mcpManager,
			IOrchestrationReporterFactory reporterFactory,
			ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			DashboardEventBroadcaster dashboardBroadcaster) =>
		{
			var modeRaw = httpContext.Request.Query["mode"].FirstOrDefault();
			var fromStep = httpContext.Request.Query["step"].FirstOrDefault();
			if (!RetryService.TryParseMode(modeRaw, out var mode))
			{
				await WriteProblemAsync(httpContext, 400, "Bad Request",
					$"Invalid retry mode '{modeRaw}'. Expected one of: failed, all, from-step.");
				return;
			}

			if (mode == RetryMode.FromStep && string.IsNullOrEmpty(fromStep))
			{
				await WriteProblemAsync(httpContext, 400, "Bad Request",
					"Retry mode 'from-step' requires a 'step' query parameter naming the target step.");
				return;
			}

			var sourceRun = await runStore.GetRunAsync(orchestrationName, runId);
			if (sourceRun is null)
			{
				await WriteProblemAsync(httpContext, 404, "Not Found",
					$"No run found for orchestration '{orchestrationName}', run '{runId}'.");
				return;
			}

			// Locate the orchestration entry by name (history is keyed by name; registry by id).
			var entry = registry.GetAll().FirstOrDefault(e =>
				string.Equals(e.Orchestration.Name, orchestrationName, StringComparison.Ordinal));
			if (entry is null)
			{
				await WriteProblemAsync(httpContext, 404, "Not Found",
					$"Orchestration '{orchestrationName}' is no longer registered. Cannot retry runs against a deleted orchestration.");
				return;
			}

			CheckpointData? checkpoint;
			try
			{
				checkpoint = RetryService.BuildCheckpoint(
					entry.Orchestration,
					sourceRun,
					mode,
					newRunId: Guid.NewGuid().ToString("N")[..12],
					checkpointedAt: DateTimeOffset.UtcNow,
					fromStep: fromStep);
			}
			catch (InvalidOperationException ex)
			{
				await WriteProblemAsync(httpContext, 400, "Bad Request", ex.Message);
				return;
			}

			// Set up SSE response
			httpContext.Response.ContentType = "text/event-stream";
			httpContext.Response.Headers.CacheControl = "no-cache";
			httpContext.Response.Headers.Connection = "keep-alive";
			await httpContext.Response.Body.FlushAsync();

			var executionId = checkpoint?.RunId ?? Guid.NewGuid().ToString("N")[..12];
			var reporter = (SseReporter)reporterFactory.Create();
			var cts = new CancellationTokenSource();
			var retryModeString = RetryService.FormatRetryMode(mode, fromStep);

			activeExecutions[executionId] = cts;
			var executionInfo = new ActiveExecutionInfo
			{
				ExecutionId = executionId,
				OrchestrationId = entry.Id,
				OrchestrationName = entry.Orchestration.Name,
				StartedAt = DateTimeOffset.UtcNow,
				TriggeredBy = "retry",
				CancellationTokenSource = cts,
				Reporter = reporter,
				Parameters = sourceRun.Parameters.Count > 0 ? new Dictionary<string, string>(sourceRun.Parameters) : null,
				TotalSteps = entry.Orchestration.Steps.Length,
				CompletedSteps = checkpoint?.CompletedSteps.Count ?? 0,
			};
			activeExecutionInfos[executionId] = executionInfo;

			reporter.OnStepStarted = (stepName) => { executionInfo.CurrentStep = stepName; };
			reporter.OnStepCompleted = (stepName) =>
			{
				executionInfo.IncrementCompletedSteps();
				executionInfo.CurrentStep = null;
			};

			// Send execution-started event with retry lineage so the UI can render a "Retried from" link.
			await httpContext.Response.WriteAsync($"event: execution-started\n");
			await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new
			{
				executionId,
				retriedFromRunId = runId,
				retryMode = retryModeString,
				stepsRestored = checkpoint?.CompletedSteps.Keys.ToArray() ?? [],
			}, jsonOptions)}\n\n");
			await httpContext.Response.Body.FlushAsync();

			dashboardBroadcaster.BroadcastExecutionStarted(
				executionId,
				entry.Id,
				entry.Orchestration.Name,
				"retry");

			var executor = new OrchestrationExecutor(
				scheduler, agentBuilder, reporter, loggerFactory,
				runStore: runStore,
				checkpointStore: checkpointStore,
				engineToolRegistry: engineToolRegistry,
				mcpResolver: mcpManager,
				globalHooks: hostOptions.Hooks,
				dataPath: hostOptions.DataPath,
				serverUrl: hostOptions.HostBaseUrl);
			var cancellationToken = cts.Token;
			var startTime = DateTimeOffset.UtcNow;
			var logger = loggerFactory.CreateLogger(typeof(RetryApi));

			var retryMetadata = new RetryMetadata
			{
				RetriedFromRunId = runId,
				RetryMode = retryModeString,
				OverrideRunId = executionId,
				TriggeredBy = "retry",
			};

			var executionTask = Task.Run(async () =>
			{
				try
				{
					OrchestrationResult result;
					if (checkpoint is null)
					{
						// Mode = "all" — fresh execution with original parameters
						result = await executor.ExecuteAsync(
							entry.Orchestration,
							parameters: sourceRun.Parameters.Count > 0
								? new Dictionary<string, string>(sourceRun.Parameters)
								: null,
							triggerId: null,
							preExecutionParameterTransform: null,
							retryMetadata: retryMetadata,
							cancellationToken: cancellationToken);
					}
					else
					{
						// Modes = "failed" or "from-step" — restore succeeded steps from checkpoint
						result = await executor.ResumeAsync(
							entry.Orchestration,
							checkpoint,
							retryMetadata,
							cancellationToken);
					}

					if (result.Status == ExecutionStatus.Cancelled)
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
					LogRetryExecutionFailed(logger, executionId, ex);
				}
				finally
				{
					reporter.Complete();
					dashboardBroadcaster.BroadcastExecutionCompleted(
						executionId,
						entry.Id,
						entry.Orchestration.Name,
						executionInfo.Status.ToString());

					_ = Task.Run(async () =>
					{
						try { await Task.Delay(TimeSpan.FromSeconds(5)); }
						catch (ObjectDisposedException) { }
						finally
						{
							activeExecutions.TryRemove(executionId, out _);
							activeExecutionInfos.TryRemove(executionId, out _);
							try { cts.Dispose(); } catch (ObjectDisposedException) { }
						}
					});
				}
			}, CancellationToken.None);

			// Subscribe and stream SSE events to this client
			var (replay, futureEvents) = reporter.Subscribe();
			var sseToken = httpContext.RequestAborted;

			foreach (var evt in replay)
			{
				await httpContext.Response.WriteAsync($"event: {evt.Type}\n", sseToken);
				await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", sseToken);
			}
			await httpContext.Response.Body.FlushAsync(sseToken);

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

	private static async Task WriteProblemAsync(HttpContext httpContext, int status, string title, string detail)
	{
		httpContext.Response.StatusCode = status;
		httpContext.Response.ContentType = "application/problem+json";
		await httpContext.Response.WriteAsJsonAsync(new
		{
			type = "https://tools.ietf.org/html/rfc7807",
			title,
			status,
			detail,
			instance = httpContext.Request.Path.Value,
		});
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "Retry execution '{ExecutionId}' failed unexpectedly")]
	private static partial void LogRetryExecutionFailed(ILogger logger, string executionId, Exception ex);
}
