using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>
/// Executes transform steps by evaluating template expressions.
/// No LLM call is made — this is pure string interpolation using
/// dependency outputs and parameters.
/// </summary>
public sealed partial class TransformStepExecutor : IStepExecutor
{
	private readonly ILogger<TransformStepExecutor> _logger;

	public TransformStepExecutor(ILogger<TransformStepExecutor> logger)
	{
		_logger = logger;
	}

	public OrchestrationStepType StepType => OrchestrationStepType.Transform;

	public Task<ExecutionResult> ExecuteAsync(
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		if (step is not TransformOrchestrationStep transformStep)
			throw new InvalidOperationException(
				$"TransformStepExecutor received a step of type '{step.GetType().Name}' " +
				$"but expected '{nameof(TransformOrchestrationStep)}'.");

		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);

		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			var output = TemplateResolver.Resolve(
				transformStep.Template,
				context.Parameters,
				context,
				step.DependsOn);

			LogTransformSuccess(step.Name, output.Length);

			return Task.FromResult(ExecutionResult.Succeeded(
				output,
				rawDependencyOutputs: rawDependencyOutputs));
		}
		catch (OperationCanceledException)
		{
			throw; // Let cancellation propagate
		}
		catch (Exception ex)
		{
			var errorMessage = $"Transform failed: {ex.Message}";
			LogTransformFailure(step.Name, ex);
			return Task.FromResult(ExecutionResult.Failed(errorMessage, rawDependencyOutputs));
		}
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Debug,
		Message = "Step '{StepName}' transform produced {OutputLength} character(s)")]
	private partial void LogTransformSuccess(string stepName, int outputLength);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' transform failed")]
	private partial void LogTransformFailure(string stepName, Exception ex);

	#endregion
}
