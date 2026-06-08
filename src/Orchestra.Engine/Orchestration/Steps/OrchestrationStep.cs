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
