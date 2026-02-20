using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Playground.Copilot.Portal;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
	options.SingleLine = true;
	options.IncludeScopes = false;
	options.TimestampFormat = "HH:mm:ss ";
	options.ColorBehavior = LoggerColorBehavior.Enabled;
});

// Register engine services
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();
builder.Services.AddSingleton<IScheduler, OrchestrationScheduler>();

// Determine data path - supports test isolation via environment variable or configuration
var dataPath = Environment.GetEnvironmentVariable("ORCHESTRA_PORTAL_DATA_PATH")
	?? builder.Configuration["data-path"]
	?? builder.Configuration["executions-path"]
	?? Path.Combine(builder.Environment.ContentRootPath, "data");

// Portal-specific run store with enhanced folder structure
var portalRunStore = new PortalRunStore(dataPath);
builder.Services.AddSingleton<PortalRunStore>(portalRunStore);
builder.Services.AddSingleton<IRunStore>(portalRunStore);

// Active executions tracking
var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
var activeExecutionInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
builder.Services.AddSingleton(activeExecutions);
builder.Services.AddSingleton(activeExecutionInfos);

// In-memory orchestration registry with persistence (uses same data path for test isolation)
var registryPersistPath = Path.Combine(dataPath, "registered-orchestrations.json");
builder.Services.AddSingleton<OrchestrationRegistry>(sp =>
{
	var logger = sp.GetService<ILogger<OrchestrationRegistry>>();
	return new OrchestrationRegistry(persistPath: registryPersistPath, logger: logger);
});

// Register TriggerManager as a hosted background service
builder.Services.AddSingleton<TriggerManager>(sp =>
{
	var runsPath = Path.Combine(dataPath, "runs");
	Directory.CreateDirectory(runsPath);
	return new TriggerManager(
		sp.GetRequiredService<ConcurrentDictionary<string, CancellationTokenSource>>(),
		sp.GetRequiredService<ConcurrentDictionary<string, ActiveExecutionInfo>>(),
		sp.GetRequiredService<AgentBuilder>(),
		sp.GetRequiredService<IScheduler>(),
		sp.GetRequiredService<ILoggerFactory>(),
		sp.GetRequiredService<ILogger<TriggerManager>>(),
		runsPath,
		sp.GetRequiredService<IRunStore>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<TriggerManager>());

var app = builder.Build();

// Load persisted orchestrations on startup
var orchestrationRegistry = app.Services.GetRequiredService<OrchestrationRegistry>();
var loadedCount = orchestrationRegistry.LoadFromDisk();
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation("Loaded {Count} persisted orchestrations on startup", loadedCount);

// Register triggers for loaded orchestrations
var triggerManagerStartup = app.Services.GetRequiredService<TriggerManager>();
var registeredTriggers = 0;
foreach (var entry in orchestrationRegistry.GetAll())
{
	if (entry.Orchestration.Trigger is { } trigger && trigger.Enabled)
	{
		triggerManagerStartup.RegisterTrigger(
			entry.Path,
			entry.McpPath,
			trigger,
			null,
			TriggerSource.Json,
			entry.Id); // Pass orchestration ID to match registry
		registeredTriggers++;
	}
}
if (registeredTriggers > 0)
{
	startupLogger.LogInformation("Registered {Count} triggers from loaded orchestrations", registeredTriggers);
}

app.UseStaticFiles();

var jsonOptions = new JsonSerializerOptions
{
	PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	Converters = { new JsonStringEnumConverter() }
};

// ============================================================================
// ORCHESTRATION REGISTRY ENDPOINTS
// ============================================================================

// GET /api/orchestrations - List all registered orchestrations
app.MapGet("/api/orchestrations", (OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	var orchestrations = registry.GetAll().Select(o =>
	{
		var trigger = triggerManager.GetTrigger(o.Id);
		var lastRun = trigger?.LastFireTime;
		var nextRun = trigger?.NextFireTime;
		var parameterNames = o.Orchestration.Steps.SelectMany(s => s.Parameters).Distinct().ToArray();

		return new
		{
			id = o.Id,
			path = o.Path,
			mcpPath = o.McpPath,
			name = o.Orchestration.Name,
			description = o.Orchestration.Description,
			version = o.Orchestration.Version,
			stepCount = o.Orchestration.Steps.Length,
			steps = o.Orchestration.Steps.Select(s => new
			{
				name = s.Name,
				type = s.Type.ToString(),
				dependsOn = s.DependsOn,
				parameters = s.Parameters,
				model = s is PromptOrchestrationStep ps ? ps.Model : null,
				mcps = s is PromptOrchestrationStep psMcp ? psMcp.Mcps.Select(m => new
				{
					name = m.Name,
					type = m.Type
				}).ToArray() : Array.Empty<object>()
			}).ToArray(),
			parameters = parameterNames,
			hasParameters = parameterNames.Length > 0,
			trigger = FormatTriggerInfoWithWebhook(o.Orchestration.Trigger, trigger, parameterNames),
			triggerType = o.Orchestration.Trigger?.Type.ToString() ?? "Manual",
			enabled = trigger?.Config.Enabled ?? o.Orchestration.Trigger?.Enabled ?? false,
			isActive = trigger?.Status.ToString() == "Running",
			lastExecutionTime = lastRun?.ToString("o"),
			lastExecutionStatus = trigger?.LastError is null ? "Success" : "Failed",
			nextExecutionTime = nextRun?.ToString("o"),
			runCount = trigger?.RunCount ?? 0,
			lastExecutionId = trigger?.LastExecutionId,
			hasInlineMcps = o.Orchestration.Mcps.Length > 0,
			models = o.Orchestration.Steps
				.OfType<PromptOrchestrationStep>()
				.Select(s => s.Model)
				.Where(m => !string.IsNullOrEmpty(m))
				.Distinct()
				.ToArray()
		};
	}).ToArray();

	return Results.Json(new { count = orchestrations.Length, orchestrations }, jsonOptions);
});

// GET /api/orchestrations/{id} - Get a specific orchestration
app.MapGet("/api/orchestrations/{id}", (string id, OrchestrationRegistry registry, IScheduler scheduler, TriggerManager triggerManager) =>
{
	var entry = registry.Get(id);
	if (entry is null)
		return Results.NotFound(new { error = $"Orchestration '{id}' not found." });

	var o = entry.Orchestration;
	var schedule = scheduler.Schedule(o);

	var steps = o.Steps.Select(s =>
	{
		var ps = s as PromptOrchestrationStep;
		return new
		{
			name = s.Name,
			type = s.Type.ToString(),
			dependsOn = s.DependsOn,
			parameters = s.Parameters,
			model = ps?.Model,
			reasoningLevel = ps?.ReasoningLevel?.ToString(),
			systemPromptMode = ps?.SystemPromptMode?.ToString(),
			systemPrompt = ps?.SystemPrompt,
			userPrompt = ps?.UserPrompt,
			inputHandlerPrompt = ps?.InputHandlerPrompt,
			outputHandlerPrompt = ps?.OutputHandlerPrompt,
			mcps = ps?.Mcps.Select(m => new { name = m.Name, type = m.Type.ToString() }).ToArray()
		};
	}).ToArray();

	var layers = schedule.Entries.Select((entry, index) => new
	{
		layer = index + 1,
		steps = entry.Steps.Select(s => s.Name).ToArray()
	}).ToArray();

	// Look up the trigger registration to get the webhook URL if applicable
	var triggerRegistration = triggerManager.GetTrigger(entry.Id);
	var allParameters = o.Steps.SelectMany(s => s.Parameters).Distinct().ToArray();

	return Results.Json(new
	{
		id = entry.Id,
		path = entry.Path,
		mcpPath = entry.McpPath,
		name = o.Name,
		description = o.Description,
		version = o.Version,
		steps,
		layers,
		parameters = allParameters,
		trigger = FormatTriggerInfoWithWebhook(o.Trigger, triggerRegistration, allParameters),
		mcps = o.Mcps.Select(m => new
		{
			name = m.Name,
			type = m.Type.ToString(),
			endpoint = (m as RemoteMcp)?.Endpoint,
			command = (m as LocalMcp)?.Command
		}).ToArray()
	}, jsonOptions);
});

// GET /api/mcps - List all MCPs used across all orchestrations
app.MapGet("/api/mcps", (OrchestrationRegistry registry) =>
{
	// Collect all unique MCPs from all orchestrations
	var mcpUsage = new Dictionary<string, (Mcp Mcp, List<string> UsedBy)>(StringComparer.OrdinalIgnoreCase);

	foreach (var entry in registry.GetAll())
	{
		var orchestrationId = entry.Id;
		var orchestrationName = entry.Orchestration.Name;

		// Collect orchestration-level MCPs
		foreach (var mcp in entry.Orchestration.Mcps)
		{
			if (!mcpUsage.TryGetValue(mcp.Name, out var usage))
			{
				usage = (mcp, new List<string>());
				mcpUsage[mcp.Name] = usage;
			}
			if (!usage.UsedBy.Contains(orchestrationId))
				usage.UsedBy.Add(orchestrationId);
		}

		// Collect step-level MCPs
		foreach (var step in entry.Orchestration.Steps.OfType<PromptOrchestrationStep>())
		{
			foreach (var mcp in step.Mcps)
			{
				if (!mcpUsage.TryGetValue(mcp.Name, out var usage))
				{
					usage = (mcp, new List<string>());
					mcpUsage[mcp.Name] = usage;
				}
				if (!usage.UsedBy.Contains(orchestrationId))
					usage.UsedBy.Add(orchestrationId);
			}
		}
	}

	var mcps = mcpUsage.Values.Select(u => new
	{
		name = u.Mcp.Name,
		type = u.Mcp.Type.ToString(),
		endpoint = (u.Mcp as RemoteMcp)?.Endpoint,
		command = (u.Mcp as LocalMcp)?.Command,
		arguments = (u.Mcp as LocalMcp)?.Arguments,
		usedByCount = u.UsedBy.Count,
		usedBy = u.UsedBy.ToArray()
	}).OrderBy(m => m.name).ToArray();

	return Results.Json(new { count = mcps.Length, mcps }, jsonOptions);
});

// POST /api/orchestrations/add - Add orchestrations from files
app.MapPost("/api/orchestrations/add", (AddOrchestrationsRequest request, OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	var added = new List<object>();
	var errors = new List<object>();

	foreach (var path in request.Paths ?? [])
	{
		try
		{
			if (!File.Exists(path))
			{
				errors.Add(new { path, error = "File not found" });
				continue;
			}

			// Auto-detect mcp.json in same directory
			var mcpPath = request.McpPath;
			if (string.IsNullOrWhiteSpace(mcpPath))
			{
				var dir = Path.GetDirectoryName(path)!;
				var candidate = Path.Combine(dir, "mcp.json");
				if (File.Exists(candidate))
					mcpPath = candidate;
			}

			var entry = registry.Register(path, mcpPath);
			added.Add(new
			{
				id = entry.Id,
				path = entry.Path,
				name = entry.Orchestration.Name
			});

			// Register trigger if orchestration has one
			if (entry.Orchestration.Trigger is { } trigger && trigger.Enabled)
			{
				triggerManager.RegisterTrigger(
					entry.Path,
					entry.McpPath,
					trigger,
					null,
					TriggerSource.Json,
					entry.Id); // Pass orchestration ID to match registry
			}
		}
		catch (Exception ex)
		{
			errors.Add(new { path, error = ex.Message });
		}
	}

	return Results.Json(new { addedCount = added.Count, added, errors }, jsonOptions);
});

// POST /api/orchestrations/add-json - Add orchestration from pasted JSON
app.MapPost("/api/orchestrations/add-json", (AddJsonRequest request, OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	try
	{
		if (string.IsNullOrWhiteSpace(request.Json))
			return Results.BadRequest(new { error = "JSON content is required." });

		// Parse the MCPs if provided
		Mcp[] mcps = [];
		if (!string.IsNullOrWhiteSpace(request.McpJson))
		{
			mcps = OrchestrationParser.ParseMcps(request.McpJson);
		}

		var orchestration = OrchestrationParser.ParseOrchestration(request.Json, mcps);

		// Save to temp file so we have a path
		var tempDir = Path.Combine(Path.GetTempPath(), "orchestra-portal");
		Directory.CreateDirectory(tempDir);
		var fileName = $"{SanitizePath(orchestration.Name)}.json";
		var tempPath = Path.Combine(tempDir, fileName);
		File.WriteAllText(tempPath, request.Json);

		var entry = registry.Register(tempPath, null, orchestration);

		// If the orchestration has an enabled trigger, register it with TriggerManager
		if (orchestration.Trigger is { Enabled: true } trigger)
		{
			triggerManager.RegisterTrigger(
				entry.Path,
				entry.McpPath,
				trigger,
				null,
				TriggerSource.Json,
				entry.Id);
		}

		return Results.Json(new
		{
			id = entry.Id,
			path = entry.Path,
			name = entry.Orchestration.Name,
			version = entry.Orchestration.Version
		}, jsonOptions);
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// DELETE /api/orchestrations/{id} - Remove an orchestration
app.MapDelete("/api/orchestrations/{id}", (string id, OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	if (registry.Remove(id))
	{
		triggerManager.RemoveTrigger(id);
		return Results.Ok(new { removed = true, id });
	}
	return Results.NotFound(new { error = $"Orchestration '{id}' not found." });
});

// POST /api/orchestrations/{id}/enable - Enable an orchestration's trigger
app.MapPost("/api/orchestrations/{id}/enable", (string id, OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	var entry = registry.Get(id);
	if (entry is null)
		return Results.NotFound(new { error = $"Orchestration '{id}' not found." });

	// If the orchestration has a trigger but it's not registered yet, register it now
	if (entry.Orchestration.Trigger is { } trigger)
	{
		var existingTrigger = triggerManager.GetTrigger(id);
		if (existingTrigger == null)
		{
			// Register the trigger with enabled = true
			var enabledTrigger = TriggerManager.CloneTriggerConfigWithEnabled(trigger, true);
			triggerManager.RegisterTrigger(
				entry.Path,
				entry.McpPath,
				enabledTrigger,
				null,
				TriggerSource.Json,
				entry.Id);
		}
		else
		{
			triggerManager.SetTriggerEnabled(id, true);
		}
		return Results.Ok(new { id, enabled = true });
	}

	return Results.BadRequest(new { error = $"Orchestration '{id}' has no trigger defined." });
});

// POST /api/orchestrations/{id}/disable - Disable an orchestration's trigger
app.MapPost("/api/orchestrations/{id}/disable", (string id, OrchestrationRegistry registry, TriggerManager triggerManager) =>
{
	var entry = registry.Get(id);
	if (entry is null)
		return Results.NotFound(new { error = $"Orchestration '{id}' not found." });

	triggerManager.SetTriggerEnabled(id, false);
	return Results.Ok(new { id, enabled = false });
});

// ============================================================================
// EXECUTION ENDPOINTS
// ============================================================================

// GET /api/orchestrations/{id}/run - Run an orchestration (SSE)
// NOTE: Must be GET for EventSource compatibility (SSE clients only support GET)
app.MapGet("/api/orchestrations/{id}/run", async (
	HttpContext httpContext,
	string id,
	OrchestrationRegistry registry,
	AgentBuilder agentBuilder,
	IScheduler scheduler,
	ILoggerFactory loggerFactory,
	PortalRunStore runStore) =>
{
	var entry = registry.Get(id);
	if (entry is null)
	{
		httpContext.Response.StatusCode = 404;
		await httpContext.Response.WriteAsJsonAsync(new { error = $"Orchestration '{id}' not found." });
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
					parameters[prop.Name] = prop.Value.GetString() ?? "";
			}
		}
		catch { /* Invalid parameters JSON */ }
	}

	// Set up SSE response
	httpContext.Response.ContentType = "text/event-stream";
	httpContext.Response.Headers.CacheControl = "no-cache";
	httpContext.Response.Headers.Connection = "keep-alive";
	await httpContext.Response.Body.FlushAsync();

	// Generate execution ID and create reporter
	var executionId = Guid.NewGuid().ToString("N")[..12];
	var reporter = new WebOrchestrationReporter();
	// NOTE: Do NOT link to httpContext.RequestAborted - we want the orchestration to keep running
	// even if the client disconnects from the SSE stream. Cancellation should only happen via
	// explicit /api/cancel/{executionId} calls.
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
		Parameters = parameters
	};
	activeExecutionInfos[executionId] = executionInfo;

	// Send execution-started event
	await httpContext.Response.WriteAsync($"event: execution-started\n");
	await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { executionId }, jsonOptions)}\n\n");
	await httpContext.Response.Body.FlushAsync();

	var logger = loggerFactory.CreateLogger<OrchestrationExecutor>();
	var executor = new OrchestrationExecutor(scheduler, agentBuilder, reporter, logger, runStore);
	var cancellationToken = cts.Token;
	var runId = executionId; // Use executionId as runId for consistency
	var runStartedAt = DateTimeOffset.UtcNow;

	// Execute in background
	var executionTask = Task.Run(async () =>
	{
		// Local helper to save cancelled run records
		async Task SaveCancelledRunAsync(
			PortalRunStore store,
			OrchestrationEntry orchestrationEntry,
			string cancelledRunId,
			DateTimeOffset startTime,
			Dictionary<string, string>? runParams,
			WebOrchestrationReporter runReporter)
		{
			var completedAt = DateTimeOffset.UtcNow;
			
			// Extract step information from accumulated events
			var stepRecords = new Dictionary<string, StepRunRecord>();
			var allStepRecords = new Dictionary<string, StepRunRecord>();
			var summary = new System.Text.StringBuilder();
			summary.AppendLine("Orchestration was cancelled.");
			
			// Parse accumulated events to build step records
			var stepsStarted = new HashSet<string>();
			var stepsCompleted = new HashSet<string>();
			var stepsCancelled = new HashSet<string>();
			var stepErrors = new Dictionary<string, string>();
			
			foreach (var evt in runReporter.AccumulatedEvents)
			{
				try
				{
					var data = JsonSerializer.Deserialize<JsonElement>(evt.Data, jsonOptions);
					
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
				catch { /* Ignore parse errors */ }
			}
			
			// Build step records for ALL steps in the orchestration
			// Include steps that: completed, cancelled, errored, in-progress, or never started
			foreach (var step in orchestrationEntry.Orchestration.Steps)
			{
				var stepName = step.Name;
				ExecutionStatus status;
				string? errorMessage = null;
				string content = "";
				
				if (stepsCompleted.Contains(stepName))
				{
					// Step completed successfully before cancellation
					status = ExecutionStatus.Succeeded;
				}
				else if (stepsCancelled.Contains(stepName))
				{
					// Step was explicitly cancelled (received step-cancelled event)
					status = ExecutionStatus.Cancelled;
					content = "[Cancelled]";
					errorMessage = "Cancelled";
				}
				else if (stepErrors.ContainsKey(stepName))
				{
					// Step failed with an error
					status = ExecutionStatus.Failed;
					errorMessage = stepErrors[stepName];
				}
				else if (stepsStarted.Contains(stepName))
				{
					// Step was started but didn't complete or get explicitly cancelled
					// This means it was in-progress when cancellation happened
					status = ExecutionStatus.Cancelled;
					content = "[Cancelled while in progress]";
					errorMessage = "Cancelled while in progress";
				}
				else
				{
					// Step never started - it was skipped due to cancellation
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
			
			// Build summary of what happened
			if (stepsCompleted.Count > 0)
				summary.AppendLine($"Completed steps: {string.Join(", ", stepsCompleted)}");
			if (stepsCancelled.Count > 0)
				summary.AppendLine($"Cancelled steps: {string.Join(", ", stepsCancelled)}");
			var inProgress = stepsStarted.Except(stepsCompleted).Except(stepsCancelled).ToList();
			if (inProgress.Count > 0)
				summary.AppendLine($"In-progress steps when cancelled: {string.Join(", ", inProgress)}");
			var skipped = orchestrationEntry.Orchestration.Steps.Select(s => s.Name).Except(stepsStarted).ToList();
			if (skipped.Count > 0)
				summary.AppendLine($"Skipped steps: {string.Join(", ", skipped)}");
			
			var cancelledRecord = new OrchestrationRunRecord
			{
				RunId = cancelledRunId,
				OrchestrationName = orchestrationEntry.Orchestration.Name,
				StartedAt = startTime,
				CompletedAt = completedAt,
				Status = ExecutionStatus.Cancelled,
				Parameters = runParams ?? new Dictionary<string, string>(),
				TriggeredBy = "manual",
				StepRecords = stepRecords,
				AllStepRecords = allStepRecords,
				FinalContent = summary.ToString()
			};
			
			try
			{
				await store.SaveRunAsync(cancelledRecord, orchestrationEntry.Orchestration);
			}
			catch (Exception ex)
			{
				// Log but don't fail - cancellation handling is best-effort
				Console.WriteLine($"Failed to save cancelled run record: {ex.Message}");
			}
		}

		// Local helper to save failed run records
		async Task SaveFailedRunAsync(
			PortalRunStore store,
			OrchestrationEntry orchestrationEntry,
			string failedRunId,
			DateTimeOffset startTime,
			Dictionary<string, string>? runParams,
			WebOrchestrationReporter runReporter,
			string errorMessage)
		{
			var completedAt = DateTimeOffset.UtcNow;
			
			// Extract step information from accumulated events
			var stepRecords = new Dictionary<string, StepRunRecord>();
			var allStepRecords = new Dictionary<string, StepRunRecord>();
			var summary = new System.Text.StringBuilder();
			summary.AppendLine($"Orchestration failed: {errorMessage}");
			
			// Parse accumulated events to build step records
			var stepsStarted = new HashSet<string>();
			var stepsCompleted = new HashSet<string>();
			var stepErrors = new Dictionary<string, string>();
			
			foreach (var evt in runReporter.AccumulatedEvents)
			{
				try
				{
					var data = JsonSerializer.Deserialize<JsonElement>(evt.Data, jsonOptions);
					
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
							    data.TryGetProperty("error", out var errorMsg))
								stepErrors[errorStepName.GetString() ?? ""] = errorMsg.GetString() ?? "";
							break;
					}
				}
				catch { /* Ignore parse errors */ }
			}
			
			// Build step records from what we know
			foreach (var stepName in stepsStarted)
			{
				var status = stepsCompleted.Contains(stepName) 
					? ExecutionStatus.Succeeded 
					: stepErrors.ContainsKey(stepName)
						? ExecutionStatus.Failed
						: ExecutionStatus.Failed; // Steps that started but didn't complete are marked failed
				var stepError = stepErrors.GetValueOrDefault(stepName);
				
				var stepRecord = new StepRunRecord
				{
					StepName = stepName,
					Status = status,
					StartedAt = startTime,
					CompletedAt = completedAt,
					Content = status == ExecutionStatus.Failed ? "[Failed]" : "",
					ErrorMessage = stepError
				};
				
				stepRecords[stepName] = stepRecord;
				allStepRecords[stepName] = stepRecord;
			}
			
			// Build summary of what happened
			if (stepsCompleted.Count > 0)
				summary.AppendLine($"Completed steps: {string.Join(", ", stepsCompleted)}");
			var failedSteps = stepErrors.Keys.ToList();
			if (failedSteps.Count > 0)
				summary.AppendLine($"Failed steps: {string.Join(", ", failedSteps)}");
			
			var failedRecord = new OrchestrationRunRecord
			{
				RunId = failedRunId,
				OrchestrationName = orchestrationEntry.Orchestration.Name,
				StartedAt = startTime,
				CompletedAt = completedAt,
				Status = ExecutionStatus.Failed,
				Parameters = runParams ?? new Dictionary<string, string>(),
				TriggeredBy = "manual",
				StepRecords = stepRecords,
				AllStepRecords = allStepRecords,
				FinalContent = summary.ToString()
			};
			
			try
			{
				await store.SaveRunAsync(failedRecord, orchestrationEntry.Orchestration);
			}
			catch (Exception ex)
			{
				// Log but don't fail - failure handling is best-effort
				Console.WriteLine($"Failed to save failed run record: {ex.Message}");
			}
		}
		
		try
		{
			var result = await executor.ExecuteAsync(
				entry.Orchestration,
				parameters,
				cancellationToken: cancellationToken);

			// Check if cancellation was requested - the executor may complete "normally" 
			// but with failed steps when cancelled (it doesn't always throw OperationCanceledException)
			if (cancellationToken.IsCancellationRequested)
			{
				// Handle as cancellation
				reporter.ReportOrchestrationCancelled();
				executionInfo.Status = "Cancelled";
				
				// Build a summary of what happened before cancellation
				await SaveCancelledRunAsync(runStore, entry, runId, runStartedAt, parameters, reporter);
				return;
			}

			foreach (var (stepName, stepResult) in result.StepResults)
			{
				if (stepResult.Status == ExecutionStatus.Succeeded)
					reporter.ReportStepOutput(stepName, stepResult.Content);
			}

			reporter.ReportOrchestrationDone(result);
			executionInfo.Status = "Completed";
		}
		catch (OperationCanceledException)
		{
			reporter.ReportOrchestrationCancelled();
			executionInfo.Status = "Cancelled";
			
			// Save cancelled run record
			await SaveCancelledRunAsync(runStore, entry, runId, runStartedAt, parameters, reporter);
		}
		catch (Exception ex)
		{
			reporter.ReportStepError("orchestration", ex.Message);
			reporter.ReportOrchestrationError(ex.Message);
			executionInfo.Status = "Failed";
			
			// Save failed run record
			await SaveFailedRunAsync(runStore, entry, runId, runStartedAt, parameters, reporter, ex.Message);
		}
		finally
		{
			reporter.Complete();
			// Keep execution info around briefly so late-joiners can see final state
			// Then clean up after a delay
			_ = Task.Run(async () =>
			{
				await Task.Delay(TimeSpan.FromSeconds(5));
				activeExecutions.TryRemove(executionId, out _);
				activeExecutionInfos.TryRemove(executionId, out _);
				cts.Dispose(); // Dispose the CancellationTokenSource
			});
		}
	}, CancellationToken.None); // Don't cancel the background task itself

	// Subscribe and stream SSE events to this client
	var (replay, futureEvents) = reporter.Subscribe();
	
	// Use httpContext.RequestAborted for SSE streaming - this handles client disconnect
	// without cancelling the orchestration itself
	var sseToken = httpContext.RequestAborted;

	// First replay any events that happened before we subscribed (shouldn't be any for initial caller)
	foreach (var evt in replay)
	{
		await httpContext.Response.WriteAsync($"event: {evt.Type}\n", sseToken);
		await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", sseToken);
	}
	await httpContext.Response.Body.FlushAsync(sseToken);

	// Stream future events until client disconnects OR orchestration completes
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
		// Client disconnected - unsubscribe but don't cancel the orchestration
		reporter.Unsubscribe(futureEvents);
	}

	// If we're still connected, wait for execution to complete
	// If client disconnected, this won't block because we've unsubscribed
	if (!sseToken.IsCancellationRequested)
	{
		await executionTask;
	}
});

// POST /api/cancel/{executionId} - Cancel a running execution
app.MapPost("/api/cancel/{executionId}", (string executionId) =>
{
	if (activeExecutionInfos.TryGetValue(executionId, out var info))
	{
		// Set status to Cancelling - don't remove yet
		info.Status = "Cancelling";
		// Broadcast status change to all attached SSE clients
		info.Reporter.ReportStatusChange("Cancelling");
		info.CancellationTokenSource.Cancel();
		return Results.Ok(new { cancelled = true, executionId, status = "Cancelling" });
	}
	return Results.NotFound(new { error = $"No active execution with ID '{executionId}'" });
});

// GET /api/execution/{executionId}/attach - Attach to a running execution's SSE stream
app.MapGet("/api/execution/{executionId}/attach", async (string executionId, HttpContext httpContext) =>
{
	if (!activeExecutionInfos.TryGetValue(executionId, out var info))
	{
		httpContext.Response.StatusCode = 404;
		await httpContext.Response.WriteAsJsonAsync(new { error = $"No active execution with ID '{executionId}'" });
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
	var (replay, futureEvents) = info.Reporter.Subscribe();

	// Replay accumulated events
	foreach (var evt in replay)
	{
		await httpContext.Response.WriteAsync($"event: {evt.Type}\n", cancellationToken);
		await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", cancellationToken);
	}
	await httpContext.Response.Body.FlushAsync(cancellationToken);

	// If already completed, we're done
	if (info.Reporter.IsCompleted)
	{
		return;
	}

	// Stream future events
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
		info.Reporter.Unsubscribe(futureEvents);
	}
});

// ============================================================================
// HISTORY ENDPOINTS
// ============================================================================

// GET /api/history - Get recent executions (lightweight summaries)
// Includes both running orchestrations and completed ones from the run store
app.MapGet("/api/history", async (PortalRunStore runStore, int? limit) =>
{
	var requestedLimit = limit ?? 15;
	
	// Get running orchestrations (these should appear at the top)
	var runningRuns = activeExecutionInfos.Values
		.OrderByDescending(e => e.StartedAt)
		.Select(e => new
		{
			runId = e.ExecutionId, // Use executionId as runId for running ones
			executionId = e.ExecutionId, // Also include executionId for attach functionality
			orchestrationId = e.OrchestrationId, // Include orchestrationId for looking up orchestration details
			orchestrationName = e.OrchestrationName,
			version = "1.0.0",
			triggeredBy = e.TriggeredBy,
			startedAt = e.StartedAt.ToString("o"),
			completedAt = (string?)null,
			durationSeconds = Math.Round((DateTimeOffset.UtcNow - e.StartedAt).TotalSeconds, 2),
			status = e.Status, // Running, Cancelling, etc.
			isActive = true,
			parameters = e.Parameters
		})
		.ToList();

	// Get completed runs from store, taking enough to fill the limit minus running ones
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
		isActive = false
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
// Includes both running orchestrations and completed ones from the run store
app.MapGet("/api/history/all", async (PortalRunStore runStore, int? limit, int? offset) =>
{
	var requestedOffset = offset ?? 0;
	var requestedLimit = limit ?? 100;

	// Get running orchestrations (shown first, before any offset)
	var runningRuns = activeExecutionInfos.Values
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
	
	// If offset is within running items range
	if (requestedOffset < runningCount)
	{
		var runningToTake = runningRuns.Skip(requestedOffset).Take(requestedLimit);
		allItems.AddRange(runningToTake.Cast<object>());
		
		// If we need more items from completed
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
				isActive = false
			});
			allItems.AddRange(completedItems.Cast<object>());
		}
	}
	else
	{
		// Offset is past running items, only get from completed
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
			isActive = false
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

// GET /api/history/{orchestrationName}/{runId} - Get full execution details
app.MapGet("/api/history/{orchestrationName}/{runId}", async (string orchestrationName, string runId, PortalRunStore runStore) =>
{
	var record = await runStore.GetRunAsync(orchestrationName, runId);
	if (record is null)
		return Results.NotFound(new { error = $"Run '{runId}' not found." });

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
		parameters = record.Parameters,
		finalContent = record.FinalContent,
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
			errorMessage = kv.Value.ErrorMessage
		}).ToArray()
	}, jsonOptions);
});

// ============================================================================
// FILE BROWSER ENDPOINTS
// ============================================================================

// POST /api/browse - Browse directory for JSON files
app.MapPost("/api/browse", (BrowseRequest request) =>
{
	try
	{
		var directory = request.Directory?.Trim();
		if (string.IsNullOrEmpty(directory))
			return Results.BadRequest(new { error = "Directory path is required." });

		if (!Directory.Exists(directory))
			return Results.BadRequest(new { error = $"Directory not found: {directory}" });

		var dirInfo = new DirectoryInfo(directory);

		var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
			.Select(f => new
			{
				name = Path.GetFileName(f),
				path = f,
				size = new FileInfo(f).Length
			})
			.OrderBy(f => f.name)
			.ToArray();

		var subdirectories = dirInfo.GetDirectories()
			.Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
			.Select(d => new { name = d.Name, path = d.FullName })
			.OrderBy(d => d.name)
			.ToArray();

		return Results.Json(new
		{
			directory = dirInfo.FullName,
			parent = dirInfo.Parent?.FullName,
			subdirectories,
			files
		}, jsonOptions);
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// POST /api/browse-dialog - Open native folder picker (Windows only)
app.MapPost("/api/browse-dialog", async () =>
{
	try
	{
		if (!OperatingSystem.IsWindows())
			return Results.BadRequest(new { error = "Native folder dialog is only supported on Windows." });

		var psi = new System.Diagnostics.ProcessStartInfo
		{
			FileName = "powershell",
			Arguments = "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = 'Select orchestration folder'; $d.ShowNewFolderButton = $false; if ($d.ShowDialog() -eq 'OK') { $d.SelectedPath } else { '' }\"",
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		using var proc = System.Diagnostics.Process.Start(psi);
		if (proc is null)
			return Results.BadRequest(new { error = "Failed to launch folder dialog." });

		var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
		await proc.WaitForExitAsync();

		if (string.IsNullOrEmpty(output))
			return Results.Json(new { cancelled = true, directory = (string?)null });

		return Results.Json(new { cancelled = false, directory = output });
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// POST /api/folder/scan - Scan folder for orchestration files
app.MapPost("/api/folder/scan", (FolderScanRequest request) =>
{
	try
	{
		if (string.IsNullOrWhiteSpace(request.Directory))
			return Results.BadRequest(new { error = "Directory path is required" });

		if (!Directory.Exists(request.Directory))
			return Results.BadRequest(new { error = $"Directory not found: {request.Directory}" });

		var files = Directory.GetFiles(request.Directory, "*.json", SearchOption.TopDirectoryOnly);
		var orchestrations = new List<object>();

		// Auto-detect mcp.json in the scanned directory
		string? detectedMcpPath = null;
		var mcpCandidate = Path.Combine(request.Directory, "mcp.json");
		if (File.Exists(mcpCandidate))
			detectedMcpPath = mcpCandidate;

		foreach (var file in files.OrderBy(f => f))
		{
			if (Path.GetFileName(file).Equals("mcp.json", StringComparison.OrdinalIgnoreCase))
				continue;

			try
			{
				var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);

				var perFileMcp = Path.Combine(
					Path.GetDirectoryName(file)!,
					Path.GetFileNameWithoutExtension(file) + ".mcp.json");
				var orchMcpPath = File.Exists(perFileMcp) ? perFileMcp : detectedMcpPath;

				orchestrations.Add(new
				{
					path = file,
					fileName = Path.GetFileName(file),
					name = orchestration.Name,
					description = orchestration.Description,
					version = orchestration.Version,
					stepCount = orchestration.Steps.Length,
					trigger = FormatTriggerInfo(orchestration.Trigger),
					mcpPath = orchMcpPath,
					valid = true,
					error = (string?)null
				});
			}
			catch (Exception ex)
			{
				orchestrations.Add(new
				{
					path = file,
					fileName = Path.GetFileName(file),
					name = (string?)null,
					description = (string?)null,
					version = (string?)null,
					stepCount = 0,
					trigger = (object?)null,
					mcpPath = (string?)null,
					valid = false,
					error = ex.Message
				});
			}
		}

		return Results.Json(new
		{
			directory = request.Directory,
			count = orchestrations.Count,
			mcpPath = detectedMcpPath,
			orchestrations
		}, jsonOptions);
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// ============================================================================
// TRIGGER ENDPOINTS (proxied from TriggerManager)
// ============================================================================

// GET /api/triggers - List all triggers
app.MapGet("/api/triggers", (TriggerManager triggerManager) =>
{
	var triggers = triggerManager.GetAllTriggers().Select(t => new
	{
		id = t.Id,
		orchestrationPath = t.OrchestrationPath,
		orchestrationName = t.OrchestrationName,
		triggerType = t.Config.Type.ToString().ToLowerInvariant(),
		enabled = t.Config.Enabled,
		status = t.Status.ToString(),
		nextFireTime = t.NextFireTime?.ToString("o"),
		lastFireTime = t.LastFireTime?.ToString("o"),
		runCount = t.RunCount,
		lastError = t.LastError,
		webhookUrl = t.Config is WebhookTriggerConfig ? $"/api/webhook/{t.Id}" : null
	}).ToArray();

	return Results.Json(new { count = triggers.Length, triggers }, jsonOptions);
});

// POST /api/triggers/{id}/fire - Manually fire a trigger
app.MapPost("/api/triggers/{id}/fire", async (HttpContext httpContext, string id, TriggerManager triggerManager) =>
{
	// Parse optional parameters
	Dictionary<string, string>? parameters = null;
	try
	{
		if (httpContext.Request.ContentLength > 0)
		{
			var body = await JsonSerializer.DeserializeAsync<JsonElement>(httpContext.Request.Body, jsonOptions);
			if (body.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
			{
				parameters = new Dictionary<string, string>();
				foreach (var prop in paramsEl.EnumerateObject())
					parameters[prop.Name] = prop.Value.GetString() ?? "";
			}
		}
	}
	catch { }

	var (found, executionId) = await triggerManager.FireTriggerAsync(id, parameters);
	if (!found)
		return Results.NotFound(new { error = $"Trigger '{id}' not found." });

	return Results.Json(new { fired = true, id, executionId }, jsonOptions);
});

// POST /api/webhook/{id} - Webhook receiver endpoint for external systems
app.MapPost("/api/webhook/{id}", async (HttpContext httpContext, string id, TriggerManager triggerManager) =>
{
	var t = triggerManager.GetTrigger(id);
	if (t == null)
		return Results.NotFound(new { error = $"Webhook trigger '{id}' not found." });

	if (t.Config is not WebhookTriggerConfig webhookConfig)
		return Results.BadRequest(new { error = $"Trigger '{id}' is not a webhook trigger." });

	// Validate webhook secret if configured
	if (!string.IsNullOrWhiteSpace(webhookConfig.Secret))
	{
		var providedSecret = httpContext.Request.Headers["X-Webhook-Secret"].FirstOrDefault()
			?? httpContext.Request.Query["secret"].FirstOrDefault();

		if (providedSecret != webhookConfig.Secret)
			return Results.Unauthorized();
	}

	// Parse parameters from webhook body
	Dictionary<string, string>? webhookParams = null;
	if (httpContext.Request.ContentLength > 0)
	{
		try
		{
			using var reader = new StreamReader(httpContext.Request.Body);
			var body = await reader.ReadToEndAsync();
			if (!string.IsNullOrWhiteSpace(body))
			{
				webhookParams = JsonSerializer.Deserialize<Dictionary<string, string>>(body, jsonOptions);
			}
		}
		catch
		{
			// Best-effort body parsing — continue without params
		}
	}

	var (found, executionId) = await triggerManager.FireWebhookTriggerAsync(id, webhookParams);
	if (!found)
		return Results.NotFound(new { error = $"Trigger '{id}' not found." });

	// If executionId is null, the trigger exists but is disabled or paused
	var accepted = executionId != null;
	return Results.Json(new
	{
		accepted,
		triggerId = id,
		executionId,
		message = accepted ? null : "Trigger is disabled or paused"
	}, jsonOptions);
});

// ============================================================================
// UTILITY ENDPOINTS
// ============================================================================

// GET /api/models - Available models
app.MapGet("/api/models", () =>
{
	var models = new[]
	{
		new { id = "gpt-4.1", name = "GPT-4.1", provider = "OpenAI" },
		new { id = "gpt-4.1-mini", name = "GPT-4.1 Mini", provider = "OpenAI" },
		new { id = "gpt-4.1-nano", name = "GPT-4.1 Nano", provider = "OpenAI" },
		new { id = "gpt-4o", name = "GPT-4o", provider = "OpenAI" },
		new { id = "gpt-4o-mini", name = "GPT-4o Mini", provider = "OpenAI" },
		new { id = "o3-mini", name = "o3-mini", provider = "OpenAI" },
		new { id = "o4-mini", name = "o4-mini", provider = "OpenAI" },
		new { id = "claude-sonnet-4", name = "Claude Sonnet 4", provider = "Anthropic" },
		new { id = "claude-opus-4", name = "Claude Opus 4", provider = "Anthropic" },
		new { id = "claude-3.5-sonnet", name = "Claude 3.5 Sonnet", provider = "Anthropic" },
		new { id = "claude-opus-4.5", name = "Claude Opus 4.5", provider = "Anthropic" },
		new { id = "gemini-2.0-flash", name = "Gemini 2.0 Flash", provider = "Google" }
	};
	return Results.Json(new { models }, jsonOptions);
});

// GET /api/status - Server status
app.MapGet("/api/status", (OrchestrationRegistry registry, TriggerManager triggerManager, PortalRunStore runStore) =>
{
	var triggers = triggerManager.GetAllTriggers();
	return Results.Json(new
	{
		status = "running",
		version = "1.0.0",
		orchestrationCount = registry.Count,
		activeTriggers = triggers.Count(t => t.Config.Enabled),
		runningExecutions = activeExecutions.Count
	}, jsonOptions);
});

// GET /api/active - Get all active (running) orchestrations
app.MapGet("/api/active", (TriggerManager triggerManager, OrchestrationRegistry registry) =>
{
	// Combine manual executions and trigger-based executions
	var activeList = new List<object>();

	// Add manual executions
	foreach (var info in activeExecutionInfos.Values)
	{
		activeList.Add(new
		{
			executionId = info.ExecutionId,
			orchestrationId = info.OrchestrationId,
			orchestrationName = info.OrchestrationName,
			startedAt = info.StartedAt,
			triggeredBy = info.TriggeredBy,
			source = "manual",
			status = info.Status,
			parameters = info.Parameters
		});
	}

	// Add trigger-based running executions
	var runningTriggers = triggerManager.GetAllTriggers()
		.Where(t => t.Status == TriggerStatus.Running && !string.IsNullOrEmpty(t.ActiveExecutionId));

	foreach (var trigger in runningTriggers)
	{
		// Avoid duplicates if somehow tracked in both
		if (!activeExecutionInfos.ContainsKey(trigger.ActiveExecutionId!))
		{
			var triggerType = trigger.Config switch
			{
				SchedulerTriggerConfig => "scheduler",
				LoopTriggerConfig => "loop",
				WebhookTriggerConfig => "webhook",
				_ => "trigger"
			};

			activeList.Add(new
			{
				executionId = trigger.ActiveExecutionId,
				orchestrationId = trigger.Id,
				orchestrationName = trigger.OrchestrationName ?? "Unknown",
				startedAt = trigger.LastFireTime,
				triggeredBy = triggerType,
				source = "trigger"
			});
		}
	}

	// Add pending/waiting triggers (scheduled and waiting for next fire, OR webhooks waiting for invocation)
	var pendingTriggers = triggerManager.GetAllTriggers()
		.Where(t => t.Config.Enabled && t.Status == TriggerStatus.Waiting && 
			(t.NextFireTime.HasValue || t.Config is WebhookTriggerConfig));

	var pending = pendingTriggers.Select(t => {
		// Look up the orchestration to get step count
		var orch = registry.Get(t.Id);
		var stepCount = orch?.Orchestration?.Steps?.Length ?? 0;
		
		return new
		{
			orchestrationId = t.Id,
			orchestrationName = t.OrchestrationName ?? "Unknown",
			orchestrationDescription = t.OrchestrationDescription,
			stepCount = stepCount,
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
				_ => "trigger"
			},
			triggeredBy = t.Config switch
			{
				SchedulerTriggerConfig => "scheduler",
				LoopTriggerConfig => "loop",
				WebhookTriggerConfig => "webhook",
				_ => "trigger"
			},
			source = "pending",
			// Include webhook URL for webhook triggers
			webhookUrl = t.Config is WebhookTriggerConfig ? $"/api/webhook/{t.Id}" : null
		};
	});

	return Results.Json(new
	{
		running = activeList,
		pending = pending,
		totalRunning = activeList.Count,
		totalPending = pending.Count()
	}, jsonOptions);
});

// Fallback to index.html
app.MapFallbackToFile("index.html");

app.Run();

// ============================================================================
// HELPER METHODS
// ============================================================================

static object? FormatTriggerInfo(TriggerConfig? config)
{
	return config switch
	{
		SchedulerTriggerConfig s => new
		{
			type = "scheduler",
			enabled = s.Enabled,
			cron = s.Cron,
			intervalSeconds = s.IntervalSeconds,
			maxRuns = s.MaxRuns
		},
		LoopTriggerConfig l => new
		{
			type = "loop",
			enabled = l.Enabled,
			delaySeconds = l.DelaySeconds,
			maxIterations = l.MaxIterations,
			continueOnFailure = l.ContinueOnFailure
		},
		WebhookTriggerConfig w => new
		{
			type = "webhook",
			enabled = w.Enabled,
			maxConcurrent = w.MaxConcurrent
		},
		_ => null
	};
}

static object? FormatTriggerInfoWithWebhook(TriggerConfig? config, TriggerRegistration? registration, string[] parameters)
{
	return config switch
	{
		SchedulerTriggerConfig s => new
		{
			type = "scheduler",
			enabled = s.Enabled,
			cron = s.Cron,
			intervalSeconds = s.IntervalSeconds,
			maxRuns = s.MaxRuns
		},
		LoopTriggerConfig l => new
		{
			type = "loop",
			enabled = l.Enabled,
			delaySeconds = l.DelaySeconds,
			maxIterations = l.MaxIterations,
			continueOnFailure = l.ContinueOnFailure
		},
		WebhookTriggerConfig w => new
		{
			type = "webhook",
			enabled = w.Enabled,
			maxConcurrent = w.MaxConcurrent,
			hasSecret = !string.IsNullOrWhiteSpace(w.Secret),
			hasInputHandler = !string.IsNullOrWhiteSpace(w.InputHandlerPrompt),
			// Webhook invocation details
			webhookUrl = registration != null ? $"/api/webhook/{registration.Id}" : null,
			expectedParameters = parameters,
			invocation = registration != null ? new
			{
				method = "POST",
				url = $"/api/webhook/{registration.Id}",
				contentType = "application/json",
				headers = !string.IsNullOrWhiteSpace(w.Secret) 
					? new { XWebhookSecret = "(your secret)" } 
					: null,
				exampleBody = parameters.Length > 0 
					? parameters.ToDictionary(p => p, p => $"<{p} value>")
					: null,
				note = !string.IsNullOrWhiteSpace(w.InputHandlerPrompt)
					? "This webhook has an input handler that will parse the raw payload using an LLM"
					: null
			} : null
		},
		_ => null
	};
}

static string SanitizePath(string name)
{
	var invalid = Path.GetInvalidFileNameChars();
	var sanitized = new char[name.Length];
	for (var i = 0; i < name.Length; i++)
		sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
	return new string(sanitized);
}

// ============================================================================
// REQUEST DTOs
// ============================================================================

record BrowseRequest(string? Directory);
record FolderScanRequest(string? Directory);
record AddOrchestrationsRequest(string[]? Paths, string? McpPath);
record AddJsonRequest(string Json, string? McpJson);

// ============================================================================
// ACTIVE EXECUTION TRACKING
// ============================================================================

/// <summary>
/// Information about an actively running orchestration execution.
/// </summary>
public class ActiveExecutionInfo
{
	public required string ExecutionId { get; init; }
	public required string OrchestrationId { get; init; }
	public required string OrchestrationName { get; init; }
	public required DateTimeOffset StartedAt { get; init; }
	public required string TriggeredBy { get; init; } // "manual", "scheduler", "loop", "webhook"
	public required CancellationTokenSource CancellationTokenSource { get; init; }
	public required WebOrchestrationReporter Reporter { get; init; }

	/// <summary>
	/// Parameters passed to the orchestration when it was started.
	/// </summary>
	public Dictionary<string, string>? Parameters { get; init; }

	/// <summary>
	/// Status: "Running", "Cancelling", "Cancelled", "Completed"
	/// </summary>
	public string Status { get; set; } = "Running";
}

// ============================================================================
// ORCHESTRATION REGISTRY
// ============================================================================

/// <summary>
/// In-memory registry of loaded orchestrations for the Portal.
/// Supports persistence to disk for reload across restarts.
/// </summary>
public class OrchestrationRegistry
{
	private readonly ConcurrentDictionary<string, OrchestrationEntry> _entries = new();
	private readonly string _persistPath;
	private readonly ILogger<OrchestrationRegistry>? _logger;

	public OrchestrationRegistry(string? persistPath = null, ILogger<OrchestrationRegistry>? logger = null)
	{
		_persistPath = persistPath ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"OrchestraPortal",
			"registered-orchestrations.json");
		_logger = logger;
	}

	public int Count => _entries.Count;

	public OrchestrationEntry Register(string path, string? mcpPath, Orchestration? preloaded = null, bool persist = true)
	{
		Mcp[] mcps = [];
		if (!string.IsNullOrWhiteSpace(mcpPath) && File.Exists(mcpPath))
			mcps = OrchestrationParser.ParseMcpFile(mcpPath);

		var orchestration = preloaded ?? OrchestrationParser.ParseOrchestrationFile(path, mcps);
		var id = GenerateId(orchestration.Name, path);

		var entry = new OrchestrationEntry
		{
			Id = id,
			Path = path,
			McpPath = mcpPath,
			Orchestration = orchestration,
			RegisteredAt = DateTimeOffset.UtcNow
		};

		_entries[id] = entry;
		
		if (persist)
			SaveToDisk();
		
		return entry;
	}

	public OrchestrationEntry? Get(string id) =>
		_entries.TryGetValue(id, out var entry) ? entry : null;

	public IEnumerable<OrchestrationEntry> GetAll() => _entries.Values;

	public bool Remove(string id)
	{
		var removed = _entries.TryRemove(id, out _);
		if (removed)
			SaveToDisk();
		return removed;
	}

	/// <summary>
	/// Save registered orchestration paths to disk for persistence.
	/// </summary>
	public void SaveToDisk()
	{
		try
		{
			var dir = Path.GetDirectoryName(_persistPath);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			var data = _entries.Values.Select(e => new PersistedOrchestration
			{
				Path = e.Path,
				McpPath = e.McpPath
			}).ToList();

			var json = JsonSerializer.Serialize(data, new JsonSerializerOptions 
			{ 
				WriteIndented = true,
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase
			});
			File.WriteAllText(_persistPath, json);
			_logger?.LogInformation("Saved {Count} orchestrations to {Path}", data.Count, _persistPath);
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Failed to save orchestrations to disk");
		}
	}

	/// <summary>
	/// Load registered orchestrations from disk.
	/// </summary>
	public int LoadFromDisk()
	{
		if (!File.Exists(_persistPath))
		{
			_logger?.LogInformation("No persisted orchestrations file found at {Path}", _persistPath);
			return 0;
		}

		try
		{
			var json = File.ReadAllText(_persistPath);
			var data = JsonSerializer.Deserialize<List<PersistedOrchestration>>(json, new JsonSerializerOptions
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase
			}) ?? [];

			var loaded = 0;
			foreach (var item in data)
			{
				if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
				{
					_logger?.LogWarning("Skipping missing orchestration file: {Path}", item.Path);
					continue;
				}

				try
				{
					Register(item.Path, item.McpPath, persist: false);
					loaded++;
				}
				catch (Exception ex)
				{
					_logger?.LogWarning(ex, "Failed to load orchestration from {Path}", item.Path);
				}
			}

			_logger?.LogInformation("Loaded {Count} orchestrations from {Path}", loaded, _persistPath);
			return loaded;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Failed to load orchestrations from disk");
			return 0;
		}
	}

	private static string GenerateId(string name, string path)
	{
		// Use name + hash of path for uniqueness
		var hash = path.GetHashCode().ToString("x8");
		return $"{SanitizeId(name)}-{hash[..4]}";
	}

	private static string SanitizeId(string name)
	{
		return new string(name
			.ToLowerInvariant()
			.Select(c => char.IsLetterOrDigit(c) ? c : '-')
			.ToArray())
			.Trim('-');
	}

	private static string SanitizePath(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		return new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
	}
}

/// <summary>
/// Minimal data needed to reload an orchestration.
/// </summary>
public class PersistedOrchestration
{
	public string Path { get; set; } = "";
	public string? McpPath { get; set; }
}

public class OrchestrationEntry
{
	public required string Id { get; init; }
	public required string Path { get; init; }
	public string? McpPath { get; init; }
	public required Orchestration Orchestration { get; init; }
	public DateTimeOffset RegisteredAt { get; init; }
}

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
