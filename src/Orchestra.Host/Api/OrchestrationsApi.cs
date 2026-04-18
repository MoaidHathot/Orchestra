using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Orchestra.Host.Profiles;
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
		group.MapGet("", (OrchestrationRegistry registry, TriggerManager triggerManager, OrchestrationTagStore tagStore, ILoggerFactory loggerFactory) =>
		{
			var logger = loggerFactory.CreateLogger(typeof(OrchestrationsApi));
			var result = new List<object>();
			foreach (var o in registry.GetAll())
			{
				try
				{
					var trigger = triggerManager.GetTrigger(o.Id);
					var lastRun = trigger?.LastFireTime;
					var nextRun = trigger?.NextFireTime;
					var parameterNames = o.Orchestration.Steps.SelectMany(s => s.Parameters).Distinct().ToArray();
					var effectiveTags = tagStore.GetEffectiveTags(o.Id, o.Orchestration.Tags);

					result.Add(new
					{
						id = o.Id,
						path = o.Path,
						name = o.Orchestration.Name,
						description = o.Orchestration.Description,
						version = o.Orchestration.Version,
						contentHash = o.ContentHash,
						tags = effectiveTags,
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
								skillDirectories = ps?.SkillDirectories.Length > 0 ? ps.SkillDirectories : null,
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
						inputs = o.Orchestration.Inputs?.ToDictionary(
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
						variables = o.Orchestration.Variables.Count > 0 ? o.Orchestration.Variables : null,
						referencedEnvVars = ExtractReferencedEnvVars(o.Orchestration),
						trigger = FormatTriggerInfoWithWebhook(o.Orchestration.Trigger, trigger, parameterNames),
						triggerType = o.Orchestration.Trigger.Type.ToString(),
						enabled = trigger?.Config.Enabled ?? o.Orchestration.Trigger.Enabled,
						isActive = trigger?.Status.ToString() == "Running",
						lastExecutionTime = lastRun?.ToString("o"),
						lastExecutionStatus = trigger?.LastError is null ? "Success" : "Failed",
						nextExecutionTime = nextRun?.ToString("o"),
						runCount = trigger?.RunCount ?? 0,
						lastExecutionId = trigger?.LastExecutionId,
						hasInlineMcps = o.Orchestration.Mcps.Length > 0,
						mcps = o.Orchestration.Mcps.Select(m => new { name = m.Name, type = m.Type.ToString() }).ToArray(),
						models = o.Orchestration.Steps
							.OfType<PromptOrchestrationStep>()
							.Select(s => s.Model)
							.Where(m => !string.IsNullOrEmpty(m))
							.Distinct()
							.ToArray()
					});
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Failed to build orchestration data for {Id}, skipping", o.Id);
				}
			}

			return Results.Json(new { count = result.Count, orchestrations = result }, jsonOptions);
		});

		// GET /api/orchestrations/{id} - Get a specific orchestration
		group.MapGet("/{id}", async (string id, OrchestrationRegistry registry, IScheduler scheduler, TriggerManager triggerManager, OrchestrationTagStore tagStore) =>
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

			// Validate template expressions (parse-time only — no runtime context)
			var templateValidation = TemplateExpressionValidator.ValidateOrchestration(o);
			if (!templateValidation.IsValid)
			{
				var templateErrors = templateValidation.Errors.Select(e => e.Message).ToArray();
				validationErrors = validationErrors is not null
					? [.. validationErrors, .. templateErrors]
					: templateErrors;
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
					skillDirectories = ps?.SkillDirectories.Length > 0 ? ps.SkillDirectories : null,
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
				name = o.Name,
				description = o.Description,
				version = o.Version,
				contentHash = entry.ContentHash,
				tags = tagStore.GetEffectiveTags(entry.Id, o.Tags),
				versionCount,
				validationErrors,
				steps,
				layers,
				parameters = allParameters,
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
				variables = o.Variables.Count > 0 ? o.Variables : null,
				referencedEnvVars = ExtractReferencedEnvVars(o),
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

					var entry = registry.Register(path);
					added.Add(new
					{
						id = entry.Id,
						path = entry.Path,
						name = entry.Orchestration.Name
					});

				// Register trigger for this orchestration
				if (entry.Orchestration.Trigger.Enabled)
				{
					triggerManager.RegisterTrigger(
						entry.Path,
						entry.Orchestration.Trigger,
						null,
						TriggerSource.Json,
						entry.Id,
						entry.Orchestration);
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

				var entry = registry.RegisterFromJson(request.Json);

			// Register trigger for this orchestration
			if (entry.Orchestration.Trigger.Enabled)
			{
				triggerManager.RegisterTrigger(
					entry.Path,
					entry.Orchestration.Trigger,
					null,
					TriggerSource.Json,
					entry.Id,
					entry.Orchestration);
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

			var existingTrigger = triggerManager.GetTrigger(id);
			if (existingTrigger == null)
			{
				// Register the trigger with enabled = true
				var enabledTrigger = TriggerManager.CloneTriggerConfigWithEnabled(entry.Orchestration.Trigger, true);
				triggerManager.RegisterTrigger(
					entry.Path,
					enabledTrigger,
					null,
					TriggerSource.Json,
					entry.Id,
					entry.Orchestration);
			}
			else
			{
				triggerManager.SetTriggerEnabled(id, true);
			}
			return Results.Ok(new { id, enabled = true });
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

				var files = OrchestrationParser.GetOrchestrationFiles(request.Directory);
				var orchestrations = new List<object>();

				foreach (var file in files.OrderBy(f => f))
				{
					// Skip the global MCP config file — it's not an orchestration
					if (Path.GetFileName(file).Equals(OrchestraConfigLoader.McpConfigFileName, StringComparison.OrdinalIgnoreCase))
						continue;

					try
					{
						var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);

						orchestrations.Add(new
						{
							path = file,
							fileName = Path.GetFileName(file),
							name = orchestration.Name,
							description = orchestration.Description,
							version = orchestration.Version,
							stepCount = orchestration.Steps.Length,
							trigger = FormatTriggerInfo(orchestration.Trigger),
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
							valid = false,
							error = ex.Message
						});
					}
				}

				return Results.Json(new
				{
					directory = request.Directory,
					count = orchestrations.Count,
					orchestrations
				}, jsonOptions);
			}
			catch (Exception ex)
			{
				return ProblemDetailsHelpers.BadRequest(ex.Message);
			}
		});

		// POST /api/orchestrations/export - Export orchestrations to a directory
		group.MapPost("/export", (ExportOrchestrationsRequest request, OrchestrationRegistry registry) =>
		{
			try
			{
				if (string.IsNullOrWhiteSpace(request.Directory))
					return ProblemDetailsHelpers.BadRequest("Directory path is required.");

				Directory.CreateDirectory(request.Directory);

				var entries = request.OrchestrationIds is { Length: > 0 }
					? registry.GetAll().Where(e => request.OrchestrationIds.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).ToArray()
					: registry.GetAll().ToArray();

				var exported = new List<object>();
				var skipped = new List<object>();
				var errors = new List<object>();

				foreach (var entry in entries)
				{
					var sanitizedName = new string(entry.Orchestration.Name
						.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c)
						.ToArray()).Trim('-');
					var fileName = $"{sanitizedName}.json";
					var filePath = Path.Combine(request.Directory, fileName);

					if (File.Exists(filePath) && !request.OverwriteExisting)
					{
						skipped.Add(new { id = entry.Id, name = entry.Orchestration.Name, reason = "File already exists" });
						continue;
					}

					try
					{
						// Read the managed copy (the source of truth)
						if (!File.Exists(entry.Path))
						{
							errors.Add(new { id = entry.Id, name = entry.Orchestration.Name, error = "Orchestration source file not found" });
							continue;
						}

						var json = File.ReadAllText(entry.Path);
						File.WriteAllText(filePath, json);
						exported.Add(new { id = entry.Id, name = entry.Orchestration.Name, path = filePath });
					}
					catch (Exception ex)
					{
						errors.Add(new { id = entry.Id, name = entry.Orchestration.Name, error = ex.Message });
					}
				}

				return Results.Json(new
				{
					exportedCount = exported.Count,
					exported,
					skipped,
					errors
				}, jsonOptions);
			}
			catch (Exception ex)
			{
				return ProblemDetailsHelpers.BadRequest(ex.Message);
			}
		});

		return endpoints;
	}

	private static object? FormatTriggerInfo(TriggerConfig config)
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
			ManualTriggerConfig m => new
			{
				type = "manual",
				enabled = m.Enabled
			},
			_ => null
		};
	}

	private static object? FormatTriggerInfoWithWebhook(TriggerConfig config, TriggerRegistration? registration, string[] parameters)
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
			ManualTriggerConfig m => new
			{
				type = "manual",
				enabled = m.Enabled
			},
			_ => null
		};
	}

	private static readonly Regex EnvExpressionPattern = new(@"\{\{env\.([^}]+)\}\}", RegexOptions.Compiled);

	/// <summary>
	/// Extracts all referenced environment variable names from an orchestration's template expressions.
	/// Scans variables, step prompts, URLs, commands, headers, bodies, and MCP endpoints.
	/// </summary>
	private static string[] ExtractReferencedEnvVars(Orchestration orchestration)
	{
		var envVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void Scan(string? text)
		{
			if (string.IsNullOrEmpty(text)) return;
			foreach (Match m in EnvExpressionPattern.Matches(text))
				envVars.Add(m.Groups[1].Value);
		}

		// Scan orchestration-level variables
		foreach (var v in orchestration.Variables.Values)
			Scan(v);

		// Scan MCP endpoints/commands
		foreach (var mcp in orchestration.Mcps)
		{
			if (mcp is RemoteMcp remote) Scan(remote.Endpoint);
			if (mcp is LocalMcp local)
			{
				Scan(local.Command);
				foreach (var arg in local.Arguments) Scan(arg);
			}
		}

		// Scan steps
		foreach (var step in orchestration.Steps)
		{
			if (step is PromptOrchestrationStep ps)
			{
				Scan(ps.SystemPrompt);
				Scan(ps.UserPrompt);
				Scan(ps.InputHandlerPrompt);
				Scan(ps.OutputHandlerPrompt);
				Scan(ps.Model);
				foreach (var mcp in ps.Mcps)
				{
					if (mcp is RemoteMcp remote) Scan(remote.Endpoint);
					if (mcp is LocalMcp local)
					{
						Scan(local.Command);
						foreach (var arg in local.Arguments) Scan(arg);
					}
				}
			}
			else if (step is HttpOrchestrationStep hs)
			{
				Scan(hs.Url);
				Scan(hs.Body);
				foreach (var h in hs.Headers.Values) Scan(h);
			}
			else if (step is CommandOrchestrationStep cs)
			{
				Scan(cs.Command);
				foreach (var arg in cs.Arguments) Scan(arg);
				foreach (var env in cs.Environment.Values) Scan(env);
			}
			else if (step is TransformOrchestrationStep ts)
			{
				Scan(ts.Template);
			}
		}

		return envVars.Count > 0 ? [.. envVars.Order()] : [];
	}
}

// Request DTOs
public record AddOrchestrationsRequest(string[]? Paths);
public record AddJsonRequest(string Json);
public record FolderScanRequest(string? Directory);
public record ExportOrchestrationsRequest(string? Directory, string[]? OrchestrationIds, bool OverwriteExisting = false);
