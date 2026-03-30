using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

public partial class PromptExecutor : Executor<PromptOrchestrationStep>
{
	private readonly AgentBuilder _agentBuilder;
	private readonly IOrchestrationReporter _reporter;
	private readonly IPromptFormatter _formatter;
	private readonly EngineToolRegistry _engineToolRegistry;
	private readonly ILogger<PromptExecutor> _logger;

	public PromptExecutor(
		AgentBuilder agentBuilder,
		IOrchestrationReporter reporter,
		IPromptFormatter formatter,
		ILogger<PromptExecutor> logger,
		EngineToolRegistry? engineToolRegistry = null)
	{
		_agentBuilder = agentBuilder;
		_reporter = reporter;
		_formatter = formatter;
		_engineToolRegistry = engineToolRegistry ?? EngineToolRegistry.CreateDefault();
		_logger = logger;
	}

	public override async Task<ExecutionResult> ExecuteAsync(
		PromptOrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		// Capture raw dependency outputs before building the prompt
		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);

		// Get the raw user prompt before input handler processing
		var userPromptRaw = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);
		userPromptRaw = TemplateResolver.Resolve(userPromptRaw, context.Parameters, context, step.DependsOn, step);

		// Create event processor to handle agent events and collect trace data
		var eventProcessor = new AgentEventProcessor(_reporter, step.Name);

		// Build MCP server descriptions for trace diagnostics
		var mcpServerDescriptions = BuildMcpServerDescriptions(step.Mcps);

		try
		{
			// Build the user prompt, incorporating dependency outputs and parameters
			var userPrompt = BuildUserPrompt(step, context);

			// Log step MCPs for debugging
			LogStepMcps(step.Name, step.Mcps.Length, string.Join(", ", step.Mcps.Select(m => m.Name)));
			LogStepMcpNames(step.Name, string.Join(", ", step.McpNames));

			// Create a fresh engine tool context for this execution
			var engineToolCtx = new EngineToolContext();
			var engineTools = _engineToolRegistry.GetAll();

		// Build and run the agent using an immutable config snapshot (thread-safe)
		var config = new AgentBuildConfig
		{
			Model = step.Model,
			SystemPrompt = step.SystemPrompt,
			Mcps = step.Mcps,
			Subagents = step.Subagents,
			ReasoningLevel = step.ReasoningLevel,
			SystemPromptMode = step.SystemPromptMode ?? context.DefaultSystemPromptMode,
			Reporter = _reporter,
			EngineTools = engineTools,
			EngineToolCtx = engineToolCtx,
		};

		var agent = await _agentBuilder
			.BuildAgentAsync(config, cancellationToken);

			var task = agent.SendAsync(userPrompt, cancellationToken);

			// Process all agent events, collecting trace data
			await eventProcessor.ProcessEventsAsync(task, cancellationToken);

			var result = await task.GetResultAsync();

			// Check if any required MCP servers failed to start.
			// When MCP servers fail, the LLM runs without the expected tools and produces
			// unreliable output. Fail the step early with a clear error rather than
			// propagating the LLM's confused response as a "success."
			var failedMcpServers = eventProcessor.GetFailedMcpServers();
			if (failedMcpServers.Count > 0 && step.Mcps.Length > 0)
			{
				var requiredFailed = failedMcpServers
					.Where(f => step.Mcps.Any(m => string.Equals(m.Name, f, StringComparison.OrdinalIgnoreCase)))
					.ToList();

				if (requiredFailed.Count > 0)
				{
					var serverList = string.Join(", ", requiredFailed);
					var errorMessage = $"Required MCP server(s) failed to start: {serverList}. The step cannot execute without these tools.";
					var mcpFailTrace = eventProcessor.BuildPartialTrace(step.SystemPrompt, userPromptRaw, mcpServerDescriptions);
					_reporter.ReportStepTrace(step.Name, mcpFailTrace);
					_reporter.ReportStepError(step.Name, errorMessage);
					LogMcpServersFailed(step.Name, serverList);
					return ExecutionResult.Failed(errorMessage, rawDependencyOutputs, trace: mcpFailTrace);
				}
			}

			// Report model and usage metadata if available
			if (result.ActualModel is not null)
			{
				_reporter.ReportStepCompleted(step.Name, result);
			}
			if (result.Usage is not null && result.ActualModel is not null)
			{
				_reporter.ReportUsage(step.Name, result.ActualModel, result.Usage);
			}

			var content = result.Content;
			string? rawContent = null;
			string? outputHandlerResult = null;

			// Apply output handler if specified
			if (step.OutputHandlerPrompt is not null)
			{
				rawContent = content;
				content = await RunHandlerAsync(step.OutputHandlerPrompt, content, step.Model, cancellationToken);
				outputHandlerResult = content;
			}

			// Convert usage to our TokenUsage type
			TokenUsage? tokenUsage = null;
			if (result.Usage is not null)
			{
				tokenUsage = new TokenUsage
				{
					InputTokens = (int)(result.Usage.InputTokens ?? 0),
					OutputTokens = (int)(result.Usage.OutputTokens ?? 0)
				};
			}

			// Build the execution trace from collected data
			var trace = eventProcessor.BuildTrace(
				step.SystemPrompt,
				userPromptRaw,
				userPrompt,
				rawContent ?? result.Content,
				outputHandlerResult,
				mcpServerDescriptions);

			// Report the step trace for live trace viewing
			_reporter.ReportStepTrace(step.Name, trace);

		// Check if an engine tool overrode the status (e.g., LLM called orchestra_set_status)
		if (engineToolCtx.HasStatusOverride && engineToolCtx.StatusOverride == ExecutionStatus.Failed)
		{
			var reason = engineToolCtx.StatusReason ?? "Step marked as failed by LLM";
			LogEngineToolStatusOverride(step.Name, reason);
			_reporter.ReportStepError(step.Name, reason);
			return WithOrchestrationComplete(ExecutionResult.Failed(
				reason,
				rawDependencyOutputs,
				userPrompt,
				result.ActualModel,
				trace), engineToolCtx, step.Name);
		}

		if (engineToolCtx.HasStatusOverride && engineToolCtx.StatusOverride == ExecutionStatus.NoAction)
		{
			var reason = engineToolCtx.StatusReason ?? "No action needed";
			LogEngineToolNoActionOverride(step.Name, reason);
			return WithOrchestrationComplete(ExecutionResult.NoAction(
				reason,
				rawDependencyOutputs,
				userPrompt,
				result.ActualModel,
				tokenUsage,
				trace), engineToolCtx, step.Name);
		}

		if (engineToolCtx.HasStatusOverride && engineToolCtx.StatusOverride == ExecutionStatus.Succeeded)
		{
			var reason = engineToolCtx.StatusReason ?? "Step marked as succeeded by LLM";
			LogEngineToolSuccessOverride(step.Name, reason);
		}

			return WithOrchestrationComplete(ExecutionResult.Succeeded(
				content,
				rawContent,
				rawDependencyOutputs,
				userPrompt,
				result.ActualModel,
				tokenUsage,
				trace), engineToolCtx, step.Name);
		}
		catch (OperationCanceledException)
		{
			throw; // Let cancellation propagate to the caller for timeout handling
		}
		catch (Exception ex)
		{
			// Build partial trace even on failure
			var trace = eventProcessor.BuildPartialTrace(step.SystemPrompt, userPromptRaw, mcpServerDescriptions);

			// Report the partial trace for live trace viewing
			_reporter.ReportStepTrace(step.Name, trace);

			_reporter.ReportStepError(step.Name, ex.Message);
			return ExecutionResult.Failed(ex.Message, rawDependencyOutputs, trace: trace);
		}
	}

	private string BuildUserPrompt(PromptOrchestrationStep step, OrchestrationExecutionContext context)
	{
		var userPrompt = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);

		// Resolve {{stepName.output}} and {{stepName.rawOutput}} template expressions inline.
		// This uses the same TemplateResolver as Command/Http/Transform steps, with a fallback
		// to TryGetResult for steps not listed in DependsOn (e.g. transitive dependencies).
		userPrompt = TemplateResolver.Resolve(userPrompt, context.Parameters, context, step.DependsOn, step);

		var dependencyOutputsDict = context.GetDependencyOutputs(step.DependsOn);
		var dependencyOutputs = _formatter.FormatDependencyOutputs(dependencyOutputsDict);
		var loopFeedback = context.ConsumeLoopFeedback(step.Name);

		return _formatter.BuildUserPrompt(userPrompt, dependencyOutputs, loopFeedback, step.InputHandlerPrompt);
	}

	private static string InjectParameters(string prompt, string[] parameterNames, Dictionary<string, string> parameters)
	{
		if (parameterNames.Length == 0 || parameters.Count == 0)
			return prompt;

		var result = prompt;
		foreach (var name in parameterNames)
		{
			if (parameters.TryGetValue(name, out var value))
			{
				result = result.Replace($"{{{{{name}}}}}", value);
			}
		}

		return result;
	}

	private static List<string> BuildMcpServerDescriptions(Mcp[] mcps)
	{
		var descriptions = new List<string>(mcps.Length);
		foreach (var mcp in mcps)
		{
			var desc = mcp switch
			{
				LocalMcp local => $"{mcp.Name} (local: {local.Command} {string.Join(" ", local.Arguments)})",
				RemoteMcp remote => $"{mcp.Name} (remote: {remote.Endpoint})",
				_ => mcp.Name,
			};
			descriptions.Add(desc);
		}
		return descriptions;
	}

	private async Task<string> RunHandlerAsync(
		string handlerPrompt,
		string content,
		string model,
		CancellationToken cancellationToken)
	{
		var systemPrompt = _formatter.BuildTransformationSystemPrompt(handlerPrompt);

		var config = new AgentBuildConfig
		{
			Model = model,
			SystemPrompt = systemPrompt,
			SystemPromptMode = SystemPromptMode.Replace,
			Mcps = [],
			Reporter = _reporter,
		};

		var agent = await _agentBuilder
			.BuildAgentAsync(config, cancellationToken);

		var wrappedContent = _formatter.WrapContentForTransformation(content);

		var task = agent.SendAsync(wrappedContent, cancellationToken);
		var result = await task.GetResultAsync();

		return result.Content;
	}

	/// <summary>
	/// Copies orchestration-complete flags from the engine tool context onto the execution result.
	/// If the LLM called orchestra_complete, the returned result will carry the signal
	/// so the orchestration executor can halt all remaining steps.
	/// </summary>
	private static ExecutionResult WithOrchestrationComplete(ExecutionResult result, EngineToolContext ctx, string stepName)
	{
		if (!ctx.OrchestrationCompleteRequested)
			return result;

		return new ExecutionResult
		{
			Content = result.Content,
			Status = result.Status,
			ErrorMessage = result.ErrorMessage,
			RawContent = result.RawContent,
			RawDependencyOutputs = result.RawDependencyOutputs,
			PromptSent = result.PromptSent,
			ActualModel = result.ActualModel,
			Usage = result.Usage,
			Trace = result.Trace,
			OrchestrationCompleteRequested = true,
			OrchestrationCompleteStatus = ctx.OrchestrationCompleteStatus,
			OrchestrationCompleteReason = ctx.OrchestrationCompleteReason,
			OrchestrationCompleteStepName = stepName,
		};
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Debug,
		Message = "Step '{StepName}' has {McpCount} MCPs: [{McpNames}]")]
	private partial void LogStepMcps(string stepName, int mcpCount, string mcpNames);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Debug,
		Message = "Step '{StepName}' McpNames configuration: [{McpNames}]")]
	private partial void LogStepMcpNames(string stepName, string mcpNames);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' status overridden to failed by engine tool: {Reason}")]
	private partial void LogEngineToolStatusOverride(string stepName, string reason);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' explicitly marked as succeeded by engine tool: {Reason}")]
	private partial void LogEngineToolSuccessOverride(string stepName, string reason);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' marked as no_action by engine tool: {Reason}")]
	private partial void LogEngineToolNoActionOverride(string stepName, string reason);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' failed because required MCP server(s) did not start: {Servers}")]
	private partial void LogMcpServersFailed(string stepName, string servers);

	#endregion
}
