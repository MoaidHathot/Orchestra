using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
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
///
/// <para>
/// For <c>mode=all</c> the caller may additionally pass <c>?params=&lt;URL-encoded JSON
/// object&gt;</c> to OVERRIDE the source run's parameters (the "re-run with edits"
/// flow exposed in the Portal as the "Re-run with edits..." button). When supplied
/// and non-empty, the override fully replaces the stored parameter set and the run
/// record is tagged <c>retryMode = "all-edited"</c> so historical browsing can tell
/// the two flavours apart. The retry lineage (<c>retriedFromRunId</c>) is preserved
/// either way. Override is rejected (HTTP 400) for <c>failed</c> / <c>from-step</c>
/// modes because those replay completed-step outputs from a checkpoint whose key
/// values were derived from the original parameter set; changing parameters mid-
/// replay would produce a run whose checkpointed step outputs no longer reflect the
/// current parameters, which is incoherent.
/// </para>
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
			IAgentProviderRegistry providerRegistry,
			IScheduler scheduler,
			ILoggerFactory loggerFactory,
			ICheckpointStore checkpointStore,
			FileSystemRunStore runStore,
			OrchestrationHostOptions hostOptions,
			EngineToolRegistry engineToolRegistry,
			McpManager mcpManager,
			IOrchestrationReporterFactory reporterFactory,
			IHostApplicationLifetime lifetime,
			IChildOrchestrationLauncher childLauncher,
			IPendingInputStore pendingInputStore,
			IHumanInputWaiter humanInputWaiter,
			ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			DashboardEventBroadcaster dashboardBroadcaster) =>
		{
			var modeRaw = httpContext.Request.Query["mode"].FirstOrDefault();
			var fromStep = httpContext.Request.Query["step"].FirstOrDefault();
			var paramsOverrideRaw = httpContext.Request.Query["params"].FirstOrDefault();
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

			// Parse the optional ?params=<URL-encoded JSON object> override (the Portal's
			// "Re-run with edits" flow sends this). We accept it only for mode=all because
			// failed / from-step replay checkpointed step outputs that were derived from the
			// original parameters; swapping parameters mid-replay would silently corrupt the
			// per-step inputs visible to dependent steps that aren't being replayed.
			Dictionary<string, string>? paramsOverride = null;
			var hasParamsOverride = false;
			if (!string.IsNullOrEmpty(paramsOverrideRaw))
			{
				if (mode != RetryMode.All)
				{
					await WriteProblemAsync(httpContext, 400, "Bad Request",
						"Parameter overrides are only valid for retry mode 'all'. " +
						"'failed' and 'from-step' replay checkpointed outputs derived from the original parameters.");
					return;
				}

				try
				{
					var parsed = JsonSerializer.Deserialize<JsonElement>(paramsOverrideRaw, jsonOptions);
					if (parsed.ValueKind == JsonValueKind.Object)
					{
						paramsOverride = new Dictionary<string, string>();
						foreach (var prop in parsed.EnumerateObject())
						{
							var val = prop.Value.ValueKind switch
							{
								JsonValueKind.String => prop.Value.GetString(),
								JsonValueKind.Number => prop.Value.ToString(),
								JsonValueKind.True => "true",
								JsonValueKind.False => "false",
								_ => null,
							};
							if (val is not null && val.Length > 0)
							{
								paramsOverride[prop.Name] = val;
							}
						}
					}
				}
				catch (JsonException)
				{
					await WriteProblemAsync(httpContext, 400, "Bad Request",
						"Invalid JSON in 'params' query parameter. Expected a URL-encoded JSON object of {key:string}.");
					return;
				}

				// Treat an empty / all-empty-values object as "no override supplied" so the
				// stored parameters survive. A genuinely user-cleared override sends nothing.
				hasParamsOverride = paramsOverride is { Count: > 0 };
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
			// Tag the retry mode in run records as "all-edited" when the caller supplied a
			// non-empty parameter override; otherwise the standard formatter wins. This keeps
			// historical browsing able to distinguish "rerun verbatim" from "rerun with edits"
			// without inventing a separate API surface.
			var retryModeString = hasParamsOverride
				? "all-edited"
				: RetryService.FormatRetryMode(mode, fromStep);

			// The parameter set that actually drives this run: override if supplied, else
			// the source run's stored parameters. Snapshotted once so the ActiveExecutionInfo,
			// the executor call, and the retry-metadata all see exactly the same dictionary.
			var effectiveParameters = hasParamsOverride
				? paramsOverride
				: (sourceRun.Parameters.Count > 0
					? new Dictionary<string, string>(sourceRun.Parameters)
					: null);

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
				Parameters = effectiveParameters,
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
				scheduler, providerRegistry, reporter, loggerFactory,
				runStore: runStore,
				checkpointStore: checkpointStore,
				engineToolRegistry: engineToolRegistry,
				mcpResolver: mcpManager,
				childLauncher: childLauncher,
				globalHooks: hostOptions.Hooks,
				dataPath: hostOptions.DataPath,
				serverUrl: hostOptions.HostBaseUrl,
				// Wire the HITL store + waiter so retried Approval / engine-tool steps actually
				// persist a PendingInputRecord and register an in-memory wait. Without these
				// the executor falls back to NullPendingInputStore (drops saves silently) and
				// NullHumanInputWaiter (blocks forever and never responds to POST /respond),
				// leaving any retried run with a human-input step permanently stuck.
				pendingInputStore: pendingInputStore,
				humanInputWaiter: humanInputWaiter);
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
						// Mode = "all" — fresh execution with parameters from the override (when
						// the caller supplied ?params=...) or the source run otherwise. The
						// snapshot is captured in `effectiveParameters` above so the executor,
						// the ActiveExecutionInfo, and the dashboard all agree.
						result = await executor.ExecuteAsync(
							entry.Orchestration,
							parameters: effectiveParameters is { Count: > 0 }
								? new Dictionary<string, string>(effectiveParameters)
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
							retryMetadata: retryMetadata,
							cancellationToken: cancellationToken);
					}

					if (result.Status == ExecutionStatus.Cancelled)
					{
						reporter.ReportOrchestrationCancelled();
						executionInfo.Status = HostExecutionStatus.Cancelled;
						return;
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
			using var sseCts = CancellationTokenSource.CreateLinkedTokenSource(
				httpContext.RequestAborted,
				lifetime.ApplicationStopping);
			var sseToken = sseCts.Token;

			foreach (var evt in replay)
			{
				await SseEventWriter.WriteAsync(httpContext.Response, evt, sseToken);
			}
			await httpContext.Response.Body.FlushAsync(sseToken);

			if (futureEvents is not null)
			{
				try
				{
					await foreach (var evt in futureEvents.ReadAllAsync(sseToken))
					{
						await SseEventWriter.WriteAsync(httpContext.Response, evt, sseToken);
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
