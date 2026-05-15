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
		[Description("Response detail level for sync mode: 'summary' (status + metadata only, no content), 'compact' (default, truncated content with metadata), 'full' (untruncated content; responses may be large).")] string detail = "compact",
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

		if (!TryParseDetailLevel(detail, out var detailParsed))
		{
			return JsonSerializer.Serialize(new
			{
				error = $"Invalid detail level '{detail}'. Valid values: 'summary', 'compact', 'full'."
			}, s_jsonOptions);
		}

		// Sync-timeout vs transport-timeout sanity check.
		// The host applies a default transport timeout (DefaultOrchestraInvokeTimeoutSeconds)
		// to the MCP request to /mcp/data when the caller's orchestration `mcps[]` entry does
		// not override it. If the caller asks for a sync `timeoutSeconds` that is larger than
		// that default, the transport layer would abort this request long before the engine's
		// own sync-invoke timeout fires, producing a generic "cancelled by caller" record
		// even though the child made real progress (the classic 30-minute cliff).
		// We can't see the caller's per-mcps[] override here, but we CAN see the host default,
		// which is the most common source of this mismatch.
		// When DefaultOrchestraInvokeTimeoutSeconds == 0, McpManager.Resolve stamps an
		// effectively-infinite transport timeout (TimeSpan.FromMilliseconds(int.MaxValue))
		// onto the MCP entry so the Copilot SDK's ~3-minute built-in default does NOT
		// silently kick in. In that case there is no cliff to protect against and this
		// guardrail correctly stays dormant.
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
		var (summaryText, summaryLength, summaryTruncated) = TruncateWithStats(
			result.FinalContent, detailParsed == DetailLevel.Full ? -1 : (detailParsed == DetailLevel.Summary ? 0 : 16000));

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
				kvp => BuildStepProjection(
					kvp.Value.Status,
					kvp.Value.Content,
					kvp.Value.RawContent,
					kvp.Value.ErrorMessage,
					kvp.Value.SavedFiles,
					detailParsed,
					perStepLimitChars: 8000)),
			summary = detailParsed == DetailLevel.Summary ? null : summaryText,
			summaryLength,
			summaryTruncated,
			detail = detailParsed.ToString().ToLowerInvariant(),
			responseHint = detailParsed == DetailLevel.Full
				? "detail=full returned untruncated content; responses may be large."
				: null,
			metadata = parsedMetadata,
		}, s_jsonOptions);
	}

	[McpServerTool(Name = "get_orchestration_status"), Description(
		"Gets the status and result of an orchestration execution by its execution ID. " +
		"Use this to check the progress of async invocations or to retrieve results after completion. " +
		"For large step outputs, prefer detail='summary' or use get_orchestration_step for paginated content access.")]
	public static async Task<string> GetOrchestrationStatus(
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		FileSystemRunStore runStore,
		[Description("The execution ID returned by invoke_orchestration.")] string executionId,
		[Description("Response detail level: 'summary' (status + metadata only, no per-step content), 'compact' (default, content truncated to ~8000 chars per step), 'full' (untruncated; responses may be large).")] string detail = "compact")
	{
		if (!TryParseDetailLevel(detail, out var detailParsed))
		{
			return JsonSerializer.Serialize(new
			{
				error = $"Invalid detail level '{detail}'. Valid values: 'summary', 'compact', 'full'."
			}, s_jsonOptions);
		}

		// Check active executions first
		if (activeExecutionInfos.TryGetValue(executionId, out var info))
		{
			// Surface the canonical keys of completed step records that the engine has
			// already published. Callers can read these by name via get_orchestration_step
			// without polling for completion. Sorted for stable output.
			var completedStepNames = info.PartialStepRecords.Keys.OrderBy(k => k).ToArray();

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
				completedStepNames,
				parameters = info.Parameters,
				nesting = info.NestingMetadata is not null ? new
				{
					parentExecutionId = info.NestingMetadata.ParentExecutionId,
					rootExecutionId = info.NestingMetadata.RootExecutionId,
					depth = info.NestingMetadata.Depth,
				} : null,
				detail = detailParsed.ToString().ToLowerInvariant(),
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
				var (summaryText, summaryLength, summaryTruncated) = TruncateWithStats(
					run.FinalContent, detailParsed == DetailLevel.Full ? -1 : (detailParsed == DetailLevel.Summary ? 0 : 16000));

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
						kvp => BuildStepProjection(
							kvp.Value.Status,
							kvp.Value.Content,
							kvp.Value.RawContent,
							kvp.Value.ErrorMessage,
							kvp.Value.SavedFiles,
							detailParsed,
							perStepLimitChars: 8000)),
					summary = detailParsed == DetailLevel.Summary ? null : summaryText,
					summaryLength,
					summaryTruncated,
					nesting = run.ParentExecutionId is not null || run.NestingDepth > 0 ? new
					{
						parentExecutionId = run.ParentExecutionId,
						parentStepName = run.ParentStepName,
						rootExecutionId = run.RootExecutionId,
						depth = run.NestingDepth,
					} : null,
					detail = detailParsed.ToString().ToLowerInvariant(),
					responseHint = detailParsed == DetailLevel.Full
						? "detail=full returned untruncated content; responses may be large."
						: null,
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

	[McpServerTool(Name = "get_orchestration_step"), Description(
		"Fetches the full (or paginated) content of a single step from an orchestration run. " +
		"Works for both ACTIVE (in-flight) runs whose step records the engine has published " +
		"into the host's active-execution table, and PERSISTED runs whose run.json has been " +
		"saved. Use this after get_orchestration_status reports `truncated: true` on a step, " +
		"or whenever you need the complete output of one specific step. " +
		"Returns the content slice plus metadata so the caller can stitch multi-page reads.")]
	public static async Task<string> GetOrchestrationStep(
		FileSystemRunStore runStore,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		[Description("Execution ID returned by invoke_orchestration (or surfaced via {{orch-step.executionId}} in templates).")] string executionId,
		[Description("Step name within the orchestration run. For loops, use the canonical name for the latest iteration or 'stepName:iteration-N' for a specific iteration.")] string stepName,
		[Description("Which part of the step to return: 'content' (default, the step output), 'rawContent' (pre-output-handler), 'errorMessage', or 'all' (all three).")] string part = "content",
		[Description("Character offset to start reading from. Default 0.")] int offset = 0,
		[Description("Maximum number of characters to return. Default 50000. Pass -1 to read until end (no cap; responses may be very large).")] int length = 50000)
	{
		// Parameter validation upfront (cheap fail-fast).
		var normalizedPart = (part ?? "content").Trim().ToLowerInvariant();
		if (normalizedPart is not ("content" or "rawcontent" or "errormessage" or "all"))
		{
			return JsonSerializer.Serialize(new
			{
				error = $"Invalid part '{part}'. Valid values: 'content', 'rawContent', 'errorMessage', 'all'."
			}, s_jsonOptions);
		}
		if (offset < 0)
		{
			return JsonSerializer.Serialize(new
			{
				error = $"offset must be >= 0. Received: {offset}."
			}, s_jsonOptions);
		}

		// Step 1: prefer the ACTIVE path. If the run is in flight and the requested step has
		// already published its record into PartialStepRecords, serve it directly. This is
		// the self-healing controller's primary drill-in point — it can read sibling steps
		// of the still-running attempt without polling the persisted store.
		if (activeExecutionInfos.TryGetValue(executionId, out var activeInfo))
		{
			var runStatus = activeInfo.Status.ToString().ToLowerInvariant();
			if (activeInfo.PartialStepRecords.TryGetValue(stepName, out var liveStep))
			{
				return BuildStepResponse(
					executionId: activeInfo.ExecutionId,
					orchestrationName: activeInfo.OrchestrationName,
					step: liveStep,
					normalizedPart: normalizedPart,
					offset: offset,
					length: length,
					source: "active",
					runStatus: runStatus);
			}

			// The run is active but the requested step hasn't completed yet (or doesn't exist
			// at all). Return a structured "in-flight" response that tells the caller exactly
			// what's running and which sibling steps they CAN drill into right now. This is
			// the canonical "is the orchestration still running?" signal — runStatus + the
			// completedStepNames list let the caller decide whether to wait or move on.
			var completedStepNames = activeInfo.PartialStepRecords.Keys.OrderBy(k => k).ToArray();
			return JsonSerializer.Serialize(new
			{
				error = "step-in-flight",
				executionId = activeInfo.ExecutionId,
				orchestrationName = activeInfo.OrchestrationName,
				stepName,
				runStatus,                                  // overall RUN status (e.g. "running", "cancelling")
				stepStatus = activeInfo.CurrentStep == stepName ? "running" : "pending",
				currentStep = activeInfo.CurrentStep,
				completedStepNames,                         // siblings you CAN drill into now
				totalSteps = activeInfo.TotalSteps,
				completedSteps = activeInfo.CompletedSteps,
				hint = activeInfo.CurrentStep == stepName
					? "This step is currently executing. Its content will become available here once it completes; in the meantime use get_orchestration_status to monitor progress."
					: completedStepNames.Length > 0
						? $"Step '{stepName}' has not produced a record yet. Completed siblings you can drill into: [{string.Join(", ", completedStepNames)}]."
						: $"Step '{stepName}' has not produced a record yet. No siblings have completed; poll get_orchestration_status to track progress.",
			}, s_jsonOptions);
		}

		// Step 2: fall back to the PERSISTED path. The run isn't active so it must have either
		// completed (run.json present) or never existed.
		var runIndex = await runStore.FindRunByIdAsync(executionId);
		if (runIndex is null)
		{
			return JsonSerializer.Serialize(new
			{
				error = $"No run found with execution ID '{executionId}'. It may have expired, been deleted, or never existed.",
			}, s_jsonOptions);
		}

		var run = await runStore.GetRunAsync(runIndex.OrchestrationName, runIndex.RunId);
		if (run is null)
		{
			return JsonSerializer.Serialize(new
			{
				error = $"Run record for execution ID '{executionId}' could not be loaded. The run.json file may be missing or corrupt."
			}, s_jsonOptions);
		}

		if (!run.StepRecords.TryGetValue(stepName, out var step))
		{
			var available = string.Join(", ", run.StepRecords.Keys);
			return JsonSerializer.Serialize(new
			{
				error = $"Step '{stepName}' not found in run '{executionId}'. Available steps: [{available}].",
				executionId = run.RunId,
				orchestrationName = run.OrchestrationName,
			}, s_jsonOptions);
		}

		return BuildStepResponse(
			executionId: run.RunId,
			orchestrationName: run.OrchestrationName,
			step: step,
			normalizedPart: normalizedPart,
			offset: offset,
			length: length,
			source: "persisted",
			runStatus: run.Status.ToString().ToLowerInvariant());
	}

	/// <summary>
	/// Shared response shape for both the active and persisted paths. Always carries the
	/// <paramref name="source"/> ("active" or "persisted") and <paramref name="runStatus"/>
	/// so callers can unambiguously tell where the data came from and whether the overall
	/// run is still in flight.
	/// </summary>
	private static string BuildStepResponse(
		string executionId,
		string orchestrationName,
		StepRunRecord step,
		string normalizedPart,
		int offset,
		int length,
		string source,
		string runStatus)
	{
		if (normalizedPart == "all")
		{
			return JsonSerializer.Serialize(new
			{
				executionId,
				orchestrationName,
				stepName = step.StepName,
				status = step.Status.ToString().ToLowerInvariant(),
				runStatus,
				source,
				startedAt = step.StartedAt,
				completedAt = step.CompletedAt,
				content = SliceWithStats(step.Content, offset, length, "content"),
				rawContent = step.RawContent is null ? null : SliceWithStats(step.RawContent, offset, length, "rawContent"),
				errorMessage = step.ErrorMessage is null ? null : SliceWithStats(step.ErrorMessage, offset, length, "errorMessage"),
				savedFiles = step.SavedFiles is { Length: > 0 } ? step.SavedFiles : null,
			}, s_jsonOptions);
		}

		var (sourceText, partLabel) = normalizedPart switch
		{
			"content" => (step.Content, "content"),
			"rawcontent" => (step.RawContent, "rawContent"),
			"errormessage" => (step.ErrorMessage, "errorMessage"),
			_ => (step.Content, "content"),
		};

		return JsonSerializer.Serialize(new
		{
			executionId,
			orchestrationName,
			stepName = step.StepName,
			status = step.Status.ToString().ToLowerInvariant(),
			runStatus,
			source,
			startedAt = step.StartedAt,
			completedAt = step.CompletedAt,
			part = partLabel,
			slice = SliceWithStats(sourceText, offset, length, partLabel),
			savedFiles = step.SavedFiles is { Length: > 0 } ? step.SavedFiles : null,
		}, s_jsonOptions);
	}

	/// <summary>
	/// Returns a JSON-friendly slice descriptor for <paramref name="sourceText"/> with the
	/// requested <paramref name="offset"/> and <paramref name="length"/>. <c>length == -1</c>
	/// means "read until the end" (no cap). Out-of-range offsets return an empty content slice
	/// with <c>truncated=false</c>.
	/// </summary>
	internal static object SliceWithStats(string? sourceText, int offset, int length, string part)
	{
		var totalLength = sourceText?.Length ?? 0;
		if (sourceText is null)
		{
			return new { part, offset, length, totalLength, content = (string?)null, truncated = false };
		}

		if (offset >= totalLength)
		{
			return new { part, offset, length, totalLength, content = string.Empty, truncated = false };
		}

		var available = totalLength - offset;
		var take = length < 0 ? available : Math.Min(available, length);
		var content = sourceText.Substring(offset, take);
		var truncated = offset + take < totalLength;
		return new { part, offset, length, totalLength, content, truncated };
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

	[McpServerTool(Name = "list_child_runs"), Description(
		"Lists orchestration runs spawned within the caller's execution chain. " +
		"Scope is auto-resolved from request headers when invoked from inside an orchestration " +
		"(defaults to the caller's whole subtree). External callers must pass parentExecutionId " +
		"or rootExecutionId explicitly. Includes BOTH active (in-flight) and persisted (completed) " +
		"runs by default; use status='running' to limit to in-flight runs. Returns lightweight " +
		"summaries; use get_orchestration_status or get_orchestration_step for per-run detail.")]
	public static async Task<string> ListChildRuns(
		FileSystemRunStore runStore,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		IHttpContextAccessor httpContextAccessor,
		[Description("Direct-children filter: when supplied, returns only runs whose ParentExecutionId matches.")] string? parentExecutionId = null,
		[Description("Subtree filter: when supplied (and parentExecutionId is not), returns every run whose RootExecutionId matches.")] string? rootExecutionId = null,
		[Description("Status filter: 'succeeded', 'failed', 'cancelled', 'running', 'awaitingInput', 'pending', 'skipped', 'noAction'. Case-insensitive.")] string? status = null,
		[Description("Maximum number of runs to return. Default 50.")] int limit = 50,
		[Description("Number of runs to skip (for pagination). Default 0.")] int offset = 0)
	{
		// Scope resolution: explicit args > stamped headers > error.
		// Source attribution lets the response surface why a particular scope was applied,
		// which is useful for self-healing controllers to confirm they're filtering against
		// their own subtree and not some unrelated chain.
		string scopeSource;
		string? resolvedParent = parentExecutionId;
		string? resolvedRoot = rootExecutionId;
		if (!string.IsNullOrWhiteSpace(resolvedParent))
		{
			scopeSource = "argument:parent";
		}
		else if (!string.IsNullOrWhiteSpace(resolvedRoot))
		{
			scopeSource = "argument:root";
		}
		else if (httpContextAccessor.HttpContext is { } httpContext
			&& httpContext.Request.Headers.TryGetValue(OrchestraHeaders.RootExecutionId, out var headerRoot)
			&& !string.IsNullOrWhiteSpace(headerRoot))
		{
			resolvedRoot = headerRoot.ToString();
			scopeSource = "header:root";
		}
		else if (httpContextAccessor.HttpContext is { } httpContext2
			&& httpContext2.Request.Headers.TryGetValue(OrchestraHeaders.ParentExecutionId, out var headerParent)
			&& !string.IsNullOrWhiteSpace(headerParent))
		{
			// Fallback: older clients (or first hop before X-Orchestra-Root-Execution-Id
			// rollout) only stamp the parent id. Treat the caller's parent as their root and
			// scope to that subtree.
			resolvedRoot = headerParent.ToString();
			scopeSource = "header:parent-as-root";
		}
		else
		{
			return JsonSerializer.Serialize(new
			{
				error = "No scope provided. Pass parentExecutionId or rootExecutionId, or invoke this tool from inside an orchestration so the engine can stamp X-Orchestra-Root-Execution-Id automatically.",
				hint = "External MCP clients must pin a scope to avoid leaking unrelated runs. To enumerate everything, use the control-plane list_runs tool (admin-only).",
			}, s_jsonOptions);
		}

		ExecutionStatus? statusFilter = null;
		if (!string.IsNullOrWhiteSpace(status))
		{
			if (!Enum.TryParse<ExecutionStatus>(status, ignoreCase: true, out var parsed))
			{
				return JsonSerializer.Serialize(new
				{
					error = $"Invalid status filter '{status}'. Valid values: pending, running, succeeded, failed, skipped, cancelled, noAction, awaitingInput.",
				}, s_jsonOptions);
			}
			statusFilter = parsed;
		}

		if (limit < 1)
		{
			return JsonSerializer.Serialize(new { error = $"limit must be >= 1. Received: {limit}." }, s_jsonOptions);
		}
		if (offset < 0)
		{
			return JsonSerializer.Serialize(new { error = $"offset must be >= 0. Received: {offset}." }, s_jsonOptions);
		}

		// Step 1: collect active in-flight runs matching the scope. These come from the
		// in-memory active executions table; they have no completedAt / duration / error
		// fields yet but DO have lineage via NestingMetadata. Active runs map to the
		// "running" status by convention (the host status `Cancelling` also maps here so
		// callers see runs that are stopping but not yet stopped).
		var activeMatches = new List<object>();
		var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var info in activeExecutionInfos.Values)
		{
			// Skip terminal states — those will be picked up by the persisted scan once
			// the run is saved. Including them here would double-count.
			if (info.Status is HostExecutionStatus.Completed
				or HostExecutionStatus.Cancelled
				or HostExecutionStatus.Failed)
			{
				continue;
			}

			var infoParent = info.NestingMetadata?.ParentExecutionId;
			var infoRoot = info.NestingMetadata?.RootExecutionId;

			var scopeMatches = !string.IsNullOrWhiteSpace(resolvedParent)
				? string.Equals(infoParent, resolvedParent, StringComparison.OrdinalIgnoreCase)
				: string.Equals(infoRoot, resolvedRoot, StringComparison.OrdinalIgnoreCase);

			if (!scopeMatches)
			{
				continue;
			}

			// Active runs always map to ExecutionStatus.Running. When a caller filters for
			// a different status they expect persisted runs only, so skip active.
			if (statusFilter is not null && statusFilter != ExecutionStatus.Running)
			{
				continue;
			}

			activeIds.Add(info.ExecutionId);
			activeMatches.Add(new
			{
				executionId = info.ExecutionId,
				orchestrationName = info.OrchestrationName,
				orchestrationVersion = (string?)null,
				status = "running",
				startedAt = info.StartedAt,
				completedAt = (DateTimeOffset?)null,
				durationSeconds = (double?)null,
				triggeredBy = info.TriggeredBy,
				parentExecutionId = infoParent,
				parentStepName = info.NestingMetadata?.ParentStepName,
				rootExecutionId = infoRoot,
				nestingDepth = info.NestingMetadata?.Depth ?? 0,
				failedStepName = (string?)null,
				errorMessage = (string?)null,
				cancellation = (object?)null,
				source = "active",
				totalSteps = info.TotalSteps > 0 ? info.TotalSteps : (int?)null,
				completedSteps = info.CompletedSteps > 0 ? info.CompletedSteps : (int?)null,
				currentStep = info.CurrentStep,
			});
		}

		// Step 2: collect persisted runs matching the scope. The store filter already
		// excludes anything outside the requested subtree; we additionally apply the
		// status filter here. When a run appears in BOTH (race during finalization), the
		// active entry wins because it carries live progress.
		// Over-fetch so we can apply pagination across the combined ordered list.
		var persisted = await runStore.FindChildRunsAsync(resolvedParent, resolvedRoot, statusFilter, limit: limit + offset + activeMatches.Count);
		var persistedMatches = persisted
			.Where(r => !activeIds.Contains(r.RunId))
			.Select(r => (object)new
			{
				executionId = r.RunId,
				orchestrationName = r.OrchestrationName,
				orchestrationVersion = r.OrchestrationVersion,
				status = r.Status.ToString().ToLowerInvariant(),
				startedAt = (DateTimeOffset?)r.StartedAt,
				completedAt = (DateTimeOffset?)r.CompletedAt,
				durationSeconds = (double?)r.Duration.TotalSeconds,
				triggeredBy = r.TriggeredBy,
				parentExecutionId = r.ParentExecutionId,
				parentStepName = r.ParentStepName,
				rootExecutionId = r.RootExecutionId,
				nestingDepth = r.NestingDepth,
				failedStepName = r.FailedStepName,
				errorMessage = r.ErrorMessage,
				cancellation = r.Cancellation is null ? null : new
				{
					kind = r.Cancellation.Kind.ToString(),
					detail = r.Cancellation.Detail,
				},
				source = "persisted",
				totalSteps = (int?)null,
				completedSteps = (int?)null,
				currentStep = (string?)null,
			});

		// Step 3: combine. Active first (running runs are typically what the caller cares
		// about most — "what am I waiting on?"), then persisted in newest-first order.
		// Both lists are already ordered by StartedAt descending internally.
		var combined = activeMatches.Concat(persistedMatches)
			.Skip(offset)
			.Take(limit)
			.ToArray();

		return JsonSerializer.Serialize(new
		{
			scope = new
			{
				parentExecutionId = resolvedParent,
				rootExecutionId = resolvedRoot,
				statusFilter = statusFilter?.ToString().ToLowerInvariant(),
				source = scopeSource,
			},
			count = combined.Length,
			limit,
			offset,
			runs = combined,
		}, s_jsonOptions);
	}

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
	/// Truncates content while reporting structured metadata about the original size and
	/// whether truncation occurred. Pass <paramref name="maxLength"/> of <c>-1</c> for
	/// untruncated, or <c>0</c> to omit content entirely (used by detail="summary").
	/// </summary>
	internal static (string? Content, int OriginalLength, bool Truncated) TruncateWithStats(string? content, int maxLength)
	{
		if (content is null) return (null, 0, false);
		var originalLength = content.Length;
		if (maxLength < 0)
		{
			// Full mode: no truncation.
			return (content, originalLength, false);
		}
		if (maxLength == 0)
		{
			// Summary mode: omit content entirely.
			return (null, originalLength, originalLength > 0);
		}
		if (originalLength <= maxLength)
		{
			return (content, originalLength, false);
		}
		return (content[..maxLength] + "... (truncated)", originalLength, true);
	}

	internal enum DetailLevel { Summary, Compact, Full }

	internal static bool TryParseDetailLevel(string? value, out DetailLevel level)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			level = DetailLevel.Compact;
			return true;
		}
		switch (value.Trim().ToLowerInvariant())
		{
			case "summary":
				level = DetailLevel.Summary;
				return true;
			case "compact":
			case "default":
				level = DetailLevel.Compact;
				return true;
			case "full":
				level = DetailLevel.Full;
				return true;
			default:
				level = DetailLevel.Compact;
				return false;
		}
	}

	/// <summary>
	/// Builds the JSON-serializable projection for a single step record, honoring the
	/// requested <see cref="DetailLevel"/>. Always emits structured metadata (contentLength,
	/// truncated, hasRawContent) so callers can decide whether to fetch the full content
	/// via <c>get_orchestration_step</c>. Belt-and-suspenders: the literal
	/// <c>... (truncated)</c> suffix is preserved on the content string in <c>compact</c>
	/// mode for human readability of the JSON.
	/// </summary>
	internal static object BuildStepProjection(
		ExecutionStatus status,
		string? content,
		string? rawContent,
		string? errorMessage,
		string[]? savedFiles,
		DetailLevel detail,
		int perStepLimitChars)
	{
		var effectiveLimit = detail switch
		{
			DetailLevel.Full => -1,
			DetailLevel.Summary => 0,
			_ => perStepLimitChars,
		};
		var (truncatedContent, contentLength, contentTruncated) = TruncateWithStats(content, effectiveLimit);
		var hasRawContent = !string.IsNullOrEmpty(rawContent) && rawContent != content;

		return new
		{
			status = status.ToString().ToLowerInvariant(),
			content = truncatedContent,
			contentLength,
			truncated = contentTruncated,
			hasRawContent,
			errorMessage,
			savedFiles = savedFiles is { Length: > 0 } ? savedFiles : null,
		};
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
