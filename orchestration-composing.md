# Composing Orchestration Files

An **orchestration file** is a JSON document that defines a directed acyclic graph (DAG) of steps that execute in dependency order. Each step can call an LLM, make an HTTP request, run a shell command, execute a script, or perform string interpolation. Steps can run in parallel when they share no dependencies, retry on failure, loop until a condition is met, and delegate work to subagents. Orchestrations can be triggered manually, on a schedule, in a continuous loop, or via webhook.

This document is the complete reference for every property, step type, trigger type, and supporting object available in an orchestration file.

---

## Table of Contents

- [File Structure Overview](#file-structure-overview)
- [Top-Level Properties](#top-level-properties)
- [Steps](#steps)
  - [Base Step Properties](#base-step-properties)
  - [Step Types](#step-types)
    - [Prompt Step](#prompt-step)
    - [Http Step](#http-step)
    - [Transform Step](#transform-step)
    - [Command Step](#command-step)
    - [Script Step](#script-step)
- [Loop Configuration](#loop-configuration)
- [Subagents](#subagents)
- [Retry Policy](#retry-policy)
- [Triggers](#triggers)
  - [Manual Trigger](#manual-trigger)
  - [Scheduler Trigger](#scheduler-trigger)
  - [Loop Trigger](#loop-trigger)
  - [Webhook Trigger](#webhook-trigger)
- [MCP Definitions](#mcp-definitions)
  - [Local MCP](#local-mcp)
  - [Remote MCP](#remote-mcp)
- [Template Expressions](#template-expressions)
- [Enums Reference](#enums-reference)
- [Examples](#examples)

---

## File Structure Overview

An orchestration file is a single JSON object with three required fields (`name`, `description`, `steps`) and several optional fields for versioning, triggers, variables, MCPs, and defaults.

Minimal valid orchestration:

```json
{
  "name": "my-orchestration",
  "description": "A simple orchestration",
  "steps": [
    {
      "name": "greet",
      "type": "Prompt",
      "systemPrompt": "You are a helpful assistant.",
      "userPrompt": "Say hello.",
      "model": "gpt-5.4"
    }
  ]
}
```

---

## Top-Level Properties

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | `string` | **Yes** | -- | Unique name identifying the orchestration. |
| `description` | `string` | **Yes** | -- | Human-readable description of what this orchestration does. |
| `steps` | `Step[]` | **Yes** | -- | Array of step definitions forming the execution DAG. |
| `version` | `string` | No | `"1.0.0"` | Semantic version for tracking changes. Accessible via `{{orchestration.version}}`. |
| `inputs` | `object` | No | `null` | Typed input schema. Keys are input names, values are `InputDefinition` objects. When defined, provides type validation, descriptions, defaults, and enum constraints. |
| `trigger` | `TriggerConfig` | No | Manual | How the orchestration is triggered. Defaults to manual (on-demand). |
| `mcps` | `Mcp[]` | No | `[]` | Inline MCP (Model Context Protocol) server definitions available to steps. |
| `defaultSystemPromptMode` | `string` | No | `null` | Default system prompt mode for all Prompt steps. Values: `"append"` or `"replace"`. |
| `defaultRetryPolicy` | `RetryPolicy` | No | `null` | Default retry policy applied to all steps unless overridden at the step level. |
| `defaultStepTimeoutSeconds` | `int` | No | `null` | Default per-step timeout in seconds. Individual steps can override this. |
| `timeoutSeconds` | `int` | No | `3600` | Maximum time in seconds for the entire orchestration run. Set to `0` or `null` to disable. |
| `variables` | `object` | No | `{}` | Key-value pairs of user-defined variables. Values can contain template expressions. Accessed via `{{vars.name}}`. |
| `tags` | `string[]` | No | `[]` | Tags for categorizing and filtering orchestrations. |

---

## Typed Inputs

The `inputs` property defines a strongly-typed schema for orchestration parameters. When present, it is the authoritative source for parameter definitions, providing type validation, descriptions, default values, and enum constraints.

When `inputs` is not defined, the orchestration uses legacy behavior: parameter names are collected from step-level `parameters` arrays and treated as required string values.

### InputDefinition Properties

Each key in the `inputs` object is the input name. Each value is an `InputDefinition`:

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `type` | `string` | No | `"string"` | Data type. One of: `"string"`, `"boolean"`, `"number"`. |
| `description` | `string` | No | `null` | Human-readable description. Used for documentation and MCP tool schema generation. |
| `required` | `bool` | No | `true` | Whether this input must be provided at runtime. |
| `default` | `string` | No | `null` | Default value for optional inputs. Ignored when `required` is `true`. |
| `enum` | `string[]` | No | `[]` | Allowed values. When non-empty, the provided value must be one of these (case-insensitive). |

### Validation Rules

When `inputs` is defined, the engine validates parameters before execution:

1. **Missing required inputs** produce an error listing the input name and its description.
2. **Type validation**: Boolean inputs must be `"true"` or `"false"`. Number inputs must be parseable as a numeric value.
3. **Enum constraints**: When `enum` is non-empty, the provided value must match one of the allowed values (case-insensitive comparison).
4. **Default application**: Optional inputs (`required: false`) that are not provided receive their `default` value automatically.

### Example

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
      "description": "Simulate without deploying",
      "required": false,
      "default": "false"
    },
    "replicas": {
      "type": "number",
      "description": "Number of replicas",
      "required": false,
      "default": "3"
    }
  },
  "steps": [
    {
      "name": "deploy",
      "type": "Prompt",
      "systemPrompt": "You are a deployment assistant.",
      "userPrompt": "Deploy {{param.serviceName}} to {{param.environment}} (replicas: {{param.replicas}}, dry run: {{param.dryRun}}).",
      "parameters": ["serviceName", "environment", "replicas", "dryRun"],
      "model": "claude-opus-4.5"
    }
  ]
}
```

In this example, running the orchestration with only `serviceName=api` and `environment=staging` succeeds because `dryRun` defaults to `"false"` and `replicas` defaults to `"3"`. Providing `environment=development` fails because it is not in the allowed enum values.

---

## Steps

Steps are the building blocks of an orchestration. They form a DAG: steps with no `dependsOn` run first (and in parallel with each other), and downstream steps run once all their dependencies have completed.

### Base Step Properties

These properties are shared by **all** step types.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | `string` | **Yes** | -- | Unique name within the orchestration. Used to reference this step in `dependsOn` and template expressions. |
| `type` | `string` | **Yes** | -- | Step type. One of: `"Prompt"`, `"Http"`, `"Transform"`, `"Command"`, `"Script"` (case-insensitive). |
| `dependsOn` | `string[]` | No | `[]` | Names of steps that must complete before this step runs. Defines the DAG edges. |
| `parameters` | `string[]` | No | `[]` | Parameter names this step expects. Values are provided at runtime and accessed via `{{param.name}}`. |
| `enabled` | `bool` | No | `true` | When `false`, the step is skipped during execution. |
| `timeoutSeconds` | `int` | No | `null` | Per-step timeout in seconds. Falls back to `defaultStepTimeoutSeconds` if not set. Set to `0` to explicitly disable timeout. |
| `retry` | `RetryPolicy` | No | `null` | Per-step retry policy. Overrides `defaultRetryPolicy`. |

---

### Step Types

#### Prompt Step

**Type value:** `"Prompt"`

Sends a prompt to a large language model (LLM) and captures the response as output. Supports input/output handlers for pre/post-processing, subagents for delegation, loops for iterative refinement, MCP tool access, and reasoning levels.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `systemPrompt` | `string` | **Yes*** | -- | System prompt text provided inline. |
| `systemPromptFile` | `string` | **Yes*** | -- | Path to a file containing the system prompt. Mutually exclusive with `systemPrompt`. |
| `userPrompt` | `string` | **Yes*** | -- | User prompt text provided inline. |
| `userPromptFile` | `string` | **Yes*** | -- | Path to a file containing the user prompt. Mutually exclusive with `userPrompt`. |
| `model` | `string` | **Yes** | -- | LLM model identifier (e.g., `"claude-opus-4.5"`, `"gpt-4o"`). |
| `inputHandlerPrompt` | `string` | No | `null` | An LLM prompt that pre-processes dependency outputs before the main prompt sees them. Useful for summarizing, filtering, or restructuring upstream outputs. |
| `inputHandlerPromptFile` | `string` | No | `null` | Path to file containing the input handler prompt. Mutually exclusive with `inputHandlerPrompt`. |
| `outputHandlerPrompt` | `string` | No | `null` | An LLM prompt that post-processes the main LLM output. Useful for formatting, validation, or extraction. |
| `outputHandlerPromptFile` | `string` | No | `null` | Path to file containing the output handler prompt. Mutually exclusive with `outputHandlerPrompt`. |
| `reasoningLevel` | `string` | No | `null` | Controls the model's extended thinking. Values: `"Low"`, `"Medium"`, `"High"`. |
| `systemPromptMode` | `string` | No | `null` | How the system prompt interacts with the SDK's built-in prompts. `"append"` adds to them; `"replace"` removes them. Overrides `defaultSystemPromptMode`. |
| `mcps` | `string[]` | No | `[]` | Names of MCP servers (defined at orchestration level or in `mcp.json`) to attach as tools for this step. |
| `loop` | `LoopConfig` | No | `null` | Loop/checker configuration for iterative refinement. See [Loop Configuration](#loop-configuration). |
| `subagents` | `Subagent[]` | No | `[]` | Subagent definitions for multi-agent delegation. See [Subagents](#subagents). |
| `skillDirectories` | `string[]` | No | `[]` | Directories containing `SKILL.md` files that provide additional context/instructions. |

> **\*Mutual exclusion rules:**
> - Exactly one of `systemPrompt` or `systemPromptFile` is required. Specifying both throws a parse error.
> - Exactly one of `userPrompt` or `userPromptFile` is required. Specifying both throws a parse error.
> - Similarly, `inputHandlerPrompt` and `inputHandlerPromptFile` are mutually exclusive, as are `outputHandlerPrompt` and `outputHandlerPromptFile`.

**Example:**

```json
{
  "name": "analyze",
  "type": "Prompt",
  "dependsOn": ["fetch-data"],
  "systemPrompt": "You are a data analyst.",
  "userPrompt": "Analyze the following data:\n\n{{fetch-data.output}}",
  "model": "claude-opus-4.5",
  "inputHandlerPrompt": "Extract only the numeric data points from the input.",
  "outputHandlerPrompt": "Format the output as a markdown table.",
  "reasoningLevel": "High",
  "mcps": ["filesystem"],
  "timeoutSeconds": 120
}
```

---

#### Http Step

**Type value:** `"Http"`

Makes an HTTP request and captures the response body as output. No LLM is involved. All string properties support template expressions.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `url` | `string` | **Yes** | -- | URL to send the request to. Supports template expressions. |
| `method` | `string` | No | `"GET"` | HTTP method: `GET`, `POST`, `PUT`, `PATCH`, `DELETE`. |
| `headers` | `object` | No | `{}` | Key-value pairs of request headers. Values support template expressions. |
| `body` | `string` | No | `null` | Request body. Supports template expressions. |
| `contentType` | `string` | No | `"application/json"` | Content-Type header for the request body. |

**Example:**

```json
{
  "name": "notify-slack",
  "type": "Http",
  "dependsOn": ["generate-report"],
  "method": "POST",
  "url": "{{vars.slackWebhookUrl}}",
  "headers": {
    "Content-Type": "application/json",
    "X-Run-Id": "{{orchestration.runId}}"
  },
  "body": "{\"text\": \"Report ready: {{generate-report.output}}\"}",
  "timeoutSeconds": 30
}
```

---

#### Transform Step

**Type value:** `"Transform"`

Performs pure string interpolation with no LLM call and no external requests. Takes a template string, substitutes expressions, and produces the result as output. Useful for combining, reformatting, or restructuring outputs from other steps.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `template` | `string` | **Yes** | -- | Template string with `{{expression}}` placeholders. See [Template Expressions](#template-expressions). |
| `contentType` | `string` | No | `"text/plain"` | Content type hint for downstream consumers. |

**Example:**

```json
{
  "name": "build-report",
  "type": "Transform",
  "dependsOn": ["security-scan", "changelog"],
  "template": "# Deployment Report\n\n## Security\n{{security-scan.output}}\n\n## Changes\n{{changelog.output}}\n\n**Version:** {{orchestration.version}}\n**Run:** {{orchestration.runId}}"
}
```

---

#### Command Step

**Type value:** `"Command"`

Executes a shell command as a child process and captures its stdout (and optionally stderr) as output. All string properties support template expressions.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `command` | `string` | **Yes** | -- | Executable to run (e.g., `"dotnet"`, `"python"`, `"git"`, `"npm"`). |
| `arguments` | `string[]` | No | `[]` | Command-line arguments. Each element supports template expressions. |
| `workingDirectory` | `string` | No | Current directory | Working directory for the process. Supports template expressions. |
| `environment` | `object` | No | `{}` | Environment variables to set. Values support template expressions. |
| `includeStdErr` | `bool` | No | `false` | Whether to append stderr to the captured output. |
| `stdin` | `string` | No | `null` | Content to pipe to the process's standard input. Supports template expressions. |

**Example:**

```json
{
  "name": "run-tests",
  "type": "Command",
  "command": "dotnet",
  "arguments": ["test", "--no-build", "--verbosity", "minimal"],
  "workingDirectory": "{{param.projectPath}}",
  "includeStdErr": true,
  "timeoutSeconds": 180
}
```

---

#### Script Step

**Type value:** `"Script"`

Executes an inline or file-based script using a specified shell interpreter (e.g., `pwsh`, `bash`, `python`, `node`). The script's stdout is captured as output. Unlike the Command step which invokes a single executable with arguments, the Script step is designed for multi-line scripts with first-class support for inline content and external script files. All string properties support template expressions.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `shell` | `string` | **Yes** | -- | Shell interpreter to use (e.g., `"pwsh"`, `"bash"`, `"python"`, `"node"`). |
| `script` | `string` | **Yes*** | -- | Inline script content. Supports template expressions. |
| `scriptFile` | `string` | **Yes*** | -- | Path to an external script file. Relative paths are resolved from the orchestration file's directory. |
| `arguments` | `string[]` | No | `[]` | Arguments passed to the script. Each element supports template expressions. |
| `workingDirectory` | `string` | No | Current directory | Working directory for the process. Supports template expressions. |
| `environment` | `object` | No | `{}` | Environment variables to set. Values support template expressions. |
| `includeStdErr` | `bool` | No | `false` | Whether to append stderr to the captured output. |
| `stdin` | `string` | No | `null` | Content to pipe to the process's standard input. Supports template expressions. |

> **\*Mutual exclusion:** Exactly one of `script` or `scriptFile` is required. Specifying both throws a parse error.

**Example (inline script):**

```json
{
  "name": "gather-info",
  "type": "Script",
  "shell": "pwsh",
  "script": "$ErrorActionPreference = 'Stop'\n$info = @{ Host = hostname; Time = Get-Date -Format 'o' }\n$info | ConvertTo-Json",
  "timeoutSeconds": 30
}
```

**Example (YAML with multiline script -- the primary ergonomic benefit):**

```yaml
- name: gather-info
  type: Script
  shell: pwsh
  script: |
    $ErrorActionPreference = 'Stop'
    $info = @{
        Host = hostname
        Time = Get-Date -Format 'o'
    }
    $info | ConvertTo-Json
  timeoutSeconds: 30
```

**Example (external script file):**

```json
{
  "name": "deploy",
  "type": "Script",
  "shell": "pwsh",
  "scriptFile": "scripts/deploy.ps1",
  "arguments": ["{{param.environment}}", "--no-prompt"],
  "workingDirectory": "{{vars.projectRoot}}"
}
```

---

## Loop Configuration

A loop (or "checker") pattern allows a Prompt step to iteratively refine the output of a target step. The step with the `loop` property acts as a **checker**: it evaluates the target step's output and either exits (when the `exitPattern` is found in the checker's output) or re-runs the target step for another iteration.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `target` | `string` | **Yes** | -- | Name of the step to re-run when the exit condition is not met. Must be a dependency of the checker step. |
| `maxIterations` | `int` | **Yes** | -- | Maximum number of loop iterations (1-10). |
| `exitPattern` | `string` | **Yes** | -- | Case-insensitive string to search for in the checker's output. When found, the loop exits. |

**Example:**

```json
{
  "name": "quality-check",
  "type": "Prompt",
  "dependsOn": ["write-draft"],
  "systemPrompt": "Review the draft. If it meets all criteria, respond with APPROVED. Otherwise, respond with REVISE and explain what needs fixing.",
  "userPrompt": "Review:\n\n{{write-draft.output}}",
  "model": "claude-opus-4.5",
  "loop": {
    "target": "write-draft",
    "maxIterations": 3,
    "exitPattern": "APPROVED"
  }
}
```

In this example, `quality-check` evaluates `write-draft`. If the checker output does not contain `"APPROVED"`, the `write-draft` step is re-executed (with the checker's feedback available), and the checker runs again. This repeats up to 3 times.

---

## Subagents

Subagents enable multi-agent orchestration within a single Prompt step. The main step's LLM acts as a coordinator and can delegate tasks to specialized subagents, each with their own system prompt, tool access, and MCP servers.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | `string` | **Yes** | -- | Unique subagent identifier. |
| `prompt` | `string` | **Yes*** | -- | System prompt for the subagent (inline). |
| `promptFile` | `string` | **Yes*** | -- | Path to a file containing the subagent's system prompt. Mutually exclusive with `prompt`. |
| `displayName` | `string` | No | `null` | Human-readable display name shown in the UI. |
| `description` | `string` | No | `null` | Description of the subagent's expertise. Helps the coordinator understand when to delegate. |
| `tools` | `string[]` | No | `null` | Tool names the subagent can use. `null` grants access to all available tools. |
| `mcps` | `string[]` | No | `[]` | MCP server names available to this subagent. |
| `infer` | `bool` | No | `true` | Whether the runtime can auto-select this subagent based on user intent. |

> **\*** Exactly one of `prompt` or `promptFile` is required.

**Example:**

```json
{
  "name": "coordinator",
  "type": "Prompt",
  "systemPrompt": "You are a research coordinator. Delegate tasks to your specialists.",
  "userPrompt": "Research {{topic}}.",
  "model": "claude-opus-4.5",
  "subagents": [
    {
      "name": "researcher",
      "displayName": "Research Specialist",
      "description": "Gathers information from the web. Use when you need to find facts or data.",
      "prompt": "You are a thorough researcher. Find and organize information from the web.",
      "mcps": ["web-fetch"],
      "infer": true
    },
    {
      "name": "writer",
      "displayName": "Content Writer",
      "description": "Creates polished written content. Use when you need to produce reports or summaries.",
      "prompt": "You are a skilled content writer. Transform research into clear, engaging content.",
      "infer": true
    }
  ]
}
```

---

## Retry Policy

Retry policies control automatic retries on step failure. They can be set at the orchestration level (`defaultRetryPolicy`) or per-step (`retry`). Step-level policies override the orchestration default. An empty `{}` object uses all defaults.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `maxRetries` | `int` | No | `3` | Maximum retry attempts after the initial failure. |
| `backoffSeconds` | `double` | No | `1.0` | Initial delay before the first retry (in seconds). |
| `backoffMultiplier` | `double` | No | `2.0` | Multiplier applied to the backoff delay after each retry (exponential backoff). |
| `retryOnTimeout` | `bool` | No | `true` | Whether to retry when the failure is a timeout. |

**Example (orchestration-level default):**

```json
{
  "name": "resilient-pipeline",
  "description": "Pipeline with retry defaults.",
  "defaultRetryPolicy": {
    "maxRetries": 2,
    "backoffSeconds": 5,
    "backoffMultiplier": 2.0,
    "retryOnTimeout": true
  },
  "steps": [
    {
      "name": "flaky-api-call",
      "type": "Http",
      "url": "https://api.example.com/data",
      "retry": {
        "maxRetries": 5,
        "backoffSeconds": 2
      }
    }
  ]
}
```

In this example, `flaky-api-call` overrides the orchestration default with 5 retries and a 2-second initial backoff. The `backoffMultiplier` and `retryOnTimeout` fall back to their own defaults (`2.0` and `true`).

---

## Triggers

Triggers define how an orchestration is started. When no `trigger` is specified, the orchestration defaults to manual (on-demand) execution.

### Base Trigger Properties

All trigger types share these properties:

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `type` | `string` | **Yes** | -- | Trigger type: `"manual"`, `"scheduler"`, `"loop"`, or `"webhook"`. |
| `enabled` | `bool` | No | `true` | Whether the trigger is active. |
| `inputHandlerPrompt` | `string` | No | `null` | LLM prompt to transform raw trigger input into the parameters expected by the orchestration. |

---

### Manual Trigger

**Type value:** `"manual"`

The default trigger. The orchestration runs only when explicitly started by a user or API call. No additional properties beyond the base trigger properties.

```json
{
  "trigger": {
    "type": "manual"
  }
}
```

---

### Scheduler Trigger

**Type value:** `"scheduler"`

Runs the orchestration on a time-based schedule using either a cron expression or a simple interval.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `cron` | `string` | No | `null` | Cron expression (e.g., `"0 */6 * * *"` for every 6 hours). Takes precedence over `intervalSeconds`. |
| `intervalSeconds` | `int` | No | `null` | Simple interval in seconds between runs. Used only if `cron` is not set. |
| `maxRuns` | `int` | No | `null` (unlimited) | Maximum number of scheduled runs before the trigger stops. |

**Example:**

```json
{
  "trigger": {
    "type": "scheduler",
    "enabled": true,
    "intervalSeconds": 600
  }
}
```

---

### Loop Trigger

**Type value:** `"loop"`

Continuously re-runs the orchestration after each completion, with optional delay and iteration limits.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `delaySeconds` | `int` | No | `0` | Delay in seconds before re-running after completion. |
| `maxIterations` | `int` | No | `null` (unlimited) | Maximum number of loop iterations. |
| `continueOnFailure` | `bool` | No | `false` | Whether to continue looping if the orchestration fails. |

**Example:**

```json
{
  "trigger": {
    "type": "loop",
    "delaySeconds": 30,
    "maxIterations": 100,
    "continueOnFailure": true
  }
}
```

---

### Webhook Trigger

**Type value:** `"webhook"`

Starts the orchestration when an HTTP request is received at the orchestration's webhook endpoint.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `secret` | `string` | No | `null` | HMAC secret for validating the `X-Webhook-Signature` header. |
| `maxConcurrent` | `int` | No | `1` | Maximum concurrent executions from incoming webhooks. |
| `response` | `WebhookResponseConfig` | No | `null` | Configuration for synchronous webhook responses. |

**Webhook Response Config:**

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `waitForResult` | `bool` | No | `false` | Whether to block the HTTP response until orchestration completes. |
| `responseTemplate` | `string` | No | `null` | Template string for formatting the response body. |
| `timeoutSeconds` | `int` | No | `120` | Maximum seconds to wait for completion. Returns 504 on timeout. |

**Example:**

```json
{
  "trigger": {
    "type": "webhook",
    "enabled": true,
    "maxConcurrent": 5,
    "inputHandlerPrompt": "Extract 'eventType' and 'eventData' from the raw JSON payload.",
    "response": {
      "waitForResult": true,
      "responseTemplate": "{{final-step.output}}",
      "timeoutSeconds": 60
    }
  }
}
```

---

## MCP Definitions

MCP (Model Context Protocol) servers provide tools to Prompt steps. They can be defined inline in the orchestration file or in a separate `mcp.json` file in the same directory. When both exist, external definitions are merged and override inline ones on name conflicts.

### Local MCP

Runs a local process as an MCP server using stdio transport.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | `string` | **Yes** | -- | Unique MCP server name. Referenced by steps via the `mcps` array. |
| `type` | `string` | **Yes** | -- | Must be `"local"`. |
| `command` | `string` | **Yes** | -- | Executable to run (e.g., `"npx"`, `"uvx"`, `"python"`). |
| `arguments` | `string[]` | No | `[]` | Command-line arguments. |
| `workingDirectory` | `string` | No | `null` | Working directory for the MCP process. |

**Example:**

```json
{
  "mcps": [
    {
      "name": "filesystem",
      "type": "local",
      "command": "npx",
      "arguments": ["-y", "@anthropic/mcp-server-filesystem", "{{workingDirectory}}"]
    }
  ]
}
```

### Remote MCP

Connects to a remote MCP server over HTTP.

| Property | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | `string` | **Yes** | -- | Unique MCP server name. |
| `type` | `string` | **Yes** | -- | Must be `"remote"`. |
| `endpoint` | `string` | **Yes** | -- | Remote MCP server URL. |
| `headers` | `object` | No | `{}` | HTTP headers for authentication or other purposes. |

**Example:**

```json
{
  "mcps": [
    {
      "name": "cloud-tools",
      "type": "remote",
      "endpoint": "https://mcp.example.com/tools",
      "headers": {
        "Authorization": "Bearer {{env.MCP_TOKEN}}"
      }
    }
  ]
}
```

### Standalone MCP Config File (`mcp.json`)

A separate `mcp.json` file can be placed alongside orchestration files. It contains a single `mcps` array:

```json
{
  "mcps": [
    {
      "name": "filesystem",
      "type": "local",
      "command": "npx",
      "arguments": ["-y", "@anthropic/mcp-server-filesystem", "."]
    }
  ]
}
```

---

## Template Expressions

Template expressions use `{{expression}}` syntax and are supported in prompts, URLs, headers, bodies, templates, command arguments, working directories, environment variable values, stdin, and variable values. They are resolved at runtime.

| Expression | Description |
|---|---|
| `{{stepName.output}}` | The processed output of a dependency step (after output handler, if any). |
| `{{stepName.rawOutput}}` | The raw output of a dependency step (before output handler). |
| `{{stepName.files}}` | JSON array of all file paths saved by a step. |
| `{{stepName.files[N]}}` | A specific file path saved by a step (0-indexed). |
| `{{param.name}}` | A runtime parameter value. |
| `{{vars.name}}` | An orchestration variable value (defined in `variables`). |
| `{{env.VAR_NAME}}` | An environment variable value. |
| `{{orchestration.name}}` | The orchestration's name. |
| `{{orchestration.version}}` | The orchestration's version. |
| `{{orchestration.runId}}` | The current execution's unique run ID. |
| `{{orchestration.startedAt}}` | Timestamp when the current run started. |
| `{{step.name}}` | The current step's name. |
| `{{step.type}}` | The current step's type. |
| `{{workingDirectory}}` | The working directory context. |

### Variable Expansion

Variables can reference other variables, parameters, and environment variables. They are expanded recursively:

```json
{
  "variables": {
    "appName": "my-app",
    "registry": "{{env.CONTAINER_REGISTRY}}/{{vars.appName}}",
    "artifactPath": "/artifacts/{{vars.appName}}/{{orchestration.runId}}"
  }
}
```

---

## Enums Reference

### Step Types

| Value | Description |
|---|---|
| `Prompt` | Calls an LLM with system/user prompts. |
| `Http` | Makes an HTTP request. |
| `Transform` | Performs string interpolation (no LLM, no I/O). |
| `Command` | Executes a shell command. |
| `Script` | Executes an inline or file-based script via a shell interpreter. |

### System Prompt Mode

| Value | Description |
|---|---|
| `Append` | Adds the custom system prompt after the SDK's built-in prompts. |
| `Replace` | Replaces the SDK's built-in prompts entirely with the custom system prompt. |

### Reasoning Level

| Value | Description |
|---|---|
| `Low` | Minimal extended thinking. |
| `Medium` | Moderate extended thinking. |
| `High` | Maximum extended thinking for complex tasks. |

### Trigger Type

| Value | Description |
|---|---|
| `Manual` | On-demand execution (default). |
| `Scheduler` | Time-based execution via cron or interval. |
| `Loop` | Continuous re-execution after each completion. |
| `Webhook` | Execution triggered by an incoming HTTP request. |

### MCP Type

| Value | Description |
|---|---|
| `Local` | Local process communicating via stdio. |
| `Remote` | Remote server communicating via HTTP. |

### Execution Status (runtime)

| Value | Description |
|---|---|
| `Pending` | Step has not started yet. |
| `Running` | Step is currently executing. |
| `Succeeded` | Step completed successfully. |
| `Failed` | Step failed. |
| `Skipped` | Step was skipped (disabled or condition not met). |
| `Cancelled` | Step was cancelled. |
| `NoAction` | Step completed with no meaningful action taken. |

---

## Examples

### Minimal Orchestration

A single Prompt step with no dependencies:

```json
{
  "name": "hello-world",
  "description": "A minimal orchestration with one step.",
  "steps": [
    {
      "name": "greet",
      "type": "Prompt",
      "systemPrompt": "You are a friendly assistant.",
      "userPrompt": "Say hello to the user.",
      "model": "claude-opus-4.5"
    }
  ]
}
```

### Multi-Step DAG with All Step Types

Demonstrates all five step types, dependencies, variables, and triggers:

```json
{
  "name": "deployment-pipeline",
  "description": "Build, test, review, and notify pipeline.",
  "version": "2.1.0",
  "variables": {
    "appName": "customer-portal",
    "registry": "{{env.CONTAINER_REGISTRY}}/{{vars.appName}}",
    "slackWebhookUrl": "{{env.SLACK_WEBHOOK_URL}}"
  },
  "steps": [
    {
      "name": "build",
      "type": "Command",
      "command": "dotnet",
      "arguments": ["publish", "-c", "Release", "-o", "/artifacts"],
      "workingDirectory": "{{param.projectPath}}",
      "parameters": ["projectPath"],
      "timeoutSeconds": 120,
      "includeStdErr": true
    },
    {
      "name": "run-tests",
      "type": "Command",
      "command": "dotnet",
      "arguments": ["test", "--no-build"],
      "workingDirectory": "{{param.projectPath}}",
      "parameters": ["projectPath"],
      "timeoutSeconds": 180
    },
    {
      "name": "security-scan",
      "type": "Prompt",
      "dependsOn": ["build"],
      "systemPrompt": "You are a security analyst.",
      "userPrompt": "Review the build output for vulnerabilities:\n\n{{build.output}}",
      "model": "claude-opus-4.5"
    },
    {
      "name": "deploy-report",
      "type": "Transform",
      "dependsOn": ["security-scan", "run-tests"],
      "template": "# Report\n\n## Security\n{{security-scan.output}}\n\n## Tests\n{{run-tests.output}}"
    },
    {
      "name": "notify-team",
      "type": "Http",
      "dependsOn": ["deploy-report"],
      "method": "POST",
      "url": "{{vars.slackWebhookUrl}}",
      "headers": { "Content-Type": "application/json" },
      "body": "{\"text\": \"Deployment report ready for {{vars.appName}}.\"}"
    }
  ]
}
```

### Loop/Checker Pattern

An iterative review loop that re-runs the draft step until the checker approves:

```json
{
  "name": "iterative-writing",
  "description": "Write and review until quality standards are met.",
  "steps": [
    {
      "name": "write-draft",
      "type": "Prompt",
      "systemPrompt": "You are a professional writer.",
      "userPrompt": "Write an article about {{topic}}.",
      "model": "claude-opus-4.5",
      "parameters": ["topic"]
    },
    {
      "name": "review",
      "type": "Prompt",
      "dependsOn": ["write-draft"],
      "systemPrompt": "You are an editor. If the draft is good, say PUBLISH. Otherwise say REVISE and explain why.",
      "userPrompt": "Review this draft:\n\n{{write-draft.output}}",
      "model": "claude-opus-4.5",
      "loop": {
        "target": "write-draft",
        "maxIterations": 3,
        "exitPattern": "PUBLISH"
      }
    }
  ]
}
```

### Multi-Agent with Subagents

A coordinator step that delegates to specialized subagents:

```json
{
  "name": "research-team",
  "description": "Coordinator delegates to researcher, analyst, and writer subagents.",
  "mcps": [
    {
      "name": "web-fetch",
      "type": "local",
      "command": "npx",
      "arguments": ["-y", "@anthropic/mcp-fetch"]
    }
  ],
  "steps": [
    {
      "name": "coordinator",
      "type": "Prompt",
      "systemPrompt": "You manage a team of specialists. Delegate tasks based on what is needed.",
      "userPrompt": "{{topic}}",
      "model": "claude-opus-4.5",
      "parameters": ["topic"],
      "subagents": [
        {
          "name": "researcher",
          "displayName": "Research Specialist",
          "description": "Finds information from the web.",
          "prompt": "You are a thorough researcher. Search and organize information.",
          "mcps": ["web-fetch"],
          "infer": true
        },
        {
          "name": "analyst",
          "displayName": "Data Analyst",
          "description": "Analyzes data and draws insights.",
          "prompt": "You are an analytical expert. Identify patterns and draw conclusions.",
          "infer": true
        },
        {
          "name": "writer",
          "displayName": "Content Writer",
          "description": "Produces polished written content.",
          "prompt": "You are a skilled writer. Transform research into clear content.",
          "infer": true
        }
      ]
    }
  ]
}
```

### Webhook with Input Handler and Synchronous Response

```json
{
  "name": "webhook-processor",
  "description": "Processes webhook payloads with LLM-powered input normalization.",
  "steps": [
    {
      "name": "process",
      "type": "Prompt",
      "systemPrompt": "You are an event processor.",
      "userPrompt": "Process this event: {{eventData}}",
      "model": "claude-opus-4.5",
      "parameters": ["eventData"]
    }
  ],
  "trigger": {
    "type": "webhook",
    "enabled": true,
    "maxConcurrent": 5,
    "inputHandlerPrompt": "Extract 'eventData' from the raw JSON payload. Return only a JSON object with an 'eventData' key.",
    "secret": "my-webhook-secret",
    "response": {
      "waitForResult": true,
      "responseTemplate": "{{process.output}}",
      "timeoutSeconds": 60
    }
  }
}
```
