using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>
/// Executes <see cref="OrchestrationInvocationStep"/> by delegating to an
/// <see cref="IChildOrchestrationLauncher"/>. Supports both sync (block until child completes)
/// and async (dispatch and continue) invocation modes, optional LLM-driven parameter shaping
/// via <see cref="OrchestrationInvocationStep.InputHandlerPrompt"/>, dynamic orchestration
/// IDs (the orchestration name supports template expressions), and forEach fan-out over a
/// JSON array (one child per item).
/// </summary>
public sealed partial class OrchestrationStepExecutor : IStepExecutor
{
	private readonly IChildOrchestrationLauncher _launcher;
	private readonly IAgentProviderRegistry _providerRegistry;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<OrchestrationStepExecutor> _logger;

	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	public OrchestrationStepExecutor(
		IChildOrchestrationLauncher launcher,
		AgentBuilder agentBuilder,
		IOrchestrationReporter reporter,
		ILogger<OrchestrationStepExecutor> logger)
		: this(launcher, new SingleAgentProviderRegistry(agentBuilder), reporter, logger)
	{
	}

	public OrchestrationStepExecutor(
		IChildOrchestrationLauncher launcher,
		IAgentProviderRegistry providerRegistry,
		IOrchestrationReporter reporter,
		ILogger<OrchestrationStepExecutor> logger)
	{
		_launcher = launcher;
		_providerRegistry = providerRegistry;
		_reporter = reporter;
		_logger = logger;
	}

	public OrchestrationStepType StepType => OrchestrationStepType.Orchestration;

	public async Task<ExecutionResult> ExecuteAsync(
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		if (step is not OrchestrationInvocationStep invocationStep)
		{
			throw new InvalidOperationException(
				$"OrchestrationStepExecutor received a step of type '{step.GetType().Name}' " +
				$"but expected '{nameof(OrchestrationInvocationStep)}'.");
		}

		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);

		// Resolve the orchestration ID from its template (supports dynamic selection).
		var resolvedOrchestrationId = TemplateResolver.Resolve(
			invocationStep.OrchestrationName,
			context.Parameters,
			context,
			step.DependsOn,
			step).Trim();

		if (string.IsNullOrWhiteSpace(resolvedOrchestrationId))
		{
			return ExecutionResult.Failed(
				"Resolved orchestration ID is empty after template expansion.",
				rawDependencyOutputs,
				errorCategory: StepErrorCategory.Unknown);
		}

		// Resolve each STATIC child parameter value (those applied to every invocation).
		var resolvedStaticParameters = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (key, valueTemplate) in invocationStep.ChildParameters)
		{
			resolvedStaticParameters[key] = TemplateResolver.Resolve(
				valueTemplate,
				context.Parameters,
				context,
				step.DependsOn,
				step);
		}

		// Branch: forEach fan-out vs. single-child invocation.
		if (!string.IsNullOrWhiteSpace(invocationStep.ForEach))
		{
			return await ExecuteForEachAsync(
				invocationStep,
				resolvedOrchestrationId,
				resolvedStaticParameters,
				rawDependencyOutputs,
				context,
				cancellationToken);
		}

		return await ExecuteSingleAsync(
			invocationStep,
			resolvedOrchestrationId,
			resolvedStaticParameters,
			rawDependencyOutputs,
			context,
			cancellationToken);
	}

	private async Task<ExecutionResult> ExecuteSingleAsync(
		OrchestrationInvocationStep invocationStep,
		string resolvedOrchestrationId,
		Dictionary<string, string> resolvedParameters,
		Dictionary<string, string> rawDependencyOutputs,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken)
	{
		var step = invocationStep;

		// Build the input handler delegate (if configured). The delegate is invoked inside the
		// CHILD orchestration's run scope so its agent build shares the child's CLI process.
		Func<CancellationToken, Task<Dictionary<string, string>?>>? inputHandlerTransform = null;
		if (!string.IsNullOrWhiteSpace(invocationStep.InputHandlerPrompt) && resolvedParameters.Count > 0)
		{
			var capturedParams = resolvedParameters;
			var capturedStep = invocationStep;
			var defaultModel = context.DefaultModel;
			inputHandlerTransform = async ct =>
				await RunInputHandlerAsync(capturedStep, capturedParams, defaultModel, ct);
		}

		var parentContext = new ParentExecutionContext
		{
			ParentExecutionId = context.OrchestrationInfo.RunId,
			ParentStepName = step.Name,
		};

		var request = new ChildLaunchRequest
		{
			OrchestrationId = resolvedOrchestrationId,
			Parameters = resolvedParameters,
			Mode = invocationStep.Mode == OrchestrationInvocationMode.Async
				? ChildLaunchMode.Async
				: ChildLaunchMode.Sync,
			TimeoutSeconds = invocationStep.Mode == OrchestrationInvocationMode.Sync
				? invocationStep.TimeoutSeconds
				: null,
			TriggeredBy = $"orchestration:{context.OrchestrationInfo.RunId}",
			ParentContext = parentContext,
			PreExecutionParameterTransform = inputHandlerTransform,
		};

		ChildOrchestrationHandle handle;
		try
		{
			handle = await _launcher.LaunchAsync(request, cancellationToken);
		}
		catch (ChildOrchestrationLaunchException ex)
		{
			LogLaunchFailed(step.Name, ex.ErrorCode, ex.Message);
			var trace = BuildTrace(invocationStep, resolvedOrchestrationId, resolvedParameters, executionId: null, errorMessage: ex.Message);
			_reporter.ReportStepTrace(step.Name, trace);
			return ExecutionResult.Failed(
				$"Failed to launch child orchestration '{resolvedOrchestrationId}': {ex.Message}",
				rawDependencyOutputs,
				errorCategory: StepErrorCategory.Unknown,
				trace: trace,
				childOrchestrationInfo: new ChildOrchestrationInfo
				{
					ExecutionId = string.Empty,
					OrchestrationName = resolvedOrchestrationId,
					Status = ExecutionStatus.Failed,
					ErrorMessage = ex.Message,
					StartedAt = DateTimeOffset.UtcNow,
				});
		}

		LogChildLaunched(step.Name, handle.ExecutionId, handle.OrchestrationName, invocationStep.Mode.ToString());

		if (invocationStep.Mode == OrchestrationInvocationMode.Async)
		{
			var dispatch = new
			{
				executionId = handle.ExecutionId,
				orchestrationId = handle.OrchestrationId,
				orchestrationName = handle.OrchestrationName,
				status = "dispatched",
				startedAt = handle.StartedAt,
			};
			var dispatchJson = JsonSerializer.Serialize(dispatch, s_jsonOptions);

			var trace = BuildTrace(invocationStep, resolvedOrchestrationId, resolvedParameters, handle.ExecutionId, errorMessage: null);
			_reporter.ReportStepTrace(step.Name, trace);
			return ExecutionResult.Succeeded(
				dispatchJson,
				rawDependencyOutputs: rawDependencyOutputs,
				trace: trace,
				childOrchestrationInfo: new ChildOrchestrationInfo
				{
					ExecutionId = handle.ExecutionId,
					OrchestrationId = handle.OrchestrationId,
					OrchestrationName = handle.OrchestrationName,
					Status = ExecutionStatus.Pending,
					StartedAt = handle.StartedAt,
				});
		}

		// Sync: await the child to terminal state.
		ChildOrchestrationResult terminal;
		try
		{
			terminal = await handle.Completion;
		}
		catch (Exception ex)
		{
			LogChildCompletionThrew(step.Name, handle.ExecutionId, ex);
			var trace = BuildTrace(invocationStep, resolvedOrchestrationId, resolvedParameters, handle.ExecutionId, ex.Message);
			_reporter.ReportStepTrace(step.Name, trace);
			return ExecutionResult.Failed(
				$"Child orchestration '{handle.OrchestrationName}' (executionId={handle.ExecutionId}) completion threw: {ex.Message}",
				rawDependencyOutputs,
				errorCategory: StepErrorCategory.Unknown,
				trace: trace,
				childOrchestrationInfo: new ChildOrchestrationInfo
				{
					ExecutionId = handle.ExecutionId,
					OrchestrationId = handle.OrchestrationId,
					OrchestrationName = handle.OrchestrationName,
					Status = ExecutionStatus.Failed,
					ErrorMessage = ex.Message,
					StartedAt = handle.StartedAt,
					CompletedAt = DateTimeOffset.UtcNow,
				});
		}

		var fullTrace = BuildTrace(
			invocationStep,
			resolvedOrchestrationId,
			resolvedParameters,
			handle.ExecutionId,
			terminal.ErrorMessage,
			finalContent: terminal.FinalContent);
		_reporter.ReportStepTrace(step.Name, fullTrace);

		var childInfo = BuildChildOrchestrationInfo(handle, terminal);

		switch (terminal.Status)
		{
			case ExecutionStatus.Succeeded:
				return ExecutionResult.Succeeded(
					terminal.FinalContent ?? string.Empty,
					rawDependencyOutputs: rawDependencyOutputs,
					trace: fullTrace,
					childOrchestrationInfo: childInfo);

			case ExecutionStatus.Cancelled:
				return ExecutionResult.Failed(
					terminal.ErrorMessage ?? "Child orchestration was cancelled.",
					rawDependencyOutputs,
					errorCategory: StepErrorCategory.Unknown,
					trace: fullTrace,
					childOrchestrationInfo: childInfo);

			default:
				return ExecutionResult.Failed(
					terminal.ErrorMessage ?? $"Child orchestration ended with status '{terminal.Status}'.",
					rawDependencyOutputs,
					errorCategory: StepErrorCategory.Unknown,
					trace: fullTrace,
					childOrchestrationInfo: childInfo);
		}
	}

	private async Task<ExecutionResult> ExecuteForEachAsync(
		OrchestrationInvocationStep step,
		string resolvedOrchestrationId,
		Dictionary<string, string> resolvedStaticParameters,
		Dictionary<string, string> rawDependencyOutputs,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken)
	{
		// Resolve the forEach template against the standard machinery so any
		// {{stepName.output}} / {{vars.*}} reference works.
		var resolvedForEach = TemplateResolver.Resolve(
			step.ForEach!,
			context.Parameters,
			context,
			step.DependsOn,
			step);

		// Extract the JSON array (optionally drilled into via ForEachPath).
		List<JsonElement> items;
		try
		{
			items = ExtractItemsArray(resolvedForEach, step.ForEachPath);
		}
		catch (Exception ex)
		{
			LogForEachParseFailed(step.Name, ex.Message);
			return ExecutionResult.Failed(
				$"Failed to parse 'forEach' as JSON array (forEachPath='{step.ForEachPath}'): {ex.Message}. Resolved template head: {Truncate(resolvedForEach, 200)}",
				rawDependencyOutputs,
				errorCategory: StepErrorCategory.Unknown);
		}

		LogForEachStarted(step.Name, items.Count, resolvedOrchestrationId, step.MaxConcurrency ?? 0, step.Mode.ToString());

		// Empty array: succeed deterministically with an empty rollup.
		if (items.Count == 0)
		{
			var emptyRollup = new
			{
				totalDispatched = 0,
				succeeded = 0,
				failed = 0,
				results = Array.Empty<object>(),
			};
			var emptyJson = JsonSerializer.Serialize(emptyRollup, s_jsonOptions);
			return ExecutionResult.Succeeded(
				emptyJson,
				rawDependencyOutputs: rawDependencyOutputs);
		}

		// Concurrency throttle: null/0 = unbounded.
		var concurrencyLimit = step.MaxConcurrency is > 0 ? step.MaxConcurrency.Value : items.Count;
		using var semaphore = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);

		// One task per item. Each task acquires the semaphore, launches the child, and
		// waits for its terminal state (sync mode) or returns immediately (async mode).
		var perItemTasks = new List<Task<ForEachItemOutcome>>(items.Count);
		for (var i = 0; i < items.Count; i++)
		{
			var index = i;
			var item = items[i];
			perItemTasks.Add(LaunchForEachItemAsync(
				step,
				resolvedOrchestrationId,
				resolvedStaticParameters,
				item,
				index,
				semaphore,
				context,
				cancellationToken));
		}

		// Wait on every task. Per-item failures are captured into the outcome — they do not
		// propagate as exceptions. This guarantees we surface every child's result rather
		// than dropping siblings when one fails.
		var outcomes = await Task.WhenAll(perItemTasks);

		var succeeded = 0;
		var failed = 0;
		var aggregate = new List<object>(outcomes.Length);
		foreach (var outcome in outcomes)
		{
			if (outcome.IsSuccess) succeeded++; else failed++;
			aggregate.Add(new
			{
				index = outcome.Index,
				executionId = outcome.ExecutionId,
				orchestrationName = outcome.OrchestrationName,
				status = outcome.Status.ToString().ToLowerInvariant(),
				errorMessage = outcome.ErrorMessage,
				finalContent = outcome.FinalContent,
				input = outcome.InputItemJson,
				startedAt = outcome.StartedAt,
				completedAt = outcome.CompletedAt,
			});
		}

		var rollup = new
		{
			totalDispatched = outcomes.Length,
			succeeded,
			failed,
			results = aggregate,
		};
		var rollupJson = JsonSerializer.Serialize(rollup, s_jsonOptions);

		LogForEachCompleted(step.Name, outcomes.Length, succeeded, failed);

		// Decide final step status.
		// In async mode, "succeeded" means "dispatched" — failed only counts launch failures.
		// In sync mode with continueOnItemFailure=true, the step succeeds even if some
		// children failed; the rollup carries the per-item status for the parent to inspect.
		// With continueOnItemFailure=false, any failed child fails the step.
		var aggregateStatusInfo = new ChildOrchestrationInfo
		{
			ExecutionId = $"foreach:{context.OrchestrationInfo.RunId}:{step.Name}",
			OrchestrationName = resolvedOrchestrationId,
			Status = failed == 0 ? ExecutionStatus.Succeeded : ExecutionStatus.Failed,
			ErrorMessage = failed == 0 ? null : $"{failed} of {outcomes.Length} child invocations failed.",
			FinalContent = rollupJson,
			StartedAt = DateTimeOffset.UtcNow,
			CompletedAt = DateTimeOffset.UtcNow,
		};

		if (failed > 0 && !step.ContinueOnItemFailure)
		{
			return ExecutionResult.Failed(
				$"forEach: {failed} of {outcomes.Length} child invocations failed (continueOnItemFailure=false).",
				rawDependencyOutputs,
				errorCategory: StepErrorCategory.Unknown,
				childOrchestrationInfo: aggregateStatusInfo);
		}

		return ExecutionResult.Succeeded(
			rollupJson,
			rawDependencyOutputs: rawDependencyOutputs,
			childOrchestrationInfo: aggregateStatusInfo);
	}

	private async Task<ForEachItemOutcome> LaunchForEachItemAsync(
		OrchestrationInvocationStep step,
		string resolvedOrchestrationId,
		Dictionary<string, string> staticParameters,
		JsonElement item,
		int index,
		SemaphoreSlim semaphore,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken)
	{
		await semaphore.WaitAsync(cancellationToken);
		try
		{
			// Per-child parameter map: static params + item bound to step.ItemParameter.
			var itemJson = item.GetRawText();
			var perItemParams = new Dictionary<string, string>(staticParameters, StringComparer.Ordinal)
			{
				[step.ItemParameter!] = itemJson,
			};

			var parentContext = new ParentExecutionContext
			{
				ParentExecutionId = context.OrchestrationInfo.RunId,
				ParentStepName = step.Name,
			};

			var request = new ChildLaunchRequest
			{
				OrchestrationId = resolvedOrchestrationId,
				Parameters = perItemParams,
				Mode = step.Mode == OrchestrationInvocationMode.Async ? ChildLaunchMode.Async : ChildLaunchMode.Sync,
				TimeoutSeconds = step.Mode == OrchestrationInvocationMode.Sync ? step.TimeoutSeconds : null,
				TriggeredBy = $"orchestration:{context.OrchestrationInfo.RunId}",
				ParentContext = parentContext,
			};

			ChildOrchestrationHandle handle;
			try
			{
				handle = await _launcher.LaunchAsync(request, cancellationToken);
			}
			catch (ChildOrchestrationLaunchException ex)
			{
				LogLaunchFailed(step.Name, ex.ErrorCode, ex.Message);
				return new ForEachItemOutcome
				{
					Index = index,
					ExecutionId = string.Empty,
					OrchestrationName = resolvedOrchestrationId,
					Status = ExecutionStatus.Failed,
					ErrorMessage = ex.Message,
					InputItemJson = itemJson,
					StartedAt = DateTimeOffset.UtcNow,
					CompletedAt = DateTimeOffset.UtcNow,
				};
			}

			LogChildLaunched(step.Name, handle.ExecutionId, handle.OrchestrationName, step.Mode.ToString());

			if (step.Mode == OrchestrationInvocationMode.Async)
			{
				return new ForEachItemOutcome
				{
					Index = index,
					ExecutionId = handle.ExecutionId,
					OrchestrationName = handle.OrchestrationName,
					Status = ExecutionStatus.Pending,
					InputItemJson = itemJson,
					StartedAt = handle.StartedAt,
				};
			}

			ChildOrchestrationResult terminal;
			try
			{
				terminal = await handle.Completion;
			}
			catch (Exception ex)
			{
				LogChildCompletionThrew(step.Name, handle.ExecutionId, ex);
				return new ForEachItemOutcome
				{
					Index = index,
					ExecutionId = handle.ExecutionId,
					OrchestrationName = handle.OrchestrationName,
					Status = ExecutionStatus.Failed,
					ErrorMessage = ex.Message,
					InputItemJson = itemJson,
					StartedAt = handle.StartedAt,
					CompletedAt = DateTimeOffset.UtcNow,
				};
			}

			return new ForEachItemOutcome
			{
				Index = index,
				ExecutionId = handle.ExecutionId,
				OrchestrationName = handle.OrchestrationName,
				Status = terminal.Status,
				ErrorMessage = terminal.ErrorMessage,
				FinalContent = terminal.FinalContent,
				InputItemJson = itemJson,
				StartedAt = terminal.StartedAt,
				CompletedAt = terminal.CompletedAt,
			};
		}
		finally
		{
			semaphore.Release();
		}
	}

	/// <summary>
	/// Extracts a JSON array from a resolved forEach template. Supports:
	///   - The template resolves directly to a JSON array (e.g. <c>"[{...},{...}]"</c>).
	///   - The template resolves to a JSON object and <paramref name="forEachPath"/> drills
	///     into a property (dotted path supported) containing the array.
	/// </summary>
	private static List<JsonElement> ExtractItemsArray(string resolvedForEach, string? forEachPath)
	{
		var trimmed = (resolvedForEach ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(trimmed))
			return new List<JsonElement>();

		// Strip a markdown code fence if a prior step emitted one.
		if (trimmed.StartsWith("```"))
		{
			var nl = trimmed.IndexOf('\n');
			if (nl > 0) trimmed = trimmed[(nl + 1)..].Trim();
			if (trimmed.EndsWith("```")) trimmed = trimmed[..^3].Trim();
		}

		using var doc = JsonDocument.Parse(trimmed);
		var root = doc.RootElement;

		JsonElement arrayElement = default;

		if (root.ValueKind == JsonValueKind.Array && string.IsNullOrWhiteSpace(forEachPath))
		{
			arrayElement = root;
		}
		else if (!string.IsNullOrWhiteSpace(forEachPath))
		{
			var current = root;
			foreach (var segment in forEachPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
			{
				if (current.ValueKind != JsonValueKind.Object)
					throw new InvalidOperationException($"forEachPath segment '{segment}' cannot be applied: current node is {current.ValueKind}.");

				var matched = false;
				foreach (var prop in current.EnumerateObject())
				{
					if (string.Equals(prop.Name, segment, StringComparison.OrdinalIgnoreCase))
					{
						current = prop.Value;
						matched = true;
						break;
					}
				}
				if (!matched)
					throw new InvalidOperationException($"forEachPath segment '{segment}' not found in JSON object.");
			}
			if (current.ValueKind != JsonValueKind.Array)
				throw new InvalidOperationException($"forEachPath '{forEachPath}' resolved to a {current.ValueKind}, not an Array.");
			arrayElement = current;
		}
		else
		{
			throw new InvalidOperationException($"forEach template resolved to a {root.ValueKind}, not an Array. Either emit a JSON array or set 'forEachPath' to drill into the object.");
		}

		// Materialize into clones so the JsonDocument can be disposed.
		var list = new List<JsonElement>(arrayElement.GetArrayLength());
		foreach (var element in arrayElement.EnumerateArray())
		{
			list.Add(element.Clone());
		}
		return list;
	}

	private static ChildOrchestrationInfo BuildChildOrchestrationInfo(
		ChildOrchestrationHandle handle,
		ChildOrchestrationResult terminal)
	{
		var orchestrationResult = terminal.OrchestrationResult;
		var stepResults = orchestrationResult is null
			? (IReadOnlyDictionary<string, ChildStepInfo>)new Dictionary<string, ChildStepInfo>(StringComparer.OrdinalIgnoreCase)
			: orchestrationResult.StepResults.ToDictionary(
				kvp => kvp.Key,
				kvp => new ChildStepInfo
				{
					Status = kvp.Value.Status,
					Content = kvp.Value.Content,
					RawContent = kvp.Value.RawContent,
					ErrorMessage = kvp.Value.ErrorMessage,
					SavedFiles = kvp.Value.SavedFiles ?? [],
				},
				StringComparer.OrdinalIgnoreCase);

		return new ChildOrchestrationInfo
		{
			ExecutionId = handle.ExecutionId,
			OrchestrationId = handle.OrchestrationId,
			OrchestrationName = handle.OrchestrationName,
			Status = terminal.Status,
			ErrorMessage = terminal.ErrorMessage,
			FinalContent = terminal.FinalContent,
			CompletionReason = orchestrationResult?.CompletionReason,
			Cancellation = orchestrationResult?.Cancellation,
			StepResults = stepResults,
			StartedAt = terminal.StartedAt,
			CompletedAt = terminal.CompletedAt,
		};
	}

	private async Task<Dictionary<string, string>?> RunInputHandlerAsync(
		OrchestrationInvocationStep step,
		Dictionary<string, string> resolvedParameters,
		string? defaultModel,
		CancellationToken cancellationToken)
	{
		try
		{
			var rawInputJson = JsonSerializer.Serialize(resolvedParameters, s_jsonOptions);
			var fullPrompt = $"{step.InputHandlerPrompt}\n\nRaw input:\n{rawInputJson}";

			// Child-orchestration input transforms run on the host default agent provider.
			// They execute inside the child's run scope, which opens the host-default scope
			// whenever a preExecutionParameterTransform is supplied.
			var agent = await _providerRegistry.Resolve(null)
				.BuildAgentAsync(new AgentBuildConfig
				{
					Model = step.InputHandlerModel ?? defaultModel ?? "claude-opus-4.6",
					SystemPrompt = "You are a parameter transformer. Given a prompt and raw input, respond with ONLY a valid JSON object mapping parameter names to string values. No markdown, no explanation — just the JSON object.",
					Mcps = [],
				}, cancellationToken);

			var task = agent.SendAsync(fullPrompt);
			var result = await task.GetResultAsync();

			var content = result.Content.Trim();
			if (content.StartsWith("```"))
			{
				var firstNewline = content.IndexOf('\n');
				if (firstNewline >= 0) content = content[(firstNewline + 1)..];
				if (content.EndsWith("```")) content = content[..^3].TrimEnd();
			}

			var transformed = JsonSerializer.Deserialize<Dictionary<string, string>>(content, s_jsonOptions);
			if (transformed is { Count: > 0 })
			{
				LogInputHandlerTransformed(step.Name, resolvedParameters.Count, transformed.Count);
				return transformed;
			}
			LogInputHandlerEmpty(step.Name);
			return null;
		}
		catch (Exception ex)
		{
			LogInputHandlerFailed(step.Name, ex);
			return null;
		}
	}

	private static StepExecutionTrace BuildTrace(
		OrchestrationInvocationStep step,
		string resolvedOrchestrationId,
		Dictionary<string, string> resolvedParameters,
		string? executionId,
		string? errorMessage,
		string? finalContent = null)
	{
		var system = new System.Text.StringBuilder();
		system.AppendLine($"Child orchestration: {resolvedOrchestrationId}");
		system.AppendLine($"Mode: {step.Mode}");
		if (executionId is not null) system.AppendLine($"ExecutionId: {executionId}");
		if (resolvedParameters.Count > 0)
		{
			system.AppendLine("Parameters:");
			foreach (var (k, v) in resolvedParameters)
				system.AppendLine($"  {k} = {Truncate(v, 200)}");
		}
		if (!string.IsNullOrWhiteSpace(step.InputHandlerPrompt))
			system.AppendLine($"Input handler: enabled");

		return new StepExecutionTrace
		{
			SystemPrompt = system.ToString().TrimEnd(),
			UserPromptRaw = step.OrchestrationName,
			FinalResponse = finalContent ?? errorMessage ?? string.Empty,
			ResponseSegments = errorMessage is not null ? [errorMessage] : [],
		};
	}

	private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

	private sealed class ForEachItemOutcome
	{
		public required int Index { get; init; }
		public required string ExecutionId { get; init; }
		public required string OrchestrationName { get; init; }
		public required ExecutionStatus Status { get; init; }
		public string? ErrorMessage { get; init; }
		public string? FinalContent { get; init; }
		public string InputItemJson { get; init; } = string.Empty;
		public DateTimeOffset StartedAt { get; init; }
		public DateTimeOffset? CompletedAt { get; init; }
		public bool IsSuccess => Status == ExecutionStatus.Succeeded;
	}

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Step '{StepName}' launched child '{OrchestrationName}' as executionId={ExecutionId} (mode={Mode}).")]
	private partial void LogChildLaunched(string stepName, string executionId, string orchestrationName, string mode);

	[LoggerMessage(Level = LogLevel.Error,
		Message = "Step '{StepName}' failed to launch child orchestration. ErrorCode={ErrorCode}, Message={Message}")]
	private partial void LogLaunchFailed(string stepName, string errorCode, string message);

	[LoggerMessage(Level = LogLevel.Error,
		Message = "Step '{StepName}' child completion task threw (executionId={ExecutionId}). Defensive: this should not normally happen.")]
	private partial void LogChildCompletionThrew(string stepName, string executionId, Exception ex);

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Step '{StepName}' input handler transformed {InputCount} → {OutputCount} parameter(s).")]
	private partial void LogInputHandlerTransformed(string stepName, int inputCount, int outputCount);

	[LoggerMessage(Level = LogLevel.Warning,
		Message = "Step '{StepName}' input handler returned empty/null transformation; using untransformed parameters.")]
	private partial void LogInputHandlerEmpty(string stepName);

	[LoggerMessage(Level = LogLevel.Warning,
		Message = "Step '{StepName}' input handler failed; using untransformed parameters.")]
	private partial void LogInputHandlerFailed(string stepName, Exception ex);

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Step '{StepName}' forEach launching {ItemCount} children of '{OrchestrationName}' (maxConcurrency={MaxConcurrency}, mode={Mode}).")]
	private partial void LogForEachStarted(string stepName, int itemCount, string orchestrationName, int maxConcurrency, string mode);

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Step '{StepName}' forEach completed: {Total} dispatched, {Succeeded} succeeded, {Failed} failed.")]
	private partial void LogForEachCompleted(string stepName, int total, int succeeded, int failed);

	[LoggerMessage(Level = LogLevel.Error,
		Message = "Step '{StepName}' forEach parsing failed: {Message}")]
	private partial void LogForEachParseFailed(string stepName, string message);
}
