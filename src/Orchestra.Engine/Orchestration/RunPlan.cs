namespace Orchestra.Engine;

/// <summary>
/// What a run would do, computed without executing anything: the same pre-flight the executor
/// performs (parameter validation with input defaults applied, static template validation,
/// runtime template validation, DAG scheduling) plus the resulting parallel layers. Backs
/// <c>orchestra run --dry-run</c>; hosts can use it to preview or lint before scheduling.
/// </summary>
public sealed class RunPlan
{
	/// <summary>The orchestration the plan describes.</summary>
	public required Orchestration Orchestration { get; init; }

	/// <summary>Parameters after input defaults were applied; empty when none are defined.</summary>
	public required IReadOnlyDictionary<string, string> EffectiveParameters { get; init; }

	/// <summary>Parallel layers in execution order; each layer's steps have no dependencies on one another.</summary>
	public required IReadOnlyList<IReadOnlyList<OrchestrationStep>> Layers { get; init; }

	/// <summary>Problems that would stop the run before its first step. Empty when the run could start.</summary>
	public required IReadOnlyList<string> Errors { get; init; }

	/// <summary>Non-fatal observations (e.g. a supplied parameter no step reads).</summary>
	public required IReadOnlyList<string> Warnings { get; init; }

	/// <summary>True when the run could start.</summary>
	public bool IsValid => Errors.Count == 0;

	/// <summary>
	/// Builds the plan. Mirrors <see cref="OrchestrationExecutor.ExecuteAsync"/>'s pre-flight
	/// order: input/parameter validation, static template validation, DAG validation, then the
	/// runtime template validation that needs the effective parameters.
	/// </summary>
	public static RunPlan Build(Orchestration orchestration, Dictionary<string, string>? parameters = null, IScheduler? scheduler = null)
	{
		ArgumentNullException.ThrowIfNull(orchestration);

		var errors = new List<string>();
		var warnings = new List<string>();
		Dictionary<string, string> effective;

		try
		{
			effective = OrchestrationExecutor.ValidateAndApplyDefaults(orchestration, parameters) ?? new Dictionary<string, string>();
		}
		catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
		{
			errors.Add(ex.Message);
			effective = parameters ?? new Dictionary<string, string>();
		}

		var parseValidation = TemplateExpressionValidator.ValidateOrchestration(orchestration);
		errors.AddRange(parseValidation.Errors.Select(FormatError));

		IReadOnlyList<IReadOnlyList<OrchestrationStep>> layers = [];
		try
		{
			var schedule = (scheduler ?? new OrchestrationScheduler()).Schedule(orchestration);
			layers = schedule.Entries.Select(e => (IReadOnlyList<OrchestrationStep>)e.Steps).ToList();
		}
		catch (InvalidOperationException ex)
		{
			errors.Add(ex.Message);
		}

		if (errors.Count == 0)
		{
			var runtime = TemplateExpressionValidator.ValidateRuntime(orchestration, effective);
			errors.AddRange(runtime.Errors.Select(FormatError));
		}

		// A parameter nothing declares is almost always a typo of one that is.
		if (parameters is { Count: > 0 })
		{
			var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (orchestration.Inputs is not null)
				declared.UnionWith(orchestration.Inputs.Keys);
			declared.UnionWith(orchestration.Steps.SelectMany(s => s.Parameters));
			foreach (var key in parameters.Keys.Where(k => !declared.Contains(k)))
				warnings.Add($"parameter '{key}' is not declared by the orchestration (no input or step lists it); it is still available as {{{{param.{key}}}}}.");
		}

		return new RunPlan
		{
			Orchestration = orchestration,
			EffectiveParameters = effective,
			Layers = layers,
			Errors = errors,
			Warnings = warnings,
		};
	}

	private static string FormatError(TemplateValidationError error)
	{
		var location = (error.StepName, error.FieldName) switch
		{
			(not null, not null) => $"[step '{error.StepName}', field '{error.FieldName}'] ",
			(not null, null) => $"[step '{error.StepName}'] ",
			(null, not null) => $"[field '{error.FieldName}'] ",
			_ => string.Empty,
		};
		var expression = error.Expression is not null ? $" (expression: {error.Expression})" : string.Empty;
		return $"{location}{error.Message}{expression}";
	}
}
