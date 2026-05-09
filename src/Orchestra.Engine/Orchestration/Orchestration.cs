using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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
	/// Optional provider-neutral agent worker pool settings for this orchestration run.
	/// Providers decide how to map these settings to their own resources (for example,
	/// Copilot maps instances to CLI clients).
	/// </summary>
	public AgentPoolConfig? AgentPool { get; init; }

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

	/// <summary>
	/// When <c>true</c> (the default), the orchestration-level <see cref="TimeoutSeconds"/>
	/// clock is paused while a step is waiting for human input (Approval step or
	/// <c>orchestra_request_user_input</c> tool call). Wait time is not compute time —
	/// authors expect a 1-hour orchestration to allow long human pauses.
	/// Set to <c>false</c> for hard SLAs where the wall-clock budget must include human
	/// response latency.
	/// </summary>
	public bool PauseTimeoutDuringWait { get; init; } = true;

	/// <summary>
	/// Default set of opt-in engine tool names enabled for every Prompt step that does
	/// not specify its own <see cref="PromptOrchestrationStep.EnableTools"/>. Use this to
	/// enable e.g. <c>request_user_input</c> across the whole orchestration without
	/// repeating it on each step. Tools listed here are merged with the always-on
	/// engine tools (<c>orchestra_set_status</c>, <c>orchestra_complete</c>, file
	/// save/read).
	/// </summary>
	public string[] DefaultEnableTools { get; init; } = [];

	/// <summary>
	/// Free-form metadata for the orchestration. Values may be any JSON type
	/// (string, number, boolean, object, array). Metadata is purely informational
	/// and does not affect execution; it is intended for orchestration authors
	/// and managers to record details such as authorship, datetime, ticket links,
	/// environment, SLA, or any other semi-structured data.
	/// </summary>
	/// <remarks>
	/// Stored as <see cref="JsonNode"/> values so the original JSON shape (objects,
	/// arrays, mixed types) is preserved on round-trip. The runtime never inspects
	/// the contents of this dictionary.
	/// </remarks>
	public Dictionary<string, JsonNode?> Metadata { get; init; } = [];

	/// <summary>
	/// Absolute path to the orchestration source file, when this orchestration was parsed from disk.
	/// For managed copies, this is the original source file path rather than the managed cache path.
	/// </summary>
	[JsonIgnore]
	public string? SourcePath { get; internal set; }

	/// <summary>
	/// Absolute directory containing <see cref="SourcePath"/>, when available.
	/// Exposed at runtime via <c>{{orchestration.sourceDirectory}}</c> for authoring
	/// absolute paths anchored to the orchestration file location.
	/// </summary>
	[JsonIgnore]
	public string? SourceDirectory { get; internal set; }
}
