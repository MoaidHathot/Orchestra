using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Triggers;

/// <summary>
/// Background service that manages all trigger registrations and fires orchestrations.
/// Supports graceful shutdown: cancels all active executions and awaits in-flight tasks.
/// </summary>
public partial class TriggerManager : BackgroundService
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
	private readonly ICheckpointStore _checkpointStore;
	private readonly ITriggerExecutionCallback? _executionCallback;
	private readonly EngineToolRegistry _engineToolRegistry;
	private readonly IMcpResolver? _mcpResolver;
	private readonly string? _dataPath;
	private readonly string? _serverUrl;
	private readonly string? _defaultModel;
	private readonly HookDefinition[] _globalHooks;
	private readonly JsonSerializerOptions _jsonOptions;

	/// <summary>
	/// Tracks all fire-and-forget tasks so they can be awaited during shutdown.
	/// </summary>
	private readonly ConcurrentDictionary<int, Task> _backgroundTasks = new();
	private int _backgroundTaskId;

	/// <summary>
	/// Maximum time to wait for in-flight tasks during graceful shutdown.
	/// </summary>
	public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Global MCPs (from orchestra.mcp.json) used when parsing orchestration files at trigger fire time.
	/// Set by the host during initialization.
	/// </summary>
	public Engine.Mcp[] GlobalMcps { get; set; } = [];

	public TriggerManager(
		ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		AgentBuilder agentBuilder,
		IScheduler scheduler,
		ILoggerFactory loggerFactory,
		ILogger<TriggerManager> logger,
		string runsDir,
		IRunStore runStore,
		ICheckpointStore checkpointStore,
		ITriggerExecutionCallback? executionCallback = null,
		EngineToolRegistry? engineToolRegistry = null,
		IMcpResolver? mcpResolver = null,
		string? dataPath = null,
		string? serverUrl = null,
		string? defaultModel = null,
		HookDefinition[]? globalHooks = null)
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
		_checkpointStore = checkpointStore;
		_executionCallback = executionCallback;
		_engineToolRegistry = engineToolRegistry ?? EngineToolRegistry.CreateDefault();
		_mcpResolver = mcpResolver;
		_dataPath = dataPath;
		_serverUrl = serverUrl;
		_defaultModel = defaultModel;
		_globalHooks = globalHooks ?? [];
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
	/// Gets a snapshot of all active execution infos.
	/// </summary>
	public IReadOnlyCollection<ActiveExecutionInfo> GetActiveExecutions()
		=> _activeExecutionInfos.Values.ToArray();

	/// <summary>
	/// Gets an active execution info by ID.
	/// </summary>
	public ActiveExecutionInfo? GetActiveExecution(string executionId)
		=> _activeExecutionInfos.TryGetValue(executionId, out var info) ? info : null;

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
		TriggerConfig config,
		Dictionary<string, string>? parameters = null,
		TriggerSource source = TriggerSource.User,
		string? orchestrationId = null,
		Orchestration? preloadedOrchestration = null)
	{
		// Use pre-loaded metadata if available, otherwise try to parse from file
		string? orchName = preloadedOrchestration?.Name;
		string? orchDesc = preloadedOrchestration?.Description;
		string? orchVersion = preloadedOrchestration?.Version;

		if (orchName is null)
		{
			try
			{
				var orch = OrchestrationParser.ParseOrchestrationFileMetadataOnly(orchestrationPath);
				orchName = orch.Name;
				orchDesc = orch.Description;
				orchVersion = orch.Version;
			}
			catch (Exception ex) { LogMetadataExtractionFailed(orchestrationPath, ex); }
		}

		// Use the provided orchestrationId if given, otherwise generate one from the path and name
		var id = orchestrationId ?? GenerateTriggerId(orchestrationPath, orchName);

		var registration = new TriggerRegistration
		{
			Id = id,
			OrchestrationPath = orchestrationPath,
			Config = config,
			Parameters = parameters,
			Source = source,
			Status = config is ManualTriggerConfig
				? TriggerStatus.Idle
				: config.Enabled ? TriggerStatus.Waiting : TriggerStatus.Paused,
			OrchestrationName = orchName,
			OrchestrationDescription = orchDesc,
			OrchestrationVersion = orchVersion,
		};

		// Calculate next fire time for scheduler triggers
		if (config is SchedulerTriggerConfig schedulerConfig && config.Enabled)
		{
			registration.NextFireTime = CalculateNextFireTime(schedulerConfig);
		}

		// Preserve runtime state from existing registration (if re-registering)
		if (_triggers.TryGetValue(id, out var existing))
		{
			registration.RunCount = existing.RunCount;
			registration.LastFireTime = existing.LastFireTime;
			registration.LastExecutionId = existing.LastExecutionId;
			registration.LastError = existing.LastError;

			// If the existing trigger has an active execution, carry it over
			if (existing.ActiveExecutionId is not null)
			{
				registration.ActiveExecutionId = existing.ActiveExecutionId;
				registration.Status = existing.Status;
			}
		}

		_triggers[id] = registration;

		// Persist if user-defined
		if (source == TriggerSource.User)
		{
			PersistTriggerOverride(registration);
		}

		LogTriggerRegistered(id, orchestrationPath, config.Type.ToString(), source.ToString());

		return registration;
	}

	/// <summary>
	/// Removes a trigger registration.
	/// </summary>
	public bool RemoveTrigger(string id)
	{
		if (_triggers.TryRemove(id, out var reg))
		{
			// Remove persisted override file (user triggers)
			var sidecarPath = GetSidecarPath(reg.OrchestrationPath);
			if (File.Exists(sidecarPath))
			{
				try { File.Delete(sidecarPath); }
				catch (Exception ex) { LogSidecarDeleteFailed(sidecarPath, ex); }
			}

			// Remove JSON trigger enabled-state override if present
			if (reg.Source == TriggerSource.Json)
			{
				RemoveJsonTriggerEnabledOverride(id);
			}

			LogTriggerRemoved(id);
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
		else if (reg.Source == TriggerSource.Json)
		{
			PersistJsonTriggerEnabledOverride(id, enabled);
		}

		LogTriggerEnabledChanged(id, enabled);
		return true;
	}

	/// <summary>
	/// Fires a webhook trigger by its ID. Returns true if the trigger was found and fired.
	/// When the webhook config has <see cref="WebhookResponseConfig.WaitForResult"/> = true,
	/// the <see cref="OrchestrationResult"/> is also returned.
	/// </summary>
	public async Task<(bool Found, string? ExecutionId, OrchestrationResult? Result)> FireWebhookTriggerAsync(
		string id, Dictionary<string, string>? webhookParameters)
	{
		if (!_triggers.TryGetValue(id, out var reg))
			return (false, null, null);

		if (reg.Config is not WebhookTriggerConfig webhookConfig)
			return (false, null, null);

		if (!reg.Config.Enabled || reg.Status == TriggerStatus.Paused)
			return (true, null, null);

		// Check concurrent execution limit
		if (reg.ActiveExecutionId != null)
		{
			if (webhookConfig.MaxConcurrent <= 1)
			{
				LogWebhookSkippedConcurrent(id);
				return (true, null, null);
			}
		}

		// Merge webhook parameters with configured parameters
		var mergedParams = new Dictionary<string, string>(reg.Parameters ?? []);
		if (webhookParameters != null)
		{
			foreach (var kv in webhookParameters)
				mergedParams[kv.Key] = kv.Value;
		}

		// If synchronous response is requested, await and capture the result
		if (webhookConfig.Response is { WaitForResult: true })
		{
			var (executionId, result) = await ExecuteOrchestrationWithResultAsync(reg, mergedParams);
			return (true, executionId, result);
		}

		var execId = await ExecuteOrchestrationAsync(reg, mergedParams);
		return (true, execId, null);
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
	/// Runs an orchestration directly by path, without requiring a registered trigger.
	/// Use this for manual execution of orchestrations that don't have triggers defined.
	/// </summary>
	public async Task<string?> RunOrchestrationAsync(
		string orchestrationPath,
		Dictionary<string, string>? parameters = null,
		string? orchestrationId = null)
	{
		// Create a temporary trigger registration for execution
		string? orchName = null;
		TriggerConfig triggerConfig;
		try
		{
			var orch = OrchestrationParser.ParseOrchestrationFileMetadataOnly(orchestrationPath);
			orchName = orch.Name;
			triggerConfig = orch.Trigger;
		}
		catch (Exception ex)
		{
			LogMetadataExtractionFailed(orchestrationPath, ex);
			triggerConfig = new ManualTriggerConfig { Type = TriggerType.Manual, Enabled = true };
		}

		var tempReg = new TriggerRegistration
		{
			Id = orchestrationId ?? GenerateTriggerId(orchestrationPath, orchName),
			OrchestrationPath = orchestrationPath,
			Config = triggerConfig,
			Parameters = parameters,
			Source = TriggerSource.User,
			Status = TriggerStatus.Running,
			OrchestrationName = orchName,
		};

		return await ExecuteOrchestrationAsync(tempReg, parameters);
	}

	/// <summary>
	/// Resumes an orchestration from a checkpoint.
	/// </summary>
	public async Task<string?> ResumeFromCheckpointAsync(
		OrchestrationEntry entry,
		CheckpointData checkpoint)
	{
		var executionId = checkpoint.RunId;

		var reporter = _executionCallback?.CreateReporter() ?? NullOrchestrationReporter.Instance;
		var executor = new OrchestrationExecutor(_scheduler, _agentBuilder, reporter, _loggerFactory, runStore: _runStore, checkpointStore: _checkpointStore, engineToolRegistry: _engineToolRegistry, mcpResolver: _mcpResolver, globalHooks: _globalHooks, dataPath: _dataPath, serverUrl: _serverUrl);

		using var cts = new CancellationTokenSource();
		_activeExecutions[executionId] = cts;

		var executionInfo = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = entry.Id,
			OrchestrationName = entry.Orchestration.Name,
			StartedAt = checkpoint.StartedAt,
			TriggeredBy = "resume",
			CancellationTokenSource = cts,
			Reporter = reporter,
			Parameters = checkpoint.Parameters.Count > 0 ? checkpoint.Parameters : null,
			TotalSteps = entry.Orchestration.Steps.Length,
			CompletedSteps = checkpoint.CompletedSteps.Count,
		};
		_activeExecutionInfos[executionId] = executionInfo;

		_executionCallback?.OnExecutionStarted(executionInfo);

		var taskId = Interlocked.Increment(ref _backgroundTaskId);

		var task = Task.Run(async () =>
		{
			try
			{
				var result = await executor.ResumeAsync(entry.Orchestration, checkpoint, cancellationToken: cts.Token);

				executionInfo.Status = result.Status switch
				{
					ExecutionStatus.Succeeded or ExecutionStatus.NoAction => HostExecutionStatus.Completed,
					ExecutionStatus.Cancelled => HostExecutionStatus.Cancelled,
					_ => HostExecutionStatus.Failed
				};

				// Send terminal SSE events so attached clients see real-time completion.
				if (reporter is SseReporter sseReporter)
				{
					if (result.Status == ExecutionStatus.Cancelled)
					{
						sseReporter.ReportOrchestrationCancelled();
					}
					else
					{
						foreach (var (stepName, stepResult) in result.StepResults)
						{
							if (stepResult.Status == ExecutionStatus.Succeeded)
								sseReporter.ReportStepOutput(stepName, stepResult.Content);
						}
						sseReporter.ReportOrchestrationDone(result);
					}
				}

				// Persist history
				try
				{
					var historyEntry = new
					{
						id = executionId,
						orchestrationName = entry.Orchestration.Name,
						orchestrationDescription = entry.Orchestration.Description,
						orchestrationVersion = entry.Orchestration.Version,
						orchestrationPath = entry.Path,
						triggerId = checkpoint.TriggerId,
						triggerType = "resume",
						startedAt = checkpoint.StartedAt.ToString("o"),
						status = result.Status.ToString(),
						stepCount = entry.Orchestration.Steps.Length,
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
				catch (Exception ex) { LogHistoryPersistFailed(executionId, ex); }
			}
			catch (OperationCanceledException)
			{
				executionInfo.Status = HostExecutionStatus.Cancelled;
				if (reporter is SseReporter cancelSseReporter)
					cancelSseReporter.ReportOrchestrationCancelled();
			}
			catch (Exception ex)
			{
				executionInfo.Status = HostExecutionStatus.Failed;
				if (reporter is SseReporter errorSseReporter)
				{
					errorSseReporter.ReportStepError("orchestration", ex.Message);
					errorSseReporter.ReportOrchestrationError(ex.Message);
				}
				LogOrchestrationResumeFailed(entry.Id, executionId, ex);
			}
			finally
			{
				// Complete the SSE reporter so attached clients' streams terminate.
				if (reporter is SseReporter completeSseReporter)
					completeSseReporter.Complete();

				_activeExecutions.TryRemove(executionId, out _);
				_backgroundTasks.TryRemove(taskId, out _);

				// Update status and remove after a short delay (matching ExecuteOrchestrationCoreAsync behavior)
				if (_activeExecutionInfos.TryGetValue(executionId, out var resumeInfo))
				{
					_executionCallback?.OnExecutionCompleted(resumeInfo);

					TrackBackgroundTask(Task.Run(async () =>
					{
						await Task.Delay(5000);
						_activeExecutionInfos.TryRemove(executionId, out _);
					}));
				}
			}
		});

		_backgroundTasks[taskId] = task;
		return executionId;
	}

	/// <summary>
	/// Cancels an active execution.
	/// </summary>
	public bool CancelExecution(string executionId)
	{
		if (_activeExecutions.TryGetValue(executionId, out var cts))
		{
			if (_activeExecutionInfos.TryGetValue(executionId, out var info))
			{
				info.Status = HostExecutionStatus.Cancelling;
				if (info.Reporter is SseReporter sseReporter)
					sseReporter.ReportStatusChange(HostExecutionStatus.Cancelling);
			}
			try { cts.Cancel(); }
			catch (ObjectDisposedException) { /* CTS was disposed by a completing execution */ }
			return true;
		}
		return false;
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
					LogPersistedTriggerMissingFile(data.OrchestrationPath);
					continue;
				}

				// Re-parse the trigger config
				var triggerJson = JsonSerializer.Serialize(data.Trigger, _jsonOptions);
				var triggerElement = JsonDocument.Parse(triggerJson).RootElement;
				var config = DeserializeTriggerConfig(triggerElement);

				if (config != null)
				{
					RegisterTrigger(data.OrchestrationPath, config, data.Parameters, TriggerSource.User);
				}
			}
			catch (Exception ex)
			{
				LogPersistedTriggerLoadFailed(ex, file);
			}
		}
	}

	/// <summary>
	/// Scans orchestration files and registers any file-defined triggers that aren't
	/// already overridden by user triggers. This includes ManualTriggerConfig for
	/// orchestrations with no explicit trigger, ensuring every orchestration has
	/// a trigger registration.
	/// </summary>
	public void ScanForJsonTriggers(string directory)
	{
		if (!Directory.Exists(directory))
			return;

		foreach (var file in OrchestrationParser.GetOrchestrationFiles(directory))
		{
			try
			{
				var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);

				// Generate ID using orchestration name for consistency with OrchestrationRegistry
				var id = GenerateTriggerId(file, orchestration.Name);
				// Don't override existing user-defined triggers
				if (_triggers.TryGetValue(id, out var existing) && existing.Source == TriggerSource.User)
					continue;

				RegisterTrigger(file, orchestration.Trigger, null, TriggerSource.Json, preloadedOrchestration: orchestration);
			}
			catch (Exception ex)
			{
				LogOrchestrationFileScanFailed(file, ex);
			}
		}
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		LogTriggerManagerStarted();

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
				LogTriggerManagerLoopError(ex);
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

		LogTriggerManagerStopped();
	}

	/// <summary>
	/// Graceful shutdown: cancel all active executions and await in-flight tasks.
	/// </summary>
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		LogGracefulShutdownStarting(_activeExecutions.Count, _backgroundTasks.Count);

		// 1. Stop the polling loop first so no new triggers fire during drain
		await base.StopAsync(cancellationToken);

		// 2. Cancel all active orchestration executions
		foreach (var kvp in _activeExecutions)
		{
			try
			{
				kvp.Value.Cancel();
			}
			catch (ObjectDisposedException)
			{
				// Already disposed, ignore
			}
		}

		// 3. Await all tracked background tasks with timeout
		var allTasks = _backgroundTasks.Values.ToArray();
		if (allTasks.Length > 0)
		{
			try
			{
				await Task.WhenAll(allTasks).WaitAsync(ShutdownTimeout, cancellationToken);
			}
			catch (TimeoutException)
			{
				LogGracefulShutdownTimeout(allTasks.Count(t => !t.IsCompleted));
			}
			catch (OperationCanceledException)
			{
				// Shutdown was forcibly cancelled
			}
			catch (Exception ex)
			{
				LogGracefulShutdownError(ex);
			}
		}

		LogGracefulShutdownComplete();
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

					// Atomically transition to Running to prevent the next poll
					// cycle from seeing Waiting and double-firing the same trigger.
					if (!reg.TryTransitionStatus(TriggerStatus.Waiting, TriggerStatus.Running))
						continue;

					TrackBackgroundTask(ExecuteAndHandleCompletionAsync(reg, schedulerConfig));
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
			LogTriggerExecutionFailed(ex, reg.Id);
		}
	}

	private async Task<string?> ExecuteOrchestrationAsync(
		TriggerRegistration reg,
		Dictionary<string, string>? parameters)
	{
		var (executionId, result) = await ExecuteOrchestrationCoreAsync(reg, parameters);
		if (executionId is null)
			return null;

		HandlePostExecutionTriggerStatus(reg, result!, parameters);
		return executionId;
	}

	/// <summary>
	/// Executes an orchestration and returns both the execution ID and the result.
	/// Used for synchronous webhook responses where the caller needs the result.
	/// </summary>
	private async Task<(string? ExecutionId, OrchestrationResult? Result)> ExecuteOrchestrationWithResultAsync(
		TriggerRegistration reg,
		Dictionary<string, string>? parameters)
	{
		var (executionId, result) = await ExecuteOrchestrationCoreAsync(reg, parameters);
		if (executionId is null)
			return (null, null);

		// Webhook sync responses always return to Waiting
		reg.Status = TriggerStatus.Waiting;
		return (executionId, result);
	}

	/// <summary>
	/// Shared execution logic: sets up trigger state, parses orchestration,
	/// transforms parameters via input handler, executes, persists history, and cleans up.
	/// </summary>
	private async Task<(string? ExecutionId, OrchestrationResult? Result)> ExecuteOrchestrationCoreAsync(
		TriggerRegistration reg,
		Dictionary<string, string>? parameters)
	{
		reg.Status = TriggerStatus.Running;
		reg.LastFireTime = DateTime.UtcNow;
		reg.IncrementRunCount();
		reg.LastError = null;

		var executionId = Guid.NewGuid().ToString("N")[..12];
		reg.ActiveExecutionId = executionId;

		LogTriggerFiring(reg.Id, reg.OrchestrationPath, executionId, reg.RunCount);

		try
		{
			// Parse orchestration (global MCPs available via GlobalMcps property)
			var orchestration = OrchestrationParser.ParseOrchestrationFile(reg.OrchestrationPath, GlobalMcps);
			var schedule = _scheduler.Schedule(orchestration);

			// ── Input Handler Prompt: transform raw parameters via LLM ──
			// This delegate is invoked by OrchestrationExecutor INSIDE its run scope so the
			// input-handler agent build shares the orchestration's CLI process (it gets its
			// own SDK session, not its own CLI subprocess). This preserves the
			// one-CLI-process-per-orchestration invariant.
			Func<CancellationToken, Task<Dictionary<string, string>?>>? inputHandlerTransform = null;
			if (!string.IsNullOrWhiteSpace(reg.Config.InputHandlerPrompt) && parameters is { Count: > 0 })
			{
				var capturedParameters = parameters;
				inputHandlerTransform = async ct =>
				{
					try
					{
						var rawInputJson = JsonSerializer.Serialize(capturedParameters, _jsonOptions);
						var fullPrompt = $"{reg.Config.InputHandlerPrompt}\n\nRaw input:\n{rawInputJson}";

						var agent = await _agentBuilder
							.BuildAgentAsync(new AgentBuildConfig
							{
								Model = reg.Config.InputHandlerModel ?? _defaultModel ?? "claude-opus-4.6",
								SystemPrompt = "You are a parameter transformer. Given a prompt and raw input, respond with ONLY a valid JSON object mapping parameter names to string values. No markdown, no explanation — just the JSON object.",
								Mcps = [],
							}, ct);

						var task = agent.SendAsync(fullPrompt);
						var result = await task.GetResultAsync();

						var content = result.Content.Trim();
						if (content.StartsWith("```"))
						{
							var firstNewline = content.IndexOf('\n');
							if (firstNewline >= 0) content = content[(firstNewline + 1)..];
							if (content.EndsWith("```")) content = content[..^3].TrimEnd();
						}

						var transformed = JsonSerializer.Deserialize<Dictionary<string, string>>(content, _jsonOptions);
						if (transformed is { Count: > 0 })
						{
							LogInputHandlerTransformed(reg.Id, capturedParameters.Count, transformed.Count);
							return transformed;
						}
						return null;
					}
					catch (Exception ex)
					{
						LogInputHandlerFailed(ex, reg.Id);
						return null;
					}
				};
			}

			// Create executor with reporter
			var reporter = _executionCallback?.CreateReporter() ?? NullOrchestrationReporter.Instance;
			var executor = new OrchestrationExecutor(_scheduler, _agentBuilder, reporter, _loggerFactory, runStore: _runStore, engineToolRegistry: _engineToolRegistry, mcpResolver: _mcpResolver, globalHooks: _globalHooks, dataPath: _dataPath, serverUrl: _serverUrl);

			using var cts = new CancellationTokenSource();
			_activeExecutions[executionId] = cts;

			// Track in activeExecutionInfos for UI visibility
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

			// Notify callback that execution has started (allows it to set up reporter callbacks)
			_executionCallback?.OnExecutionStarted(executionInfo);

			OrchestrationResult? executionResult = null;
			try
			{
				// Wrap the input handler so it also updates executionInfo.Parameters with the
				// transformed values when the executor invokes it (otherwise the UI would
				// keep showing the pre-transform raw input).
				Func<CancellationToken, Task<Dictionary<string, string>?>>? executorTransform = null;
				if (inputHandlerTransform is not null)
				{
					executorTransform = async ct =>
					{
						var transformed = await inputHandlerTransform(ct).ConfigureAwait(false);
						if (transformed is not null)
						{
							executionInfo.Parameters = transformed;
						}
						return transformed;
					};
				}

				var orchResult = await executor.ExecuteAsync(
					orchestration,
					parameters,
					triggerId: reg.Id,
					preExecutionParameterTransform: executorTransform,
					cancellationToken: cts.Token);
				executionResult = orchResult;

				// Send terminal SSE events so attached clients see real-time completion.
				// This mirrors what ExecutionApi does for manual SSE-based executions.
				if (reporter is SseReporter sseReporter)
				{
					if (orchResult.Status == ExecutionStatus.Cancelled)
					{
						sseReporter.ReportOrchestrationCancelled();
					}
					else
					{
						foreach (var (stepName, stepResult) in orchResult.StepResults)
						{
							if (stepResult.Status == ExecutionStatus.Succeeded)
								sseReporter.ReportStepOutput(stepName, stepResult.Content);
						}
						sseReporter.ReportOrchestrationDone(orchResult);
					}
				}

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
						status = orchResult.Status.ToString(),
						stepCount = orchestration.Steps.Length,
						results = orchResult.StepResults.ToDictionary(
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
				catch (Exception ex) { LogHistoryPersistFailed(executionId, ex); }

				return (executionId, orchResult);
			}
			catch (OperationCanceledException)
			{
				if (reporter is SseReporter cancelSseReporter)
					cancelSseReporter.ReportOrchestrationCancelled();
				throw;
			}
			catch (Exception ex)
			{
				if (reporter is SseReporter errorSseReporter)
				{
					errorSseReporter.ReportStepError("orchestration", ex.Message);
					errorSseReporter.ReportOrchestrationError(ex.Message);
				}
				throw;
			}
			finally
			{
				// Complete the SSE reporter so attached clients' streams terminate.
				if (reporter is SseReporter completeSseReporter)
					completeSseReporter.Complete();

				_activeExecutions.TryRemove(executionId, out _);
				// Update status and notify callback
				if (_activeExecutionInfos.TryGetValue(executionId, out var info))
				{
					info.Status = executionResult?.Status switch
					{
						ExecutionStatus.Cancelled => HostExecutionStatus.Cancelled,
						ExecutionStatus.Failed => HostExecutionStatus.Failed,
						_ => HostExecutionStatus.Completed
					};
					_executionCallback?.OnExecutionCompleted(info);

					// Remove after a short delay — tracked for graceful shutdown
					TrackBackgroundTask(Task.Run(async () =>
					{
						await Task.Delay(5000);
						_activeExecutionInfos.TryRemove(executionId, out _);
					}));
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
			LogOrchestrationExecutionFailed(ex, reg.Id);
			return (null, null);
		}
	}

	/// <summary>
	/// Updates trigger status based on trigger type after a successful execution.
	/// Handles loop re-scheduling, webhook waiting, and scheduler next-fire-time.
	/// </summary>
	internal void HandlePostExecutionTriggerStatus(
		TriggerRegistration reg,
		OrchestrationResult result,
		Dictionary<string, string>? parameters)
	{
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
					// Immediately re-run — tracked for graceful shutdown
					TrackBackgroundTask(Task.Run(async () =>
					{
						await Task.Delay(100); // Small delay to avoid tight loops
						await ExecuteOrchestrationAsync(reg, parameters);
					}));
				}
			}
			else
			{
				reg.Status = loopConfig.MaxIterations.HasValue && reg.RunCount >= loopConfig.MaxIterations.Value
					? TriggerStatus.Completed
					: TriggerStatus.Paused;
			}
		}
		else if (reg.Config is WebhookTriggerConfig)
		{
			reg.Status = TriggerStatus.Waiting;
		}
		else if (reg.Config is SchedulerTriggerConfig schedulerConfig)
		{
			reg.Status = TriggerStatus.Waiting;
			reg.NextFireTime = CalculateNextFireTime(schedulerConfig);
		}
		else if (reg.Config is ManualTriggerConfig)
		{
			// Manual triggers return to Idle after execution completes.
			reg.Status = TriggerStatus.Idle;
		}
	}

	/// <summary>
	/// Tracks a fire-and-forget task so it can be awaited during graceful shutdown.
	/// </summary>
	private void TrackBackgroundTask(Task task)
	{
		var id = Interlocked.Increment(ref _backgroundTaskId);
		_backgroundTasks[id] = task;

		// Self-cleanup when the task completes
		task.ContinueWith(static (_, state) =>
		{
			var (dict, taskId) = ((ConcurrentDictionary<int, Task>, int))state!;
			dict.TryRemove(taskId, out Task? _);
		}, (_backgroundTasks, id), TaskContinuationOptions.ExecuteSynchronously);
	}

	private void PersistTriggerOverride(TriggerRegistration reg)
	{
		try
		{
			var sidecarPath = GetSidecarPath(reg.OrchestrationPath);
			var data = new PersistedTrigger
			{
				OrchestrationPath = reg.OrchestrationPath,
				Trigger = reg.Config,
				Parameters = reg.Parameters,
			};

			var json = JsonSerializer.Serialize(data, _jsonOptions);
			File.WriteAllText(sidecarPath, json);
		}
		catch (Exception ex)
		{
			LogTriggerPersistFailed(ex, reg.Id);
		}
	}

	/// <summary>
	/// Persists an enabled-state override for a JSON-sourced trigger.
	/// This stores a lightweight sidecar file so that the user's enable/disable
	/// decision survives a restart (where the JSON file would otherwise reset it).
	/// </summary>
	private void PersistJsonTriggerEnabledOverride(string triggerId, bool enabled)
	{
		try
		{
			var overridePath = GetJsonTriggerOverridePath(triggerId);
			var data = new JsonTriggerEnabledOverride { TriggerId = triggerId, Enabled = enabled };
			var json = JsonSerializer.Serialize(data, _jsonOptions);
			File.WriteAllText(overridePath, json);
		}
		catch (Exception ex)
		{
			LogTriggerPersistFailed(ex, triggerId);
		}
	}

	/// <summary>
	/// Checks for a persisted enabled-state override for a JSON-sourced trigger.
	/// Returns null if no override exists.
	/// </summary>
	public bool? GetJsonTriggerEnabledOverride(string triggerId)
	{
		var overridePath = GetJsonTriggerOverridePath(triggerId);
		if (!File.Exists(overridePath))
			return null;

		try
		{
			var json = File.ReadAllText(overridePath);
			var data = JsonSerializer.Deserialize<JsonTriggerEnabledOverride>(json, _jsonOptions);
			return data?.Enabled;
		}
		catch (Exception ex)
		{
			LogOverrideReadFailed(overridePath, ex);
			return null;
		}
	}

	/// <summary>
	/// Removes the persisted enabled-state override for a JSON-sourced trigger.
	/// </summary>
	private void RemoveJsonTriggerEnabledOverride(string triggerId)
	{
		var overridePath = GetJsonTriggerOverridePath(triggerId);
		if (File.Exists(overridePath))
		{
			try { File.Delete(overridePath); }
			catch (Exception ex) { LogOverrideDeleteFailed(overridePath, ex); }
		}
	}

	private string GetJsonTriggerOverridePath(string triggerId)
	{
		var safeName = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(
				System.Text.Encoding.UTF8.GetBytes(triggerId)))[..16];
		return Path.Combine(_triggersDir, $"{safeName}.json-override.json");
	}

	private string GetSidecarPath(string orchestrationPath)
	{
		// Store in triggers directory using a safe filename derived from the path
		var safeName = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(
				System.Text.Encoding.UTF8.GetBytes(orchestrationPath)))[..16];
		return Path.Combine(_triggersDir, $"{safeName}.trigger.json");
	}

	/// <summary>
	/// Generates a unique ID for a trigger based on orchestration path and name.
	/// </summary>
	public static string GenerateTriggerId(string orchestrationPath, string? orchestrationName = null)
	{
		// Use the same algorithm as OrchestrationRegistry.GenerateId for consistency
		var name = orchestrationName ?? Path.GetFileNameWithoutExtension(orchestrationPath);
		var hash = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(
				System.Text.Encoding.UTF8.GetBytes(orchestrationPath)))[..8].ToLowerInvariant();
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

	private static DateTime CalculateNextCronTime(string cron)
	{
		var expression = Cronos.CronExpression.Parse(cron);
		var next = expression.GetNextOccurrence(DateTime.UtcNow);

		// Fallback to 1 hour if the expression yields no next occurrence
		return next ?? DateTime.UtcNow.AddHours(1);
	}

	/// <summary>
	/// Creates a clone of a trigger config with a new enabled state.
	/// </summary>
	public static TriggerConfig CloneTriggerConfigWithEnabled(TriggerConfig config, bool enabled)
	{
		return config switch
		{
			SchedulerTriggerConfig s => new SchedulerTriggerConfig
			{
				Type = s.Type,
				Enabled = enabled,
				InputHandlerPrompt = s.InputHandlerPrompt,
				InputHandlerModel = s.InputHandlerModel,
				Cron = s.Cron,
				IntervalSeconds = s.IntervalSeconds,
				MaxRuns = s.MaxRuns,
			},
			LoopTriggerConfig l => new LoopTriggerConfig
			{
				Type = l.Type,
				Enabled = enabled,
				InputHandlerPrompt = l.InputHandlerPrompt,
				InputHandlerModel = l.InputHandlerModel,
				DelaySeconds = l.DelaySeconds,
				MaxIterations = l.MaxIterations,
				ContinueOnFailure = l.ContinueOnFailure,
			},
		WebhookTriggerConfig w => new WebhookTriggerConfig
		{
			Type = w.Type,
			Enabled = enabled,
			InputHandlerPrompt = w.InputHandlerPrompt,
			InputHandlerModel = w.InputHandlerModel,
			Secret = w.Secret,
			MaxConcurrent = w.MaxConcurrent,
			Response = w.Response,
		},
			ManualTriggerConfig m => new ManualTriggerConfig
			{
				Type = m.Type,
				Enabled = enabled,
				InputHandlerPrompt = m.InputHandlerPrompt,
				InputHandlerModel = m.InputHandlerModel,
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
		var inputHandlerPrompt = element.TryGetProperty("inputHandlerPrompt", out var ihpProp) ? ihpProp.GetString() : null;
		var inputHandlerModel = element.TryGetProperty("inputHandlerModel", out var ihmProp) ? ihmProp.GetString() : null;

		return type switch
		{
			TriggerType.Scheduler => new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = enabled,
				InputHandlerPrompt = inputHandlerPrompt,
				InputHandlerModel = inputHandlerModel,
				Cron = element.TryGetProperty("cron", out var cron) ? cron.GetString() : null,
				IntervalSeconds = element.TryGetProperty("intervalSeconds", out var interval) ? interval.GetInt32() : null,
				MaxRuns = element.TryGetProperty("maxRuns", out var maxRuns) ? maxRuns.GetInt32() : null,
			},
			TriggerType.Loop => new LoopTriggerConfig
			{
				Type = TriggerType.Loop,
				Enabled = enabled,
				InputHandlerPrompt = inputHandlerPrompt,
				InputHandlerModel = inputHandlerModel,
				DelaySeconds = element.TryGetProperty("delaySeconds", out var delay) ? delay.GetInt32() : 0,
				MaxIterations = element.TryGetProperty("maxIterations", out var maxIter) ? maxIter.GetInt32() : null,
				ContinueOnFailure = element.TryGetProperty("continueOnFailure", out var cof) && cof.GetBoolean(),
			},
		TriggerType.Webhook => new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = enabled,
			InputHandlerPrompt = inputHandlerPrompt,
			InputHandlerModel = inputHandlerModel,
			Secret = element.TryGetProperty("secret", out var secret) ? secret.GetString() : null,
			MaxConcurrent = element.TryGetProperty("maxConcurrent", out var maxConc) ? maxConc.GetInt32() : 1,
			Response = element.TryGetProperty("response", out var responseProp)
				? DeserializeWebhookResponseConfig(responseProp)
				: null,
		},
			TriggerType.Manual => new ManualTriggerConfig
			{
				Type = TriggerType.Manual,
				Enabled = enabled,
				InputHandlerPrompt = inputHandlerPrompt,
				InputHandlerModel = inputHandlerModel,
			},
			_ => null,
		};
	}

	private static WebhookResponseConfig DeserializeWebhookResponseConfig(JsonElement element)
	{
		return new WebhookResponseConfig
		{
			WaitForResult = element.TryGetProperty("waitForResult", out var wfr) && wfr.GetBoolean(),
			ResponseTemplate = element.TryGetProperty("responseTemplate", out var rt) ? rt.GetString() : null,
			TimeoutSeconds = element.TryGetProperty("timeoutSeconds", out var ts) ? ts.GetInt32() : 120,
		};
	}

	private class PersistedTrigger
	{
		public required string OrchestrationPath { get; init; }
		public required TriggerConfig Trigger { get; init; }
		public Dictionary<string, string>? Parameters { get; init; }
	}

	private class JsonTriggerEnabledOverride
	{
		public required string TriggerId { get; init; }
		public required bool Enabled { get; init; }
	}

	// ── Structured Logging ──

	[LoggerMessage(Level = LogLevel.Information, Message = "Trigger '{TriggerId}' registered for '{Path}' (type={TriggerType}, source={Source})")]
	private partial void LogTriggerRegistered(string triggerId, string path, string triggerType, string source);

	[LoggerMessage(Level = LogLevel.Information, Message = "Trigger '{TriggerId}' removed")]
	private partial void LogTriggerRemoved(string triggerId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Trigger '{TriggerId}' {EnabledState}")]
	private partial void LogTriggerEnabledChanged(string triggerId, bool enabledState);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Webhook trigger '{TriggerId}' already has an active execution. Skipping.")]
	private partial void LogWebhookSkippedConcurrent(string triggerId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Persisted trigger references missing file '{Path}', skipping")]
	private partial void LogPersistedTriggerMissingFile(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load persisted trigger from '{File}'")]
	private partial void LogPersistedTriggerLoadFailed(Exception ex, string file);

	[LoggerMessage(Level = LogLevel.Information, Message = "TriggerManager started")]
	private partial void LogTriggerManagerStarted();

	[LoggerMessage(Level = LogLevel.Information, Message = "TriggerManager stopped")]
	private partial void LogTriggerManagerStopped();

	[LoggerMessage(Level = LogLevel.Error, Message = "Error in trigger manager loop")]
	private partial void LogTriggerManagerLoopError(Exception ex);

	[LoggerMessage(Level = LogLevel.Error, Message = "Trigger '{TriggerId}' execution failed")]
	private partial void LogTriggerExecutionFailed(Exception ex, string triggerId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Trigger '{TriggerId}' firing orchestration '{Path}' (execution={ExecutionId}, run #{RunCount})")]
	private partial void LogTriggerFiring(string triggerId, string path, string executionId, int runCount);

	[LoggerMessage(Level = LogLevel.Information, Message = "Trigger '{TriggerId}' input handler transformed {RawCount} raw params into {NewCount} params")]
	private partial void LogInputHandlerTransformed(string triggerId, int rawCount, int newCount);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Trigger '{TriggerId}' input handler prompt failed - using raw parameters")]
	private partial void LogInputHandlerFailed(Exception ex, string triggerId);

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to execute orchestration for trigger '{TriggerId}'")]
	private partial void LogOrchestrationExecutionFailed(Exception ex, string triggerId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist trigger override for '{TriggerId}'")]
	private partial void LogTriggerPersistFailed(Exception ex, string triggerId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Graceful shutdown starting: cancelling {ActiveCount} active executions, awaiting {BackgroundCount} background tasks")]
	private partial void LogGracefulShutdownStarting(int activeCount, int backgroundCount);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Graceful shutdown timed out: {RemainingCount} tasks still running")]
	private partial void LogGracefulShutdownTimeout(int remainingCount);

	[LoggerMessage(Level = LogLevel.Error, Message = "Error during graceful shutdown")]
	private partial void LogGracefulShutdownError(Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "Graceful shutdown complete")]
	private partial void LogGracefulShutdownComplete();

	[LoggerMessage(Level = LogLevel.Debug, Message = "Failed to extract metadata from orchestration file '{Path}'")]
	private partial void LogMetadataExtractionFailed(string path, Exception ex);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Failed to delete sidecar file '{Path}'")]
	private partial void LogSidecarDeleteFailed(string path, Exception ex);

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist run history for execution '{ExecutionId}'")]
	private partial void LogHistoryPersistFailed(string executionId, Exception ex);

	[LoggerMessage(Level = LogLevel.Error, Message = "Orchestration resume failed for '{OrchestrationId}' (execution '{ExecutionId}')")]
	private partial void LogOrchestrationResumeFailed(string orchestrationId, string executionId, Exception ex);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Skipping invalid orchestration file during scan: '{File}'")]
	private partial void LogOrchestrationFileScanFailed(string file, Exception ex);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Failed to read trigger override file '{Path}'")]
	private partial void LogOverrideReadFailed(string path, Exception ex);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Failed to delete trigger override file '{Path}'")]
	private partial void LogOverrideDeleteFailed(string path, Exception ex);
}
