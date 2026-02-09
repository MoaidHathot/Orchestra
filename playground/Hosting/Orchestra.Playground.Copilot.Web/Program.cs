using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
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

		// Compute schedule for history persistence (before execution starts)
		var schedule = scheduler.Schedule(orchestration);

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
						steps = orchestration.Steps.Select(s =>
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
						}).ToArray(),
						layers = schedule.Entries.Select((entry, index) => new
						{
							layer = index + 1,
							steps = entry.Steps.Select(s => s.Name).ToArray(),
						}).ToArray(),
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

		var dirInfo = new DirectoryInfo(directory);

		var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
			.Select(f => new
			{
				name = Path.GetFileName(f),
				path = f,
				size = new FileInfo(f).Length,
			})
			.OrderBy(f => f.name)
			.ToArray();

		var subdirectories = dirInfo.GetDirectories()
			.Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
			.Select(d => new { name = d.Name, path = d.FullName })
			.OrderBy(d => d.name)
			.ToArray();

		var parent = dirInfo.Parent?.FullName;

		return Results.Json(new { directory = dirInfo.FullName, parent, subdirectories, files }, jsonOptions);
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// ── POST /api/browse-dialog ─────────────────────────────────────────────
// Opens a native OS folder-picker dialog and returns the selected path
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

// ── POST /api/upload ─────────────────────────────────────────────────────
// Accepts an uploaded JSON file, saves it to a temp folder, returns server-side path
app.MapPost("/api/upload", async (HttpRequest request) =>
{
	try
	{
		if (!request.HasFormContentType)
			return Results.BadRequest(new { error = "Expected multipart/form-data." });

		var form = await request.ReadFormAsync();
		var file = form.Files.GetFile("file");
		if (file is null || file.Length == 0)
			return Results.BadRequest(new { error = "No file provided." });

		var fileName = Path.GetFileName(file.FileName);
		if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			return Results.BadRequest(new { error = "Only .json files are accepted." });

		var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
		System.IO.Directory.CreateDirectory(uploadsDir);

		var destPath = Path.Combine(uploadsDir, fileName);
		await using var stream = new FileStream(destPath, FileMode.Create);
		await file.CopyToAsync(stream);

		return Results.Json(new { path = destPath, name = fileName, size = file.Length });
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

// ── GET /api/runs-dir ────────────────────────────────────────────────────
// Returns the server-side path where execution history JSON files are saved
app.MapGet("/api/runs-dir", () => Results.Json(new { path = runsDir }));

// ── POST /api/validate ──────────────────────────────────────────────────
// Validates orchestration + MCP files, returns structured validation results
app.MapPost("/api/validate", (LoadRequest request) =>
{
	var errors = new List<object>();
	var warnings = new List<object>();

	try
	{
		// 1. File existence checks
		if (string.IsNullOrWhiteSpace(request.OrchestrationPath))
		{
			errors.Add(new { field = "orchestrationPath", message = "Orchestration path is required." });
			return Results.Json(new { valid = false, errors, warnings }, jsonOptions);
		}

		if (!File.Exists(request.OrchestrationPath))
		{
			errors.Add(new { field = "orchestrationPath", message = $"File not found: {request.OrchestrationPath}" });
			return Results.Json(new { valid = false, errors, warnings }, jsonOptions);
		}

		// 2. Parse MCP file (if provided)
		Mcp[] mcps = [];
		if (!string.IsNullOrWhiteSpace(request.McpPath))
		{
			if (!File.Exists(request.McpPath))
			{
				errors.Add(new { field = "mcpPath", message = $"MCP file not found: {request.McpPath}" });
				return Results.Json(new { valid = false, errors, warnings }, jsonOptions);
			}

			try
			{
				mcps = OrchestrationParser.ParseMcpFile(request.McpPath);
			}
			catch (Exception ex)
			{
				errors.Add(new { field = "mcpPath", message = $"MCP parse error: {ex.Message}" });
				return Results.Json(new { valid = false, errors, warnings }, jsonOptions);
			}
		}

		// 3. Parse orchestration JSON
		Orchestration orchestration;
		try
		{
			orchestration = OrchestrationParser.ParseOrchestrationFile(request.OrchestrationPath, mcps);
		}
		catch (Exception ex)
		{
			errors.Add(new { field = "orchestration", message = $"Parse error: {ex.Message}" });
			return Results.Json(new { valid = false, errors, warnings }, jsonOptions);
		}

		// 4. Empty required fields
		if (string.IsNullOrWhiteSpace(orchestration.Name))
			errors.Add(new { field = "name", message = "Orchestration name is empty." });

		if (string.IsNullOrWhiteSpace(orchestration.Description))
			warnings.Add(new { field = "description", message = "Orchestration description is empty." });

		if (orchestration.Steps.Length == 0)
		{
			errors.Add(new { field = "steps", message = "Orchestration has no steps." });
			return Results.Json(new { valid = errors.Count == 0, errors, warnings }, jsonOptions);
		}

		var stepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var duplicateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var step in orchestration.Steps)
		{
			var prefix = $"Step '{step.Name}'";

			// 5. Empty step name
			if (string.IsNullOrWhiteSpace(step.Name))
			{
				errors.Add(new { field = "step.name", message = "A step has an empty name." });
				continue;
			}

			// 6. Duplicate step names
			if (!stepNames.Add(step.Name))
			{
				if (duplicateNames.Add(step.Name))
					errors.Add(new { field = "step.name", message = $"{prefix}: Duplicate step name." });
				continue;
			}

			// 7. Prompt step validations
			if (step is PromptOrchestrationStep ps)
			{
				if (string.IsNullOrWhiteSpace(ps.Model))
					errors.Add(new { field = "step.model", message = $"{prefix}: Model is empty." });

				if (string.IsNullOrWhiteSpace(ps.SystemPrompt))
					errors.Add(new { field = "step.systemPrompt", message = $"{prefix}: System prompt is empty." });

				if (string.IsNullOrWhiteSpace(ps.UserPrompt))
					errors.Add(new { field = "step.userPrompt", message = $"{prefix}: User prompt is empty." });

				// 8. Check for {{param}} references in prompts vs declared parameters
				var allPromptText = string.Join(" ", new[]
				{
					ps.SystemPrompt, ps.UserPrompt, ps.InputHandlerPrompt, ps.OutputHandlerPrompt
				}.Where(t => !string.IsNullOrEmpty(t)));

				var referencedParams = System.Text.RegularExpressions.Regex.Matches(allPromptText, @"\{\{(\w+)\}\}")
					.Select(m => m.Groups[1].Value)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToHashSet(StringComparer.OrdinalIgnoreCase);

				var declaredParams = new HashSet<string>(step.Parameters, StringComparer.OrdinalIgnoreCase);

				foreach (var referenced in referencedParams)
				{
					if (!declaredParams.Contains(referenced))
						warnings.Add(new { field = "step.parameters", message = $"{prefix}: Template '{{{{{referenced}}}}}' is used in prompts but not declared in parameters." });
				}

				foreach (var declared in declaredParams)
				{
					if (!referencedParams.Contains(declared))
						warnings.Add(new { field = "step.parameters", message = $"{prefix}: Parameter '{declared}' is declared but not referenced in any prompt." });
				}
			}

			// 9. Self-dependency
			if (step.DependsOn.Any(d => string.Equals(d, step.Name, StringComparison.OrdinalIgnoreCase)))
				errors.Add(new { field = "step.dependsOn", message = $"{prefix}: Step depends on itself." });
		}

		// 10. Missing dependency references
		foreach (var step in orchestration.Steps)
		{
			foreach (var dep in step.DependsOn)
			{
				if (!stepNames.Contains(dep))
					errors.Add(new { field = "step.dependsOn", message = $"Step '{step.Name}': Depends on '{dep}' which does not exist." });
			}
		}

		// 11. Dependency cycle detection (Kahn's algorithm)
		var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

		foreach (var step in orchestration.Steps)
		{
			if (!inDegree.ContainsKey(step.Name)) inDegree[step.Name] = 0;
			if (!adjacency.ContainsKey(step.Name)) adjacency[step.Name] = [];

			foreach (var dep in step.DependsOn)
			{
				if (stepNames.Contains(dep))
				{
					if (!adjacency.ContainsKey(dep)) adjacency[dep] = [];
					adjacency[dep].Add(step.Name);
					inDegree[step.Name] = inDegree.GetValueOrDefault(step.Name, 0) + 1;
				}
			}
		}

		var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
		var sorted = new List<string>();

		while (queue.Count > 0)
		{
			var node = queue.Dequeue();
			sorted.Add(node);
			foreach (var neighbor in adjacency.GetValueOrDefault(node, []))
			{
				inDegree[neighbor]--;
				if (inDegree[neighbor] == 0) queue.Enqueue(neighbor);
			}
		}

		if (sorted.Count < stepNames.Count)
		{
			var cycleSteps = stepNames.Except(sorted, StringComparer.OrdinalIgnoreCase).ToArray();
			errors.Add(new { field = "step.dependsOn", message = $"Dependency cycle detected involving: {string.Join(", ", cycleSteps)}" });
		}

		// 12. Warn about steps with no dependents (leaf steps) — informational only
		var allDependencies = orchestration.Steps.SelectMany(s => s.DependsOn).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var rootSteps = orchestration.Steps.Where(s => s.DependsOn.Length == 0).Select(s => s.Name).ToArray();
		if (rootSteps.Length == 0 && orchestration.Steps.Length > 0)
			warnings.Add(new { field = "steps", message = "No root steps found (all steps have dependencies). This may indicate a circular dependency." });

		return Results.Json(new { valid = errors.Count == 0, errors, warnings }, jsonOptions);
	}
	catch (Exception ex)
	{
		errors.Add(new { field = "general", message = ex.Message });
		return Results.Json(new { valid = false, errors, warnings }, jsonOptions);
	}
});

// ── POST /api/save ──────────────────────────────────────────────────────
// Saves orchestration JSON to disk
app.MapPost("/api/save", (SaveRequest request) =>
{
	try
	{
		if (string.IsNullOrWhiteSpace(request.Path))
			return Results.BadRequest(new { error = "File path is required." });

		if (request.Orchestration == null)
			return Results.BadRequest(new { error = "Orchestration data is required." });

		// Ensure the directory exists
		var dir = Path.GetDirectoryName(request.Path);
		if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
			Directory.CreateDirectory(dir);

		var json = JsonSerializer.Serialize(request.Orchestration, new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		});
		File.WriteAllText(request.Path, json);

		return Results.Ok(new { saved = true, path = request.Path });
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// ── GET /api/models ─────────────────────────────────────────────────────
// Returns a list of commonly available models for the editor dropdown
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
		new { id = "gemini-2.0-flash", name = "Gemini 2.0 Flash", provider = "Google" },
	};
	return Results.Json(new { models }, jsonOptions);
});

// ── POST /api/compare ────────────────────────────────────────────────────
// Runs the same orchestration multiple times with different model overrides
// Returns a JSON comparison of all runs (not SSE — runs sequentially, returns at the end)
app.MapPost("/api/compare", async (
	HttpContext httpContext,
	CompareRequest request,
	AgentBuilder agentBuilder,
	IScheduler scheduler,
	ILoggerFactory loggerFactory) =>
{
	try
	{
		// Validate inputs
		if (!File.Exists(request.OrchestrationPath))
			return Results.BadRequest(new { error = $"Orchestration file not found: {request.OrchestrationPath}" });

		if (request.Runs is not { Length: > 0 })
			return Results.BadRequest(new { error = "At least one comparison run is required." });

		if (request.Runs.Length > 10)
			return Results.BadRequest(new { error = "Maximum 10 comparison runs allowed." });

		Mcp[] mcps = [];
		if (!string.IsNullOrWhiteSpace(request.McpPath))
		{
			if (!File.Exists(request.McpPath))
				return Results.BadRequest(new { error = $"MCP file not found: {request.McpPath}" });
			mcps = OrchestrationParser.ParseMcpFile(request.McpPath);
		}

		// Read the raw orchestration JSON for model override manipulation
		var orchestrationJson = File.ReadAllText(request.OrchestrationPath);

		// Execute each comparison run sequentially
		var comparisonResults = new List<object>();
		var overallStartTime = DateTime.UtcNow;

		for (var runIndex = 0; runIndex < request.Runs.Length; runIndex++)
		{
			var run = request.Runs[runIndex];
			var runLabel = !string.IsNullOrWhiteSpace(run.Label)
				? run.Label
				: $"Run {runIndex + 1}";

			// Apply model overrides to the JSON
			var modifiedJson = orchestrationJson;
			if (run.ModelOverrides is { Count: > 0 })
			{
				using var doc = JsonDocument.Parse(orchestrationJson);
				using var memStream = new MemoryStream();
				using (var writer = new Utf8JsonWriter(memStream, new JsonWriterOptions { Indented = false }))
				{
					WriteWithModelOverrides(writer, doc.RootElement, run.ModelOverrides);
				}
				modifiedJson = System.Text.Encoding.UTF8.GetString(memStream.ToArray());
			}

			// Parse the modified orchestration
			var orchestration = OrchestrationParser.ParseOrchestration(modifiedJson, mcps);
			var schedule = scheduler.Schedule(orchestration);

			// Create a per-run reporter (we collect events but don't stream them)
			var reporter = new WebOrchestrationReporter();
			var logger = loggerFactory.CreateLogger<OrchestrationExecutor>();
			var executor = new OrchestrationExecutor(scheduler, agentBuilder, reporter, logger);

			var runStartTime = DateTime.UtcNow;
			OrchestrationResult? result = null;
			string? errorMessage = null;

			// Drain the reporter channel in a background task
			var events = new List<SseEvent>();
			var drainTask = Task.Run(async () =>
			{
				await foreach (var evt in reporter.Events.ReadAllAsync())
				{
					events.Add(evt);
				}
			});

			try
			{
				result = await executor.ExecuteAsync(
					orchestration,
					request.Parameters,
					httpContext.RequestAborted);

				// Send step outputs to reporter so they appear in events
				foreach (var (stepName, stepResult) in result.StepResults)
				{
					if (stepResult.Status == ExecutionStatus.Succeeded)
						reporter.ReportStepOutput(stepName, stepResult.Content);
				}
				reporter.ReportOrchestrationDone(result);
			}
			catch (OperationCanceledException)
			{
				errorMessage = "Execution was cancelled.";
			}
			catch (Exception ex)
			{
				errorMessage = ex.Message;
			}
			finally
			{
				reporter.Complete();
			}

			await drainTask;

			var runDuration = (DateTime.UtcNow - runStartTime).TotalSeconds;

			// Extract usage data from events
			var usageEvents = events
				.Where(e => e.Type == "usage")
				.Select(e =>
				{
					using var doc = JsonDocument.Parse(e.Data);
					var root = doc.RootElement;
					return new
					{
						stepName = root.TryGetProperty("stepName", out var sn) ? sn.GetString() : null,
						model = root.TryGetProperty("model", out var m) ? m.GetString() : null,
						inputTokens = root.TryGetProperty("inputTokens", out var it) ? it.GetDouble() : 0,
						outputTokens = root.TryGetProperty("outputTokens", out var ot) ? ot.GetDouble() : 0,
						cacheReadTokens = root.TryGetProperty("cacheReadTokens", out var crt) ? crt.GetDouble() : 0,
						cacheWriteTokens = root.TryGetProperty("cacheWriteTokens", out var cwt) ? cwt.GetDouble() : 0,
						cost = root.TryGetProperty("cost", out var c) ? c.GetDouble() : 0,
						duration = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0,
					};
				})
				.ToArray();

			var totalInputTokens = usageEvents.Sum(u => u.inputTokens);
			var totalOutputTokens = usageEvents.Sum(u => u.outputTokens);
			var totalCacheReadTokens = usageEvents.Sum(u => u.cacheReadTokens);
			var totalCacheWriteTokens = usageEvents.Sum(u => u.cacheWriteTokens);
			var totalCost = usageEvents.Sum(u => u.cost);

			comparisonResults.Add(new
			{
				runIndex,
				label = runLabel,
				modelOverrides = run.ModelOverrides ?? new Dictionary<string, string>(),
				status = result?.Status.ToString() ?? "Failed",
				error = errorMessage,
				durationSeconds = Math.Round(runDuration, 2),
				totalInputTokens,
				totalOutputTokens,
				totalCacheReadTokens,
				totalCacheWriteTokens,
				totalCost = Math.Round(totalCost, 6),
				stepResults = result != null
					? result.StepResults.ToDictionary(
						kv => kv.Key,
						kv => (object)new
						{
							status = kv.Value.Status.ToString(),
							content = kv.Value.Content,
							error = kv.Value.ErrorMessage,
						})
					: new Dictionary<string, object>(),
				perStepUsage = usageEvents,
			});
		}

		var overallDuration = (DateTime.UtcNow - overallStartTime).TotalSeconds;

		return Results.Json(new
		{
			comparedAt = DateTime.UtcNow.ToString("o"),
			orchestrationPath = request.OrchestrationPath,
			totalDurationSeconds = Math.Round(overallDuration, 2),
			runCount = request.Runs.Length,
			runs = comparisonResults,
		}, jsonOptions);
	}
	catch (Exception ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
});

// Helper: Rewrite orchestration JSON with model overrides applied to steps
static void WriteWithModelOverrides(Utf8JsonWriter writer, JsonElement element, Dictionary<string, string> modelOverrides)
{
	switch (element.ValueKind)
	{
		case JsonValueKind.Object:
			writer.WriteStartObject();
			// Check if this is a step object (has "name" and "model" properties)
			string? stepName = null;
			if (element.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
				stepName = nameProp.GetString();

			foreach (var property in element.EnumerateObject())
			{
				writer.WritePropertyName(property.Name);

				// Override model if this step is in the overrides
				if (property.Name == "model" && stepName != null &&
					modelOverrides.TryGetValue(stepName, out var overrideModel))
				{
					writer.WriteStringValue(overrideModel);
				}
				else
				{
					WriteWithModelOverrides(writer, property.Value, modelOverrides);
				}
			}
			writer.WriteEndObject();
			break;

		case JsonValueKind.Array:
			writer.WriteStartArray();
			foreach (var item in element.EnumerateArray())
			{
				WriteWithModelOverrides(writer, item, modelOverrides);
			}
			writer.WriteEndArray();
			break;

		default:
			element.WriteTo(writer);
			break;
	}
}

// ── Fallback: serve index.html ──────────────────────────────────────────
app.MapFallbackToFile("index.html");

app.Run();

// ── Request DTOs ────────────────────────────────────────────────────────

record LoadRequest(string OrchestrationPath, string? McpPath);
record ExecuteRequest(string OrchestrationPath, string? McpPath, Dictionary<string, string>? Parameters);
record BrowseRequest(string? Directory);
record SaveRequest(string Path, JsonElement? Orchestration);
record CompareRequest(string OrchestrationPath, string? McpPath, Dictionary<string, string>? Parameters, CompareRun[] Runs);
record CompareRun(string? Label, Dictionary<string, string>? ModelOverrides);
