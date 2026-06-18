# Orchestra

Orchestra is a deterministic AI orchestration engine for .NET. Define multi-step AI workflows in JSON with DAG-based execution, MCP integration, quality control loops, checkpointing, triggers, and a built-in web portal, CLI, and REST API.

## Features

- **Declarative JSON Pipelines** - Define complex LLM workflows as JSON files with a comprehensive schema
- **Pluggable Agent Providers** - Run Prompt steps on GitHub Copilot or OpenCode, selectable per orchestration or per step
- **DAG-Based Execution** - Automatic parallel execution of independent steps with cycle detection
- **Five Step Types** - Prompt (LLM), Command (shell), Script (inline/file scripts), Http (REST calls), and Transform (string interpolation)
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
| **Engine** | `Orchestra.Engine` | Core orchestration runtime: step executors, DAG scheduler, template resolution, MCP, triggers, storage abstractions, agent-provider registry |
| **Host** | `Orchestra.Host` | ASP.NET Core hosting: REST API, SSE streaming, trigger management, MCP server, profiles, tags, versioning, retention |
| **Copilot** | `Orchestra.Copilot` | GitHub Copilot SDK adapter implementing the `AgentBuilder`/`IAgent` abstractions |
| **OpenCode** | `Orchestra.OpenCode` | [OpenCode](https://opencode.ai) adapter: drives an `opencode serve` HTTP+SSE server, with an engine-tool MCP bridge |
| **Server** | `Orchestra.Server` | Standalone ASP.NET Core server composing Engine + Host + Copilot + OpenCode with CORS and OpenAPI |
| **CLI / tool** | `Orchestra.Cli` | The single `orchestra` tool (NuGet id `Orchestra`): portal, one-shot `run`/`exec`, and the HTTP/SSE client verbs |
| **Portal UI** | `Orchestra.Playground.Copilot.Portal` | React + TypeScript web portal (DAG visualization, execution streaming); served by `orchestra portal` |

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
|  - ScriptStepExecutor   (inline/file scripts)             |
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

### Editor Validation (`$schema` setup)

Orchestra ships three JSON Schemas that give you autocomplete, type-checking, and unknown-field errors in any editor that supports JSON Schema (VS Code, JetBrains, neovim, etc.):

| Schema file | Validates |
|---|---|
| `orchestration.schema.json` | Orchestration definition files (`.json` and `.yaml`) |
| `orchestra.mcp.schema.json` | `orchestra.mcp.json` (folder-scoped MCP server lists) |
| `orchestra.services.schema.json` | `orchestra.services.json` (managed processes and lifecycle hooks) |

You have three ways to reference them — pick whichever fits your environment:

**1. Public URL (zero setup, network required to fetch once).** Reference the schemas hosted on GitHub:

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/MoaidHathot/orchestra/main/schemas/orchestration.schema.json",
  "name": "my-orchestration",
  ...
}
```

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/MoaidHathot/orchestra/main/schemas/orchestration.schema.json
name: my-orchestration
```

For stable validation that does not move with `main`, replace `main` with a release tag (e.g., `v0.2.0`).

**2. Local copy bundled with the tool (offline, version-pinned to your installed Orchestra).** Run once in your project root:

```bash
orchestra schemas
# Wrote: <cwd>/.orchestra/schemas/orchestration.schema.json
# Wrote: <cwd>/.orchestra/schemas/orchestra.mcp.schema.json
# Wrote: <cwd>/.orchestra/schemas/orchestra.services.schema.json
```

This copies the three schemas from the installed `Orchestra` NuGet tool into `./.orchestra/schemas/`. Then reference them with a relative path:

```jsonc
{ "$schema": ".orchestra/schemas/orchestration.schema.json", ... }
```

```yaml
# yaml-language-server: $schema=./.orchestra/schemas/orchestration.schema.json
```

Flags:
- `--output <dir>` — write to a different directory (default: `./.orchestra/schemas`)
- `--force` — overwrite existing schema files
- `--help` — print usage

**3. Repository-relative path (only when authoring inside this repo's `examples/` folder).** Most bundled examples use:

```yaml
# yaml-language-server: $schema=../schemas/orchestration.schema.json
```

## Table of Contents

- [Step Types](#step-types)
- [Editor Validation (`$schema` setup)](#editor-validation-schema-setup)
- [Orchestration Schema](#orchestration-schema)
- [Typed Inputs](#typed-inputs)
- [Template Expressions](#template-expressions)
- [Variables](#variables)
- [Subagents](#subagents)
- [Retry Policy](#retry-policy)
- [Engine Tools](#engine-tools)
- [Agent Providers](#agent-providers)
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

Orchestra supports five step types, each with a dedicated executor:

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

### Script Step

Executes an inline or file-based script using a specified shell interpreter (`pwsh`, `bash`, `python`, `node`, etc.). Designed for multi-line scripts with first-class support for inline content -- particularly readable in YAML with `|` blocks. All string fields support template expressions.

```json
{
  "name": "gather-system-info",
  "type": "Script",
  "shell": "pwsh",
  "script": "$ErrorActionPreference = 'Stop'\n$info = @{ Host = hostname; OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription }\n$info | ConvertTo-Json",
  "timeoutSeconds": 30
}
```

YAML format (recommended for scripts):

```yaml
- name: gather-system-info
  type: Script
  shell: pwsh
  script: |
    $ErrorActionPreference = 'Stop'
    $info = @{
        Host = hostname
        OS   = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    }
    $info | ConvertTo-Json
  timeoutSeconds: 30
```

Script steps can also reference external files:

```json
{
  "name": "deploy",
  "type": "Script",
  "shell": "pwsh",
  "scriptFile": "scripts/deploy.ps1",
  "arguments": ["{{param.environment}}"]
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
| `hooks` | array | No | `[]` | Lifecycle hooks that run after step/orchestration outcomes |
| `metadata` | object | No | `{}` | Free-form metadata (any JSON shape). Not inspected by the runtime; for authors and managers only. |

### Editor Schema Validation

The `schemas/orchestration.schema.json` JSON Schema works for both JSON and YAML orchestration files in any editor that supports JSON Schema. Once bound, you get autocomplete, hover docs, type checks, and unknown-field errors.

**JSON files** -- editors auto-detect via the `$schema` property. Just keep this at the top:

```json
{
  "$schema": "../schemas/orchestration.schema.json",
  "name": "...",
  ...
}
```

**YAML files** -- because YAML has no built-in schema indirection, declare it explicitly using one of these conventions:

```yaml
# yaml-language-server: $schema=../schemas/orchestration.schema.json   <-- modeline (recommended)
name: ...
```

or

```yaml
$schema: ../schemas/orchestration.schema.json   # top-level key
name: ...
```

Known-good editors: VS Code (Red Hat YAML extension), JetBrains IDEs (Rider/IntelliJ), and anything else built on `yaml-language-server` (Neovim, Helix, etc.).

### Base Step Properties (All Step Types)

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `name` | string | Yes | Unique step identifier |
| `type` | enum | Yes | `Prompt`, `Command`, `Script`, `Http`, or `Transform` |
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

## Hooks

Hooks let an orchestration react to step and run outcomes without embedding all of that logic in the main DAG. In v1, hooks are lifecycle-driven and execute scripts with a structured JSON payload provided on stdin.

Supported events:

- `step.success`
- `step.failure`
- `step.after`
- `orchestration.success`
- `orchestration.failure`
- `orchestration.after`

Payload options:

- `detail`: `compact`, `standard`, or `full`
- `steps`: `none`, `current`, `failed`, `nonSucceeded`, `terminal`, `all`, or an explicit step-name array
- `includeRefs`: include API / MCP references for fetching more run data

Filtering:

- `when.steps.names`: step names to evaluate
- `when.steps.status`: `any`, `succeeded`, `failed`, `cancelled`, `skipped`, `noAction`, `nonSucceeded`
- `when.steps.match`: `any` or `all`

Example:

```yaml
hooks:
  - name: triage-critical-step-failures
    on: step.failure
    when:
      steps:
        names: [build, deploy]
        status: failed
        match: any
    payload:
      detail: compact
      steps: current
      includeRefs: true
    action:
      type: script
      shell: pwsh
      script: |
        $payload = $input | Out-String
        $payload | Set-Content ./hook-output.json
```

Hooks are different from `inputHandlerPrompt` and `outputHandlerPrompt`:

- handlers transform prompt input or output for a Prompt step
- hooks run after lifecycle events and are meant for follow-up automation

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
| `{{step.type}}` | Current step's type | `"Prompt"`, `"Command"`, `"Script"`, `"Transform"`, `"Http"` |

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

## Agent Providers

Orchestra runs Prompt steps through a pluggable agent provider. Two are built in:

| Provider | Name | Backend |
|----------|------|---------|
| GitHub Copilot | `copilot` | Spawns the Copilot CLI per run and drives it via the GitHub Copilot SDK (JSON-RPC over stdio). |
| OpenCode | `opencode` | Spawns an [`opencode serve`](https://opencode.ai/docs/server) HTTP server per run and drives it over REST + the `/event` SSE bus. |

### Provider capability matrix

Every provider declares which step-level features it supports via `AgentBuilder.GetCapabilities()`. When a step **uses** a feature the resolved provider does **not** support, the engine **fails the step before it runs** (category `ValidationError`) — it never silently drops configuration. The check only fires for features the step actually sets, so it never trips spuriously. Cross-provider conformance tests keep this matrix honest.

| Step feature | `copilot` | `opencode` |
|---|---|---|
| `model`, `systemPrompt` (Replace) | ✅ | ✅ |
| `mcps` (step MCP servers) | ✅ | ✅ |
| `subagents` (inline) | ✅ | ✅ |
| `reasoningLevel` | ✅ | ✅ |
| `workingDirectory` | ✅ | ✅ |
| `skillDirectories` | ✅ | ✅ |
| engine tools / `attachments` / `humanInput` / `permissionPolicy` | ✅ | ✅ |
| `excludedTools` | ✅ | ✅ (OpenCode tool names) |
| `infiniteSessions` (enable/disable) | ✅ | ✅ (toggle via OPENCODE_DISABLE_AUTOCOMPACT) |
| CLI/worker **swap** on transport failure | ✅ | ✅ |
| session **resume** on swap | ✅ | ✅ (re-prompts the persisted session) |
| `systemPromptMode` **Append/Customize** + sections | ✅ | ⛔ **fails the step** (Replace works) |
| `reasoningSummary`, `contextTier`, `gitHubToken` | ✅ | ⛔ **fails the step** |
| `sandbox` | ✅ | ⛔ **fails the step** (no equivalent) |

### Selecting a provider

Resolution precedence for each Prompt step is **step `provider` → orchestration `defaultProvider` → host default**:

```jsonc
{
  "defaultProvider": "opencode",          // default for every Prompt step in this orchestration
  "defaultModel": "github-copilot/claude-opus-4.8",
  "steps": [
    { "name": "research", "type": "Prompt", "model": "github-copilot/claude-opus-4.8", "systemPrompt": "...", "userPrompt": "..." },
    { "name": "draft",    "type": "Prompt", "provider": "copilot", "model": "claude-opus-4.8", "systemPrompt": "...", "userPrompt": "..." }
  ]
}
```

A single run may mix providers across steps; the engine opens one per-run worker pool per provider actually used. The host's default provider comes from `orchestra.json`:

```jsonc
{
  "provider": "copilot",                  // host default when an orchestration/step doesn't specify one
  "opencode": {
    "cliPath": "opencode",                // optional: path to the opencode binary (else PATH)
    "fallbackProvider": "github-copilot", // bare model ids (e.g. claude-opus-4.8) get this provider prefix
    "swapBudgetPerStep": 1,               // optional: max cold-restart swaps per step on transport failure (default 1)
    "serverPassword": "${OPENCODE_SERVER_PASSWORD}"
  }
}
```

### OpenCode notes

- **Models** are addressed as `provider/model` (e.g. `github-copilot/claude-opus-4.8`). A bare model id is paired with `opencode.fallbackProvider` (default `github-copilot`), so existing Copilot-style ids keep working. OpenCode must already be authenticated to the target provider (e.g. its GitHub Copilot connection).
- **Spawn-only**: the adapter always launches its own `opencode serve` on a loopback port (resolved from `opencode.cliPath`, `ORCHESTRA_OPENCODE_PATH`, or `opencode` on PATH). There is no connect-only mode — steps that need per-step config get a dedicated server with a generated `opencode.json`.
- **Per-step config in the artifact folder**: steps using `reasoningLevel`, `subagents`, `mcps`, `excludedTools`, `workingDirectory`, `skillDirectories`, or `infiniteSessions` (disable) spawn a *dedicated* server pointed (via `OPENCODE_CONFIG`) at a generated `opencode.json` written into the run's artifact folder, with skills staged under `<cwd>/.opencode/skills/<name>/`. Plain text-prompt steps share the run pool. The primary agent carries the system prompt + `reasoningEffort`; each `subagents[]` entry becomes a `subagent` the model delegates to via OpenCode's Task tool (scoped by a `permission.task` allow-list); each `mcps[]` entry becomes a `local`/`remote` MCP server; `excludedTools` become `tools:{name:false}` on the agent (OpenCode tool names — `bash`, `edit`, `write`, …); `infiniteSessions.enabled:false` sets `OPENCODE_DISABLE_AUTOCOMPACT`.
- **Swap & resume**: a transport-class failure (event-stream loss or a transient upstream session error) is retried on a fresh server via the shared swap loop, bounded by `opencode.swapBudgetPerStep` (default 1). OpenCode persists sessions in its data dir (shared across server processes), so a swap **resumes** the prior session by re-prompting its id (preserving tool-call progress); if the session is unreachable it cold-restarts on a new one. Set `opencode.resumeOnSwapEnabled:false` to always cold-restart. The session is deleted only when the turn completes; a failed attempt's session is left for the next swap to resume (orphans are pruned by OpenCode).
- **MCP fail-fast**: declared MCP servers that don't load on the server (absent from `GET /mcp`) are reported as failed so the step fails fast instead of running without its tools. Global (proxy-routed) MCPs are additionally tool-count-probed by the engine before the LLM runs, the same as for Copilot.
- **Engine tools** (`orchestra_set_status`, `orchestra_complete`, file save/read, `request_user_input`) are exposed to OpenCode via a loopback HTTP MCP bridge that calls back into the per-step `EngineToolContext`. Disable with `opencode.engineToolBridgeEnabled: false`.
- **Permissions / HITL** map to OpenCode's `permission.updated` events and the `POST /session/{id}/permissions/{id}` reply (auto-approve, deny-list, or human approval).
- **System prompt**: `systemPromptMode: replace` (the default) is honored. `append` / `customize` are not supported (OpenCode's API can't compose with its built-in base prompt); a step that uses them fails fast.


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
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "{{workingDirectory}}"]
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
| `ListPendingInputs` | List orchestration runs awaiting human input (Approval steps + `orchestra_request_user_input` tool calls) |
| `RespondToInput` | Submit a response to a pending human-input wait, unblocking the orchestration |

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

## CLI (`orchestra`)

Everything ships as a single .NET tool — package id **`Orchestra`**, command **`orchestra`** —
built on **Spectre.Console.Cli**, so every subcommand has its own `--help` with typed
arguments and examples. Running `orchestra` with no command prints the help.

```bash
dotnet tool install --global Orchestra        # then: orchestra ...
# or run without installing:  dnx Orchestra --yes -- <command> [options]
```

The tool covers four things under one command:

- **`orchestra portal`** — launch the long-running host + Portal web UI (REST API, MCP endpoints, dashboard).
- **`orchestra run` / `orchestra exec`** — run a single orchestration to completion (see below).
- **client verbs** — manage a *running* server over HTTP/SSE (`list`, `get`, `register`, `attach`, …).
- **`orchestra schemas`** — copy the bundled JSON schemas locally for editor `$schema` validation.

### Running an orchestration: `run` / `exec`

`orchestra run` runs one orchestration to completion. Its `--mode` decides where it runs:

- **`auto` (default)** — attach to a running Orchestra instance when one is configured (via `--server`, `ORCHESTRA_URL`, or the `hostBaseUrl`/`urls` in the discovered `orchestra.json`) **and** healthy; otherwise spawn a throwaway isolated in-process host for the run.
- **`existing`** — require a healthy configured instance (error if none).
- **`isolated`** — always spawn a self-contained throwaway host. `orchestra exec` is an alias for `run --mode isolated`.

```bash
# Run a registered orchestration (auto: uses your running server if reachable, else self-hosts)
orchestra run research-assistant --param topic=AI

# Run an ad-hoc file in a self-contained host and print a report
orchestra exec --run-file ./pipeline.yaml --report markdown

# Force talking to your server (never self-host)
orchestra run deploy-pipeline --mode existing -q
```

### Managing a running server

These verbs are a thin HTTP/SSE client; the server URL resolves from `--server <URL>`, then
`ORCHESTRA_URL`, then `http://localhost:5000`.

```bash
# Discover orchestrations
orchestra list
orchestra list --filter deploy --tag prod --enabled
orchestra get research-assistant --format table

# Register / scan / remove
orchestra register ./orchestrations/hello-world.json
orchestra scan ./orchestrations
orchestra remove research-assistant

# Re-attach to an in-flight run, or inspect past ones
orchestra attach research-assistant run-abc123
orchestra runs list --limit 50
orchestra runs get research-assistant run-abc123

# Manage triggers, profiles, tags, HITL
orchestra triggers list
orchestra profiles activate nightly-research
orchestra tags add research-assistant prod,nightly
orchestra pending
orchestra respond research-assistant run-abc123 review --choice approve --by alice
```

Command groups: `portal`, `run`, `exec`, `schemas`, `list`, `get`, `register`, `remove`,
`scan`, `enable`, `disable`, `attach`, `active`, `cancel`, `runs`, `triggers`, `profiles`,
`tags`, `pending`, `respond`, `server-status`.

See [`docs/cli.md`](docs/cli.md) for the full command reference, exit-code mapping, and
HITL workflow walkthrough.

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

See the `examples/` folder for complete orchestration examples, including:

| Example | Description |
|---------|-------------|
| `deployment-pipeline.json` | All 5 step types with variables, metadata, and environment variables |
| `typed-inputs-deployment.json` | Typed input schema with type validation, enum constraints, and defaults |
| `subagents-research-team.json` | Multi-agent orchestration with subagent delegation |
| `mcp-orchestration-coordinator.json` | Cross-orchestration invocation via the data-plane MCP |
| `step-files-cross-reference.json` | File save/read and cross-referencing between steps |
| `skill-directories-example.json` | Agent skill directories with SKILL.md |
| `command-build-and-analyze.json` | Command steps with build and git analysis |
| `script-step-example.yaml` | Script step with inline PowerShell and mixed step types |
| `variables-and-metadata.json` | Variables with recursive expansion and metadata expressions |
| `variables-and-metadata.yaml` | YAML twin of variables-and-metadata.json -- shows free-form metadata in YAML |
| `system-prompt-mode-example.json` | System prompt mode demonstration |
| `remote-schema-reference.yaml` | YAML example using the public GitHub schema URL |
| `remote-schema-reference.json` | JSON example using the public GitHub schema URL |
| `advanced-combined-features.json` | Full pipeline with loops and MCPs |
| `webhook-triggered-notification.json` | Webhook trigger with input handler and sync response |
| `hooks-step-failure.yaml` | Step failure hook with current-step payload |
| `hooks-orchestration-failure.json` | Orchestration failure hook with filtered failed steps |
| `code-review-azure-devops.json` | Code review workflow |
| `weather-roads-seattle.json` | Parallel prompt execution |
| `opencode-content-pipeline.json` | Research-then-write pipeline running on the OpenCode provider |
| `mixed-provider-pipeline.json` | Per-step provider selection (Copilot + OpenCode in one run) |

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
