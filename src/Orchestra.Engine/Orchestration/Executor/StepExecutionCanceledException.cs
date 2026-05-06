namespace Orchestra.Engine;

/// <summary>
/// Carries partial step execution state when cancellation interrupts an executor.
/// </summary>
internal sealed class StepExecutionCanceledException : OperationCanceledException
{
	public StepExecutionCanceledException(
		string message,
		ExecutionResult partialResult,
		Exception? innerException,
		CancellationToken cancellationToken)
		: base(message, innerException, cancellationToken)
	{
		PartialResult = partialResult;
	}

	public ExecutionResult PartialResult { get; }
}
