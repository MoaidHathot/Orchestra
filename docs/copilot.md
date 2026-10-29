---
layout: default
title: Orchestra.Copilot
nav_order: 5
---
{% raw %}

# Orchestra.Copilot

Orchestra.Copilot provides a GitHub Copilot SDK implementation of the agent interfaces defined in Orchestra.Engine.

## Overview

This package bridges Orchestra with GitHub Copilot, enabling orchestrations to leverage Copilot's AI capabilities. It implements the `IAgent` and `AgentBuilder` abstractions, handling all the complexity of communicating with the Copilot SDK.

## Copilot CLI parity

The SDK drives the real Copilot runtime: one Prompt step can perform multiple model calls,
native tool executions, edits, and verification commands. Matching the prompt and model
alone does not match the CLI's effective configuration. Select the intended `workingDirectory`,
enable configuration/on-demand instruction discovery or supply explicit directories, and
preserve the runtime system prompt with `Append` when CLI-like behavior is desired.
Prompt steps use non-interactive guidance by default and normally have separate sessions.
Copilot memory and cross-session history are explicitly disabled, including on resume.

The [October 2026 evidence-based investigation](research/copilot-harness-parity-2026-10-29.md)
compares direct CLI, direct SDK, and Orchestra runs, including an actual interactive CLI
probe and a mixed deterministic/AI orchestration. It documents discovery, memory, native
mode, tool-catalog, and completion differences, with a reproducible experiment suite.

## Installation

The Copilot provider ships inside the `Orchestra` tool - there is no `Orchestra.Copilot`
package on NuGet. To use it as a library, reference the project from a clone of the repository.

## Prerequisites

- GitHub Copilot subscription
- GitHub authentication configured

## Quick Start

```csharp
using Orchestra.Copilot;
using Orchestra.Engine;

// Create the agent builder
await using var builder = new CopilotAgentBuilder(loggerFactory);

// Build an agent
var agent = await builder
    .WithModel("claude-opus-4.8")
    .WithSystemPrompt("You are a helpful assistant.")
    .BuildAgentAsync(cancellationToken);

// Send a prompt and stream the response
var task = agent.SendAsync("Explain quantum computing", cancellationToken);

await foreach (var evt in task)
{
    if (evt.Type == AgentEventType.MessageDelta)
    {
        Console.Write(evt.Content);
    }
}

// Get the final result
var result = await task.GetResultAsync();
Console.WriteLine($"\nTokens: {result.Usage?.InputTokens} in, {result.Usage?.OutputTokens} out");
```

## Integration with Orchestra.Host

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register CopilotAgentBuilder as the AgentBuilder implementation
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

// Add Orchestra Host services
builder.Services.AddOrchestraHost(options =>
{
    options.DataPath = "./data";
});

var app = builder.Build();
await app.Services.InitializeOrchestraHostAsync();
app.MapOrchestraHostEndpoints();
app.Run();
```

## CopilotAgentBuilder

The `CopilotAgentBuilder` extends the abstract `AgentBuilder` from Orchestra.Engine:

```csharp
public class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
{
    public CopilotAgentBuilder(ILoggerFactory? loggerFactory = null);
    
    public override Task<IAgent> BuildAgentAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### Configuration Methods

All configuration is done through the base `AgentBuilder` fluent API:

```csharp
var agent = await builder
    .WithModel("claude-opus-4.8")           // Set the AI model
    .WithSystemPrompt("You are an expert.") // Set system prompt
    .WithSystemPromptMode(SystemPromptMode.Replace)  // Replace or Append
    .WithReasoningLevel(ReasoningLevel.High)         // Low, Medium, High, XHigh, Max
    .WithMcp(localMcp, remoteMcp)           // Add MCP servers
    .WithSubagents(subagent1, subagent2)    // Add subagents
    .WithReporter(reporter)                 // Set event reporter
    .BuildAgentAsync(cancellationToken);
```

### Lifecycle Management

The builder manages the Copilot client lifecycle. Always dispose when done:

```csharp
await using var builder = new CopilotAgentBuilder(loggerFactory);
// Use builder...
// Client is automatically stopped on disposal
```

## CopilotAgent

The `CopilotAgent` implements `IAgent` and handles communication with the Copilot SDK:

```csharp
public interface IAgent
{
    AgentTask SendAsync(string prompt, CancellationToken ct = default);
}
```

### Sending Prompts

```csharp
AgentTask task = agent.SendAsync("Your prompt here", cancellationToken);
```

### Streaming Events

The `AgentTask` is an `IAsyncEnumerable<AgentEvent>`:

```csharp
await foreach (var evt in task)
{
    switch (evt.Type)
    {
        case AgentEventType.SessionStart:
            Console.WriteLine("Session started");
            break;
            
        case AgentEventType.MessageDelta:
            Console.Write(evt.Content);
            break;
            
        case AgentEventType.ReasoningDelta:
            Console.Write($"[Thinking: {evt.Content}]");
            break;
            
        case AgentEventType.ToolExecutionStart:
            Console.WriteLine($"Calling {evt.ToolName} from {evt.McpServerName}");
            break;
            
        case AgentEventType.ToolExecutionComplete:
            Console.WriteLine($"Tool result: {evt.ToolResult}");
            break;
            
        case AgentEventType.SubagentStarted:
            Console.WriteLine($"Delegating to {evt.SubagentName}");
            break;
            
        case AgentEventType.SubagentCompleted:
            Console.WriteLine($"Subagent completed: {evt.SubagentName}");
            break;
            
        case AgentEventType.Usage:
            Console.WriteLine($"Tokens: {evt.Usage?.InputTokens} in, {evt.Usage?.OutputTokens} out");
            break;
            
        case AgentEventType.Error:
            Console.WriteLine($"Error: {evt.ErrorMessage}");
            break;
    }
}
```

### Getting Results

```csharp
AgentResult result = await task.GetResultAsync();

Console.WriteLine($"Content: {result.Content}");
Console.WriteLine($"Model: {result.ActualModel}");
Console.WriteLine($"Input tokens: {result.Usage?.InputTokens}");
Console.WriteLine($"Output tokens: {result.Usage?.OutputTokens}");
Console.WriteLine($"Cost: ${result.Usage?.Cost}");
```

## Event Types

| Type | Description | Key Properties |
|------|-------------|----------------|
| `SessionStart` | Session has started | `Model` |
| `ModelChange` | Model selection changed | `PreviousModel`, `Model` |
| `MessageDelta` | Streaming text chunk | `Content` |
| `Message` | Complete message | `Content` |
| `ReasoningDelta` | Streaming reasoning | `Content` |
| `Reasoning` | Complete reasoning | `Content` |
| `ToolExecutionStart` | Tool call started | `ToolName`, `ToolArguments`, `McpServerName` |
| `ToolExecutionComplete` | Tool call completed | `ToolName`, `ToolResult`, `ToolSuccess`, `McpServerName` |
| `SubagentSelected` | Subagent was selected | `SubagentName`, `SubagentTools` |
| `SubagentStarted` | Subagent started | `SubagentName`, `SubagentDisplayName` |
| `SubagentCompleted` | Subagent completed | `SubagentName` |
| `SubagentFailed` | Subagent failed | `SubagentName`, `ErrorMessage` |
| `SubagentDeselected` | Subagent deselected | `SubagentName` |
| `Usage` | Token usage info | `Usage` (AgentUsage object) |
| `Error` | Error occurred | `ErrorMessage` |
| `SessionIdle` | Session completed | - |

## MCP Server Configuration

### Local MCP Servers

```csharp
var localMcp = new LocalMcp
{
    Name = "filesystem",
    Type = McpType.Local,
    Command = "npx",
    Arguments = new[] { "-y", "@modelcontextprotocol/server-filesystem", "." },
    WorkingDirectory = "/path/to/project"
};

var agent = await builder
    .WithMcp(localMcp)
    .BuildAgentAsync();
```

### Remote MCP Servers

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

var agent = await builder
    .WithMcp(remoteMcp)
    .BuildAgentAsync();
```

## Subagents

Subagents allow delegation to specialized agents:

```csharp
var codeReviewer = new Subagent
{
    Name = "code-reviewer",
    DisplayName = "Code Reviewer",
    Description = "Reviews code for bugs and best practices",
    Prompt = "You are an expert code reviewer. Analyze the code for issues.",
    Tools = new[] { "read_file", "search_code" },
    Infer = true  // Allow automatic selection
};

var agent = await builder
    .WithSubagents(codeReviewer)
    .BuildAgentAsync();
```

### Subagent Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Unique identifier |
| `DisplayName` | `string?` | Human-readable name |
| `Description` | `string?` | Description of capabilities |
| `Prompt` | `string` | System prompt for the subagent |
| `Tools` | `string[]?` | Allowed tool names |
| `Mcps` | `Mcp[]` | MCP servers for this subagent |
| `Infer` | `bool` | Allow automatic selection (default: true) |

## Reasoning Levels

Control the reasoning effort. The level is forwarded verbatim (lower-cased) as the Copilot CLI's
`reasoning_effort`; every reasoning model accepts `low` / `medium` / `high`, while `xhigh` and
`max` are only honoured by models that advertise them (GPT-5.x/6, Claude Opus 4.7+).

```csharp
// Quick responses
builder.WithReasoningLevel(ReasoningLevel.Low);

// Balanced
builder.WithReasoningLevel(ReasoningLevel.Medium);

// Deep analysis
builder.WithReasoningLevel(ReasoningLevel.High);

// Extra-deep / the model's maximum effort (model must support it)
builder.WithReasoningLevel(ReasoningLevel.XHigh);
builder.WithReasoningLevel(ReasoningLevel.Max);
```

When a step sets no level, the executor falls back to the orchestration's
`defaultReasoningLevel`, then the host's `defaultReasoningLevel` (`orchestra.json`), then the
provider/model default.

## System Prompt Modes

Control how the system prompt is applied:

```csharp
// Replace the default system prompt entirely
builder.WithSystemPromptMode(SystemPromptMode.Replace);

// Append to the default system prompt
builder.WithSystemPromptMode(SystemPromptMode.Append);
```

## Per-step Copilot options (orchestration JSON)

Prompt steps expose **opt-in** discovery, model-tuning, and structured-output controls.
`humanInput` selects interactive versus non-interactive guidance. The full
schema lives in [`schemas/orchestration.schema.json`](../schemas/orchestration.schema.json);
worked examples are in [`examples/governed-prompt-step.json`](../examples/governed-prompt-step.json)
and [`examples/tuned-prompt-step.json`](../examples/tuned-prompt-step.json).

```json
{
  "name": "review",
  "type": "Prompt",
  "systemPrompt": "...",
  "userPrompt": "...",
  "model": "claude-opus-4.8",

  "reasoningSummary": "concise",
  "contextTier": "longContext",
  "workingDirectory": "{{env.PROJECT_DIR}}",
  "githubToken": "{{env.GITHUB_TOKEN}}",

  "humanInput": true,
  "permissionPolicy": { "mode": "denyList", "deny": ["shell", "url"] },
  "sandbox": {
    "enabled": true,
    "filesystem": { "readonly": ["/work/repo"], "denied": ["/etc"] },
    "network": { "allowOutbound": false }
  }
}
```

### Model tuning

| Field | Values | Effect |
|-------|--------|--------|
| `reasoningLevel` | `low` / `medium` / `high` / `xhigh` / `max` | Reasoning effort (depth of chain-of-thought). Falls back to the orchestration's `defaultReasoningLevel`, then the host's. `xhigh`/`max` require a model that advertises them. |
| `reasoningSummary` | `none` / `concise` / `detailed` | Verbosity of the model's reasoning summary. |
| `contextTier` | `default` / `longContext` | Opts into the model's extended context window where supported. |

### Working directory

`workingDirectory` defaults to the **orchestration file's directory**, equivalent to
`workingDirectory: "{{orchestration.sourceDirectory}}"`. This also applies when Orchestra
runs as a .NET tool. Prompt, Command, and Script steps share this rule.

Explicit values are template-resolved; relative values resolve against the orchestration
file's directory. Use `workingDirectory: ".."` to work in its parent directory, or supply an
absolute project path. Prompt directories are validated to exist. In-memory definitions
without a source directory retain the process-current-directory fallback. The installation
directory is never the automatic base.

### Discovery and explicit directories

| Step field | Behavior |
|---|---|
| `enableConfigDiscovery` | Enable the runtime's ambient configuration discovery: project skills, custom agents, MCP configuration, plugins, and other supported configuration. |
| `enableOnDemandInstructionDiscovery` | Discover instructions covering files after successful agent file reads, including nested `AGENTS.md`. Separate from root instruction loading. |
| `skillDirectories` | Additional explicit skill directories, usable even when ambient discovery is disabled. |
| `instructionDirectories` | Additional explicit instruction directories, usable independently of skill directories. An explicit `[]` supplies none. When omitted, the legacy skill-directory fallback applies. |

Boolean precedence is **step → orchestration `defaultEnableConfigDiscovery` /
`defaultEnableOnDemandInstructionDiscovery` → `orchestra.json`'s corresponding `copilot`
settings → false**. Explicit `false` overrides an inherited `true`.

Automatic discovery uses the agent working directory, repository context, user configuration,
and the runtime's discovery rules. On-demand discovery follows the files actually read.
Relative **explicit** skill/instruction directories, including template-resolved relative
values, resolve from the orchestration file's directory. These are intentionally different
bases: moving the agent working directory does not relocate explicitly referenced files.

The skill engine remains enabled: disabling ambient discovery does not disable explicitly
registered or built-in skills. These discovery controls currently require the Copilot provider;
a provider that cannot honor a requested control fails before running the agent.

### Structured JSON responses

Set `responseSchema` to a JSON Schema object. Orchestra forwards it to Copilot's native
structured-output API and validates the final successful step output against the same schema.
The schema is passed verbatim; supported schema keywords depend on the model/provider.

```yaml
responseSchema:
  type: object
  properties:
    count:
      type: integer
    summary:
      type: string
  required: [count, summary]
  additionalProperties: false
```

The step still runs the full agent loop and can use tools before returning JSON. Downstream
steps can use expressions such as `{{analyze.output.count}}`. Omission requests ordinary text.
See [`examples/copilot-structured-output.yaml`](../examples/copilot-structured-output.yaml).

- Validation covers the exported result, including an optional output handler. The output
  handler receives the same native schema request.
- Output handlers preserve the step's authentication, directory, discovery, permissions, and
  sandbox settings. They run as non-interactive transformations without the main session's
  autopilot policy or orchestration-control tools.
- Invalid JSON/schema output fails with a validation error and preserves the response in
  diagnostics. Markdown fences and prose are not silently stripped to manufacture valid JSON.
- **Authoring:** set `responseSchema` and describe the data you want. No special completion
  prompt or tool call is needed. Use the validated fields in downstream conditions/scripts;
  a JSON field named `status` is data, not an automatic Orchestra status override.
- **Internal behavior:** Orchestra omits `orchestra_set_status` and `orchestra_complete`
  from these steps because they interrupt final JSON delivery. Authors do not need to
  manage this filtering. File tools and other agent tools remain available.
- OpenCode V2 supports the same validated contract using schema guidance rather than native
  constrained generation. See [provider contracts](providers.md) for the distinction.

### Session isolation and execution modes

Copilot `Memory.Enabled` and `EnableSessionStore` are set to **false** on creation and resume.
The native persistent-memory and cross-session-history tools are therefore unavailable.
Ordinary conversation history and the current session's `sql` workspace, including `todos`
and `todo_deps`, remain available. The model chooses whether to use todos; offering the tool
does not force every Prompt step to build a todo list. Explicit MCP integrations, such as a
Zakira memory service, are configured through the usual `mcps` mechanism.

Each Prompt step gets its own conversation. Dependencies pass explicit outputs/artifacts;
worker recovery can resume that same step's session. Normal internal planning remains
available. Orchestra does not select native **plan** mode.

### Opt-in autopilot

```yaml
executionPolicy:
  mode: autopilot
  maxContinues: 3
timeoutSeconds: 300
```

Omission selects `standard`: the ordinary full agent/tool loop with non-interactive guidance
unless `humanInput` overrides it. `autopilot` additionally lets the runtime continue after
an otherwise finished turn when the task is not complete. It uses Copilot SDK **1.0.28** and
the pinned CLI **1.0.105**, native session mode, and native continuation options.

`maxContinues` defaults to 5 and accepts non-negative integers; 0 permits the initial
agent/tool loop but no automatic continuations. This is a continuation limit, not a tool-call
or token limit. No active objective is created (objectives have different budgeting rules).
Timeout/cancellation still stop the step. A native stop without task completion, including
budget exhaustion, fails it. Recovery/retry budgets remain separate.

Autopilot stays inside one Prompt session; it does not advance or rewrite the DAG. It can be
combined with `responseSchema`. Immediate-stop engine tools are filtered internally, and
completion is correlated with the native request's `session.autopilot_stopped` event rather
than an intermediate idle or child-agent event. See [the cross-provider example](../examples/provider-autopilot.yaml).

### Authentication

`githubToken` authenticates this step's Copilot session, overriding the host default. Prefer
a template reference such as `{{env.GITHUB_TOKEN}}` over a hardcoded secret. The host-level
default lives in `orchestra.json` (`copilot.gitHubToken` / `copilot.useLoggedInUser` — see
[host.md](host.md)); when neither is set, the bundled CLI's own stored credentials are used.

### Human-in-the-loop

`humanInput: false` (the default) selects non-interactive model guidance and disables native
questions. `humanInput: true` selects interactive guidance and routes native **`ask_user`**,
**elicitation**, and **exit-plan-mode** requests to Orchestra's human-input surface
(Portal/CLI/API). Operators see requests via `GET /api/runs/pending` and answer with
`POST /api/orchestrations/{name}/runs/{runId}/respond`.

For compatibility, enabling the existing `request_user_input` engine tool implies interactive
behavior when `humanInput` is omitted. Explicit `humanInput: false` overrides that opt-in.
Native memory/history remain disabled in interactive steps. Tool permission policy is a
separate setting: an unattended step may still require operator approval for specific actions.

`Replace` system-prompt mode can remove the generated interaction guidance. Use `Append` or
targeted `Customize` overrides when preserving the native harness instructions is intended.

### Permission policy

`permissionPolicy.mode` controls how the agent's permission requests (shell, file read/write,
url, mcp, …) are resolved:

| Mode | Behavior |
|------|----------|
| `approveAll` | Auto-approve everything (default). |
| `denyList` | Approve unless a request's kind or target matches a `deny` glob (case-insensitive, `*`/`?` wildcards) — e.g. `["shell", "url", "*.env"]`. |
| `requireHumanApproval` | Route each request to a human operator (serialized per step); falls back to "user not available" when no operator context exists. |

### Sandbox

`sandbox` constrains the agent's shell/file/network tool access, applied to the live session
via the runtime's options-update RPC. `filesystem` accepts `readonly` / `readwrite` / `denied`
path lists; `network` accepts `allowedHosts` / `blockedHosts` / `allowOutbound` /
`allowLocalNetwork`. Set `enabled: false` to disable. Sandbox paths are passed to the runtime
verbatim (not template-resolved), and enforcement is provided on Linux/macOS.

## Model Mismatch Detection

The agent automatically detects when the requested model differs from the actual model used:

```csharp
var result = await task.GetResultAsync();

if (result.SelectedModel != result.ActualModel)
{
    Console.WriteLine($"Requested: {result.SelectedModel}");
    Console.WriteLine($"Actual: {result.ActualModel}");
    
    // Available models are included when there's a mismatch
    if (result.AvailableModels != null)
    {
        foreach (var model in result.AvailableModels)
        {
            Console.WriteLine($"  - {model.Id}: {model.Name}");
        }
    }
}
```

## Usage Information

Token usage and cost information is provided:

```csharp
var result = await task.GetResultAsync();

if (result.Usage != null)
{
    Console.WriteLine($"Input tokens: {result.Usage.InputTokens}");
    Console.WriteLine($"Output tokens: {result.Usage.OutputTokens}");
    Console.WriteLine($"Cache read tokens: {result.Usage.CacheReadTokens}");
    Console.WriteLine($"Cache write tokens: {result.Usage.CacheWriteTokens}");
    Console.WriteLine($"Cost: ${result.Usage.Cost:F4}");
    Console.WriteLine($"Duration: {result.Usage.Duration}ms");
}
```

## Error Handling

```csharp
try
{
    var task = agent.SendAsync("Hello", cancellationToken);
    
    await foreach (var evt in task)
    {
        if (evt.Type == AgentEventType.Error)
        {
            Console.WriteLine($"Error during execution: {evt.ErrorMessage}");
        }
    }
    
    var result = await task.GetResultAsync();
}
catch (OperationCanceledException)
{
    Console.WriteLine("Operation was cancelled");
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
}
```

## Complete Example

```csharp
using Microsoft.Extensions.Logging;
using Orchestra.Copilot;
using Orchestra.Engine;

// Setup logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Create the builder
await using var agentBuilder = new CopilotAgentBuilder(loggerFactory);

// Configure MCP server
var filesystem = new LocalMcp
{
    Name = "filesystem",
    Type = McpType.Local,
    Command = "npx",
    Arguments = new[] { "-y", "@modelcontextprotocol/server-filesystem", "." }
};

// Build the agent
var agent = await agentBuilder
    .WithModel("claude-opus-4.8")
    .WithSystemPrompt("You are a helpful coding assistant.")
    .WithSystemPromptMode(SystemPromptMode.Replace)
    .WithReasoningLevel(ReasoningLevel.Medium)
    .WithMcp(filesystem)
    .BuildAgentAsync();

// Execute
var task = agent.SendAsync("List all TypeScript files and summarize them");

// Stream output
await foreach (var evt in task)
{
    switch (evt.Type)
    {
        case AgentEventType.MessageDelta:
            Console.Write(evt.Content);
            break;
        case AgentEventType.ToolExecutionStart:
            Console.WriteLine($"\n[Calling {evt.ToolName}...]");
            break;
    }
}

// Get final result
var result = await task.GetResultAsync();
Console.WriteLine($"\n\n--- Complete ---");
Console.WriteLine($"Model: {result.ActualModel}");
Console.WriteLine($"Tokens: {result.Usage?.InputTokens} in, {result.Usage?.OutputTokens} out");
```

## Dependencies

- **Target Framework**: .NET 10.0
- **Project Reference**: `Orchestra.Engine`
- **Package Reference**: `GitHub.Copilot.SDK` (v1.0.28)
{% endraw %}
