# Orchestra

A declarative orchestration engine for LLM workflows built on .NET. Define multi-step AI pipelines in JSON with DAG-based execution, multiple step types, MCP tool integration, quality control loops, subagent delegation, checkpointing, and automatic triggers. Includes a full-featured web portal, CLI client, and REST API.

## Features

- **Declarative JSON Pipelines** - Define complex LLM workflows as JSON files with a comprehensive schema
- **DAG-Based Execution** - Automatic parallel execution of independent steps with cycle detection
- **Four Step Types** - Prompt (LLM), Command (shell), Http (REST calls), and Transform (string interpolation)
- **Typed Input Schema** - Strongly-typed parameters with types, descriptions, defaults, and enum constraints
- **Variables & Metadata** - Reusable variables with recursive expansion, plus built-in orchestration and step metadata
- **Template Expressions** - Rich `{{expression}}` syntax for parameters, variables, metadata, environment variables, step outputs, and file references
- **MCP Integration** - Extend LLM capabilities with Model Context Protocol servers (local stdio and remote HTTP)
- **MCP Server** - Expose orchestrations to external AI agents via data-plane and control-plane MCP endpoints
- **Quality Control Loops** - Retry steps with feedback until criteria are met
- **Subagent Delegation** - Multi-agent orchestration where steps delegate work to specialized subagents
- **Handler Transformations** - Transform inputs and outputs between steps with LLM-powered handlers
- **Multiple Triggers** - Manual, scheduler (cron/interval), webhook (with sync response), and loop-based automation
- **Checkpointing & Resume** - Persist execution state after each step and resume failed runs from the last checkpoint
- **Retry Policies** - Per-step and orchestration-level retry with exponential backoff
- **Step Timeouts** - Per-step and orchestration-level timeout configuration
- **Engine Tools** - Built-in tools for file save/read, status control, and orchestration completion
- **Skill Directories** - Attach specialized knowledge to steps via SKILL.md files
- **Prompt File References** - Load system/user/handler prompts from external files
- **Web Portal** - React + TypeScript SPA with DAG visualization, execution streaming, profile management, and import/export
- **CLI Client** - Full command-line interface for managing orchestrations, triggers, profiles, tags, and runs
- **REST API** - Complete HTTP API with SSE streaming for real-time execution monitoring
- **Profiles & Tags** - Organize orchestrations with tags and activate sets of orchestrations via named profiles
- **Version History** - Content-hash-based version tracking with diff comparison
- **Run Retention** - Automatic cleanup of old execution records
- **Customizable Formatting** - Inject custom prompt formatting via `IPromptFormatter`
- **System Prompt Control** - Fine-grained control over SDK system prompts (append or replace)

## Architecture

Orchestra is built as a layered .NET architecture:

| Layer | Project | Description |
|-------|---------|-------------|
| **Engine** | `Orchestra.Engine` | Core orchestration runtime: step executors, DAG scheduler, template resolution, MCP, triggers, storage abstractions |
| **Host** | `Orchestra.Host` | ASP.NET Core hosting: REST API, SSE streaming, trigger management, MCP server, profiles, tags, versioning, retention |
| **Copilot** | `Orchestra.Copilot` | GitHub Copilot SDK adapter implementing the `AgentBuilder`/`IAgent` abstractions |
| **Server** | `Orchestra.Server` | Standalone ASP.NET Core server composing Engine + Host + Copilot with CORS and OpenAPI |
| **CLI** | `Orchestra.Cli` | Command-line client for managing orchestrations via the REST API |
| **Portal** | `Orchestra.Playground.Copilot.Portal` | React + TypeScript web portal with DAG visualization and execution streaming |

```
+----------------------------------------------------------+
|                   Orchestration JSON                      |
|  (name, description, steps[], mcps[], trigger, inputs)   |
+----------------------------------------------------------+
                            |
                            v
+----------------------------------------------------------+
|                  OrchestrationParser                      |
|  - Parses JSON into Orchestration objects                |
|  - Resolves MCP references and prompt files              |
+----------------------------------------------------------+
                            |
                            v
+----------------------------------------------------------+
|                 OrchestrationScheduler                    |
|  - Validates DAG (detects cycles, missing deps)          |
|  - Groups steps into parallel execution layers           |
+----------------------------------------------------------+
                            |
                            v
+----------------------------------------------------------+
|                 OrchestrationExecutor                     |
|  - Executes steps based on dependency graph              |
|  - Parallel execution of independent steps               |
|  - Handles loops, retries, timeouts, checkpointing       |
+----------------------------------------------------------+
                            |
                            v
+----------------------------------------------------------+
|               Step Executors (per type)                   |
|  - PromptStepExecutor   (LLM calls via AgentBuilder)     |
|  - CommandStepExecutor  (shell commands)                  |
|  - HttpStepExecutor     (REST requests)                  |
|  - TransformStepExecutor (string interpolation)          |
+----------------------------------------------------------+
                            |
                            v
+----------------------------------------------------------+
|                     AgentBuilder                          |
|  - Abstract builder for LLM agents                       |
|  - Implementation: CopilotAgentBuilder (GitHub Copilot)  |
+----------------------------------------------------------+
```

## Quick Start

### Basic Orchestration

```json
{
  "name": "content-pipeline",
  "description": "Research and write about a topic",
  "steps": [
    {
      "name": "research",
      "type": "Prompt",
      "dependsOn": [],
      "systemPrompt": "You are a research assistant.",
      "userPrompt": "Research the topic: {{param.topic}}",
      "parameters": ["topic"],
      "model": "claude-opus-4.5"
    },
    {
      "name": "write-article",
      "type": "Prompt",
      "dependsOn": ["research"],
      "systemPrompt": "You are a content writer.",
      "userPrompt": "Write an article based on the research above.",
      "model": "claude-opus-4.5"
    }
  ]
}
```

### Running an Orchestration

```bash
dotnet run --project playground/Hosting/Orchestra.Playground.Copilot \
  -orchestration examples/my-orchestration.json \
  -mcp examples/mcp.json \
  -param topic="AI in Healthcare" \
  -print
```

## Table of Contents

- [Step Types](#step-types)
- [Orchestration Schema](#orchestration-schema)
- [Typed Inputs](#typed-inputs)
- [Template Expressions](#template-expressions)
- [Variables](#variables)
- [Subagents](#subagents)
- [Retry Policy](#retry-policy)
- [Engine Tools](#engine-tools)
- [MCP Integration](#mcp-integration)
- [MCP Server](#mcp-server)
- [Triggers](#triggers)
- [Checkpointing & Resume](#checkpointing--resume)
- [Profiles & Tags](#profiles--tags)
- [Version History](#version-history)
- [System Prompt Modes](#system-prompt-modes)
- [IPromptFormatter](#ipromptformatter)
- [Web Portal](#web-portal)
- [CLI Client](#cli-client)
- [REST API](#rest-api)
- [Programmatic Usage](#programmatic-usage)
- [Examples](#examples)
- [License](#license)

## Step Types

Orchestra supports four step types, each with a dedicated executor:

### Prompt Step

Sends prompts to an LLM. Supports system/user prompts (inline or from files), dependency context injection, input/output handler transformations, MCP tool access, quality control loops, subagent delegation, skill directories, and reasoning levels.

```json
{
  "name": "analyzer",
  "type": "Prompt",
  "dependsOn": ["data-fetcher"],
  "systemPrompt": "You are a data analyst.",
  "userPrompt": "Analyze the data and provide insights.",
  "model": "claude-opus-4.5",
  "mcps": ["filesystem"],
  "reasoningLevel": "high",
  "inputHandlerPrompt": "Extract only the numerical data.",
  "outputHandlerPrompt": "Format as a bullet-point summary."
}
```

### Command Step

Executes shell commands. Supports custom working directories, environment variables, stdin piping, and stderr capture. All string fields support template expressions.

```json
{
  "name": "build",
  "type": "Command",
  "dependsOn": [],
  "command": "dotnet",
  "arguments": ["build", "--configuration", "Release"],
  "workingDirectory": "{{vars.projectDir}}",
  "environment": {
    "BUILD_NUMBER": "{{orchestration.runId}}"
  },
  "includeStdErr": true
}
```

### Http Step

Makes HTTP requests. Supports all standard methods, custom headers, request bodies, and content types. All string fields support template expressions. No LLM involved.

```json
{
  "name": "fetch-status",
  "type": "Http",
  "dependsOn": [],
  "url": "{{vars.apiEndpoint}}/status",
  "method": "GET",
  "headers": {
    "Authorization": "Bearer {{env.API_TOKEN}}"
  }
}
```

### Transform Step

Pure string interpolation using template expressions. No LLM call, no external I/O. Useful for composing outputs from previous steps.

```json
{
  "name": "build-report",
  "type": "Transform",
  "dependsOn": ["research", "analysis"],
  "template": "# Report\n\n## Research\n{{research.output}}\n\n## Analysis\n{{analysis.output}}",
  "contentType": "text/markdown"
}
```

## Orchestration Schema

### Top-Level Properties

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `name` | string | Yes | -- | Unique name for the orchestration |
| `description` | string | Yes | -- | Human-readable description |
| `steps` | array | Yes | -- | Array of step configurations forming the execution DAG |
| `version` | string | No | `"1.0.0"` | Version string, accessible via `{{orchestration.version}}` |
| `inputs` | object | No | `null` | Typed input schema with types, descriptions, defaults, and enum constraints |
| `variables` | object | No | `{}` | User-defined variables accessible via `{{vars.name}}` |
| `tags` | array | No | `[]` | Tags for categorizing and filtering orchestrations |
| `defaultSystemPromptMode` | enum | No | `null` | Default mode for all Prompt steps: `append` or `replace` |
| `defaultRetryPolicy` | object | No | `null` | Default retry policy for all steps |
| `defaultStepTimeoutSeconds` | int | No | `null` | Default per-step timeout in seconds |
| `timeoutSeconds` | int | No | `3600` | Maximum time for the entire orchestration run |
| `mcps` | array | No | `[]` | Inline MCP server definitions |
| `trigger` | object | No | Manual | Automatic trigger configuration |

### Base Step Properties (All Step Types)

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `name` | string | Yes | Unique step identifier |
| `type` | enum | Yes | `Prompt`, `Command`, `Http`, or `Transform` |
| `dependsOn` | array | Yes | Step names this step depends on (empty `[]` for root steps) |
| `parameters` | array | No | Parameter names required by this step |
| `enabled` | bool | No | Whether the step is enabled (default: `true`) |
| `timeoutSeconds` | int | No | Per-step timeout override |
| `retry` | object | No | Per-step retry policy override |

### Prompt Step Additional Properties

| Property | Type | Description |
|----------|------|-------------|
| `systemPrompt` / `systemPromptFile` | string | System prompt (inline or file path) |
| `userPrompt` / `userPromptFile` | string | User prompt (inline or file path) |
| `model` | string | LLM model identifier |
| `mcps` | array | MCP server names this step can use |
| `inputHandlerPrompt` / `inputHandlerPromptFile` | string | Transform dependency outputs before use |
| `outputHandlerPrompt` / `outputHandlerPromptFile` | string | Transform step output |
| `systemPromptMode` | enum | `replace` or `append` (overrides orchestration default) |
| `reasoningLevel` | enum | `low`, `medium`, or `high` |
| `loop` | object | Retry/check loop configuration |
| `subagents` | array | Subagent definitions for multi-agent delegation |
| `skillDirectories` | array | Directories containing SKILL.md files |

## Typed Inputs

Define a strongly-typed input schema at the orchestration level. Provides type validation, descriptions, default values, and enum constraints.

```json
{
  "name": "deploy-service",
  "description": "Deploys a service to a target environment",
  "inputs": {
    "serviceName": {
      "type": "string",
      "description": "Name of the service to deploy",
      "required": true
    },
    "environment": {
      "type": "string",
      "description": "Target environment",
      "enum": ["staging", "production"]
    },
    "dryRun": {
      "type": "boolean",
      "description": "Simulate without making changes",
      "required": false,
      "default": "false"
    },
    "replicas": {
      "type": "number",
      "description": "Number of replicas",
      "required": false,
      "default": "3"
    }
  }
}
```

### Validation Rules

- **Required inputs**: Missing required inputs throw an error with their description
- **Type validation**: Boolean inputs must be `"true"` or `"false"`, number inputs must be parseable
- **Enum constraints**: Values must match one of the allowed values (case-insensitive)
- **Defaults**: Optional inputs that are not provided receive their default value automatically

Orchestrations without `inputs` fall back to legacy behavior: parameter names are collected from step-level `parameters` arrays and treated as required strings.

## Template Expressions

Orchestra uses `{{expression}}` syntax for dynamic values in prompts, URLs, headers, templates, and command arguments. All expressions are case-insensitive and whitespace-tolerant.

### Expression Namespaces

| Namespace | Syntax | Description |
|-----------|--------|-------------|
| Parameters | `{{param.name}}` | User-supplied parameters passed at runtime |
| Variables | `{{vars.name}}` | User-defined orchestration variables (with recursive expansion) |
| Orchestration | `{{orchestration.property}}` | Built-in orchestration metadata |
| Step | `{{step.property}}` | Current step metadata |
| Environment | `{{env.VAR_NAME}}` | OS environment variable value |
| Server | `{{server.url}}` | Orchestra server base URL |
| Working Directory | `{{workingDirectory}}` | Current working directory |
| Step Output | `{{stepName.output}}` | Output content from a completed step |
| Step Raw Output | `{{stepName.rawOutput}}` | Raw (unprocessed) output from a completed step |
| Step Files | `{{stepName.files}}` | JSON array of all file paths saved by a step |
| Step File (indexed) | `{{stepName.files[N]}}` | Path of the Nth file (0-based) saved by a step |

### Orchestration Metadata

| Property | Description | Example |
|----------|-------------|---------|
| `{{orchestration.name}}` | Orchestration name | `"deployment-pipeline"` |
| `{{orchestration.version}}` | Version | `"2.1.0"` |
| `{{orchestration.runId}}` | Unique run ID | `"abc123-def456"` |
| `{{orchestration.startedAt}}` | Start time (ISO 8601) | `"2025-06-15T10:30:00+00:00"` |

### Step Metadata

| Property | Description | Example |
|----------|-------------|---------|
| `{{step.name}}` | Current step's name | `"security-scan"` |
| `{{step.type}}` | Current step's type | `"Prompt"`, `"Command"`, `"Transform"`, `"Http"` |

## Variables

Variables let you define reusable values at the orchestration level, referenced via `{{vars.name}}`. Variable values can contain other template expressions, which are resolved recursively when used.

```json
{
  "variables": {
    "baseDir": "/data/{{param.env}}",
    "outputDir": "{{vars.baseDir}}/reports",
    "logPrefix": "[{{orchestration.name}}:{{orchestration.runId}}]"
  }
}
```

Circular references are detected and left unresolved. Unknown variables remain as-is in the output.

## Subagents

Prompt steps can delegate work to specialized subagents for multi-agent orchestration:

```json
{
  "name": "research-team",
  "type": "Prompt",
  "systemPrompt": "You are a research coordinator. Delegate tasks to your team.",
  "userPrompt": "Research {{param.topic}} thoroughly.",
  "model": "claude-opus-4.5",
  "subagents": [
    {
      "prompt": "You are a data researcher. Find quantitative data.",
      "displayName": "Data Researcher",
      "description": "Finds quantitative data and statistics",
      "mcps": ["web-fetch"]
    },
    {
      "prompt": "You are a domain expert. Provide deep analysis.",
      "displayName": "Domain Expert",
      "description": "Provides domain-specific analysis"
    }
  ]
}
```

Each subagent can have its own prompt (inline or file), display name, description, tools, and MCP server access.

## Retry Policy

Configure retry behavior per-step or as an orchestration-level default:

```json
{
  "defaultRetryPolicy": {
    "maxRetries": 3,
    "backoffSeconds": 2,
    "backoffMultiplier": 2.0,
    "retryOnTimeout": true
  },
  "steps": [
    {
      "name": "critical-step",
      "retry": {
        "maxRetries": 5,
        "backoffSeconds": 5,
        "backoffMultiplier": 1.5
      }
    }
  ]
}
```

Uses exponential backoff: delay = `backoffSeconds * (backoffMultiplier ^ attemptIndex)`.

## Engine Tools

Built-in tools available to the LLM during Prompt step execution:

| Tool | Description |
|------|-------------|
| `orchestra_save_file` | Save content to a temp file. Saved file paths are accessible via `{{stepName.files}}` and `{{stepName.files[N]}}` expressions. |
| `orchestra_read_file` | Read a previously saved file |
| `orchestra_set_status` | Set step status: `success`, `failed`, or `no_action` (skips downstream steps) |
| `orchestra_complete` | Halt the entire orchestration immediately |

## MCP Integration

Orchestra supports [Model Context Protocol](https://modelcontextprotocol.io/) servers for extending LLM capabilities with tools.

### MCP Types

- **Local**: Process communicating via stdio
- **Remote**: HTTP endpoint

### External MCP Configuration (mcp.json)

```json
{
  "mcps": [
    {
      "name": "filesystem",
      "type": "local",
      "command": "npx",
      "arguments": ["-y", "@anthropic/mcp-server-filesystem", "{{workingDirectory}}"]
    },
    {
      "name": "remote-api",
      "type": "remote",
      "endpoint": "https://api.example.com/mcp"
    }
  ]
}
```

### Inline MCP Definitions

MCPs can also be defined directly in the orchestration file under the top-level `mcps` array, then referenced by name in step-level `mcps` arrays.

## MCP Server

Orchestra exposes orchestrations to external AI agents via MCP endpoints.

### Data Plane (default: enabled)

| Tool | Description |
|------|-------------|
| `ListOrchestrations` | List and filter orchestrations by tags or name pattern |
| `InvokeOrchestration` | Invoke an orchestration (async or sync mode) |
| `GetOrchestrationStatus` | Check status/result of a running or completed execution |
| `CancelOrchestration` | Cancel a running execution |

### Control Plane (opt-in)

Full management capabilities: orchestration CRUD, tag management, profile management, trigger management, and run history.

### Configuration

```csharp
builder.Services.AddOrchestraMcpServer(options =>
{
    options.DataPlaneEnabled = true;
    options.DataPlaneRoute = "/mcp/data";
    options.ControlPlaneEnabled = false;
    options.ControlPlaneRoute = "/mcp/control";
});
```

### Connecting from an MCP Client

```json
{
  "mcps": [
    {
      "name": "orchestra",
      "type": "remote",
      "endpoint": "{{server.url}}/mcp/data"
    }
  ]
}
```

## Triggers

### Manual Trigger (default)

On-demand execution only. No additional configuration needed.

### Scheduler Trigger

Run on a cron schedule or at fixed intervals:

```json
{
  "trigger": {
    "type": "scheduler",
    "enabled": true,
    "cron": "0 9 * * MON-FRI",
    "maxRuns": 100
  }
}
```

### Webhook Trigger

Execute via HTTP POST with optional HMAC secret validation and synchronous response:

```json
{
  "trigger": {
    "type": "webhook",
    "enabled": true,
    "secret": "your-hmac-secret",
    "maxConcurrent": 5,
    "inputHandlerPrompt": "Extract 'topic' and 'audience' from the JSON payload.",
    "response": {
      "waitForResult": true,
      "responseTemplate": "Orchestration completed: {{result}}",
      "timeoutSeconds": 120
    }
  }
}
```

### Loop Trigger

Re-run on completion:

```json
{
  "trigger": {
    "type": "loop",
    "enabled": true,
    "delaySeconds": 300,
    "maxIterations": 10,
    "continueOnFailure": false
  }
}
```

## Checkpointing & Resume

Orchestra can checkpoint execution state after each step completes, allowing failed runs to be resumed from the last successful checkpoint rather than restarting from scratch.

- Full checkpoint storage abstraction (`ICheckpointStore`) with a file system implementation
- Resume via REST API: `GET /api/orchestrations/{id}/resume/{runId}` (SSE)
- List, get, and delete checkpoints via `/api/checkpoints`

## Profiles & Tags

### Tags

Categorize orchestrations with author-defined tags (in the JSON) and host-managed tags. Effective tags are the union of both. Tags are used for filtering and profile-based activation.

### Profiles

Named collections of orchestration filters that determine which orchestrations are active:

- Tag-based and ID-based filtering
- Time-window scheduling for automatic profile activation/deactivation
- Import/export profiles
- Activation history tracking
- Manual activation overrides scheduled activation
- Full REST API under `/api/profiles`

## Version History

Orchestra tracks orchestration versions using content hashing:

- Automatic version snapshots stored on disk
- Diff comparison between any two versions
- API: `/api/orchestrations/{id}/versions` and `/api/orchestrations/{id}/versions/{hash1}/diff/{hash2}`

## System Prompt Modes

Control how system prompts interact with the SDK's built-in prompts:

- **`append`** (default): Your system prompt is added to the SDK's default
- **`replace`**: Your system prompt completely replaces the SDK's default

Set at orchestration level with `defaultSystemPromptMode`, override per step with `systemPromptMode`.

## IPromptFormatter

Customize how prompts and context are formatted by implementing `IPromptFormatter`:

```csharp
public interface IPromptFormatter
{
    string FormatDependencyOutputs(IReadOnlyDictionary<string, string> dependencyOutputs);
    string BuildUserPrompt(string userPrompt, string dependencyOutputs,
        string? loopFeedback = null, string? inputHandlerPrompt = null);
    string BuildTransformationSystemPrompt(string handlerInstructions);
    string WrapContentForTransformation(string content);
}
```

The `DefaultPromptFormatter` formats dependency outputs with markdown headers, includes loop feedback when retrying, and wraps content in `<INPUT_CONTENT>` tags for transformations. Register a custom implementation via DI.

## Web Portal

The Portal (`Orchestra.Playground.Copilot.Portal`) is a full React 18 + TypeScript SPA (built with Vite) served by an ASP.NET Core backend. Features include:

- **DAG Visualization** - Interactive Mermaid-based diagrams of orchestration step graphs
- **Execution Streaming** - Real-time SSE streaming of orchestration execution progress
- **Orchestration Management** - Register, enable/disable, and browse orchestrations
- **Run History** - View past execution runs with step-level details
- **Profile Selector** - Switch between named profiles to control active orchestrations
- **MCP Viewer** - Inspect MCP server configurations
- **Import/Export** - Import and export orchestrations and profiles
- **Step Details** - Drill into individual step results and outputs

## CLI Client

The CLI (`Orchestra.Cli`) provides a command-line interface for managing orchestrations via the REST API, built with Spectre.Console for rich terminal output:

```bash
# List orchestrations
orchestra list

# Register an orchestration
orchestra register path/to/orchestration.json

# Run an orchestration
orchestra run <id> --param key=value

# Manage triggers, profiles, tags, runs
orchestra triggers list
orchestra profiles list
orchestra tags list <id>
orchestra runs list <id>
```

Commands: `list`, `get`, `register`, `remove`, `scan`, `enable`, `disable`, `run`, `active`, `cancel`, `runs`, `triggers`, `profiles`, `tags`, `server-status`.

## REST API

Orchestra.Host exposes a complete REST API. Key endpoint groups:

| Group | Prefix | Description |
|-------|--------|-------------|
| Orchestrations | `/api/orchestrations` | CRUD, browse, execute (SSE), resume |
| Runs | `/api/orchestrations/{id}/runs` | Run history, status, cancellation |
| Triggers | `/api/triggers` | Trigger management, state, history |
| Profiles | `/api/profiles` | Profile CRUD, activation, scheduling |
| Tags | `/api/tags` | Tag management |
| Versions | `/api/orchestrations/{id}/versions` | Version history and diffs |
| Checkpoints | `/api/checkpoints` | Checkpoint management |
| MCP | `/mcp/data`, `/mcp/control` | MCP server endpoints |

See `docs/api-reference.md` for the complete API specification with request/response examples.

## Programmatic Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orchestra.Engine;
using Orchestra.Copilot;

var mcps = OrchestrationParser.ParseMcpFile("mcp.json");
var orchestration = OrchestrationParser.ParseOrchestrationFile("orchestration.json", mcps);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();
builder.Services.AddSingleton<IOrchestrationReporter, NullOrchestrationReporter>();
builder.Services.AddSingleton<IScheduler, OrchestrationScheduler>();
builder.Services.AddSingleton<OrchestrationExecutor>();

var host = builder.Build();
var executor = host.Services.GetRequiredService<OrchestrationExecutor>();
var result = await executor.ExecuteAsync(orchestration, new Dictionary<string, string>
{
    ["topic"] = "AI in Healthcare"
});

if (result.Status == ExecutionStatus.Succeeded)
{
    foreach (var (stepName, stepResult) in result.Results)
    {
        Console.WriteLine($"=== {stepName} ===");
        Console.WriteLine(stepResult.Content);
    }
}
```

## Examples

See the `examples/` folder for 20 complete orchestration examples:

| Example | Description |
|---------|-------------|
| `deployment-pipeline.json` | All 4 step types with variables, metadata, and environment variables |
| `typed-inputs-deployment.json` | Typed input schema with type validation, enum constraints, and defaults |
| `subagents-research-team.json` | Multi-agent orchestration with subagent delegation |
| `mcp-orchestration-coordinator.json` | Cross-orchestration invocation via the data-plane MCP |
| `step-files-cross-reference.json` | File save/read and cross-referencing between steps |
| `skill-directories-example.json` | Agent skill directories with SKILL.md |
| `command-build-and-analyze.json` | Command steps with build and git analysis |
| `variables-and-metadata.json` | Variables with recursive expansion and metadata expressions |
| `system-prompt-mode-example.json` | System prompt mode demonstration |
| `advanced-combined-features.json` | Full pipeline with loops and MCPs |
| `webhook-triggered-notification.json` | Webhook trigger with input handler and sync response |
| `code-review-azure-devops.json` | Code review workflow |
| `weather-roads-seattle.json` | Parallel prompt execution |

See `orchestration-composing.md` for the complete orchestration schema reference.

## Documentation

Full documentation is available in the `docs/` folder and deployed via GitHub Pages:

- **Getting Started** - Installation, setup, and first orchestration
- **Engine Reference** - Core engine concepts, step types, and execution model
- **Host Reference** - REST API, SSE events, trigger management, MCP server
- **Copilot Integration** - GitHub Copilot SDK adapter, streaming events, subagents
- **API Reference** - Complete HTTP API specification

## License

[Add your license here]
