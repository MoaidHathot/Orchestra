using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Playground.Copilot.Portal;

/// <summary>
/// Runtime state for a single registered trigger.
/// </summary>
public class TriggerRegistration
{
	public required string Id { get; init; }
	public required string OrchestrationPath { get; init; }
	public string? McpPath { get; init; }
	public required TriggerConfig Config { get; set; }
	public Dictionary<string, string>? Parameters { get; set; }

	// Runtime state
	public TriggerStatus Status { get; set; } = TriggerStatus.Idle;
	public DateTime? NextFireTime { get; set; }
	public DateTime? LastFireTime { get; set; }
	public int RunCount { get; set; }
	public string? LastError { get; set; }
	public string? ActiveExecutionId { get; set; }
	public string? LastExecutionId { get; set; }
	public string? OrchestrationName { get; set; }
	public string? OrchestrationDescription { get; set; }
	public string? OrchestrationVersion { get; set; }

	/// <summary>
	/// Whether this trigger was defined in the JSON file (vs. UI override).
	/// </summary>
	public TriggerSource Source { get; set; } = TriggerSource.User;
}

public enum TriggerSource
{
	/// <summary>Trigger was defined in the orchestration JSON file.</summary>
	Json,

	/// <summary>Trigger was set or overridden by the user via the UI.</summary>
	User,
}

/// <summary>
/// Background service that manages all trigger registrations and fires orchestrations.
/// </summary>
public class TriggerManager : BackgroundService
{
	private readonly ConcurrentDictionary<string, TriggerRegistration> _triggers = new();
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeExecutions;
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _activeExecutionInfos;
	private readonly AgentBuilder _agentBuilder;
	private readonly IScheduler _scheduler;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<TriggerManager> _logger;
	private readonly string _runsDir;
	private readonly string _triggersDir;
	private readonly IRunStore _runStore;
	private readonly JsonSerializerOptions _jsonOptions;

	public TriggerManager(
		ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		AgentBuilder agentBuilder,
		IScheduler scheduler,
		ILoggerFactory loggerFactory,
		ILogger<TriggerManager> logger,
		string runsDir,
		IRunStore runStore)
	{
		_activeExecutions = activeExecutions;
		_activeExecutionInfos = activeExecutionInfos;
		_agentBuilder = agentBuilder;
		_scheduler = scheduler;
		_loggerFactory = loggerFactory;
		_logger = logger;
		_runsDir = runsDir;
		_triggersDir = Path.Combine(Path.GetDirectoryName(runsDir)!, "triggers");
		_runStore = runStore;
		Directory.CreateDirectory(_triggersDir);

		_jsonOptions = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		};
	}

	/// <summary>
	/// Gets a snapshot of all registered triggers.
	/// </summary>
	public IReadOnlyCollection<TriggerRegistration> GetAllTriggers()
		=> _triggers.Values.ToArray();

	/// <summary>
	/// Gets a specific trigger by ID.
	/// </summary>
	public TriggerRegistration? GetTrigger(string id)
		=> _triggers.TryGetValue(id, out var reg) ? reg : null;

	/// <summary>
	/// Registers or updates a trigger for an orchestration.
	/// </summary>
	public TriggerRegistration RegisterTrigger(
		string orchestrationPath,
		string? mcpPath,
		TriggerConfig config,
		Dictionary<string, string>? parameters = null,
		TriggerSource source = TriggerSource.User,
		string? orchestrationId = null)
	{
		// Try to load orchestration metadata for display
		string? orchName = null;
		string? orchDesc = null;
		string? orchVersion = null;
		try
		{
			var orch = OrchestrationParser.ParseOrchestrationFileMetadataOnly(orchestrationPath);
			orchName = orch.Name;
			orchDesc = orch.Description;
			orchVersion = orch.Version;
		}
		catch { /* best-effort metadata extraction */ }

		// Use the provided orchestrationId if given, otherwise generate one from the path and name
		var id = orchestrationId ?? GenerateTriggerId(orchestrationPath, orchName);

		var registration = new TriggerRegistration
		{
			Id = id,
			OrchestrationPath = orchestrationPath,
			McpPath = mcpPath,
			Config = config,
			Parameters = parameters,
			Source = source,
			Status = config.Enabled ? TriggerStatus.Waiting : TriggerStatus.Paused,
			OrchestrationName = orchName,
			OrchestrationDescription = orchDesc,
			OrchestrationVersion = orchVersion,
		};

		// Calculate next fire time for scheduler triggers
		if (config is SchedulerTriggerConfig schedulerConfig && config.Enabled)
		{
			registration.NextFireTime = CalculateNextFireTime(schedulerConfig);
		}

		_triggers[id] = registration;

		// Persist if user-defined
		if (source == TriggerSource.User)
		{
			PersistTriggerOverride(registration);
		}

		_logger.LogInformation("Trigger '{Id}' registered for '{Path}' (type={Type}, source={Source})",
			id, orchestrationPath, config.Type, source);

		return registration;
	}

	/// <summary>
	/// Removes a trigger registration.
	/// </summary>
	public bool RemoveTrigger(string id)
	{
		if (_triggers.TryRemove(id, out var reg))
		{
			// Remove persisted override file
			var sidecarPath = GetSidecarPath(reg.OrchestrationPath);
			if (File.Exists(sidecarPath))
			{
				try { File.Delete(sidecarPath); }
				catch { /* best-effort */ }
			}

			_logger.LogInformation("Trigger '{Id}' removed.", id);
			return true;
		}
		return false;
	}

	/// <summary>
	/// Enables or disables a trigger.
	/// </summary>
	public bool SetTriggerEnabled(string id, bool enabled)
	{
		if (!_triggers.TryGetValue(id, out var reg))
			return false;

		// Create a new config with the updated enabled state
		reg.Config = CloneTriggerConfigWithEnabled(reg.Config, enabled);
		reg.Status = enabled ? TriggerStatus.Waiting : TriggerStatus.Paused;

		if (enabled && reg.Config is SchedulerTriggerConfig schedulerConfig)
		{
			reg.NextFireTime = CalculateNextFireTime(schedulerConfig);
		}
		else if (!enabled)
		{
			reg.NextFireTime = null;
		}

		if (reg.Source == TriggerSource.User)
		{
			PersistTriggerOverride(reg);
		}

		_logger.LogInformation("Trigger '{Id}' {State}.", id, enabled ? "enabled" : "disabled");
		return true;
	}

	/// <summary>
	/// Fires a webhook trigger by its ID. Returns true if the trigger was found and fired.
	/// </summary>
	public async Task<(bool Found, string? ExecutionId)> FireWebhookTriggerAsync(
		string id, Dictionary<string, string>? webhookParameters)
	{
		if (!_triggers.TryGetValue(id, out var reg))
			return (false, null);

		if (reg.Config is not WebhookTriggerConfig webhookConfig)
			return (false, null);

		if (!reg.Config.Enabled || reg.Status == TriggerStatus.Paused)
			return (true, null);

		// Check concurrent execution limit
		if (reg.ActiveExecutionId != null)
		{
			if (webhookConfig.MaxConcurrent <= 1)
			{
				_logger.LogWarning("Webhook trigger '{Id}' already has an active execution. Skipping.", id);
				return (true, null);
			}
		}

		// Merge webhook parameters with configured parameters
		var mergedParams = new Dictionary<string, string>(reg.Parameters ?? []);
		if (webhookParameters != null)
		{
			foreach (var kv in webhookParameters)
				mergedParams[kv.Key] = kv.Value;
		}

		var executionId = await ExecuteOrchestrationAsync(reg, mergedParams);
		return (true, executionId);
	}

	/// <summary>
	/// Manually fires any trigger by its ID, regardless of type.
	/// </summary>
	public async Task<(bool Found, string? ExecutionId)> FireTriggerAsync(
		string id, Dictionary<string, string>? extraParameters = null)
	{
		if (!_triggers.TryGetValue(id, out var reg))
			return (false, null);

		if (!reg.Config.Enabled || reg.Status == TriggerStatus.Paused)
			return (true, null);

		// Merge extra parameters with configured parameters
		var mergedParams = new Dictionary<string, string>(reg.Parameters ?? []);
		if (extraParameters != null)
		{
			foreach (var kv in extraParameters)
				mergedParams[kv.Key] = kv.Value;
		}

		var executionId = await ExecuteOrchestrationAsync(reg, mergedParams);
		return (true, executionId);
	}

	/// <summary>
	/// Loads persisted trigger overrides from the triggers directory.
	/// </summary>
	public void LoadPersistedTriggers()
	{
		if (!Directory.Exists(_triggersDir))
			return;

		foreach (var file in Directory.GetFiles(_triggersDir, "*.trigger.json"))
		{
			try
			{
				var json = File.ReadAllText(file);
				var data = JsonSerializer.Deserialize<PersistedTrigger>(json, _jsonOptions);
				if (data == null) continue;

				// Verify the orchestration file still exists
				if (!File.Exists(data.OrchestrationPath))
				{
					_logger.LogWarning("Persisted trigger references missing file '{Path}', skipping.", data.OrchestrationPath);
					continue;
				}

				// Re-parse the trigger config
				var triggerJson = JsonSerializer.Serialize(data.Trigger, _jsonOptions);
				var triggerElement = JsonDocument.Parse(triggerJson).RootElement;
				var config = DeserializeTriggerConfig(triggerElement);

				if (config != null)
				{
					RegisterTrigger(data.OrchestrationPath, data.McpPath, config, data.Parameters, TriggerSource.User);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to load persisted trigger from '{File}'.", file);
			}
		}
	}

	/// <summary>
	/// Scans orchestration files and registers any JSON-defined triggers that aren't
	/// already overridden by user triggers.
	/// </summary>
	public void ScanForJsonTriggers(string directory)
	{
		if (!Directory.Exists(directory))
			return;

		foreach (var file in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
		{
			try
			{
				var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);
				if (orchestration.Trigger == null) continue;

				// Generate ID using orchestration name for consistency with OrchestrationRegistry
				var id = GenerateTriggerId(file, orchestration.Name);
				// Don't override existing user-defined triggers
				if (_triggers.TryGetValue(id, out var existing) && existing.Source == TriggerSource.User)
					continue;

				RegisterTrigger(file, null, orchestration.Trigger, null, TriggerSource.Json);
			}
			catch
			{
				// Not a valid orchestration file, skip
			}
		}
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("TriggerManager started.");

		// Load persisted triggers on startup
		LoadPersistedTriggers();

		// Main loop: check triggers every second
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await CheckSchedulerTriggersAsync(stoppingToken);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogError(ex, "Error in trigger manager loop.");
			}

			try
			{
				await Task.Delay(1000, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		_logger.LogInformation("TriggerManager stopped.");
	}

	private async Task CheckSchedulerTriggersAsync(CancellationToken stoppingToken)
	{
		var now = DateTime.UtcNow;

		foreach (var reg in _triggers.Values)
		{
			if (stoppingToken.IsCancellationRequested) break;

			if (reg.Status != TriggerStatus.Waiting) continue;
			if (!reg.Config.Enabled) continue;

			if (reg.Config is SchedulerTriggerConfig schedulerConfig)
			{
				if (reg.NextFireTime.HasValue && now >= reg.NextFireTime.Value)
				{
					// Check max runs
					if (schedulerConfig.MaxRuns.HasValue && reg.RunCount >= schedulerConfig.MaxRuns.Value)
					{
						reg.Status = TriggerStatus.Completed;
						reg.NextFireTime = null;
						continue;
					}

					_ = ExecuteAndHandleCompletionAsync(reg, schedulerConfig);
				}
			}
		}
	}

	private async Task ExecuteAndHandleCompletionAsync(TriggerRegistration reg, SchedulerTriggerConfig? schedulerConfig)
	{
		try
		{
			await ExecuteOrchestrationAsync(reg, reg.Parameters);

			// After execution, reschedule if it's a scheduler trigger
			if (schedulerConfig != null)
			{
				if (schedulerConfig.MaxRuns.HasValue && reg.RunCount >= schedulerConfig.MaxRuns.Value)
				{
					reg.Status = TriggerStatus.Completed;
					reg.NextFireTime = null;
				}
				else
				{
					reg.NextFireTime = CalculateNextFireTime(schedulerConfig);
					reg.Status = TriggerStatus.Waiting;
				}
			}
		}
		catch (Exception ex)
		{
			reg.Status = TriggerStatus.Error;
			reg.LastError = ex.Message;
			_logger.LogError(ex, "Trigger '{Id}' execution failed.", reg.Id);
		}
	}

	private async Task<string?> ExecuteOrchestrationAsync(
		TriggerRegistration reg,
		Dictionary<string, string>? parameters)
	{
		reg.Status = TriggerStatus.Running;
		reg.LastFireTime = DateTime.UtcNow;
		reg.RunCount++;
		reg.LastError = null;

		var executionId = Guid.NewGuid().ToString("N")[..12];
		reg.ActiveExecutionId = executionId;

		_logger.LogInformation("Trigger '{Id}' firing orchestration '{Path}' (execution={ExecId}, run #{Count})",
			reg.Id, reg.OrchestrationPath, executionId, reg.RunCount);

		try
		{
			// Parse orchestration
			Mcp[] mcps = [];
			if (!string.IsNullOrWhiteSpace(reg.McpPath) && File.Exists(reg.McpPath))
			{
				mcps = OrchestrationParser.ParseMcpFile(reg.McpPath);
			}

			var orchestration = OrchestrationParser.ParseOrchestrationFile(reg.OrchestrationPath, mcps);
			var schedule = _scheduler.Schedule(orchestration);

			// ── Input Handler Prompt: transform raw parameters via LLM ──
			if (!string.IsNullOrWhiteSpace(reg.Config.InputHandlerPrompt) && parameters is { Count: > 0 })
			{
				try
				{
					var rawInputJson = JsonSerializer.Serialize(parameters, _jsonOptions);
					var fullPrompt = $"{reg.Config.InputHandlerPrompt}\n\nRaw input:\n{rawInputJson}";

					var agent = await _agentBuilder
						.WithModel("gpt-4o-mini")
						.WithSystemPrompt("You are a parameter transformer. Given a prompt and raw input, respond with ONLY a valid JSON object mapping parameter names to string values. No markdown, no explanation — just the JSON object.")
						.WithMcp()
						.WithReasoningLevel(null)
						.WithSystemPromptMode(null)
						.WithReporter(NullOrchestrationReporter.Instance)
						.BuildAgentAsync();

					var task = agent.SendAsync(fullPrompt);
					var result = await task.GetResultAsync();

					// Parse the LLM response as JSON parameters
					var content = result.Content.Trim();
					// Strip markdown code fences if present
					if (content.StartsWith("```"))
					{
						var firstNewline = content.IndexOf('\n');
						if (firstNewline >= 0) content = content[(firstNewline + 1)..];
						if (content.EndsWith("```")) content = content[..^3].TrimEnd();
					}

					var transformed = JsonSerializer.Deserialize<Dictionary<string, string>>(content, _jsonOptions);
					if (transformed is { Count: > 0 })
					{
						_logger.LogInformation("Trigger '{Id}' input handler transformed {RawCount} raw params into {NewCount} params",
							reg.Id, parameters.Count, transformed.Count);
						parameters = transformed;
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Trigger '{Id}' input handler prompt failed — using raw parameters", reg.Id);
				}
			}

			// Create executor with WebOrchestrationReporter for UI tracking
			var reporter = new WebOrchestrationReporter();
			var executor = new OrchestrationExecutor(_scheduler, _agentBuilder, reporter, _loggerFactory, _runStore);

			using var cts = new CancellationTokenSource();
			_activeExecutions[executionId] = cts;

			// Track in activeExecutionInfos for UI visibility (View Details, Cancel, etc.)
			var executionInfo = new ActiveExecutionInfo
			{
				ExecutionId = executionId,
				OrchestrationId = reg.Id,
				OrchestrationName = orchestration.Name,
				StartedAt = DateTimeOffset.UtcNow,
				TriggeredBy = reg.Config.Type.ToString().ToLowerInvariant(),
				CancellationTokenSource = cts,
				Reporter = reporter,
				Parameters = parameters,
				TotalSteps = orchestration.Steps.Length
			};
			_activeExecutionInfos[executionId] = executionInfo;

			// Set up progress callbacks for UI tracking
			reporter.OnStepStarted = (stepName) =>
			{
				executionInfo.CurrentStep = stepName;
			};
			reporter.OnStepCompleted = (stepName) =>
			{
				executionInfo.CompletedSteps++;
				executionInfo.CurrentStep = null;
			};

			try
			{
				var result = await executor.ExecuteAsync(orchestration, parameters, triggerId: reg.Id, cancellationToken: cts.Token);

				// Persist history
				try
				{
					var historyEntry = new
					{
						id = executionId,
						orchestrationName = orchestration.Name,
						orchestrationDescription = orchestration.Description,
						orchestrationVersion = orchestration.Version,
						orchestrationPath = reg.OrchestrationPath,
						triggerId = reg.Id,
						triggerType = reg.Config.Type.ToString(),
						startedAt = reg.LastFireTime?.ToString("o"),
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
					var historyJson = JsonSerializer.Serialize(historyEntry, _jsonOptions);
					var historyPath = Path.Combine(_runsDir, $"{executionId}.json");
					await File.WriteAllTextAsync(historyPath, historyJson);
				}
				catch { /* best-effort */ }

				// Handle loop trigger
				if (reg.Config is LoopTriggerConfig loopConfig)
				{
					var shouldContinue = result.Status == ExecutionStatus.Succeeded || loopConfig.ContinueOnFailure;
					var withinLimit = !loopConfig.MaxIterations.HasValue || reg.RunCount < loopConfig.MaxIterations.Value;

					if (shouldContinue && withinLimit && reg.Config.Enabled)
					{
						if (loopConfig.DelaySeconds > 0)
						{
							reg.Status = TriggerStatus.Waiting;
							reg.NextFireTime = DateTime.UtcNow.AddSeconds(loopConfig.DelaySeconds);
						}
						else
						{
							// Immediately re-run
							_ = Task.Run(async () =>
							{
								await Task.Delay(100); // Small delay to avoid tight loops
								await ExecuteOrchestrationAsync(reg, parameters);
							});
							return executionId;
						}
					}
					else
					{
						reg.Status = loopConfig.MaxIterations.HasValue && reg.RunCount >= loopConfig.MaxIterations.Value
							? TriggerStatus.Completed
							: TriggerStatus.Paused;
					}
				}
				// Handle webhook trigger - return to Waiting status to accept next invocation
				else if (reg.Config is WebhookTriggerConfig)
				{
					reg.Status = TriggerStatus.Waiting;
				}
				// Handle scheduler trigger - return to Waiting with next fire time
				else if (reg.Config is SchedulerTriggerConfig schedulerConfig)
				{
					reg.Status = TriggerStatus.Waiting;
					reg.NextFireTime = CalculateNextFireTime(schedulerConfig);
				}

				return executionId;
			}
			finally
			{
				_activeExecutions.TryRemove(executionId, out _);
				// Update status and remove from active execution infos after a delay
				// to allow UI to see final status
				if (_activeExecutionInfos.TryGetValue(executionId, out var info))
				{
					info.Status = "Completed";
					info.Reporter.Complete();
					// Remove after a short delay to allow SSE clients to receive completion
					_ = Task.Run(async () =>
					{
						await Task.Delay(5000);
						_activeExecutionInfos.TryRemove(executionId, out _);
					});
				}
				reg.LastExecutionId = executionId;
				reg.ActiveExecutionId = null;
			}
		}
		catch (Exception ex)
		{
			reg.Status = TriggerStatus.Error;
			reg.LastError = ex.Message;
			reg.ActiveExecutionId = null;
			_logger.LogError(ex, "Failed to execute orchestration for trigger '{Id}'.", reg.Id);
			return null;
		}
	}

	private void PersistTriggerOverride(TriggerRegistration reg)
	{
		try
		{
			var sidecarPath = GetSidecarPath(reg.OrchestrationPath);
			var data = new PersistedTrigger
			{
				OrchestrationPath = reg.OrchestrationPath,
				McpPath = reg.McpPath,
				Trigger = reg.Config,
				Parameters = reg.Parameters,
			};

			var json = JsonSerializer.Serialize(data, _jsonOptions);
			File.WriteAllText(sidecarPath, json);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to persist trigger override for '{Id}'.", reg.Id);
		}
	}

	private string GetSidecarPath(string orchestrationPath)
	{
		// Store in triggers directory using a safe filename derived from the path
		var safeName = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(
				System.Text.Encoding.UTF8.GetBytes(orchestrationPath)))[..16];
		return Path.Combine(_triggersDir, $"{safeName}.trigger.json");
	}

	private static string GenerateTriggerId(string orchestrationPath, string? orchestrationName = null)
	{
		// Use the same algorithm as OrchestrationRegistry.GenerateId for consistency
		var name = orchestrationName ?? Path.GetFileNameWithoutExtension(orchestrationPath);
		var hash = orchestrationPath.GetHashCode().ToString("x8");
		var sanitizedName = SanitizeIdName(name);
		return $"{sanitizedName}-{hash[..4]}";
	}

	private static string SanitizeIdName(string name)
	{
		return new string(name
			.ToLowerInvariant()
			.Select(c => char.IsLetterOrDigit(c) ? c : '-')
			.ToArray())
			.Trim('-');
	}

	private static DateTime CalculateNextFireTime(SchedulerTriggerConfig config)
	{
		if (!string.IsNullOrWhiteSpace(config.Cron))
		{
			return CalculateNextCronTime(config.Cron);
		}

		if (config.IntervalSeconds.HasValue && config.IntervalSeconds.Value > 0)
		{
			return DateTime.UtcNow.AddSeconds(config.IntervalSeconds.Value);
		}

		// Default: 1 minute from now
		return DateTime.UtcNow.AddMinutes(1);
	}

	/// <summary>
	/// Simple cron parser supporting basic patterns:
	/// "second minute hour day month dayOfWeek" or "minute hour day month dayOfWeek" (5-field).
	/// For simplicity, just calculates interval-based approximation.
	/// Full cron parsing can be added later with a library.
	/// </summary>
	private static DateTime CalculateNextCronTime(string cron)
	{
		// Simple implementation: parse common patterns
		var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

		// For now, handle common interval patterns:
		// "*/N * * * *" = every N minutes
		// "0 */N * * *" = every N hours
		// "0 0 */N * *" = every N days
		if (parts.Length >= 5)
		{
			// Check for "*/N" in the minute field
			if (parts[0].StartsWith("*/"))
			{
				if (int.TryParse(parts[0][2..], out var minutes))
					return DateTime.UtcNow.AddMinutes(minutes);
			}

			// Check for "*/N" in the hour field
			if (parts[0] == "0" && parts[1].StartsWith("*/"))
			{
				if (int.TryParse(parts[1][2..], out var hours))
					return DateTime.UtcNow.AddHours(hours);
			}
		}

		// Fallback: 1 hour from now for unrecognized patterns
		return DateTime.UtcNow.AddHours(1);
	}

	public static TriggerConfig CloneTriggerConfigWithEnabled(TriggerConfig config, bool enabled)
	{
		return config switch
		{
			SchedulerTriggerConfig s => new SchedulerTriggerConfig
			{
				Type = s.Type,
				Enabled = enabled,
				Cron = s.Cron,
				IntervalSeconds = s.IntervalSeconds,
				MaxRuns = s.MaxRuns,
			},
			LoopTriggerConfig l => new LoopTriggerConfig
			{
				Type = l.Type,
				Enabled = enabled,
				DelaySeconds = l.DelaySeconds,
				MaxIterations = l.MaxIterations,
				ContinueOnFailure = l.ContinueOnFailure,
			},
			WebhookTriggerConfig w => new WebhookTriggerConfig
			{
				Type = w.Type,
				Enabled = enabled,
				Secret = w.Secret,
				MaxConcurrent = w.MaxConcurrent,
			},
			EmailTriggerConfig e => new EmailTriggerConfig
			{
				Type = e.Type,
				Enabled = enabled,
				FolderPath = e.FolderPath,
				PollIntervalSeconds = e.PollIntervalSeconds,
				MaxItemsPerPoll = e.MaxItemsPerPoll,
				SubjectContains = e.SubjectContains,
				SenderContains = e.SenderContains,
			},
			_ => config,
		};
	}

	private static TriggerConfig? DeserializeTriggerConfig(JsonElement element)
	{
		if (!element.TryGetProperty("type", out var typeProp))
			return null;

		var typeStr = typeProp.GetString();
		if (!Enum.TryParse<TriggerType>(typeStr, ignoreCase: true, out var type))
			return null;

		var enabled = element.TryGetProperty("enabled", out var enabledProp) ? enabledProp.GetBoolean() : true;

		return type switch
		{
			TriggerType.Scheduler => new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = enabled,
				Cron = element.TryGetProperty("cron", out var cron) ? cron.GetString() : null,
				IntervalSeconds = element.TryGetProperty("intervalSeconds", out var interval) ? interval.GetInt32() : null,
				MaxRuns = element.TryGetProperty("maxRuns", out var maxRuns) ? maxRuns.GetInt32() : null,
			},
			TriggerType.Loop => new LoopTriggerConfig
			{
				Type = TriggerType.Loop,
				Enabled = enabled,
				DelaySeconds = element.TryGetProperty("delaySeconds", out var delay) ? delay.GetInt32() : 0,
				MaxIterations = element.TryGetProperty("maxIterations", out var maxIter) ? maxIter.GetInt32() : null,
				ContinueOnFailure = element.TryGetProperty("continueOnFailure", out var cof) && cof.GetBoolean(),
			},
			TriggerType.Webhook => new WebhookTriggerConfig
			{
				Type = TriggerType.Webhook,
				Enabled = enabled,
				Secret = element.TryGetProperty("secret", out var secret) ? secret.GetString() : null,
				MaxConcurrent = element.TryGetProperty("maxConcurrent", out var maxConc) ? maxConc.GetInt32() : 1,
			},
			TriggerType.Email => new EmailTriggerConfig
			{
				Type = TriggerType.Email,
				Enabled = enabled,
				FolderPath = element.TryGetProperty("folderPath", out var folderPath) ? folderPath.GetString() ?? "Inbox" : "Inbox",
				PollIntervalSeconds = element.TryGetProperty("pollIntervalSeconds", out var pollInterval) ? pollInterval.GetInt32() : 60,
				MaxItemsPerPoll = element.TryGetProperty("maxItemsPerPoll", out var maxItems) ? maxItems.GetInt32() : 10,
				SubjectContains = element.TryGetProperty("subjectContains", out var subjectContains) ? subjectContains.GetString() : null,
				SenderContains = element.TryGetProperty("senderContains", out var senderContains) ? senderContains.GetString() : null,
			},
			_ => null,
		};
	}

	private class PersistedTrigger
	{
		public required string OrchestrationPath { get; init; }
		public string? McpPath { get; init; }
		public required TriggerConfig Trigger { get; init; }
		public Dictionary<string, string>? Parameters { get; init; }
	}
}
