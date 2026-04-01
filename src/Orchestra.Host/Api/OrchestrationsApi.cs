using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for orchestration management.
/// </summary>
public static class OrchestrationsApi
{
	/// <summary>
	/// Maps orchestration management endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapOrchestrationsApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		var group = endpoints.MapGroup("/api/orchestrations");

		// GET /api/orchestrations - List all registered orchestrations
		group.MapGet("", async (OrchestrationRegistry registry, TriggerManager triggerManager) =>
		{
			var orchestrations = await Task.WhenAll(registry.GetAll().Select(async o =>
			{
				var trigger = triggerManager.GetTrigger(o.Id);
				var lastRun = trigger?.LastFireTime;
				var nextRun = trigger?.NextFireTime;
				var parameterNames = o.Orchestration.Steps.SelectMany(s => s.Parameters).Distinct().ToArray();

				// Get version count if version store is available
				int? versionCount = null;
				if (registry.VersionStore is not null)
				{
					var versions = await registry.VersionStore.ListVersionsAsync(o.Id);
					versionCount = versions.Count;
				}

				return new
				{
					id = o.Id,
					path = o.Path,
					mcpPath = o.McpPath,
					name = o.Orchestration.Name,
					description = o.Orchestration.Description,
					version = o.Orchestration.Version,
					contentHash = o.ContentHash,
					versionCount,
					stepCount = o.Orchestration.Steps.Length,
					steps = o.Orchestration.Steps.Select(s =>
					{
						var ps = s as PromptOrchestrationStep;
						var hs = s as HttpOrchestrationStep;
						var cs = s as CommandOrchestrationStep;
						var ts = s as TransformOrchestrationStep;
					return new
					{
						name = s.Name,
						type = s.Type.ToString(),
						dependsOn = s.DependsOn,
						parameters = s.Parameters,
						enabled = s.Enabled,
						model = ps?.Model,
						mcps = ps?.Mcps.Select(m => new
						{
							name = m.Name,
							type = m.Type
						}).ToArray() ?? Array.Empty<object>(),
						loopConfig = ps?.Loop is not null ? new
						{
							target = ps.Loop.Target,
							maxIterations = ps.Loop.MaxIterations,
							exitPattern = ps.Loop.ExitPattern
						} : null,
						subagents = ps?.Subagents.Length > 0 ? ps.Subagents.Select(sa => new
						{
							name = sa.Name,
							displayName = sa.DisplayName,
							description = sa.Description
						}).ToArray() : null,
						// Http step fields
						method = hs?.Method,
						url = hs?.Url,
						// Command step fields
						command = cs?.Command,
						arguments = cs?.Arguments,
						// Transform step fields
						template = ts?.Template
					};
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
					mcps = o.Orchestration.Mcps.Select(m => m.Name).ToArray(),
				models = o.Orchestration.Steps
						.OfType<PromptOrchestrationStep>()
						.Select(s => s.Model)
						.Where(m => !string.IsNullOrEmpty(m))
						.Distinct()
						.ToArray()
				};
			}));

			return Results.Json(new { count = orchestrations.Length, orchestrations }, jsonOptions);
		});

		// GET /api/orchestrations/{id} - Get a specific orchestration
		group.MapGet("/{id}", async (string id, OrchestrationRegistry registry, IScheduler scheduler, TriggerManager triggerManager) =>
		{
			var entry = registry.Get(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			var o = entry.Orchestration;

			// Try to compute the schedule; if the orchestration has invalid dependencies
			// (e.g. a step references a non-existent step), we still want to return the
			// orchestration data so the user can view and fix it.
			Schedule? schedule = null;
			string[]? validationErrors = null;
			try
			{
				schedule = scheduler.Schedule(o);
			}
			catch (InvalidOperationException ex)
			{
				validationErrors = [ex.Message];
			}

			var steps = o.Steps.Select(s =>
			{
				var ps = s as PromptOrchestrationStep;
				var hs = s as HttpOrchestrationStep;
				var cs = s as CommandOrchestrationStep;
				var ts = s as TransformOrchestrationStep;
				return new
				{
					name = s.Name,
					type = s.Type.ToString(),
					dependsOn = s.DependsOn,
					parameters = s.Parameters,
					enabled = s.Enabled,
					model = ps?.Model,
					reasoningLevel = ps?.ReasoningLevel?.ToString(),
					systemPromptMode = ps?.SystemPromptMode?.ToString(),
					systemPrompt = ps?.SystemPrompt,
					userPrompt = ps?.UserPrompt,
					inputHandlerPrompt = ps?.InputHandlerPrompt,
					outputHandlerPrompt = ps?.OutputHandlerPrompt,
					loop = ps?.Loop is not null ? new
					{
						target = ps.Loop.Target,
						maxIterations = ps.Loop.MaxIterations,
						exitPattern = ps.Loop.ExitPattern
					} : null,
					mcps = ps?.Mcps.Select(m => new
					{
						name = m.Name,
						type = m.Type.ToString(),
						endpoint = (m as RemoteMcp)?.Endpoint,
						command = (m as LocalMcp)?.Command,
						arguments = (m as LocalMcp)?.Arguments,
						workingDirectory = (m as LocalMcp)?.WorkingDirectory
					}).ToArray(),
					subagents = ps?.Subagents.Length > 0 ? ps.Subagents.Select(sa => new
					{
						name = sa.Name,
						displayName = sa.DisplayName,
						description = sa.Description,
						tools = sa.Tools,
						mcps = sa.Mcps.Select(m => m.Name).ToArray(),
						infer = sa.Infer
					}).ToArray() : null,
					// Http step fields
					method = hs?.Method,
					url = hs?.Url,
					headers = hs?.Headers.Count > 0 ? hs.Headers : null,
					body = hs?.Body,
					contentType = hs?.ContentType,
					// Command step fields
					command = cs?.Command,
					arguments = cs?.Arguments.Length > 0 ? cs.Arguments : null,
					workingDirectory = cs?.WorkingDirectory,
					// Transform step fields
					template = ts?.Template
				};
			}).ToArray();

			var layers = schedule?.Entries.Select((entry, index) => new
			{
				layer = index + 1,
				steps = entry.Steps.Select(s => s.Name).ToArray()
			}).ToArray() ?? [];

			// Look up the trigger registration to get the webhook URL if applicable
			var triggerRegistration = triggerManager.GetTrigger(entry.Id);
			var allParameters = o.Steps.SelectMany(s => s.Parameters).Distinct().ToArray();

			// Get version count if version store is available
			int? versionCount = null;
			if (registry.VersionStore is not null)
			{
				var versions = await registry.VersionStore.ListVersionsAsync(entry.Id);
				versionCount = versions.Count;
			}

			return Results.Json(new
			{
				id = entry.Id,
				path = entry.Path,
				mcpPath = entry.McpPath,
				name = o.Name,
				description = o.Description,
				version = o.Version,
				contentHash = entry.ContentHash,
				versionCount,
				validationErrors,
				steps,
				layers,
				parameters = allParameters,
				trigger = FormatTriggerInfoWithWebhook(o.Trigger, triggerRegistration, allParameters),
				mcps = o.Mcps.Select(m => new
				{
					name = m.Name,
					type = m.Type.ToString(),
					endpoint = (m as RemoteMcp)?.Endpoint,
					command = (m as LocalMcp)?.Command,
					arguments = (m as LocalMcp)?.Arguments,
					workingDirectory = (m as LocalMcp)?.WorkingDirectory
				}).ToArray()
			}, jsonOptions);
		});

		// POST /api/orchestrations - Add orchestrations from files
		group.MapPost("", (AddOrchestrationsRequest request, OrchestrationRegistry registry, TriggerManager triggerManager) =>
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
							entry.Id);
					}
				}
				catch (Exception ex)
				{
					errors.Add(new { path, error = ex.Message });
				}
			}

			return Results.Json(new { addedCount = added.Count, added, errors }, jsonOptions);
		});

		// POST /api/orchestrations/json - Add orchestration from pasted JSON
		group.MapPost("/json", (AddJsonRequest request, OrchestrationRegistry registry, TriggerManager triggerManager) =>
		{
			try
			{
				if (string.IsNullOrWhiteSpace(request.Json))
					return ProblemDetailsHelpers.BadRequest("JSON content is required.");

				// Parse the MCPs if provided
				Mcp[] mcps = [];
				if (!string.IsNullOrWhiteSpace(request.McpJson))
				{
					mcps = OrchestrationParser.ParseMcps(request.McpJson);
				}

				var orchestration = OrchestrationParser.ParseOrchestration(request.Json, mcps);

				// Save to temp file so we have a path
				var tempDir = Path.Combine(Path.GetTempPath(), "orchestra-host");
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
				return ProblemDetailsHelpers.BadRequest(ex.Message);
			}
		});

		// DELETE /api/orchestrations/{id} - Remove an orchestration
		group.MapDelete("/{id}", (string id, OrchestrationRegistry registry, TriggerManager triggerManager) =>
		{
			if (registry.Remove(id))
			{
				triggerManager.RemoveTrigger(id);
				return Results.Ok(new { removed = true, id });
			}
			return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");
		});

		// POST /api/orchestrations/{id}/enable - Enable an orchestration's trigger
		group.MapPost("/{id}/enable", (string id, OrchestrationRegistry registry, TriggerManager triggerManager) =>
		{
			var entry = registry.Get(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

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

			return ProblemDetailsHelpers.BadRequest($"Orchestration '{id}' has no trigger defined.");
		});

		// POST /api/orchestrations/{id}/disable - Disable an orchestration's trigger
		group.MapPost("/{id}/disable", (string id, OrchestrationRegistry registry, TriggerManager triggerManager) =>
		{
			var entry = registry.Get(id);
			if (entry is null)
				return ProblemDetailsHelpers.NotFound($"Orchestration '{id}' not found.");

			triggerManager.SetTriggerEnabled(id, false);
			return Results.Ok(new { id, enabled = false });
		});

		// POST /api/orchestrations/scan - Scan folder for orchestration files
		group.MapPost("/scan", (FolderScanRequest request) =>
		{
			try
			{
				if (string.IsNullOrWhiteSpace(request.Directory))
					return ProblemDetailsHelpers.BadRequest("Directory path is required.");

				if (!Directory.Exists(request.Directory))
					return ProblemDetailsHelpers.BadRequest($"Directory not found: {request.Directory}");

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
				return ProblemDetailsHelpers.BadRequest(ex.Message);
			}
		});

		return endpoints;
	}

	private static object? FormatTriggerInfo(TriggerConfig? config)
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

	private static object? FormatTriggerInfoWithWebhook(TriggerConfig? config, TriggerRegistration? registration, string[] parameters)
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
				webhookUrl = registration != null ? $"/api/webhooks/{registration.Id}" : null,
				expectedParameters = parameters,
				invocation = registration != null ? new
				{
					method = "POST",
					url = $"/api/webhooks/{registration.Id}",
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

	private static string SanitizePath(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var sanitized = new char[name.Length];
		for (var i = 0; i < name.Length; i++)
			sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
		return new string(sanitized);
	}
}

// Request DTOs
public record AddOrchestrationsRequest(string[]? Paths, string? McpPath);
public record AddJsonRequest(string Json, string? McpJson);
public record FolderScanRequest(string? Directory);
