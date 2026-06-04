using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
			IChildOrchestrationLauncher launcher,
			FileSystemRunStore runStore,
			IOrchestrationReporterFactory reporterFactory,
			ILoggerFactory loggerFactory,
			IHostApplicationLifetime lifetime,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
			DashboardEventBroadcaster dashboardBroadcaster) =>
		{
			var entry = registry.GetByIdOrName(id);
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
			// Resolve once: downstream stores (reporter, dashboard broadcaster, launcher)
			// are all keyed by the canonical registry ID. Using the input `id` after a
			// name-based lookup would mis-attribute the run on every observer surface.
			var resolvedId = entry.Id;

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

			// Create the reporter ourselves (rather than letting the launcher create it) so we
			// can subscribe to its event stream BEFORE the orchestration starts emitting events.
			// Both the early-replay and the late-future-events branches of Subscribe() then have
			// access to the full SSE timeline.
			var reporter = (SseReporter)reporterFactory.Create();

			// Set up SSE response
			httpContext.Response.ContentType = "text/event-stream";
			httpContext.Response.Headers.CacheControl = "no-cache";
			httpContext.Response.Headers.Connection = "keep-alive";
			await httpContext.Response.Body.FlushAsync();

			ChildOrchestrationHandle handle;
			try
			{
				handle = await launcher.LaunchAsync(new ChildLaunchRequest
				{
					OrchestrationId = resolvedId,
					Parameters = parameters,
					Mode = ChildLaunchMode.Async, // We stream SSE; do not block the request thread
					TriggeredBy = "manual",
					Reporter = reporter,
				});
			}
			catch (ChildOrchestrationLaunchException ex)
			{
				httpContext.Response.StatusCode = 500;
				httpContext.Response.ContentType = "application/problem+json";
				await httpContext.Response.WriteAsJsonAsync(new
				{
					type = "https://tools.ietf.org/html/rfc7807",
					title = "Launch Failed",
					status = 500,
					detail = ex.Message,
					instance = httpContext.Request.Path.Value,
				});
				return;
			}

			var executionId = handle.ExecutionId;

			// Send execution-started event
			await httpContext.Response.WriteAsync($"event: execution-started\n");
			await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { executionId }, jsonOptions)}\n\n");
			await httpContext.Response.Body.FlushAsync();

			// Capture execution-started metadata on the reporter so the authoritative snapshot
			// (served on attach and via /state) reflects it without needing to scan the event log.
			reporter.SetExecutionContext(
				executionId,
				resolvedId,
				entry.Orchestration.Name,
				handle.StartedAt,
				"manual",
				parameters);

			// Notify dashboard subscribers so the Portal can refresh Active/Recent lists
			// without polling.
			dashboardBroadcaster.BroadcastExecutionStarted(
				executionId,
				resolvedId,
				entry.Orchestration.Name,
				"manual");

			var logger = loggerFactory.CreateLogger(typeof(ExecutionApi));
			var runStartedAt = handle.StartedAt;

			// Track the orchestration result for fallback persistence on cancellation/failure
			// (the launcher's reporter completion arrives asynchronously through Completion).
			var executionTask = Task.Run(async () =>
			{
				var result = await handle.Completion;

				if (result.Status is ExecutionStatus.Cancelled
					&& result.OrchestrationResult is null)
				{
					await SaveCancelledRunAsync(runStore, entry, executionId, runStartedAt, parameters, reporter, logger);
				}
				else if (result.Status is ExecutionStatus.Failed
					&& result.OrchestrationResult is null
					&& result.ErrorMessage is not null)
				{
					await SaveFailedRunAsync(runStore, entry, executionId, runStartedAt, parameters, reporter, result.ErrorMessage, logger);
				}

				// Notify dashboard subscribers that the execution reached a terminal state.
				var dashboardStatus = activeExecutionInfos.TryGetValue(executionId, out var info)
					? info.Status.ToString()
					: result.Status.ToString();
				dashboardBroadcaster.BroadcastExecutionCompleted(
					executionId,
					resolvedId,
					entry.Orchestration.Name,
					dashboardStatus);
			}, CancellationToken.None);

			// Subscribe and stream SSE events to this client.
			// /run is a fresh subscribe (no Last-Event-Id) so we always send the full
			// snapshot+replay sequence — for /run there's nothing prior to replay but the
			// snapshot frame still primes the client's state map atomically.
			var lastEventId = SseEventWriter.ParseLastEventId(httpContext.Request);
			var subscription = reporter.SubscribeWithSnapshot(lastEventId);
			using var sseCts = CancellationTokenSource.CreateLinkedTokenSource(
				httpContext.RequestAborted,
				lifetime.ApplicationStopping);
			var sseToken = sseCts.Token;

			// 1. Emit the authoritative snapshot first so the UI has a complete state map
			//    even if some events were evicted from the circular buffer before this client
			//    attached. The snapshot is followed by the replay so any deltas/streaming
			//    text since the snapshot is still delivered.
			await WriteSnapshotFrameAsync(httpContext.Response, subscription.Snapshot, jsonOptions, sseToken);

			// 2. If the requested Last-Event-Id is older than the buffer, surface a
			//    "replay-truncated" frame so the Portal can show a banner.
			if (subscription.ReplayTruncated)
			{
				await WriteReplayTruncatedAsync(httpContext.Response, lastEventId, subscription.Snapshot.LastEventSequence, sseToken);
			}

			// 3. Replay any events that happened before we subscribed (or since lastEventId).
			foreach (var evt in subscription.Replay)
			{
				await SseEventWriter.WriteAsync(httpContext.Response, evt, sseToken);
			}
			await httpContext.Response.Body.FlushAsync(sseToken);

			// Start heartbeat to keep the SSE connection alive
			_ = SendHeartbeatsAsync(reporter, sseToken);

			// Stream future events until client disconnects OR orchestration completes
			if (subscription.Future is not null)
			{
				try
				{
					await foreach (var evt in subscription.Future.ReadAllAsync(sseToken))
					{
						await SseEventWriter.WriteAsync(httpContext.Response, evt, sseToken);
						await httpContext.Response.Body.FlushAsync(sseToken);
					}
				}
				catch (OperationCanceledException)
				{
					reporter.Unsubscribe(subscription.Future);
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
				await WriteProblemAsync(httpContext, 404, "Not Found", $"No active execution with ID '{executionId}'.");
				return;
			}

			await StreamAttachedExecutionAsync(httpContext, info, jsonOptions);
		});

		// GET /api/orchestrations/{orchestrationName}/runs/{runId}/attach - Attach by user-visible
		// (orchestration, runId) pair. Uses the same SSE transport as /api/execution/.../attach
		// but lets callers (CLI, Portal, integrations) work with the IDs they already have without
		// needing to discover the internal executionId. For active runs the runId is the same as
		// the executionId, so this is just a convenience surface with name verification.
		endpoints.MapGet("/api/orchestrations/{orchestrationName}/runs/{runId}/attach", async (
			string orchestrationName,
			string runId,
			HttpContext httpContext,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
		{
			if (!activeExecutionInfos.TryGetValue(runId, out var info))
			{
				await WriteProblemAsync(httpContext, 404, "Not Found", $"No active run '{runId}' for orchestration '{orchestrationName}'.");
				return;
			}

			// Fail fast when the runId belongs to a different orchestration so users don't get
			// silently confused by mismatched IDs (e.g. copy-pasting a runId across two orchestrations).
			if (!string.Equals(info.OrchestrationName, orchestrationName, StringComparison.Ordinal))
			{
				await WriteProblemAsync(
					httpContext,
					404,
					"Not Found",
					$"Run '{runId}' belongs to orchestration '{info.OrchestrationName}', not '{orchestrationName}'.");
				return;
			}

			await StreamAttachedExecutionAsync(httpContext, info, jsonOptions);
		});

		// GET /api/execution/{executionId}/state - Return the reporter's authoritative
		// snapshot of orchestration + per-step state for the given active execution.
		// Useful for tooling (CLI, integrations, tests) that wants a one-shot view of
		// the current state without consuming an SSE stream, and as a fallback when the
		// SSE subscriber cap has been reached.
		endpoints.MapGet("/api/execution/{executionId}/state", async (
			string executionId,
			HttpContext httpContext,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
		{
			if (!activeExecutionInfos.TryGetValue(executionId, out var info))
			{
				await WriteProblemAsync(httpContext, 404, "Not Found", $"No active execution with ID '{executionId}'.");
				return;
			}

			await WriteSnapshotJsonAsync(httpContext, info, jsonOptions);
		});

		// GET /api/orchestrations/{orchestrationName}/runs/{runId}/state - Friendly alias
		// that takes (orchestrationName, runId) instead of the internal executionId.
		// For active runs the runId is the same as the executionId, so this is just a
		// convenience surface with name verification.
		endpoints.MapGet("/api/orchestrations/{orchestrationName}/runs/{runId}/state", async (
			string orchestrationName,
			string runId,
			HttpContext httpContext,
			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
		{
			if (!activeExecutionInfos.TryGetValue(runId, out var info))
			{
				await WriteProblemAsync(httpContext, 404, "Not Found", $"No active run '{runId}' for orchestration '{orchestrationName}'.");
				return;
			}

			if (!string.Equals(info.OrchestrationName, orchestrationName, StringComparison.Ordinal))
			{
				await WriteProblemAsync(
					httpContext,
					404,
					"Not Found",
					$"Run '{runId}' belongs to orchestration '{info.OrchestrationName}', not '{orchestrationName}'.");
				return;
			}

			await WriteSnapshotJsonAsync(httpContext, info, jsonOptions);
		});

		return endpoints;
	}

	/// <summary>
	/// Serializes the reporter's current authoritative <see cref="ExecutionStateSnapshot"/>
	/// to the response as JSON. Falls back to a 500 problem response when the active
	/// execution's reporter is not an <see cref="SseReporter"/> (which shouldn't happen
	/// in production because <see cref="SseReporterFactory"/> is the only registered factory).
	/// </summary>
	private static async Task WriteSnapshotJsonAsync(
		HttpContext httpContext,
		ActiveExecutionInfo info,
		JsonSerializerOptions jsonOptions)
	{
		if (info.Reporter is not SseReporter sseReporter)
		{
			await WriteProblemAsync(httpContext, 500, "Internal Server Error", "Execution reporter is not an SseReporter.");
			return;
		}

		// Make sure execution-level metadata is captured on the snapshot even when this is
		// the very first read (no one has attached yet) — same fold the SSE attach path does.
		sseReporter.SetExecutionContext(
			info.ExecutionId,
			info.OrchestrationId,
			info.OrchestrationName,
			info.StartedAt,
			info.TriggeredBy,
			info.Parameters);

		var snapshot = sseReporter.GetCurrentSnapshot();
		httpContext.Response.ContentType = "application/json";
		await httpContext.Response.WriteAsync(JsonSerializer.Serialize(snapshot, jsonOptions));
	}

	/// <summary>
	/// Shared SSE-attach implementation: writes the <c>execution-info</c> frame, replays
	/// accumulated events, then streams future events until the client disconnects or the
	/// reporter completes.
	/// </summary>
	private static async Task StreamAttachedExecutionAsync(
		HttpContext httpContext,
		ActiveExecutionInfo info,
		JsonSerializerOptions jsonOptions)
	{
		if (info.Reporter is not SseReporter sseReporter)
		{
			await WriteProblemAsync(httpContext, 500, "Internal Server Error", "Execution reporter is not an SseReporter.");
			return;
		}

		// Set up SSE response
		httpContext.Response.ContentType = "text/event-stream";
		httpContext.Response.Headers.CacheControl = "no-cache";
		httpContext.Response.Headers.Connection = "keep-alive";
		await httpContext.Response.Body.FlushAsync();

		var lifetime = httpContext.RequestServices.GetRequiredService<IHostApplicationLifetime>();
		using var sseCts = CancellationTokenSource.CreateLinkedTokenSource(
			httpContext.RequestAborted,
			lifetime.ApplicationStopping);
		var cancellationToken = sseCts.Token;

		// Mirror execution-info to the reporter's snapshot so the authoritative state is
		// complete even for clients that connect via /attach (the engine itself never
		// emits execution-info because it doesn't know about HTTP attach semantics).
		sseReporter.SetExecutionContext(
			info.ExecutionId,
			info.OrchestrationId,
			info.OrchestrationName,
			info.StartedAt,
			info.TriggeredBy,
			info.Parameters);

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

		// Subscribe to the reporter, optionally resuming from the client's Last-Event-Id
		// header so reconnects don't refire events the client already saw.
		var lastEventId = SseEventWriter.ParseLastEventId(httpContext.Request);
		var subscription = sseReporter.SubscribeWithSnapshot(lastEventId);

		// 1. Authoritative snapshot first — primes DAG colors + per-step details even when
		//    earlier step-started/step-completed events have rolled off the replay buffer.
		await WriteSnapshotFrameAsync(httpContext.Response, subscription.Snapshot, jsonOptions, cancellationToken);

		// 2. Replay-truncated banner when applicable.
		if (subscription.ReplayTruncated)
		{
			await WriteReplayTruncatedAsync(httpContext.Response, lastEventId, subscription.Snapshot.LastEventSequence, cancellationToken);
		}

		// 3. Replay accumulated events newer than the cursor.
		foreach (var evt in subscription.Replay)
		{
			await SseEventWriter.WriteAsync(httpContext.Response, evt, cancellationToken);
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
		if (subscription.Future is not null)
		{
			try
			{
				await foreach (var evt in subscription.Future.ReadAllAsync(cancellationToken))
				{
					await SseEventWriter.WriteAsync(httpContext.Response, evt, cancellationToken);
					await httpContext.Response.Body.FlushAsync(cancellationToken);
				}
			}
			catch (OperationCanceledException)
			{
				sseReporter.Unsubscribe(subscription.Future);
			}
		}
	}

	/// <summary>
	/// Writes the <c>execution-snapshot</c> frame containing the reporter's authoritative
	/// state at attach time. This is the cornerstone of the recovery path: even if the
	/// circular replay buffer evicted earlier <c>step-completed</c> / <c>step-trace</c>
	/// events, the snapshot still carries the latest known status, trace, and output for
	/// each step, so the UI can render the DAG correctly and show per-step details on click.
	/// </summary>
	private static async Task WriteSnapshotFrameAsync(
		HttpResponse response,
		ExecutionStateSnapshot snapshot,
		JsonSerializerOptions jsonOptions,
		CancellationToken cancellationToken)
	{
		var snapshotJson = JsonSerializer.Serialize(snapshot, jsonOptions);
		// The snapshot itself is not part of the resumable sequence — its sequence number
		// is embedded in the payload (lastEventSequence) so clients know what cursor to
		// resume from after consuming it.
		await response.WriteAsync($"event: execution-snapshot\n", cancellationToken);
		await response.WriteAsync($"data: {snapshotJson}\n\n", cancellationToken);
	}

	/// <summary>
	/// Writes a <c>replay-truncated</c> frame when the requested <c>Last-Event-Id</c>
	/// is older than the oldest event still in the reporter's replay buffer. Clients
	/// should treat the snapshot frame as authoritative and use the included
	/// <c>fromSequence</c> as the new resume cursor.
	/// </summary>
	private static async Task WriteReplayTruncatedAsync(
		HttpResponse response,
		long? lastEventId,
		long currentSequence,
		CancellationToken cancellationToken)
	{
		var payload = $"{{\"requestedLastEventId\":{lastEventId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"},\"resumeFromSequence\":{currentSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
		await response.WriteAsync($"event: replay-truncated\n", cancellationToken);
		await response.WriteAsync($"data: {payload}\n\n", cancellationToken);
	}

	private static async Task WriteProblemAsync(HttpContext httpContext, int status, string title, string detail)
	{
		// Set ContentType AFTER WriteAsJsonAsync so the json extension's default media-type
		// assignment doesn't overwrite it. WriteAsJsonAsync flushes via SerializeAsync which
		// reads ContentType when writing the headers; we need it set first, but the header
		// dictionary is open until the body is flushed.
		httpContext.Response.StatusCode = status;
		httpContext.Response.ContentType = "application/problem+json";
		var payload = new
		{
			type = "https://tools.ietf.org/html/rfc7807",
			title,
			status,
			detail,
			instance = httpContext.Request.Path.Value,
		};
		await System.Text.Json.JsonSerializer.SerializeAsync(httpContext.Response.Body, payload);
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
		var stepSavedFiles = new Dictionary<string, List<string>>();

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
						if (data.TryGetProperty("stepName", out completedName) &&
							data.TryGetProperty("savedFiles", out var completedSavedFiles) &&
							completedSavedFiles.ValueKind == JsonValueKind.Array)
							AddSavedFiles(stepSavedFiles, completedName.GetString(), completedSavedFiles);
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
					case "saved-file":
						if (data.TryGetProperty("stepName", out var savedStepName) &&
							data.TryGetProperty("filePath", out var filePath))
						{
							var key = savedStepName.GetString() ?? "";
							if (!string.IsNullOrWhiteSpace(key) && filePath.GetString() is { } path)
							{
								if (!stepSavedFiles.TryGetValue(key, out var paths))
									stepSavedFiles[key] = paths = [];
								if (!paths.Contains(path))
									paths.Add(path);
							}
						}
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
				ErrorMessage = errorMessage,
				SavedFiles = stepSavedFiles.GetValueOrDefault(stepName)?.ToArray() ?? [],
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
			SavedFiles = stepSavedFiles.Values.SelectMany(paths => paths).ToArray(),
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
		var stepSavedFiles = new Dictionary<string, List<string>>();

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
						if (data.TryGetProperty("stepName", out completedName) &&
							data.TryGetProperty("savedFiles", out var completedSavedFiles) &&
							completedSavedFiles.ValueKind == JsonValueKind.Array)
							AddSavedFiles(stepSavedFiles, completedName.GetString(), completedSavedFiles);
						break;
					case "step-error":
						if (data.TryGetProperty("stepName", out var errorStepName) &&
							data.TryGetProperty("error", out var errMsg))
							stepErrors[errorStepName.GetString() ?? ""] = errMsg.GetString() ?? "";
						break;
					case "saved-file":
						if (data.TryGetProperty("stepName", out var savedStepName) &&
							data.TryGetProperty("filePath", out var filePath))
						{
							var key = savedStepName.GetString() ?? "";
							if (!string.IsNullOrWhiteSpace(key) && filePath.GetString() is { } path)
							{
								if (!stepSavedFiles.TryGetValue(key, out var paths))
									stepSavedFiles[key] = paths = [];
								if (!paths.Contains(path))
									paths.Add(path);
							}
						}
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
				ErrorMessage = stepError,
				SavedFiles = stepSavedFiles.GetValueOrDefault(stepName)?.ToArray() ?? [],
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
			SavedFiles = stepSavedFiles.Values.SelectMany(paths => paths).ToArray(),
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

	private static void AddSavedFiles(Dictionary<string, List<string>> stepSavedFiles, string? stepName, JsonElement fileArray)
	{
		if (string.IsNullOrWhiteSpace(stepName))
			return;

		if (!stepSavedFiles.TryGetValue(stepName, out var paths))
			stepSavedFiles[stepName] = paths = [];

		foreach (var item in fileArray.EnumerateArray())
		{
			if (item.GetString() is { } path && !paths.Contains(path))
				paths.Add(path);
		}
	}

	/// <summary>
	/// Sends periodic heartbeat events on the execution SSE stream to prevent
	/// proxies, load balancers, and idle TCP timeouts from silently closing the connection.
	/// Uses the reporter's configured <see cref="SseReporter.HeartbeatInterval"/>.
	/// </summary>
	private static async Task SendHeartbeatsAsync(SseReporter reporter, CancellationToken cancellationToken)
	{
		try
		{
			var interval = reporter.HeartbeatInterval;
			while (!cancellationToken.IsCancellationRequested && !reporter.IsCompleted)
			{
				await Task.Delay(interval, cancellationToken);
				reporter.SendHeartbeat();
			}
		}
		catch (OperationCanceledException) { }
	}
}
