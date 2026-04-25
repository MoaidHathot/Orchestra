using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

internal sealed partial class HookRuntime
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false,
	};

	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<HookRuntime> _logger;
	private readonly string? _serverUrl;
	private readonly IOrchestrationReporter _reporter;

	public HookRuntime(ILoggerFactory loggerFactory, string? serverUrl, IOrchestrationReporter reporter)
	{
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<HookRuntime>();
		_serverUrl = serverUrl;
		_reporter = reporter;
	}

	public async Task<IReadOnlyList<HookExecutionRecord>> ExecuteAsync(
		IEnumerable<HookDefinition> hooks,
		HookEventType eventType,
		HookExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		var executions = new List<HookExecutionRecord>();

		foreach (var hook in hooks)
		{
			if (hook.On != eventType)
				continue;

			if (!Matches(hook, context))
				continue;

			executions.Add(await ExecuteHookSafeAsync(hook, context, cancellationToken).ConfigureAwait(false));
		}

		return executions;
	}

	private async Task<HookExecutionRecord> ExecuteHookSafeAsync(HookDefinition hook, HookExecutionContext context, CancellationToken cancellationToken)
	{
		var startedAt = DateTimeOffset.UtcNow;
		try
		{
			var result = await ExecuteHookAsync(hook, context, cancellationToken).ConfigureAwait(false);
			var record = BuildExecutionRecord(hook, context, startedAt, DateTimeOffset.UtcNow, result.Status, result.Content, result.ErrorMessage);
			_reporter.ReportHookExecuted(record);
			return record;
		}
		catch (Exception ex) when (hook.FailurePolicy is HookFailurePolicy.Ignore or HookFailurePolicy.Warn)
		{
			LogHookFailed(hook.Name ?? hook.On.ToString(), ex);
			var record = BuildExecutionRecord(hook, context, startedAt, DateTimeOffset.UtcNow, ExecutionStatus.Failed, content: null, errorMessage: ex.Message);
			_reporter.ReportHookExecuted(record);
			return record;
		}
	}

	private async Task<ExecutionResult> ExecuteHookAsync(HookDefinition hook, HookExecutionContext context, CancellationToken cancellationToken)
	{
		if (hook.Action.Type != HookActionType.Script)
			throw new InvalidOperationException($"Unsupported hook action type '{hook.Action.Type}'.");

		var payloadJson = JsonSerializer.Serialize(BuildPayload(hook, context), s_jsonOptions);
		var action = hook.Action;

		var scriptStep = new ScriptOrchestrationStep
		{
			Name = hook.Name ?? hook.On.ToString(),
			Type = OrchestrationStepType.Script,
			Shell = action.Shell ?? "pwsh",
			Script = action.Script,
			ScriptFile = action.ScriptFile,
			Arguments = action.Arguments,
			WorkingDirectory = action.WorkingDirectory,
			Environment = action.Environment,
			IncludeStdErr = action.IncludeStdErr,
			Stdin = payloadJson,
		};

		var executor = new ScriptStepExecutor(NullOrchestrationReporter.Instance, _loggerFactory.CreateLogger<ScriptStepExecutor>());
		var result = await executor.ExecuteAsync(scriptStep, context.ExecutionContext, cancellationToken).ConfigureAwait(false);

		if (result.Status == ExecutionStatus.Failed)
		{
			throw new InvalidOperationException(result.ErrorMessage ?? $"Hook '{hook.Name ?? hook.On.ToString()}' failed.");
		}

		return result;
	}

	private static HookExecutionRecord BuildExecutionRecord(
		HookDefinition hook,
		HookExecutionContext context,
		DateTimeOffset startedAt,
		DateTimeOffset completedAt,
		ExecutionStatus status,
		string? content,
		string? errorMessage)
	{
		return new HookExecutionRecord
		{
			HookName = hook.Name ?? ToEventName(hook.On),
			EventType = hook.On,
			Source = hook.Source,
			Status = status,
			StartedAt = startedAt,
			CompletedAt = completedAt,
			StepName = context.CurrentStepRecord?.StepName,
			ErrorMessage = errorMessage,
			Content = string.IsNullOrWhiteSpace(content) ? null : content,
			FailurePolicy = hook.FailurePolicy,
			ActionType = hook.Action.Type,
		};
	}

	private object BuildPayload(HookDefinition hook, HookExecutionContext context)
	{
		var detail = hook.Payload.Detail;
		var selectedSteps = SelectSteps(hook.Payload.Steps, context).ToArray();
		var stepDtos = selectedSteps.Select(step => ToStepPayload(step, detail)).ToArray();

		var statusCounts = context.StepRecords.Values
			.GroupBy(s => s.Status)
			.ToDictionary(g => JsonNamingPolicy.CamelCase.ConvertName(g.Key.ToString()), g => g.Count(), StringComparer.OrdinalIgnoreCase);

		return new
		{
			eventType = ToEventName(hook.On),
			timestamp = DateTimeOffset.UtcNow,
			orchestration = new
			{
				name = context.Orchestration.Name,
				version = context.Orchestration.Version,
				runId = context.RunId,
				status = context.OrchestrationStatus?.ToString(),
				triggerId = context.TriggerId,
				startedAt = context.RunStartedAt,
				completedAt = context.RunCompletedAt,
			},
			step = context.CurrentStepRecord is not null
				? ToStepPayload(context.CurrentStepRecord, detail)
				: null,
			summary = new
			{
				totalSteps = context.StepRecords.Count,
				statusCounts,
			},
			steps = stepDtos.Length > 0 ? stepDtos : null,
			finalContent = detail is HookPayloadDetail.Standard or HookPayloadDetail.Full ? context.FinalContent : null,
			refs = hook.Payload.IncludeRefs ? BuildRefs(context) : null,
		};
	}

	private object BuildRefs(HookExecutionContext context)
	{
		var apiRun = _serverUrl is not null
			? $"{_serverUrl.TrimEnd('/')}/api/history/{Uri.EscapeDataString(context.Orchestration.Name)}/{Uri.EscapeDataString(context.RunId)}"
			: null;

		return new
		{
			api = apiRun is not null ? new { run = apiRun } : null,
			mcp = new object[]
			{
				new
				{
					tool = "get_run",
					arguments = new
					{
						orchestrationName = context.Orchestration.Name,
						runId = context.RunId,
					}
				}
			}
		};
	}

	private static object ToStepPayload(StepRunRecord step, HookPayloadDetail detail)
	{
		return detail switch
		{
			HookPayloadDetail.Compact => new
			{
				name = step.StepName,
				status = step.Status.ToString(),
				errorMessage = step.ErrorMessage,
				errorCategory = step.ErrorCategory?.ToString(),
			},
			HookPayloadDetail.Standard => new
			{
				name = step.StepName,
				status = step.Status.ToString(),
				startedAt = step.StartedAt,
				completedAt = step.CompletedAt,
				durationSeconds = Math.Round(step.Duration.TotalSeconds, 2),
				actualModel = step.ActualModel,
				selectedModel = step.SelectedModel,
				usage = step.Usage,
				content = step.Content,
				errorMessage = step.ErrorMessage,
				errorCategory = step.ErrorCategory?.ToString(),
			},
			_ => new
			{
				name = step.StepName,
				status = step.Status.ToString(),
				startedAt = step.StartedAt,
				completedAt = step.CompletedAt,
				durationSeconds = Math.Round(step.Duration.TotalSeconds, 2),
				actualModel = step.ActualModel,
				selectedModel = step.SelectedModel,
				usage = step.Usage,
				content = step.Content,
				rawContent = step.RawContent,
				promptSent = step.PromptSent,
				rawDependencyOutputs = step.RawDependencyOutputs.Count > 0 ? step.RawDependencyOutputs : null,
				retryHistory = step.RetryHistory,
				trace = step.Trace,
				errorMessage = step.ErrorMessage,
				errorCategory = step.ErrorCategory?.ToString(),
			}
		};
	}

	private static IEnumerable<StepRunRecord> SelectSteps(HookStepSelection? selection, HookExecutionContext context)
	{
		if (selection is null)
			return [];

		if (selection.Names is { Length: > 0 } names)
		{
			var lookup = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
			return context.StepRecords.Values.Where(s => lookup.Contains(s.StepName));
		}

		return selection.Selector switch
		{
			HookStepSelector.None => [],
			HookStepSelector.Current => context.CurrentStepRecord is not null ? [context.CurrentStepRecord] : [],
			HookStepSelector.Failed => context.StepRecords.Values.Where(s => s.Status == ExecutionStatus.Failed),
			HookStepSelector.NonSucceeded => context.StepRecords.Values.Where(s => s.Status != ExecutionStatus.Succeeded),
			HookStepSelector.Terminal => context.TerminalStepNames
				.Select(name => context.StepRecords.TryGetValue(name, out var record) ? record : null)
				.Where(record => record is not null)!
				.Cast<StepRunRecord>(),
			HookStepSelector.All => context.StepRecords.Values,
			_ => [],
		};
	}

	private static bool Matches(HookDefinition hook, HookExecutionContext context)
	{
		var condition = hook.When?.Steps;
		if (condition is null)
			return true;

		var names = condition.Names.Length > 0
			? condition.Names
			: context.CurrentStepRecord is not null
				? [context.CurrentStepRecord.StepName]
				: [];

		if (names.Length == 0)
			return true;

		var statuses = names.Select(name => context.StepRecords.TryGetValue(name, out var record)
			? MatchesStatus(record.Status, condition.Status)
			: false);

		return condition.Match == HookStepMatch.All ? statuses.All(v => v) : statuses.Any(v => v);
	}

	private static bool MatchesStatus(ExecutionStatus status, HookStepStatusFilter filter)
	{
		return filter switch
		{
			HookStepStatusFilter.Any => true,
			HookStepStatusFilter.Succeeded => status == ExecutionStatus.Succeeded,
			HookStepStatusFilter.Failed => status == ExecutionStatus.Failed,
			HookStepStatusFilter.Cancelled => status == ExecutionStatus.Cancelled,
			HookStepStatusFilter.Skipped => status == ExecutionStatus.Skipped,
			HookStepStatusFilter.NoAction => status == ExecutionStatus.NoAction,
			HookStepStatusFilter.NonSucceeded => status != ExecutionStatus.Succeeded,
			_ => false,
		};
	}

	private static string ToEventName(HookEventType eventType)
	{
		return eventType switch
		{
			HookEventType.OrchestrationSuccess => "orchestration.success",
			HookEventType.OrchestrationFailure => "orchestration.failure",
			HookEventType.OrchestrationAfter => "orchestration.after",
			HookEventType.StepSuccess => "step.success",
			HookEventType.StepFailure => "step.failure",
			HookEventType.StepAfter => "step.after",
			_ => throw new InvalidOperationException($"Unknown hook event '{eventType}'.")
		};
	}

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Warning,
		Message = "Hook '{HookName}' failed")]
	private partial void LogHookFailed(string hookName, Exception ex);
}

internal sealed class HookExecutionContext
{
	public required Orchestration Orchestration { get; init; }

	public required OrchestrationExecutionContext ExecutionContext { get; init; }

	public required string RunId { get; init; }

	public required DateTimeOffset RunStartedAt { get; init; }

	public DateTimeOffset? RunCompletedAt { get; init; }

	public string? TriggerId { get; init; }

	public ExecutionStatus? OrchestrationStatus { get; init; }

	public required IReadOnlyDictionary<string, StepRunRecord> StepRecords { get; init; }

	public required IReadOnlyCollection<string> TerminalStepNames { get; init; }

	public StepRunRecord? CurrentStepRecord { get; init; }

	public string? FinalContent { get; init; }
}
