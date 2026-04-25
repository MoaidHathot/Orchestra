namespace Orchestra.Engine;

public class Orchestration
{
	public required string Name { get; init; }
	public required string Description { get; init; }
	public required OrchestrationStep[] Steps { get; init; }

	/// <summary>
	/// Version of the orchestration. Defaults to "1.0.0".
	/// Used for tracking execution history and orchestration changes.
	/// </summary>
	public string Version { get; init; } = "1.0.0";

	/// <summary>
	/// Trigger configuration for the orchestration.
	/// Defaults to <see cref="ManualTriggerConfig"/> (manual-only, no automated trigger).
	/// Can be overridden by user-defined triggers set via the UI.
	/// </summary>
	public TriggerConfig Trigger { get; init; } = new ManualTriggerConfig { Type = TriggerType.Manual };

	/// <summary>
	/// Optional inline MCP server definitions in the orchestration JSON.
	/// At runtime, these are merged with any global orchestra.mcp.json definitions
	/// (inline definitions take priority on name conflicts).
	/// </summary>
	public Mcp[] Mcps { get; init; } = [];

	/// <summary>
	/// Default system prompt mode for all steps in the orchestration.
	/// Individual steps can override this value with their own SystemPromptMode.
	/// When null, the SDK's default behavior is used.
	/// </summary>
	/// <remarks>
	/// Use <see cref="SystemPromptMode.Replace"/> to completely replace the SDK's
	/// default system prompt (e.g., Copilot's coding instructions) with your custom prompt.
	/// Use <see cref="SystemPromptMode.Append"/> to add your prompt to the SDK's default,
	/// preserving built-in capabilities like coding assistance.
	/// </remarks>
	public SystemPromptMode? DefaultSystemPromptMode { get; init; }

	/// <summary>
	/// Default retry policy applied to all steps that don't define their own.
	/// When null, no retries are performed on step failures.
	/// </summary>
	public RetryPolicy? DefaultRetryPolicy { get; init; }

	/// <summary>
	/// Default model applied to all Prompt steps that don't define their own
	/// <see cref="PromptOrchestrationStep.Model"/>.
	/// When null, each Prompt step must specify its own model.
	/// </summary>
	public string? DefaultModel { get; init; }

	/// <summary>
	/// Default timeout in seconds applied to all steps that don't define their own
	/// <see cref="OrchestrationStep.TimeoutSeconds"/>.
	/// When null, steps without an explicit timeout run with no per-step timeout
	/// (only the orchestration-level timeout applies).
	/// </summary>
	public int? DefaultStepTimeoutSeconds { get; init; }

	/// <summary>
	/// Maximum time in seconds for the entire orchestration to complete.
	/// When elapsed, all running steps are cancelled via CancellationToken.
	/// Default is 3600 seconds (1 hour). Set to null or 0 to disable.
	/// </summary>
	public int? TimeoutSeconds { get; init; } = 3600;

	/// <summary>
	/// User-defined variables available to all steps via <c>{{vars.name}}</c> template expressions.
	/// Variable values may themselves contain template expressions (e.g., <c>{{param.project}}</c>)
	/// which are resolved lazily when the variable is first referenced.
	/// </summary>
	public Dictionary<string, string> Variables { get; init; } = [];

	/// <summary>
	/// Optional author-defined tags for categorizing the orchestration.
	/// At runtime, these are merged with host-managed tags to form effective tags.
	/// Used by profiles to filter and group orchestrations.
	/// </summary>
	public string[] Tags { get; init; } = [];

	/// <summary>
	/// Optional typed input schema for the orchestration.
	/// When defined, this is the authoritative source of truth for parameter definitions,
	/// providing types, descriptions, required flags, defaults, and enum constraints.
	/// Step-level <c>Parameters</c> arrays still declare which inputs each step needs,
	/// but validation and documentation use this schema.
	/// <para>
	/// When not defined, the orchestration falls back to the legacy behavior:
	/// parameter names are collected from step-level <c>Parameters</c> arrays
	/// and treated as required string values with no defaults or descriptions.
	/// </para>
	/// </summary>
	public Dictionary<string, InputDefinition>? Inputs { get; init; }

	/// <summary>
	/// Optional lifecycle hooks that run for this orchestration.
	/// Hooks can observe step/orchestration outcomes and execute follow-up actions.
	/// </summary>
	public HookDefinition[] Hooks { get; init; } = [];
}
