using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
using Orchestra.Host.Persistence;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.McpServer;

/// <summary>
/// MCP tools for the Orchestra data plane.
/// Provides orchestration discovery and invocation capabilities to external AI agents.
/// </summary>
[McpServerToolType]
public sealed class DataPlaneTools
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
		OrchestrationRegistry registry,
		AgentBuilder agentBuilder,
		IScheduler scheduler,
		ILoggerFactory loggerFactory,
		FileSystemRunStore runStore,
		OrchestrationHostOptions hostOptions,
		EngineToolRegistry engineToolRegistry,
		McpServerOptions mcpOptions,
		McpManager mcpManager,
		ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		[Description("The orchestration ID to invoke.")] string orchestrationId,
		[Description("JSON object with parameter key-value pairs. All values must be strings.")] string? parameters = null,
		[Description("Execution mode: 'async' (default, returns immediately with execution ID) or 'sync' (blocks until completion).")] string mode = "async",
		[Description("Maximum seconds to wait in sync mode. Default: 300 (5 minutes). Ignored in async mode.")] int timeoutSeconds = 300,
		[Description("Optional metadata JSON object with key-value pairs for tracking (e.g., correlation IDs, ticket numbers).")] string? metadata = null,
		[Description("Parent execution ID for nested invocations. Set automatically when called from within an orchestration.")] string? parentExecutionId = null,
		CancellationToken cancellationToken = default)
	{
		var entry = registry.Get(orchestrationId);
		if (entry is null)
			return JsonSerializer.Serialize(new { error = $"Orchestration '{orchestrationId}' not found." }, s_jsonOptions);

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

		// Parse metadata
		Dictionary<string, string>? parsedMetadata = null;
		if (!string.IsNullOrWhiteSpace(metadata))
		{
			try
			{
				parsedMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadata, s_jsonOptions);
			}
			catch (JsonException)
			{
				// Metadata parsing failure is non-fatal
			}
		}

		// Parse the orchestration (global MCPs are available via registry.GlobalMcps)
		Orchestration orchestration;
		try
		{
			orchestration = OrchestrationParser.ParseOrchestrationFile(entry.Path, registry.GlobalMcps);
		}
		catch (Exception ex)
		{
			return JsonSerializer.Serialize(new { error = $"Failed to parse orchestration: {ex.Message}" }, s_jsonOptions);
		}

		// Create execution infrastructure
		var executionId = Guid.NewGuid().ToString("N")[..12];
		var reporter = NullOrchestrationReporter.Instance;

		// ── Nesting: compute depth and enforce limit ──
		var parentDepth = 0;
		string? rootExecutionId = executionId;

		if (!string.IsNullOrWhiteSpace(parentExecutionId) &&
			activeExecutionInfos.TryGetValue(parentExecutionId, out var parentInfo))
		{
			parentDepth = (parentInfo.NestingMetadata?.Depth ?? 0) + 1;
			rootExecutionId = parentInfo.NestingMetadata?.RootExecutionId ?? parentExecutionId;

			if (parentDepth > mcpOptions.MaxNestingDepth)
			{
				return JsonSerializer.Serialize(new
				{
					error = $"Maximum nesting depth ({mcpOptions.MaxNestingDepth}) exceeded. " +
						$"This orchestration would be at depth {parentDepth}. " +
						$"Root execution: {rootExecutionId}.",
				}, s_jsonOptions);
			}
		}

		var nestingMetadata = new ExecutionMetadata
		{
			ParentExecutionId = parentExecutionId,
			ParentStepName = null, // Set by the calling step if available
			RootExecutionId = rootExecutionId,
			Depth = parentDepth,
			UserMetadata = parsedMetadata ?? [],
		};

		// Link cancellation to parent if nested
		var cts = !string.IsNullOrWhiteSpace(parentExecutionId) &&
			activeExecutions.TryGetValue(parentExecutionId, out var parentCts)
				? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, parentCts.Token)
				: CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		activeExecutions[executionId] = cts;
		var executionInfo = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = orchestrationId,
			OrchestrationName = orchestration.Name,
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = parentDepth > 0 ? $"orchestration:{parentExecutionId}" : "mcp",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Parameters = parsedParams,
			TotalSteps = orchestration.Steps.Length,
			NestingMetadata = nestingMetadata,
		};
		activeExecutionInfos[executionId] = executionInfo;

		var executor = new OrchestrationExecutor(
			scheduler, agentBuilder, reporter, loggerFactory,
			runStore: runStore,
			engineToolRegistry: engineToolRegistry,
			mcpResolver: mcpManager,
			dataPath: hostOptions.DataPath,
			serverUrl: hostOptions.HostBaseUrl);

		var isSync = string.Equals(mode, "sync", StringComparison.OrdinalIgnoreCase);

		if (isSync)
		{
			// Synchronous mode: block until completion
			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
			timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

			try
			{
				var result = await executor.ExecuteAsync(orchestration, parsedParams, cancellationToken: timeoutCts.Token);

				executionInfo.Status = result.Status == ExecutionStatus.Succeeded
					? HostExecutionStatus.Completed
					: HostExecutionStatus.Failed;

				// Build a summary from terminal step results
				var terminalContent = string.Join("\n---\n",
					result.Results
						.Where(kvp => kvp.Value.Status == ExecutionStatus.Succeeded)
						.Select(kvp => $"[{kvp.Key}]\n{kvp.Value.Content}"));

				return JsonSerializer.Serialize(new
				{
					executionId,
					orchestrationId,
					orchestrationName = orchestration.Name,
					mode = "sync",
					status = result.Status.ToString().ToLowerInvariant(),
					completionReason = result.CompletionReason,
					stepResults = result.StepResults.ToDictionary(
						kvp => kvp.Key,
						kvp => new
						{
							status = kvp.Value.Status.ToString().ToLowerInvariant(),
							content = TruncateContent(kvp.Value.Content, 4000),
						}),
					summary = TruncateContent(terminalContent, 8000),
					metadata = parsedMetadata,
				}, s_jsonOptions);
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				executionInfo.Status = HostExecutionStatus.Failed;
				return JsonSerializer.Serialize(new
				{
					executionId,
					orchestrationId,
					mode = "sync",
					status = "timeout",
					error = $"Orchestration did not complete within {timeoutSeconds} seconds.",
				}, s_jsonOptions);
			}
			catch (Exception ex)
			{
				executionInfo.Status = HostExecutionStatus.Failed;
				return JsonSerializer.Serialize(new
				{
					executionId,
					orchestrationId,
					mode = "sync",
					status = "error",
					error = ex.Message,
				}, s_jsonOptions);
			}
			finally
			{
				CleanupExecution(executionId, activeExecutions, activeExecutionInfos, cts);
			}
		}
		else
		{
			// Async mode: fire-and-forget, return execution ID
			_ = Task.Run(async () =>
			{
				try
				{
					var result = await executor.ExecuteAsync(orchestration, parsedParams, cancellationToken: cts.Token);
					executionInfo.Status = result.Status == ExecutionStatus.Succeeded
						? HostExecutionStatus.Completed
						: HostExecutionStatus.Failed;
				}
				catch (OperationCanceledException)
				{
					executionInfo.Status = HostExecutionStatus.Cancelled;
				}
				catch
				{
					executionInfo.Status = HostExecutionStatus.Failed;
				}
				finally
				{
					CleanupExecution(executionId, activeExecutions, activeExecutionInfos, cts);
				}
			}, CancellationToken.None);

			return JsonSerializer.Serialize(new
			{
				executionId,
				orchestrationId,
				orchestrationName = orchestration.Name,
				mode = "async",
				status = "started",
				message = "Orchestration started. Use get_orchestration_status to check progress.",
				metadata = parsedMetadata,
			}, s_jsonOptions);
		}
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
		"Only active (in-progress) executions can be cancelled.")]
	public static string CancelOrchestration(
		ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		[Description("The execution ID to cancel.")] string executionId)
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

		cts.Cancel();

		if (activeExecutionInfos.TryGetValue(executionId, out var info))
		{
			info.Status = HostExecutionStatus.Cancelled;
		}

		return JsonSerializer.Serialize(new
		{
			executionId,
			status = "cancelling",
			message = "Cancellation requested. The orchestration will stop at the next safe point."
		}, s_jsonOptions);
	}

	private static string? TruncateContent(string? content, int maxLength)
	{
		if (content is null) return null;
		if (content.Length <= maxLength) return content;
		return content[..maxLength] + "... (truncated)";
	}

	private static void CleanupExecution(
		string executionId,
		ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		CancellationTokenSource cts)
	{
		_ = Task.Run(async () =>
		{
			// Keep the execution info around briefly so status queries can find it
			await Task.Delay(TimeSpan.FromSeconds(30));
			activeExecutions.TryRemove(executionId, out _);
			activeExecutionInfos.TryRemove(executionId, out _);
			cts.Dispose();
		});
	}

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
	};
}
