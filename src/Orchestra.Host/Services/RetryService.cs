using Orchestra.Engine;

namespace Orchestra.Host.Services;

/// <summary>
/// Modes describing how an existing run should be retried.
/// </summary>
public enum RetryMode
{
	/// <summary>
	/// Re-run only steps whose final status was Failed, Skipped, Cancelled, or NoAction.
	/// Succeeded steps are restored from the original run via a synthesized checkpoint.
	/// </summary>
	Failed,

	/// <summary>
	/// Re-run every step from scratch with the original parameters. No checkpoint is built.
	/// </summary>
	All,

	/// <summary>
	/// Re-run a specific step plus every downstream dependent. All other succeeded steps
	/// are restored via a synthesized checkpoint.
	/// </summary>
	FromStep,
}

/// <summary>
/// Builds <see cref="CheckpointData"/> snapshots from a stored <see cref="OrchestrationRunRecord"/>
/// so the engine can rerun selected portions of a previous execution via
/// <see cref="OrchestrationExecutor.ResumeAsync"/>.
/// </summary>
public static class RetryService
{
	/// <summary>
	/// Computes the set of step names that should be re-executed for the given retry mode.
	/// Steps NOT in the returned set will be restored from the source run's outputs.
	/// </summary>
	/// <remarks>
	/// For <see cref="RetryMode.All"/> the entire orchestration is returned.
	/// For <see cref="RetryMode.Failed"/> any step with a non-succeeded final status is included.
	/// For <see cref="RetryMode.FromStep"/> the target step plus the transitive closure of
	/// its dependents (computed from the orchestration's DAG) is included.
	/// Steps that exist in the orchestration but have no record in the source run are
	/// always treated as needing execution (defensive: orchestration may have changed).
	/// </remarks>
	public static HashSet<string> ComputeStepsToRerun(
		Orchestration orchestration,
		OrchestrationRunRecord sourceRun,
		RetryMode mode,
		string? fromStep = null)
	{
		var allStepNames = orchestration.Steps.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

		if (mode == RetryMode.All)
		{
			return new HashSet<string>(allStepNames, StringComparer.Ordinal);
		}

		if (mode == RetryMode.FromStep)
		{
			if (string.IsNullOrEmpty(fromStep))
				throw new ArgumentException("fromStep must be provided for RetryMode.FromStep.", nameof(fromStep));

			if (!allStepNames.Contains(fromStep))
				throw new InvalidOperationException(
					$"Step '{fromStep}' does not exist in orchestration '{orchestration.Name}'. " +
					$"The orchestration may have changed since the original run.");

			return ComputeDownstreamClosure(orchestration, fromStep);
		}

		// RetryMode.Failed
		var toRerun = new HashSet<string>(StringComparer.Ordinal);
		foreach (var step in orchestration.Steps)
		{
			// Step was added to the orchestration after the original run — it has no record
			// and must be executed.
			if (!sourceRun.StepRecords.TryGetValue(step.Name, out var stepRecord))
			{
				toRerun.Add(step.Name);
				continue;
			}

			if (stepRecord.Status != ExecutionStatus.Succeeded)
			{
				toRerun.Add(step.Name);
			}
		}

		return toRerun;
	}

	/// <summary>
	/// Builds the transitive closure of dependents of <paramref name="rootStep"/>:
	/// the step itself plus every step (direct or indirect) that depends on it.
	/// </summary>
	public static HashSet<string> ComputeDownstreamClosure(Orchestration orchestration, string rootStep)
	{
		// Reverse dependency adjacency: stepName -> steps that DependsOn it.
		var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var step in orchestration.Steps)
		{
			if (!dependents.ContainsKey(step.Name))
				dependents[step.Name] = [];
		}
		foreach (var step in orchestration.Steps)
		{
			foreach (var dep in step.DependsOn)
			{
				if (!dependents.TryGetValue(dep, out var list))
				{
					list = [];
					dependents[dep] = list;
				}
				list.Add(step.Name);
			}
		}

		var closure = new HashSet<string>(StringComparer.Ordinal) { rootStep };
		var queue = new Queue<string>();
		queue.Enqueue(rootStep);

		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			if (!dependents.TryGetValue(current, out var children)) continue;
			foreach (var child in children)
			{
				if (closure.Add(child))
					queue.Enqueue(child);
			}
		}

		return closure;
	}

	/// <summary>
	/// Builds a <see cref="CheckpointData"/> for the retry. Returns null when the retry
	/// mode is <see cref="RetryMode.All"/> (no checkpoint — the orchestration runs from scratch).
	/// The returned checkpoint includes only succeeded steps from <paramref name="sourceRun"/>
	/// that are NOT scheduled for re-execution.
	/// </summary>
	public static CheckpointData? BuildCheckpoint(
		Orchestration orchestration,
		OrchestrationRunRecord sourceRun,
		RetryMode mode,
		string newRunId,
		DateTimeOffset checkpointedAt,
		string? fromStep = null)
	{
		if (mode == RetryMode.All)
			return null;

		var stepsToRerun = ComputeStepsToRerun(orchestration, sourceRun, mode, fromStep);
		var allStepNames = orchestration.Steps.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

		var completedSteps = new Dictionary<string, CheckpointStepResult>(StringComparer.Ordinal);
		foreach (var (stepName, stepRecord) in sourceRun.StepRecords)
		{
			// Drop any step that no longer exists in the (possibly updated) orchestration.
			if (!allStepNames.Contains(stepName)) continue;

			// Only succeeded steps can be restored.
			if (stepRecord.Status != ExecutionStatus.Succeeded) continue;

			// Skip steps scheduled for re-execution.
			if (stepsToRerun.Contains(stepName)) continue;

			completedSteps[stepName] = new CheckpointStepResult
			{
				Status = stepRecord.Status,
				Content = stepRecord.Content,
				RawContent = stepRecord.RawContent,
				ErrorMessage = stepRecord.ErrorMessage,
				RawDependencyOutputs = stepRecord.RawDependencyOutputs is Dictionary<string, string> dict
					? new Dictionary<string, string>(dict)
					: new Dictionary<string, string>(stepRecord.RawDependencyOutputs ?? new Dictionary<string, string>()),
				PromptSent = stepRecord.PromptSent,
				ActualModel = stepRecord.ActualModel,
				SelectedModel = stepRecord.SelectedModel,
				RequestedModelInfo = stepRecord.RequestedModelInfo,
				SelectedModelInfo = stepRecord.SelectedModelInfo,
				ActualModelInfo = stepRecord.ActualModelInfo,
				Usage = stepRecord.Usage,
				Trace = stepRecord.Trace,
				RetryHistory = stepRecord.RetryHistory,
				ErrorCategory = stepRecord.ErrorCategory,
			};
		}

		return new CheckpointData
		{
			RunId = newRunId,
			OrchestrationName = orchestration.Name,
			StartedAt = checkpointedAt,
			CheckpointedAt = checkpointedAt,
			Parameters = sourceRun.Parameters is Dictionary<string, string> srcDict
				? new Dictionary<string, string>(srcDict)
				: new Dictionary<string, string>(sourceRun.Parameters),
			TriggerId = null, // Retries are independent of the original trigger
			CompletedSteps = completedSteps,
		};
	}

	/// <summary>
	/// Parses the <c>mode</c> query string parameter into a <see cref="RetryMode"/> value.
	/// </summary>
	public static bool TryParseMode(string? raw, out RetryMode mode)
	{
		switch (raw?.Trim().ToLowerInvariant())
		{
			case "failed":
				mode = RetryMode.Failed;
				return true;
			case "all":
				mode = RetryMode.All;
				return true;
			case "from-step":
			case "fromstep":
				mode = RetryMode.FromStep;
				return true;
			default:
				mode = RetryMode.Failed;
				return false;
		}
	}

	/// <summary>
	/// Builds the canonical <see cref="RetryMetadata.RetryMode"/> string for a given mode.
	/// </summary>
	public static string FormatRetryMode(RetryMode mode, string? fromStep = null) => mode switch
	{
		RetryMode.Failed => "failed",
		RetryMode.All => "all",
		RetryMode.FromStep => $"from-step:{fromStep}",
		_ => "failed",
	};
}
