---
layout: default
title: Orchestra.Engine
nav_order: 3
---

# Orchestra.Engine

Orchestra.Engine is the core orchestration engine that provides execution, scheduling, and storage abstractions for LLM workflows.

## Overview

The engine is provider-agnostic - it defines abstract interfaces (`IAgent`, `AgentBuilder`) that can be implemented for any LLM SDK. This allows you to swap out the AI provider without changing your orchestration definitions.

## Installation

```bash
dotnet add package Orchestra.Engine
```

## Core Concepts

### Orchestration

An `Orchestration` is a container defining a complete workflow:

```csharp
public class Orchestration
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required OrchestrationStep[] Steps { get; init; }
    public string Version { get; init; } = "1.0.0";
    public Dictionary<string, InputDefinition>? Inputs { get; init; }
    public Dictionary<string, string> Variables { get; init; } = [];
    public TriggerConfig? Trigger { get; init; }
    public Mcp[] Mcps { get; init; } = [];
    public SystemPromptMode? DefaultSystemPromptMode { get; init; }
}
```

### Typed Inputs

Define a strongly-typed input schema using the `Inputs` property:

```csharp
public class InputDefinition
{
    public InputType Type { get; init; } = InputType.String;
    public string? Description { get; init; }
    public bool Required { get; init; } = true;
    public string? Default { get; init; }
    public string[] Enum { get; init; } = [];
}

public enum InputType { String, Boolean, Number }
```

When `Inputs` is defined on an orchestration, parameters are validated against the schema before execution. The engine checks required inputs, validates types (boolean must be `"true"`/`"false"`, number must be parseable), enforces enum constraints, and applies default values for optional inputs not provided.

When `Inputs` is not defined, the engine falls back to legacy behavior: parameter names are collected from step-level `Parameters` arrays and all are treated as required strings.

### Hooks

Orchestrations can define lifecycle hooks that run after step or orchestration outcomes. Hooks are a lightweight extensibility point for notifications, archival, incident triage, and similar follow-up automation.

```csharp
public class HookDefinition
{
    public string? Name { get; set; }
    public HookEventType On { get; set; }
    public HookWhenFilter? When { get; set; }
    public HookPayloadOptions Payload { get; set; } = new();
    public required HookAction Action { get; set; }
    public HookFailurePolicy FailurePolicy { get; set; } = HookFailurePolicy.Warn;
}
```

Supported events:

- `step.success`
- `step.failure`
- `step.after`
- `orchestration.success`
- `orchestration.failure`
- `orchestration.after`

Filtering is intentionally small in v1:

```yaml
when:
  steps:
    names: [build, deploy]
    status: failed
    match: any
```

Payload shaping is also preset-based rather than field-by-field:

- `detail`: `compact`, `standard`, `full`
- `steps`: `none`, `current`, `failed`, `nonSucceeded`, `terminal`, `all`, or explicit step names
- `includeRefs`: include API and MCP references for fetching more run data

In v1, hooks support `script` actions. The hook payload is serialized as JSON and sent to the script on stdin.

Both ends of that pipe are UTF-8, so payloads containing non-ASCII text — an em-dash in a run
title, a curly quote in an error message — arrive intact. In `pwsh`/`powershell`, read stdin with
`[Console]::In.ReadToEnd()`:

```powershell
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
```

> **Do not use `$input`.** PowerShell materialises its input pipeline before the script body runs,
> using the OEM code page regardless of the encoding Orchestra sets, so non-ASCII content is
> corrupted. Referencing `$input` also drains the stream, after which
> `[Console]::In.ReadToEnd()` returns empty — the two idioms are mutually exclusive.

```yaml
hooks:
  - name: archive-run-failure
    on: orchestration.failure
    payload:
      detail: compact
      steps: failed
      includeRefs: true
    action:
      type: script
      shell: pwsh
      scriptFile: ./hooks/archive-failure.ps1
```

Hooks are different from Prompt step handlers:

- `inputHandlerPrompt` and `outputHandlerPrompt` transform Prompt step input/output
- hooks run after lifecycle events and do not alter orchestration execution

### Steps

Steps are the building blocks of orchestrations. Each step has a type that determines its executor: `PromptOrchestrationStep` for LLM calls, `CommandOrchestrationStep` for shell commands, `ScriptOrchestrationStep` for inline/file scripts, `HttpOrchestrationStep` for REST requests, and `TransformOrchestrationStep` for string interpolation.

Prompt-heavy orchestrations can request provider worker-pool capacity with top-level `agentPool`. Providers map these neutral settings to their own resources; the Copilot provider maps instances to CLI clients. `maxInstances` is a ceiling, not a static count: workers are created on demand up to the ceiling, and idle workers above `minInstances` are stopped after `idleTimeoutSeconds`.

```yaml
agentPool:
  minInstances: 1
  maxInstances: 4
  maxSessionsPerInstance: 1
  idleTimeoutSeconds: 120
```

```csharp
public abstract class OrchestrationStep
{
    public required string Name { get; init; }
    public required OrchestrationStepType Type { get; init; }
    public required string[] DependsOn { get; init; }
    public string[] Parameters { get; init; } = [];
}

public class PromptOrchestrationStep : OrchestrationStep
{
    public required string Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public string? InputHandlerPrompt { get; init; }
    public string? OutputHandlerPrompt { get; init; }
    public Mcp[] Mcps { get; internal set; } = [];
    public Subagent[] Subagents { get; init; } = [];
    public ReasoningLevel? ReasoningLevel { get; init; }
    public SystemPromptMode? SystemPromptMode { get; init; }
    public LoopConfig? Loop { get; init; }
}
```

### Script step stdin encoding

Script and Command steps can pipe content to the child via `stdin:`. Orchestra pins **UTF-8 on
both ends** of that pipe, so non-ASCII content round-trips byte-for-byte:

- host side — `StandardInputEncoding` is set alongside the existing stdout/stderr encodings;
- child side — `pwsh`/`powershell` get an injected `[Console]::InputEncoding` prologue (applied for
  every `strictMode` setting), and `python`/`python3` get `PYTHONIOENCODING=utf-8`.

Without both halves the Windows OEM code page best-fit-transliterates the payload on the way in:
`U+2014` flattens to `-`, the curly quotes `U+201C`/`U+201D` collapse to an unescaped ASCII `"`
(which silently breaks any JSON carrying them), and anything outside the code page becomes `?`.
Setting only one half is worse than neither — the child then decodes UTF-8 bytes as OEM and
produces mojibake.

Read stdin with `[Console]::In.ReadToEnd()` in PowerShell. **`$input` is not encoding-safe** and
consumes the same stream, so the two idioms are mutually exclusive. `bash`/`sh` and `node` need
nothing — they handle the raw bytes themselves.

### Script step control channel

A **Script** step (`shell: pwsh|bash|python|node`) is deterministic — it only reports success (exit 0) or failure (non-zero). To let a script decide the orchestration's fate the way a Prompt step can with the `orchestra_complete` / `orchestra_set_status` engine tools, the engine gives every Script step a **control channel**: an engine-owned file whose path is exported as the `ORCHESTRA_CONTROL_FILE` environment variable (alongside `ORCHESTRA_RUN_ID` and `ORCHESTRA_STEP_NAME`).

The script writes a single JSON object to that file:

```json
{ "action": "complete" | "set_status", "status": "success" | "failed" | "no_action", "reason": "…" }
```

- **`action: "set_status"`** sets this step's terminal status — `success`, `failed`, or `no_action` (a `no_action` step skips every step that depends on it).
- **`action: "complete"`** halts the *entire* orchestration immediately (`success` or `failed`), cancelling all remaining steps — the non-LLM equivalent of `orchestra_complete`.

The signal is read only when the script exits `0`. The engine deletes the working file afterward but persists a copy of the raw payload to the run's temp directory and records the decision in the run history. Malformed contents fail the step loudly. `stdout` stays the step's normal output, so a script can emit data **and** signal control in the same run.

Three ways to write the signal:

```yaml
# 1. PowerShell helpers (injected automatically for pwsh/powershell, any strictMode):
- name: gate
  type: Script
  shell: pwsh
  script: |
    if ($count -eq 0) { Orchestra-Complete -Status success -Reason 'Nothing to do'; return }
    # ... otherwise emit data on stdout ...

# 2. The orchestra CLI (any shell):
- name: gate
  type: Script
  shell: bash
  script: |
    if [ "$count" -eq 0 ]; then orchestra step complete --status success --reason 'Nothing to do'; exit 0; fi

# 3. Write the JSON directly (any language):
- name: gate
  type: Script
  shell: python
  script: |
    import os, json
    if count == 0:
        open(os.environ["ORCHESTRA_CONTROL_FILE"], "w").write(
            json.dumps({"action": "complete", "status": "success", "reason": "Nothing to do"}))
```

### Dependencies

Steps can depend on other steps using the `DependsOn` property. The engine:
- Validates the dependency graph (DAG) for cycles
- Executes independent steps in parallel
- Passes outputs from dependencies to dependent steps automatically

### Template Expressions

The engine uses `{{expression}}` syntax for dynamic values in prompts, URLs, headers, templates, and command arguments. All expressions are case-insensitive and whitespace-tolerant.

#### Supported Namespaces

| Namespace | Syntax | Resolution |
|-----------|--------|------------|
| `param` | `{{param.name}}` | Replaced with the runtime parameter value. Unknown parameters are left as-is. |
| `orchestration` | `{{orchestration.property}}` | Replaced with orchestration metadata. Unknown properties throw `InvalidOperationException`. |
| `step` | `{{step.property}}` | Replaced with current step metadata. Unknown properties throw `InvalidOperationException`. |
| `vars` | `{{vars.name}}` | Replaced with the variable value (recursively expanded). Unknown variables are left as-is. |
| `env` | `{{env.VAR_NAME}}` | Replaced with the OS environment variable value. Undefined variables are left as-is. |
| Step output | `{{stepName.output}}` | Replaced with the processed output of the named step. |
| Step raw output | `{{stepName.rawOutput}}` | Replaced with the raw (unprocessed) output of the named step. |
| Step files | `{{stepName.files}}` | Replaced with a JSON array of all file paths saved by the named step via `orchestra_save_file`. |
| Step file (indexed) | `{{stepName.files[N]}}` | Replaced with the path of the Nth file (0-based) saved by the named step via `orchestra_save_file`. |

##### Orchestration-step accessors

For steps of type `Orchestration` (steps that invoke another orchestration), the following
additional accessors drill into the child run's data via the `ChildOrchestrationInfo` that
`OrchestrationStepExecutor` populates on every terminal branch — including failed and
cancelled child runs. Self-healing controllers rely on this to inspect partial child
progress before generating a repair.

| Accessor | Resolution |
|----------|------------|
| `{{stepName.executionId}}` | The child run's execution id. Useful for follow-up MCP calls (`get_orchestration_status`, `get_orchestration_step`). |
| `{{stepName.status}}` | Lowercase child status: `succeeded`, `failed`, `cancelled`, `pending` (async dispatch). |
| `{{stepName.errorMessage}}` | Top-level error message from the child run (empty when succeeded). |
| `{{stepName.completionReason}}` | `orchestra_complete` reason, if any. |
| `{{stepName.childResult}}` | JSON blob with `executionId`, `status`, `errorMessage`, `finalContent`, `completionReason`, `cancellation`, `stepResults`. |
| `{{stepName.steps}}` | JSON map of all child-step results keyed by child step name. |
| `{{stepName.steps.<childStepName>}}` | JSON of one child step (`status`, `output`, `rawOutput`, `error`, `files`). |
| `{{stepName.steps.<childStepName>.output}}` | Untruncated content of one child step. |
| `{{stepName.steps.<childStepName>.rawOutput}}` | Pre-output-handler content of one child step. |
| `{{stepName.steps.<childStepName>.error}}` | Error message of one child step (empty when succeeded). |
| `{{stepName.steps.<childStepName>.status}}` | Lowercase status of one child step. |
| `{{stepName.steps.<childStepName>.files}}` | JSON array of one child step's saved files. |
| `{{stepName.steps.<childStepName>.files[N]}}` | Indexed access to one child step's saved files. |

Resolution is in-process (no MCP, no truncation). The parent persists only the child's
`executionId`/`orchestrationName`/`status`/`errorMessage` on its own `run.json`; the rich
per-step data is consumed via templates during the parent's run. Use the data-plane
`get_orchestration_step` MCP tool to re-fetch child step content after restart or from
agents that lack access to the parent's in-memory state.

#### Orchestration Metadata

Available via `{{orchestration.property}}`:

| Property | Type | Description |
|----------|------|-------------|
| `name` | string | The orchestration name |
| `version` | string | The orchestration version |
| `runId` | string | Unique identifier for this execution run |
| `startedAt` | string | Run start time in ISO 8601 format |

These properties come from the `OrchestrationInfo` record, created once per execution:

```csharp
public record OrchestrationInfo(
    string Name,
    string Version,
    string RunId,
    DateTimeOffset StartedAt);
```

#### Step Metadata

Available via `{{step.property}}`:

| Property | Type | Description |
|----------|------|-------------|
| `name` | string | The current step's name |
| `type` | string | The current step's type (`Prompt`, `Command`, `Script`, `Transform`, `Http`) |

#### Template Resolution

Templates are resolved by `TemplateResolver.Resolve()`, which is called by each step executor. The resolver:

1. Finds all `{{expression}}` patterns in the input string
2. Identifies the namespace (`param`, `orchestration`, `step`, `vars`, or step output)
3. Replaces each match with the resolved value
4. For `vars.*`, recursively resolves any nested template expressions in the variable's value
5. Detects circular variable references and leaves them as-is to prevent infinite loops

#### Environment Variables

The `{{env.VAR_NAME}}` namespace reads OS environment variables at resolution time:

- Variable names are passed through as-is to `Environment.GetEnvironmentVariable()` (case-sensitive on Linux, case-insensitive on Windows)
- Undefined environment variables are left as-is in the output (e.g., `{{env.MISSING}}` remains `{{env.MISSING}}`)
- Environment variables set to an empty string resolve to an empty string
- Can be used inside variable values for recursive expansion (e.g., `"connectionString": "Server={{env.DB_HOST}};Database=mydb"`)

### Variables

Variables provide reusable, orchestration-scoped values that can be referenced by any step via `{{vars.name}}`. They are defined in the top-level `variables` dictionary:

```json
{
  "variables": {
    "appName": "customer-portal",
    "registry": "ghcr.io/myorg/{{vars.appName}}",
    "outputDir": "/reports/{{param.project}}",
    "logTag": "[{{orchestration.name}}:{{orchestration.runId}}]"
  }
}
```

Key behaviors:
- **Recursive expansion**: Variable values can contain template expressions (including references to other variables, parameters, orchestration metadata, etc.), which are resolved when the variable is used
- **Chained references**: Variables can reference other variables (e.g., `registry` references `appName` above)
- **Circular protection**: Circular references are detected via a resolution stack and left as-is
- **Unknown variables**: References to undefined variables are left as-is in the output
- **No caching**: Variables are resolved inline each time they are referenced

## Parsing Orchestrations

Use `OrchestrationParser` to load orchestrations from JSON files:

```csharp
// Parse MCP configuration
Mcp[] mcps = OrchestrationParser.ParseMcpFile("mcp.json");

// Parse orchestration with MCP resolution
Orchestration orchestration = OrchestrationParser.ParseOrchestrationFile(
    "orchestration.json", 
    mcps
);

// Parse orchestration metadata only (no MCP resolution)
Orchestration metadata = OrchestrationParser.ParseOrchestrationFileMetadataOnly(
    "orchestration.json"
);
```

## Executing Orchestrations

### Create an Executor

```csharp
var executor = new OrchestrationExecutor(
    scheduler: new OrchestrationScheduler(),
    agentBuilder: myAgentBuilder,  // Your AgentBuilder implementation
    reporter: myReporter,           // IOrchestrationReporter implementation
    loggerFactory: loggerFactory,
    promptFormatter: new DefaultPromptFormatter(),  // Optional
    runStore: myRunStore            // Optional: IRunStore implementation
);
```

### Execute

```csharp
var result = await executor.ExecuteAsync(
    orchestration,
    parameters: new Dictionary<string, string> 
    { 
        ["topic"] = "artificial intelligence" 
    },
    triggerId: null,
    cancellationToken
);

if (result.Status == ExecutionStatus.Succeeded)
{
    foreach (var (stepName, stepResult) in result.Results)
    {
        Console.WriteLine($"{stepName}: {stepResult.Content}");
    }
}
```

### ExecutionStatus

The `ExecutionStatus` enum represents the outcome of an orchestration or step:

```csharp
public enum ExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}
```

## Implementing AgentBuilder

To use Orchestra.Engine with your preferred LLM provider, implement the `AgentBuilder` abstract class:

```csharp
public abstract class AgentBuilder
{
    protected string? Model { get; private set; }
    protected string? SystemPrompt { get; private set; }
    protected Mcp[] Mcps { get; private set; } = [];
    protected Subagent[] Subagents { get; private set; } = [];
    protected ReasoningLevel? ReasoningLevel { get; private set; }
    protected SystemPromptMode? SystemPromptMode { get; private set; }
    protected IOrchestrationReporter Reporter { get; private set; }
    
    // Fluent configuration methods
    public AgentBuilder WithModel(string model);
    public AgentBuilder WithSystemPrompt(string systemPrompt);
    public AgentBuilder WithMcp(params Mcp[] mcps);
    public AgentBuilder WithSubagents(params Subagent[] subagents);
    public AgentBuilder WithReasoningLevel(ReasoningLevel? level);
    public AgentBuilder WithSystemPromptMode(SystemPromptMode? mode);
    public AgentBuilder WithReporter(IOrchestrationReporter reporter);
    
    // Implement this method
    public abstract Task<IAgent> BuildAgentAsync(CancellationToken ct = default);
}
```

### Example Implementation

```csharp
public class MyAgentBuilder : AgentBuilder
{
    private readonly MyLlmClient _client;
    
    public MyAgentBuilder(MyLlmClient client)
    {
        _client = client;
    }
    
    public override async Task<IAgent> BuildAgentAsync(CancellationToken ct)
    {
        return new MyAgent(
            _client,
            Model,
            SystemPrompt,
            Mcps,
            Subagents,
            ReasoningLevel,
            SystemPromptMode,
            Reporter
        );
    }
}
```

## Agent Interface

The `IAgent` interface is simple:

```csharp
public interface IAgent
{
    AgentTask SendAsync(string prompt, CancellationToken ct = default);
}
```

### AgentTask

`AgentTask` is an async enumerable that streams events:

```csharp
AgentTask task = agent.SendAsync("Hello", cancellationToken);

// Stream events
await foreach (AgentEvent evt in task)
{
    switch (evt.Type)
    {
        case AgentEventType.MessageDelta:
            Console.Write(evt.Content);
            break;
        case AgentEventType.ToolExecutionStart:
            Console.WriteLine($"Tool: {evt.ToolName}");
            break;
        case AgentEventType.ReasoningDelta:
            Console.Write($"[Thinking: {evt.Content}]");
            break;
    }
}

// Get final result
AgentResult result = await task.GetResultAsync();
Console.WriteLine($"Final: {result.Content}");
Console.WriteLine($"Tokens: {result.Usage?.InputTokens} in, {result.Usage?.OutputTokens} out");
```

### Event Types

| Event Type | Description |
|------------|-------------|
| `SessionStart` | Session has started |
| `ModelChange` | Model selection changed |
| `MessageDelta` | Streaming text chunk |
| `Message` | Complete message |
| `ReasoningDelta` | Streaming reasoning chunk |
| `Reasoning` | Complete reasoning |
| `ToolExecutionStart` | Tool call started |
| `ToolExecutionComplete` | Tool call completed |
| `SubagentSelected` | Subagent was selected |
| `SubagentStarted` | Subagent execution started |
| `SubagentCompleted` | Subagent execution completed |
| `SubagentFailed` | Subagent execution failed |
| `SubagentDeselected` | Subagent was deselected |
| `Usage` | Token usage information |
| `Error` | Error occurred |
| `SessionIdle` | Session completed |

## MCP (Model Context Protocol)

Orchestra supports two types of MCP servers:

### Local MCP

```csharp
var localMcp = new LocalMcp
{
    Name = "filesystem",
    Type = McpType.Local,
    Command = "npx",
    Arguments = new[] { "-y", "@modelcontextprotocol/server-filesystem", "." },
    WorkingDirectory = "/path/to/project"
};
```

### Remote MCP

```csharp
var remoteMcp = new RemoteMcp
{
    Name = "web-search",
    Type = McpType.Remote,
    Endpoint = "https://mcp.example.com/search",
    Headers = new Dictionary<string, string>
    {
        ["Authorization"] = "Bearer your-api-key"
    }
};
```

## Quality Control Loops

Use loops for iterative refinement with checker steps:

```json
{
  "steps": [
    {
      "name": "writer",
      "type": "prompt",
      "userPrompt": "Write a poem about {{topic}}"
    },
    {
      "name": "reviewer",
      "type": "prompt",
      "dependsOn": ["writer"],
      "userPrompt": "Review the poem. If it's excellent, say 'APPROVED'. Otherwise, provide feedback.",
      "loop": {
        "target": "writer",
        "maxIterations": 3,
        "exitPattern": "APPROVED"
      }
    }
  ]
}
```

The loop will:
1. Run `writer` to generate a poem
2. Run `reviewer` to check the output
3. If output contains "APPROVED", stop
4. Otherwise, re-run `writer` with the reviewer's feedback
5. Repeat up to 3 times

## Triggers

Orchestrations can be triggered automatically. All trigger types inherit from `TriggerConfig`:

```csharp
public abstract class TriggerConfig
{
    public required TriggerType Type { get; init; }
    public bool Enabled { get; init; } = true;
    public string? InputHandlerPrompt { get; init; }
}
```

The `InputHandlerPrompt` property is available on all trigger types. When set, it instructs an LLM to transform the raw trigger input (webhook body, manual parameters, etc.) into the orchestration's expected parameter format.

### TriggerStatus

The `TriggerStatus` enum represents the runtime state of a trigger:

```csharp
public enum TriggerStatus
{
    Idle,       // Configured but not yet activated
    Waiting,    // Active and waiting for next fire event
    Running,    // Fired and orchestration currently running
    Paused,     // Disabled or reached max runs
    Completed,  // Completed configured runs (e.g., maxRuns reached)
    Error       // Encountered an error
}
```

### Scheduler Trigger

```json
{
  "trigger": {
    "type": "scheduler",
    "cron": "0 9 * * *",
    "enabled": true
  }
}
```

Or with interval (in seconds):

```json
{
  "trigger": {
    "type": "scheduler",
    "intervalSeconds": 3600,
    "enabled": true
  }
}
```

### Webhook Trigger

```json
{
  "trigger": {
    "type": "webhook",
    "secret": "${WEBHOOK_SECRET}",
    "enabled": true
  }
}
```

### Loop Trigger

```json
{
  "trigger": {
    "type": "loop",
    "enabled": true
  }
}
```

### Email Trigger

```json
{
  "trigger": {
    "type": "email",
    "folderPath": "Inbox",
    "pollIntervalSeconds": 60,
    "maxItemsPerPoll": 10,
    "subjectContains": "Action Required",
    "senderContains": "@company.com",
    "enabled": true
  }
}
```

## Custom Prompt Formatting

Implement `IPromptFormatter` to customize how prompts are built:

```csharp
public interface IPromptFormatter
{
    string FormatDependencyOutputs(IReadOnlyDictionary<string, string> deps);
    string BuildUserPrompt(string userPrompt, string deps, string? feedback, string? handler);
    string BuildTransformationSystemPrompt(string instructions);
    string WrapContentForTransformation(string content);
}
```

A `DefaultPromptFormatter` is provided that uses markdown-style formatting (separators, headers, and XML-like tags for content wrapping). A singleton instance is available for convenience when not using DI:

```csharp
IPromptFormatter formatter = DefaultPromptFormatter.Instance;
```

## Custom Reporting

Implement `IOrchestrationReporter` for custom event handling:

```csharp
public interface IOrchestrationReporter
{
    void ReportSessionStarted(string requestedModel, string? selectedModel);
    void ReportModelChange(string? previousModel, string newModel);
    void ReportUsage(string stepName, string model, AgentUsage usage);
    void ReportContentDelta(string stepName, string chunk);
    void ReportReasoningDelta(string stepName, string chunk);
    void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer);
    void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error);
    void ReportStepStarted(string stepName);
    void ReportStepCompleted(string stepName, AgentResult result);
    void ReportStepOutput(string stepName, string content);
    void ReportStepError(string stepName, string errorMessage);
    void ReportStepCancelled(string stepName);
    void ReportStepSkipped(string stepName, string reason);
    void ReportStepTrace(string stepName, StepExecutionTrace trace);
    void ReportModelMismatch(ModelMismatchInfo mismatch);
    void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations);
    
    // Subagent events
    void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools);
    void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description);
    void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName);
    void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error);
    void ReportSubagentDeselected(string stepName);
}
```

### ModelMismatchInfo

When the actual model used differs from the configured model, `ReportModelMismatch` is called with a `ModelMismatchInfo`:

```csharp
public class ModelMismatchInfo
{
    public required string ConfiguredModel { get; init; }
    public required string ActualModel { get; init; }
    public string? SystemPromptMode { get; init; }
    public string? ReasoningLevel { get; init; }
    public string? SystemPromptPreview { get; init; }
    public string[]? McpServers { get; init; }
    public IReadOnlyList<AvailableModelInfo>? AvailableModels { get; init; }
}
```

### AvailableModelInfo

Provides metadata about models available from the LLM provider:

```csharp
public class AvailableModelInfo
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public double? BillingMultiplier { get; init; }
    public string[]? ReasoningEfforts { get; init; }
}
```

## Run Storage

Implement `IRunStore` to persist execution history:

```csharp
public interface IRunStore
{
    Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(string orchestrationName, int? limit = null, CancellationToken ct = default);
    Task<IReadOnlyList<OrchestrationRunRecord>> ListAllRunsAsync(int? limit = null, CancellationToken ct = default);
    Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsByTriggerAsync(string triggerId, int? limit = null, CancellationToken ct = default);
    Task<OrchestrationRunRecord?> GetRunAsync(string orchestrationName, string runId, CancellationToken ct = default);
    Task<bool> DeleteRunAsync(string orchestrationName, string runId, CancellationToken ct = default);
}
```

### OrchestrationRunRecord

A complete record of an orchestration execution:

```csharp
public class OrchestrationRunRecord
{
    public required string RunId { get; init; }
    public required string OrchestrationName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Duration => CompletedAt - StartedAt;
    public required ExecutionStatus Status { get; init; }
    public string OrchestrationVersion { get; init; } = "1.0.0";
    public string TriggeredBy { get; init; } = "manual";
    public Dictionary<string, string> Parameters { get; init; } = [];
    public string? TriggerId { get; init; }
    public required IReadOnlyDictionary<string, StepRunRecord> StepRecords { get; init; }
    public required IReadOnlyDictionary<string, StepRunRecord> AllStepRecords { get; init; }
    public required string FinalContent { get; init; }
}
```

- `StepRecords`: Step records keyed by step name. For looped steps, contains the final iteration's record.
- `AllStepRecords`: All step records including loop iterations, keyed by `"stepName"` or `"stepName:iteration-N"`.

### StepRunRecord

Detailed record of a single step execution:

```csharp
public class StepRunRecord
{
    public required string StepName { get; init; }
    public required ExecutionStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Duration => CompletedAt - StartedAt;
    public required string Content { get; init; }
    public string? RawContent { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public int? LoopIteration { get; init; }
    public IReadOnlyDictionary<string, string> RawDependencyOutputs { get; init; } = new Dictionary<string, string>();
    public string? PromptSent { get; init; }
    public string? ActualModel { get; init; }
    public TokenUsage? Usage { get; init; }
    public StepExecutionTrace? Trace { get; init; }
}
```

### TokenUsage

Token usage statistics for an LLM call:

```csharp
public class TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
}
```

### StepExecutionTrace

Detailed execution trace for debugging and inspection:

```csharp
public class StepExecutionTrace
{
    public string? SystemPrompt { get; init; }
    public string? UserPromptRaw { get; init; }
    public string? UserPromptProcessed { get; init; }
    public string? Reasoning { get; init; }
    public List<ToolCallRecord> ToolCalls { get; init; } = [];
    public List<string> ResponseSegments { get; init; } = [];
    public string? FinalResponse { get; init; }
    public string? OutputHandlerResult { get; init; }
}
```

### ToolCallRecord

Record of a single MCP tool call within a step trace:

```csharp
public class ToolCallRecord
{
    public string? CallId { get; init; }
    public string? McpServer { get; init; }
    public required string ToolName { get; init; }
    public string? Arguments { get; init; }
    public bool Success { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
```

## Execution Flow

```
JSON Config
    │
    ▼
OrchestrationParser
    │
    ▼
Orchestration
    │
    ▼
OrchestrationScheduler (validate DAG, create execution plan)
    │
    ▼
OrchestrationExecutor
    │
    ├──▶ Step 1 ──▶ PromptExecutor ──▶ AgentBuilder ──▶ IAgent ──▶ LLM
    │
    ├──▶ Step 2 ──▶ PromptExecutor ──▶ AgentBuilder ──▶ IAgent ──▶ LLM
    │       (parallel with Step 1 if no dependencies)
    │
    └──▶ Step 3 ──▶ PromptExecutor ──▶ AgentBuilder ──▶ IAgent ──▶ LLM
            (waits for dependencies)
    │
    ▼
OrchestrationResult
    │
    ▼
IRunStore (persist)
```

## Dependencies

- **Target Framework**: .NET 10.0
- **Package**: `Microsoft.Extensions.Logging.Abstractions` (v10.0.2)

The engine has minimal dependencies, making it portable and allowing consumers to choose their own logging and DI frameworks.
