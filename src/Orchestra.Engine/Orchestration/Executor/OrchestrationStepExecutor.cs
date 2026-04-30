using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>
/// Executes <see cref="OrchestrationInvocationStep"/> by delegating to an
/// <see cref="IChildOrchestrationLauncher"/>. Supports both sync (block until child completes)
/// and async (dispatch and continue) invocation modes, optional LLM-driven parameter shaping
/// via <see cref="OrchestrationInvocationStep.InputHandlerPrompt"/>, and dynamic orchestration
/// IDs (the orchestration name supports template expressions).
/// </summary>
public sealed partial class OrchestrationStepExecutor : IStepExecutor
{
	private readonly IChildOrchestrationLauncher _launcher;
	private readonly AgentBuilder _agentBuilder;
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
	{
		_launcher = launcher;
		_agentBuilder = agentBuilder;
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

		// Resolve each child parameter value.
		var resolvedParameters = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (key, valueTemplate) in invocationStep.ChildParameters)
		{
			resolvedParameters[key] = TemplateResolver.Resolve(
				valueTemplate,
				context.Parameters,
				context,
				step.DependsOn,
				step);
		}

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

		// Build parent execution context so child run records and active info carry lineage.
		// The OrchestrationInfo's RunId is the parent execution ID.
		var parentContext = new ParentExecutionContext
		{
			ParentExecutionId = context.OrchestrationInfo.RunId,
			ParentStepName = step.Name,
			// Depth is computed by the launcher from the active executions table; we leave it 0 here
			// and rely on the launcher to look up the parent's depth. RootExecutionId likewise.
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
			return ExecutionResult.Failed(
				$"Failed to launch child orchestration '{resolvedOrchestrationId}': {ex.Message}",
				rawDependencyOutputs,
				errorCategory: StepErrorCategory.Unknown,
				trace: BuildTrace(invocationStep, resolvedOrchestrationId, resolvedParameters, executionId: null, errorMessage: ex.Message));
		}

		LogChildLaunched(step.Name, handle.ExecutionId, handle.OrchestrationName, invocationStep.Mode.ToString());

		if (invocationStep.Mode == OrchestrationInvocationMode.Async)
		{
			// Async: do not wait for the child; emit a JSON dispatch summary.
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
			return ExecutionResult.Succeeded(
				dispatchJson,
				rawDependencyOutputs: rawDependencyOutputs,
				trace: trace);
		}

		// Sync: await the child to terminal state.
		ChildOrchestrationResult terminal;
		try
		{
			terminal = await handle.Completion;
		}
		catch (Exception ex)
		{
			// Defensive: handle.Completion is documented as never throwing, but cover the case.
			LogChildCompletionThrew(step.Name, handle.ExecutionId, ex);
			return ExecutionResult.Failed(
				$"Child orchestration '{handle.OrchestrationName}' (executionId={handle.ExecutionId}) completion threw: {ex.Message}",
				rawDependencyOutputs,
				errorCategory: StepErrorCategory.Unknown,
				trace: BuildTrace(invocationStep, resolvedOrchestrationId, resolvedParameters, handle.ExecutionId, ex.Message));
		}

		var fullTrace = BuildTrace(
			invocationStep,
			resolvedOrchestrationId,
			resolvedParameters,
			handle.ExecutionId,
			terminal.ErrorMessage,
			finalContent: terminal.FinalContent);

		// Map terminal status to ExecutionResult.
		switch (terminal.Status)
		{
			case ExecutionStatus.Succeeded:
				return ExecutionResult.Succeeded(
					terminal.FinalContent ?? string.Empty,
					rawDependencyOutputs: rawDependencyOutputs,
					trace: fullTrace);

			case ExecutionStatus.Cancelled:
				return ExecutionResult.Failed(
					terminal.ErrorMessage ?? "Child orchestration was cancelled.",
					rawDependencyOutputs,
					errorCategory: StepErrorCategory.Unknown,
					trace: fullTrace);

			default:
				return ExecutionResult.Failed(
					terminal.ErrorMessage ?? $"Child orchestration ended with status '{terminal.Status}'.",
					rawDependencyOutputs,
					errorCategory: StepErrorCategory.Unknown,
					trace: fullTrace);
		}
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

			var agent = await _agentBuilder
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
}
