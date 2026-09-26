namespace Orchestra.Engine;

public abstract class OrchestrationStep
{
	public required string Name { get; init; }
	public required OrchestrationStepType Type { get; init; }
	public string[] DependsOn { get; init; } = [];
	public string[] Parameters { get; init; } = [];

	/// <summary>
	/// Whether this step is enabled. When false, the step is skipped immediately
	/// and returns an empty result. Downstream steps that depend on a disabled step
	/// receive an empty string as the dependency output. Defaults to true.
	/// </summary>
	public bool Enabled { get; init; } = true;

	/// <summary>
	/// Optional run-time condition. Evaluated after every dependency has finished and just
	/// before the step would start; when it is false the step is recorded as
	/// <see cref="ExecutionStatus.Skipped"/> with <see cref="SkipKind.Condition"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A condition-skipped step does <em>not</em> block its dependents (unlike a failed or
	/// dependency-skipped step): they run and see an empty string for this step's output, the
	/// same contract as <see cref="Enabled"/> = false. That is what makes if/else and join
	/// patterns expressible — gate two branches on opposite conditions and let a third step
	/// depend on both. Authors who want a false condition to cascade can gate the dependents
	/// explicitly with <c>when: "{{thisStep.status}} == 'succeeded'"</c>.
	/// </para>
	/// <para>
	/// Syntax: template expressions (<c>{{param.x}}</c>, <c>{{step.output}}</c>,
	/// <c>{{step.status}}</c>) are resolved first, then the text is parsed as a boolean
	/// expression with <c>==</c> <c>!=</c> <c>&lt;</c> <c>&lt;=</c> <c>&gt;</c> <c>&gt;=</c>
	/// <c>&amp;&amp;</c> <c>||</c> <c>!</c>, parentheses, string/number/boolean/null literals and
	/// the functions <c>contains</c>, <c>startsWith</c>, <c>endsWith</c>, <c>matches</c>,
	/// <c>empty</c>, <c>lower</c>, <c>trim</c>, <c>length</c>. A bare value is truthy when it is
	/// non-empty and not <c>false</c>/<c>0</c>/<c>null</c>. See <see cref="ConditionEvaluator"/>.
	/// </para>
	/// </remarks>
	public string? When { get; init; }

	/// <summary>
	/// Optional timeout in seconds for this step. When set, the step will be cancelled
	/// if it takes longer than this duration. If not set, falls back to the orchestration's
	/// <see cref="Orchestration.DefaultStepTimeoutSeconds"/> (if any).
	/// Set to 0 to explicitly disable timeout for this step, even when a default is configured.
	/// </summary>
	public int? TimeoutSeconds { get; init; }

	/// <summary>
	/// Optional retry policy for this step. Overrides the orchestration's
	/// <see cref="Orchestration.DefaultRetryPolicy"/> when set.
	/// When null, the orchestration-level default is used (if any).
	/// </summary>
	public RetryPolicy? Retry { get; init; }

	/// <summary>
	/// When set to <c>true</c>, the step is marked as <see cref="ExecutionStatus.Failed"/>
	/// with <see cref="StepErrorCategory.ToolError"/> if ANY tool call inside the agent
	/// loop fails (MCP server error, built-in tool exception, etc.). When <c>false</c>,
	/// tool failures are recorded in the trace and surfaced to the LLM but do not by
	/// themselves change the step's terminal status — the LLM may decide to retry,
	/// adapt, or summarize the failure and the step still completes as
	/// <see cref="ExecutionStatus.Succeeded"/>.
	/// <para>
	/// When <see langword="null"/>, the orchestration-level
	/// <see cref="Orchestration.DefaultFailOnToolError"/> is used; if that is also unset,
	/// the historical behavior (tool failures are non-fatal) applies for backward
	/// compatibility.
	/// </para>
	/// <para>
	/// Only meaningful for step types that drive an agent loop (Prompt). Other step
	/// types ignore this setting because they have no tool-call concept.
	/// </para>
	/// </summary>
	public bool? FailOnToolError { get; init; }
}
