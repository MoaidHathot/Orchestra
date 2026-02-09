using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Playground.Copilot.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
	options.SingleLine = true;
	options.IncludeScopes = false;
	options.TimestampFormat = "HH:mm:ss ";
	options.ColorBehavior = LoggerColorBehavior.Enabled;
});

// Register engine services as singletons (reporter is per-request, not registered here)
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();
builder.Services.AddSingleton<IScheduler, OrchestrationScheduler>();

var app = builder.Build();

// Ensure runs directory exists for execution history
var runsDir = Path.Combine(app.Environment.ContentRootPath, "runs");
Directory.CreateDirectory(runsDir);

app.UseStaticFiles();

var jsonOptions = new JsonSerializerOptions
{
	PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

// Track active executions for cancel support
var activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();

// ── POST /api/load ──────────────────────────────────────────────────────
// Loads orchestration + MCP files, returns metadata for the frontend
app.MapPost("/api/load", (LoadRequest request, IScheduler scheduler) =>
{
	try
	{
		if (!File.Exists(request.OrchestrationPath))
			return Results.BadRequest(new { error = $"Orchestration file not found: {request.OrchestrationPath}" });

		Mcp[] mcps = [];
		if (!string.IsNullOrWhiteSpace(request.McpPath))
		{
			if (!File.Exists(request.McpPath))
				return Results.BadRequest(new { error = $"MCP file not found: {request.McpPath}" });
			mcps = OrchestrationParser.ParseMcpFile(request.McpPath);
		}

		var orchestration = OrchestrationParser.ParseOrchestrationFile(request.OrchestrationPath, mcps);
		var schedule = scheduler.Schedule(orchestration);

		// Build step metadata for the frontend
		var steps = orchestration.Steps.Select(s =>
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
				mcps = ps?.Mcps.Select(m => new { name = m.Name, type = m.Type.ToString() }).ToArray(),
			};
		}).ToArray();

		// Build schedule layers
		var layers = schedule.Entries.Select((entry, index) => new
		{
			layer = index + 1,
			steps = entry.Steps.Select(s => s.Name).ToArray(),
		}).ToArray();

		// Collect all unique parameter names
		var allParameters = orchestration.Steps
			.SelectMany(s => s.Parameters)
			.Distinct()
			.ToArray();

		// MCP info
		var mcpInfo = mcps.Select(m => new
		{
			name = m.Name,
			type = m.Type.ToString(),
			endpoint = (m as RemoteMcp)?.Endpoint,
			command = (m as LocalMcp)?.Command,
			arguments = (m as LocalMcp)?.Arguments,
		}).ToArray();

		return Results.Json(new
		{
			name = orchestration.Name,
			description = orchestration.Description,
			steps,
			layers,
			parameters = allParameters,
			mcps = mcpInfo,
		}, jsonOptions);
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// ── POST /api/execute ───────────────────────────────────────────────────
// Starts orchestration execution, streams SSE events back to the client
app.MapPost("/api/execute", async (
	HttpContext httpContext,
	ExecuteRequest request,
	AgentBuilder agentBuilder,
	IScheduler scheduler,
	ILoggerFactory loggerFactory) =>
{
	try
	{
		// Validate inputs
		if (!File.Exists(request.OrchestrationPath))
		{
			httpContext.Response.StatusCode = 400;
			await httpContext.Response.WriteAsJsonAsync(new { error = $"Orchestration file not found: {request.OrchestrationPath}" });
			return;
		}

		Mcp[] mcps = [];
		if (!string.IsNullOrWhiteSpace(request.McpPath))
		{
			if (!File.Exists(request.McpPath))
			{
				httpContext.Response.StatusCode = 400;
				await httpContext.Response.WriteAsJsonAsync(new { error = $"MCP file not found: {request.McpPath}" });
				return;
			}
			mcps = OrchestrationParser.ParseMcpFile(request.McpPath);
		}

		var orchestration = OrchestrationParser.ParseOrchestrationFile(request.OrchestrationPath, mcps);

		// Set up SSE response
		httpContext.Response.ContentType = "text/event-stream";
		httpContext.Response.Headers.CacheControl = "no-cache";
		httpContext.Response.Headers.Connection = "keep-alive";
		await httpContext.Response.Body.FlushAsync();

		// Generate execution ID and register for cancel support
		var executionId = Guid.NewGuid().ToString("N")[..12];
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
		activeExecutions[executionId] = cts;

		// Send execution ID as the first SSE event
		await httpContext.Response.WriteAsync($"event: execution-started\n");
		var executionStartedJson = JsonSerializer.Serialize(new { executionId }, jsonOptions);
	await httpContext.Response.WriteAsync($"data: {executionStartedJson}\n\n");
		await httpContext.Response.Body.FlushAsync();

		// Create a per-request reporter
		var reporter = new WebOrchestrationReporter();

		// Create a per-request executor
		var logger = loggerFactory.CreateLogger<OrchestrationExecutor>();
		var executor = new OrchestrationExecutor(scheduler, agentBuilder, reporter, logger);

		var cancellationToken = cts.Token;

		// Start execution on a background task
		var executionTask = Task.Run(async () =>
		{
			try
			{
				var result = await executor.ExecuteAsync(
					orchestration,
					request.Parameters,
					cancellationToken);

				// Send full step results as step-output events
				foreach (var (stepName, stepResult) in result.StepResults)
				{
					if (stepResult.Status == ExecutionStatus.Succeeded)
					{
						reporter.ReportStepOutput(stepName, stepResult.Content);
					}
				}

				reporter.ReportOrchestrationDone(result);

				// Persist execution history
				try
				{
					var historyEntry = new
					{
						id = executionId,
						orchestrationName = orchestration.Name,
						orchestrationDescription = orchestration.Description,
						orchestrationPath = request.OrchestrationPath,
						startedAt = DateTime.UtcNow.ToString("o"),
						status = result.Status.ToString(),
						stepCount = orchestration.Steps.Length,
						results = result.StepResults.ToDictionary(
							kv => kv.Key,
							kv => new
							{
								status = kv.Value.Status.ToString(),
								content = kv.Value.Content,
								error = kv.Value.ErrorMessage,
							}),
					};
					var historyJson = JsonSerializer.Serialize(historyEntry, jsonOptions);
					var historyPath = Path.Combine(runsDir, $"{executionId}.json");
					await File.WriteAllTextAsync(historyPath, historyJson);
				}
				catch
				{
					// Best-effort history persistence — don't fail the execution
				}
			}
			catch (OperationCanceledException)
			{
				// Cancelled — send cancel event
				reporter.ReportStepError("orchestration", "Execution was cancelled.");
			}
			catch (Exception ex)
			{
				reporter.ReportStepError("orchestration", ex.Message);
			}
			finally
			{
				reporter.Complete();
			}
		}, cancellationToken);

		// Stream SSE events to the client
		try
		{
			await foreach (var evt in reporter.Events.ReadAllAsync(cancellationToken))
			{
				await httpContext.Response.WriteAsync($"event: {evt.Type}\n", cancellationToken);
				await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", cancellationToken);
				await httpContext.Response.Body.FlushAsync(cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			// Client disconnected or cancelled — normal
		}

		await executionTask;

		// Clean up
		activeExecutions.TryRemove(executionId, out _);
	}
	catch (Exception ex)
	{
		if (!httpContext.Response.HasStarted)
		{
			httpContext.Response.StatusCode = 500;
			await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message });
		}
	}
});

// ── POST /api/cancel/{executionId} ──────────────────────────────────────
// Cancels a running orchestration execution
app.MapPost("/api/cancel/{executionId}", (string executionId) =>
{
	if (activeExecutions.TryRemove(executionId, out var cts))
	{
		cts.Cancel();
		return Results.Ok(new { cancelled = true, executionId });
	}

	return Results.NotFound(new { error = $"No active execution with ID '{executionId}'" });
});

// ── POST /api/browse ────────────────────────────────────────────────────
// Lists orchestration/MCP JSON files in a given directory
app.MapPost("/api/browse", (BrowseRequest request) =>
{
	try
	{
		var directory = request.Directory?.Trim();
		if (string.IsNullOrEmpty(directory))
			return Results.BadRequest(new { error = "Directory path is required." });

		if (!Directory.Exists(directory))
			return Results.BadRequest(new { error = $"Directory not found: {directory}" });

		var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
			.Select(f => new
			{
				name = Path.GetFileName(f),
				path = f,
				size = new FileInfo(f).Length,
			})
			.OrderBy(f => f.name)
			.ToArray();

		return Results.Json(new { directory, files }, jsonOptions);
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// ── GET /api/history ─────────────────────────────────────────────────────
// Lists past execution runs (lightweight — no full step content)
app.MapGet("/api/history", () =>
{
	try
	{
		var files = Directory.GetFiles(runsDir, "*.json", SearchOption.TopDirectoryOnly);
		var runs = new List<(string? startedAt, object data)>();

		foreach (var file in files)
		{
			try
			{
				var json = File.ReadAllText(file);
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				var startedAt = root.TryGetProperty("startedAt", out var startProp) ? startProp.GetString() : null;
				runs.Add((startedAt, new
				{
					id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : Path.GetFileNameWithoutExtension(file),
					orchestrationName = root.TryGetProperty("orchestrationName", out var nameProp) ? nameProp.GetString() : "Unknown",
					orchestrationDescription = root.TryGetProperty("orchestrationDescription", out var descProp) ? descProp.GetString() : null,
					startedAt,
					status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "Unknown",
					stepCount = root.TryGetProperty("stepCount", out var stepProp) ? stepProp.GetInt32() : 0,
				}));
			}
			catch
			{
				// Skip malformed files
			}
		}

		// Sort by startedAt descending (newest first)
		var sorted = runs
			.OrderByDescending(r => DateTime.TryParse(r.startedAt, out var dt) ? dt : DateTime.MinValue)
			.Select(r => r.data)
			.ToArray();

		return Results.Json(new { runs = sorted }, jsonOptions);
	}
	catch (Exception ex)
	{
		return Results.Problem(ex.Message);
	}
});

// ── GET /api/history/{runId} ────────────────────────────────────────────
// Returns the full execution data for a specific run
app.MapGet("/api/history/{runId}", (string runId) =>
{
	var filePath = Path.Combine(runsDir, $"{runId}.json");
	if (!File.Exists(filePath))
		return Results.NotFound(new { error = $"Run '{runId}' not found." });

	try
	{
		var json = File.ReadAllText(filePath);
		return Results.Content(json, "application/json");
	}
	catch (Exception ex)
	{
		return Results.Problem(ex.Message);
	}
});

// ── DELETE /api/history/{runId} ─────────────────────────────────────────
// Deletes a specific execution run
app.MapDelete("/api/history/{runId}", (string runId) =>
{
	var filePath = Path.Combine(runsDir, $"{runId}.json");
	if (!File.Exists(filePath))
		return Results.NotFound(new { error = $"Run '{runId}' not found." });

	try
	{
		File.Delete(filePath);
		return Results.Ok(new { deleted = true, runId });
	}
	catch (Exception ex)
	{
		return Results.Problem(ex.Message);
	}
});

// ── Fallback: serve index.html ──────────────────────────────────────────
app.MapFallbackToFile("index.html");

app.Run();

// ── Request DTOs ────────────────────────────────────────────────────────

record LoadRequest(string OrchestrationPath, string? McpPath);
record ExecuteRequest(string OrchestrationPath, string? McpPath, Dictionary<string, string>? Parameters);
record BrowseRequest(string? Directory);
