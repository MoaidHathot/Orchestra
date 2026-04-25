using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for execution streaming via SSE.
/// </summary>
public static partial class ExecutionApi
{
	/// <summary>
	/// Maps execution streaming endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapExecutionApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		// GET /api/orchestrations/{id}/run - Run an orchestration (SSE)
		// NOTE: Must be GET for EventSource compatibility (SSE clients only support GET)
		endpoints.MapGet("/api/orchestrations/{id}/run", async (
			HttpContext httpContext,
			string id,
			OrchestrationRegistry registry,
			AgentBuilder agentBuilder,
			IScheduler scheduler,
			ILoggerFactory loggerFactory,
			FileSystemRunStore runStore,
			OrchestrationHostOptions hostOptions,
			EngineToolRegistry engineToolRegistry,
			McpManager mcpManager,
			IOrchestrationReporterFactory reporterFactory,
			ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			DashboardEventBroadcaster dashboardBroadcaster) =>
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

			// Parse optional parameters from query string (EventSource can't send body)
			Dictionary<string, string>? parameters = null;
			var paramsQuery = httpContext.Request.Query["params"].FirstOrDefault();
			if (!string.IsNullOrEmpty(paramsQuery))
			{
				try
				{
					var paramsEl = JsonSerializer.Deserialize<JsonElement>(paramsQuery, jsonOptions);
					if (paramsEl.ValueKind == JsonValueKind.Object)
					{
						parameters = new Dictionary<string, string>();
						foreach (var prop in paramsEl.EnumerateObject())
						{
							var val = prop.Value.GetString();
							if (val is not null && val.Length > 0)
								parameters[prop.Name] = val;
						}
					}
				}
				catch (JsonException)
				{
					httpContext.Response.StatusCode = 400;
					httpContext.Response.ContentType = "application/problem+json";
					await httpContext.Response.WriteAsJsonAsync(new
					{
						type = "https://tools.ietf.org/html/rfc7807",
						title = "Bad Request",
						status = 400,
						detail = "Invalid JSON in 'params' query parameter.",
						instance = httpContext.Request.Path.Value,
					});
					return;
				}
			}

			// Set up SSE response
			httpContext.Response.ContentType = "text/event-stream";
			httpContext.Response.Headers.CacheControl = "no-cache";
			httpContext.Response.Headers.Connection = "keep-alive";
			await httpContext.Response.Body.FlushAsync();

			// Generate execution ID and create reporter
			var executionId = Guid.NewGuid().ToString("N")[..12];
			var reporter = (SseReporter)reporterFactory.Create();
			var cts = new CancellationTokenSource();

			activeExecutions[executionId] = cts;
			var executionInfo = new ActiveExecutionInfo
			{
				ExecutionId = executionId,
				OrchestrationId = id,
				OrchestrationName = entry.Orchestration.Name,
				StartedAt = DateTimeOffset.UtcNow,
				TriggeredBy = "manual",
				CancellationTokenSource = cts,
				Reporter = reporter,
				Parameters = parameters,
				TotalSteps = entry.Orchestration.Steps.Length
			};
			activeExecutionInfos[executionId] = executionInfo;

			// Set up progress callbacks
			reporter.OnStepStarted = (stepName) =>
			{
				executionInfo.CurrentStep = stepName;
			};
			reporter.OnStepCompleted = (stepName) =>
			{
				executionInfo.IncrementCompletedSteps();
				executionInfo.CurrentStep = null;
			};

			// Send execution-started event
			await httpContext.Response.WriteAsync($"event: execution-started\n");
			await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { executionId }, jsonOptions)}\n\n");
			await httpContext.Response.Body.FlushAsync();

			// Notify dashboard subscribers so the Portal can refresh Active/Recent lists
			// without polling.
			dashboardBroadcaster.BroadcastExecutionStarted(
				executionId,
				id,
				entry.Orchestration.Name,
				"manual");

			var executor = new OrchestrationExecutor(scheduler, agentBuilder, reporter, loggerFactory, runStore: runStore, engineToolRegistry: engineToolRegistry, mcpResolver: mcpManager, globalHooks: hostOptions.Hooks, dataPath: hostOptions.DataPath, serverUrl: hostOptions.HostBaseUrl);
			var cancellationToken = cts.Token;
			var runId = executionId;
			var runStartedAt = DateTimeOffset.UtcNow;
			var logger = loggerFactory.CreateLogger(typeof(ExecutionApi));

			// Execute in background
			var executionTask = Task.Run(async () =>
			{
				try
				{
					var result = await executor.ExecuteAsync(
						entry.Orchestration,
						parameters,
						cancellationToken: cancellationToken);

					if (result.Status == ExecutionStatus.Cancelled)
					{
						// Engine already saved the run record with Cancelled status.
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
					// Engine may not have saved the run record if cancellation occurred
					// before steps could complete, so save a cancelled record from SSE events.
					reporter.ReportOrchestrationCancelled();
					executionInfo.Status = HostExecutionStatus.Cancelled;
					await SaveCancelledRunAsync(runStore, entry, runId, runStartedAt, parameters, reporter, logger);
				}
				catch (Exception ex)
				{
					reporter.ReportStepError("orchestration", ex.Message);
				reporter.ReportOrchestrationError(ex.Message);
				executionInfo.Status = HostExecutionStatus.Failed;
					await SaveFailedRunAsync(runStore, entry, runId, runStartedAt, parameters, reporter, ex.Message, logger);
				}
				finally
				{
					reporter.Complete();

					// Notify dashboard subscribers that the execution reached a terminal state.
					dashboardBroadcaster.BroadcastExecutionCompleted(
						executionId,
						id,
						entry.Orchestration.Name,
						executionInfo.Status.ToString());

					_ = Task.Run(async () =>
					{
						try
						{
							await Task.Delay(TimeSpan.FromSeconds(5));
						}
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

			// Replay any events that happened before we subscribed
			foreach (var evt in replay)
			{
				await httpContext.Response.WriteAsync($"event: {evt.Type}\n", sseToken);
				await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", sseToken);
			}
			await httpContext.Response.Body.FlushAsync(sseToken);

			// Start heartbeat to keep the SSE connection alive
			_ = SendHeartbeatsAsync(reporter, sseToken);

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

		// GET /api/execution/{executionId}/attach - Attach to a running execution's SSE stream
		endpoints.MapGet("/api/execution/{executionId}/attach", async (
			string executionId,
			HttpContext httpContext,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
		{
			if (!activeExecutionInfos.TryGetValue(executionId, out var info))
			{
				httpContext.Response.StatusCode = 404;
				httpContext.Response.ContentType = "application/problem+json";
				await httpContext.Response.WriteAsJsonAsync(new
				{
					type = "https://tools.ietf.org/html/rfc7807",
					title = "Not Found",
					status = 404,
					detail = $"No active execution with ID '{executionId}'.",
					instance = httpContext.Request.Path.Value,
				});
				return;
			}

			if (info.Reporter is not SseReporter sseReporter)
			{
				httpContext.Response.StatusCode = 500;
				httpContext.Response.ContentType = "application/problem+json";
				await httpContext.Response.WriteAsJsonAsync(new
				{
					type = "https://tools.ietf.org/html/rfc7807",
					title = "Internal Server Error",
					status = 500,
					detail = "Execution reporter is not an SseReporter.",
					instance = httpContext.Request.Path.Value,
				});
				return;
			}

			// Set up SSE response
			httpContext.Response.ContentType = "text/event-stream";
			httpContext.Response.Headers.CacheControl = "no-cache";
			httpContext.Response.Headers.Connection = "keep-alive";
			await httpContext.Response.Body.FlushAsync();

			var cancellationToken = httpContext.RequestAborted;

			// Send current execution info
			await httpContext.Response.WriteAsync($"event: execution-info\n");
			await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new
			{
				executionId = info.ExecutionId,
				orchestrationId = info.OrchestrationId,
				orchestrationName = info.OrchestrationName,
				startedAt = info.StartedAt,
				triggeredBy = info.TriggeredBy,
				status = info.Status,
				parameters = info.Parameters
			}, jsonOptions)}\n\n");
			await httpContext.Response.Body.FlushAsync();

			// Subscribe to the reporter
			var (replay, futureEvents) = sseReporter.Subscribe();

			// Replay accumulated events
			foreach (var evt in replay)
			{
				await httpContext.Response.WriteAsync($"event: {evt.Type}\n", cancellationToken);
				await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", cancellationToken);
			}
			await httpContext.Response.Body.FlushAsync(cancellationToken);

			// If already completed, we're done
			if (sseReporter.IsCompleted)
			{
				return;
			}

			// Start heartbeat to keep the SSE connection alive
			_ = SendHeartbeatsAsync(sseReporter, cancellationToken);

			// Stream future events
			if (futureEvents is not null)
			{
				try
				{
					await foreach (var evt in futureEvents.ReadAllAsync(cancellationToken))
					{
						await httpContext.Response.WriteAsync($"event: {evt.Type}\n", cancellationToken);
						await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", cancellationToken);
						await httpContext.Response.Body.FlushAsync(cancellationToken);
					}
				}
				catch (OperationCanceledException)
				{
					sseReporter.Unsubscribe(futureEvents);
				}
			}
		});

		return endpoints;
	}

	private static async Task SaveCancelledRunAsync(
		FileSystemRunStore store,
		OrchestrationEntry entry,
		string runId,
		DateTimeOffset startTime,
		Dictionary<string, string>? parameters,
		SseReporter reporter,
		ILogger logger)
	{
		var completedAt = DateTimeOffset.UtcNow;
		var stepRecords = new Dictionary<string, StepRunRecord>();
		var allStepRecords = new Dictionary<string, StepRunRecord>();
		var summary = new System.Text.StringBuilder();
		summary.AppendLine("Orchestration was cancelled.");

		// Parse accumulated events to build step records
		var stepsStarted = new HashSet<string>();
		var stepsCompleted = new HashSet<string>();
		var stepsCancelled = new HashSet<string>();
		var stepErrors = new Dictionary<string, string>();

		foreach (var evt in reporter.AccumulatedEvents)
		{
			try
			{
				var data = JsonSerializer.Deserialize<JsonElement>(evt.Data);
				switch (evt.Type)
				{
					case "step-started":
						if (data.TryGetProperty("stepName", out var startedName))
							stepsStarted.Add(startedName.GetString() ?? "");
						break;
					case "step-completed":
						if (data.TryGetProperty("stepName", out var completedName))
							stepsCompleted.Add(completedName.GetString() ?? "");
						break;
					case "step-cancelled":
						if (data.TryGetProperty("stepName", out var cancelledName))
							stepsCancelled.Add(cancelledName.GetString() ?? "");
						break;
					case "step-error":
						if (data.TryGetProperty("stepName", out var errorStepName) &&
							data.TryGetProperty("error", out var errorMsg))
							stepErrors[errorStepName.GetString() ?? ""] = errorMsg.GetString() ?? "";
						break;
				}
			}
			catch (JsonException) { /* Ignore parse errors */ }
		}

		// Build step records for ALL steps
		foreach (var step in entry.Orchestration.Steps)
		{
			var stepName = step.Name;
			ExecutionStatus status;
			string? errorMessage = null;
			string content = "";

			if (stepsCompleted.Contains(stepName))
			{
				status = ExecutionStatus.Succeeded;
			}
			else if (stepsCancelled.Contains(stepName))
			{
				status = ExecutionStatus.Cancelled;
				content = "[Cancelled]";
				errorMessage = "Cancelled";
			}
			else if (stepErrors.TryGetValue(stepName, out var err))
			{
				status = ExecutionStatus.Failed;
				errorMessage = err;
			}
			else if (stepsStarted.Contains(stepName))
			{
				status = ExecutionStatus.Cancelled;
				content = "[Cancelled while in progress]";
				errorMessage = "Cancelled while in progress";
			}
			else
			{
				status = ExecutionStatus.Skipped;
				content = "[Skipped - orchestration cancelled]";
			}

			var stepRecord = new StepRunRecord
			{
				StepName = stepName,
				Status = status,
				StartedAt = stepsStarted.Contains(stepName) ? startTime : completedAt,
				CompletedAt = completedAt,
				Content = content,
				ErrorMessage = errorMessage
			};

			stepRecords[stepName] = stepRecord;
			allStepRecords[stepName] = stepRecord;
		}

		if (stepsCompleted.Count > 0)
			summary.AppendLine($"Completed steps: {string.Join(", ", stepsCompleted)}");
		if (stepsCancelled.Count > 0)
			summary.AppendLine($"Cancelled steps: {string.Join(", ", stepsCancelled)}");
		var inProgress = stepsStarted.Except(stepsCompleted).Except(stepsCancelled).ToList();
		if (inProgress.Count > 0)
			summary.AppendLine($"In-progress steps when cancelled: {string.Join(", ", inProgress)}");
		var skipped = entry.Orchestration.Steps.Select(s => s.Name).Except(stepsStarted).ToList();
		if (skipped.Count > 0)
			summary.AppendLine($"Skipped steps: {string.Join(", ", skipped)}");

		var record = new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = entry.Orchestration.Name,
			StartedAt = startTime,
			CompletedAt = completedAt,
			Status = ExecutionStatus.Cancelled,
			Parameters = parameters ?? new Dictionary<string, string>(),
			TriggeredBy = "manual",
			StepRecords = stepRecords,
			AllStepRecords = allStepRecords,
			FinalContent = summary.ToString(),
			HookExecutions = [],
		};

		try
		{
			await store.SaveRunAsync(record, entry.Orchestration);
		}
		catch (Exception ex)
		{
			LogSaveCancelledRunFailed(logger, runId, ex);
		}
	}

	private static async Task SaveFailedRunAsync(
		FileSystemRunStore store,
		OrchestrationEntry entry,
		string runId,
		DateTimeOffset startTime,
		Dictionary<string, string>? parameters,
		SseReporter reporter,
		string errorMessage,
		ILogger logger)
	{
		var completedAt = DateTimeOffset.UtcNow;
		var stepRecords = new Dictionary<string, StepRunRecord>();
		var allStepRecords = new Dictionary<string, StepRunRecord>();
		var summary = new System.Text.StringBuilder();
		summary.AppendLine($"Orchestration failed: {errorMessage}");

		var stepsStarted = new HashSet<string>();
		var stepsCompleted = new HashSet<string>();
		var stepErrors = new Dictionary<string, string>();

		foreach (var evt in reporter.AccumulatedEvents)
		{
			try
			{
				var data = JsonSerializer.Deserialize<JsonElement>(evt.Data);
				switch (evt.Type)
				{
					case "step-started":
						if (data.TryGetProperty("stepName", out var startedName))
							stepsStarted.Add(startedName.GetString() ?? "");
						break;
					case "step-completed":
						if (data.TryGetProperty("stepName", out var completedName))
							stepsCompleted.Add(completedName.GetString() ?? "");
						break;
					case "step-error":
						if (data.TryGetProperty("stepName", out var errorStepName) &&
							data.TryGetProperty("error", out var errMsg))
							stepErrors[errorStepName.GetString() ?? ""] = errMsg.GetString() ?? "";
						break;
				}
			}
			catch (JsonException) { /* Ignore parse errors */ }
		}

		foreach (var stepName in stepsStarted)
		{
			var status = stepsCompleted.Contains(stepName)
				? ExecutionStatus.Succeeded
				: stepErrors.ContainsKey(stepName)
					? ExecutionStatus.Failed
					: ExecutionStatus.Cancelled;
			var stepError = stepErrors.GetValueOrDefault(stepName);

			var stepRecord = new StepRunRecord
			{
				StepName = stepName,
				Status = status,
				StartedAt = startTime,
				CompletedAt = completedAt,
				Content = status == ExecutionStatus.Failed ? "[Failed]" : status == ExecutionStatus.Cancelled ? "[Cancelled]" : "",
				ErrorMessage = stepError
			};

			stepRecords[stepName] = stepRecord;
			allStepRecords[stepName] = stepRecord;
		}

		if (stepsCompleted.Count > 0)
			summary.AppendLine($"Completed steps: {string.Join(", ", stepsCompleted)}");
		var failedSteps = stepErrors.Keys.ToList();
		if (failedSteps.Count > 0)
			summary.AppendLine($"Failed steps: {string.Join(", ", failedSteps)}");

		var record = new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = entry.Orchestration.Name,
			StartedAt = startTime,
			CompletedAt = completedAt,
			Status = ExecutionStatus.Failed,
			Parameters = parameters ?? new Dictionary<string, string>(),
			TriggeredBy = "manual",
			StepRecords = stepRecords,
			AllStepRecords = allStepRecords,
			FinalContent = summary.ToString(),
			HookExecutions = [],
		};

		try
		{
			await store.SaveRunAsync(record, entry.Orchestration);
		}
		catch (Exception ex)
		{
			LogSaveFailedRunFailed(logger, runId, ex);
		}
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to save cancelled run record for run '{RunId}'")]
	private static partial void LogSaveCancelledRunFailed(ILogger logger, string runId, Exception ex);

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to save failed run record for run '{RunId}'")]
	private static partial void LogSaveFailedRunFailed(ILogger logger, string runId, Exception ex);

	/// <summary>
	/// Sends periodic heartbeat events on the execution SSE stream to prevent
	/// proxies, load balancers, and idle TCP timeouts from silently closing the connection.
	/// </summary>
	private static async Task SendHeartbeatsAsync(SseReporter reporter, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested && !reporter.IsCompleted)
			{
				await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
				reporter.SendHeartbeat();
			}
		}
		catch (OperationCanceledException) { }
	}
}
