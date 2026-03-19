using System.Collections.Concurrent;

namespace Orchestra.Engine;

/// <summary>
/// Registry that maps <see cref="OrchestrationStepType"/> values to their
/// corresponding <see cref="IStepExecutor"/> implementations.
/// Custom step types can be registered to extend the orchestration engine.
/// </summary>
public sealed class StepExecutorRegistry
{
	private readonly ConcurrentDictionary<OrchestrationStepType, IStepExecutor> _executors = new();

	/// <summary>
	/// Registers an executor for a given step type.
	/// Overwrites any previously registered executor for the same type.
	/// </summary>
	public StepExecutorRegistry Register(IStepExecutor executor)
	{
		_executors[executor.StepType] = executor;
		return this;
	}

	/// <summary>
	/// Resolves the executor for the given step type.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown when no executor is registered for the step type.</exception>
	public IStepExecutor Resolve(OrchestrationStepType stepType)
	{
		if (_executors.TryGetValue(stepType, out var executor))
			return executor;

		throw new InvalidOperationException(
			$"No step executor registered for step type '{stepType}'. " +
			$"Register an IStepExecutor via StepExecutorRegistry.Register().");
	}

	/// <summary>
	/// Attempts to resolve the executor for the given step type.
	/// Returns false if no executor is registered.
	/// </summary>
	public bool TryResolve(OrchestrationStepType stepType, out IStepExecutor? executor)
	{
		return _executors.TryGetValue(stepType, out executor);
	}

	/// <summary>
	/// Returns all registered step types.
	/// </summary>
	public IReadOnlyCollection<OrchestrationStepType> RegisteredTypes => [.. _executors.Keys];
}
