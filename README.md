# Orchestra

A declarative orchestration engine for LLM (Large Language Model) workflows. Define multi-step AI pipelines in JSON with dependency-based execution, MCP tool integration, quality control loops, and automatic triggers.

## Features

- **Declarative JSON Pipelines** - Define complex LLM workflows as simple JSON files
- **DAG-Based Execution** - Automatic parallel execution of independent steps
- **Variables & Metadata** - Reusable variables with recursive expansion, plus built-in orchestration and step metadata
- **Template Expressions** - Rich expression syntax for parameters, variables, metadata, and step outputs
- **MCP Integration** - Extend LLM capabilities with Model Context Protocol servers
- **Quality Control Loops** - Retry steps with feedback until criteria are met
- **Handler Transformations** - Transform inputs and outputs between steps
- **Multiple Triggers** - Scheduler, webhook, email, and loop-based automation
- **Customizable Formatting** - Inject custom prompt formatting via `IPromptFormatter`
- **System Prompt Control** - Fine-grained control over SDK system prompts

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
      "userPrompt": "Research the topic: {{topic}}",
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

- [Orchestration Schema](#orchestration-schema)
- [Step Configuration](#step-configuration)
- [Template Expressions](#template-expressions)
- [Variables](#variables)
- [MCP Integration](#mcp-integration)
- [Triggers](#triggers)
- [System Prompt Modes](#system-prompt-modes)
- [IPromptFormatter](#ipromptformatter)
- [Programmatic Usage](#programmatic-usage)
- [Architecture](#architecture)

## Orchestration Schema

### Top-Level Properties

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `name` | string | Yes | Unique name for the orchestration |
| `description` | string | Yes | Human-readable description |
| `version` | string | No | Version string (default: `"1.0.0"`) |
| `variables` | object | No | User-defined variables accessible via `{{vars.name}}` |
| `defaultSystemPromptMode` | enum | No | Default mode for all steps: `append` or `replace` |
| `mcps` | array | No | Inline MCP server definitions |
| `steps` | array | Yes | Array of step configurations |
| `trigger` | object | No | Automatic trigger configuration |

### Example

```json
{
  "name": "my-orchestration",
  "description": "A multi-step AI workflow",
  "version": "1.0.0",
  "variables": {
    "baseUrl": "https://api.example.com",
    "outputDir": "/reports/{{param.project}}"
  },
  "defaultSystemPromptMode": "replace",
  "mcps": [],
  "steps": [],
  "trigger": null
}
```

## Step Configuration

### Prompt Step Properties

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `name` | string | Yes | Unique step identifier |
| `type` | enum | Yes | Step type (currently only `Prompt`) |
| `dependsOn` | array | Yes | Step names this step depends on (empty `[]` for root steps) |
| `systemPrompt` | string | Yes | System prompt for the LLM |
| `userPrompt` | string | Yes | User prompt with `{{paramName}}` placeholders |
| `model` | string | Yes | LLM model identifier |
| `parameters` | array | No | Parameter names required by this step |
| `mcps` | array | No | MCP server names this step can use |
| `inputHandlerPrompt` | string | No | Transform dependency outputs before use |
| `outputHandlerPrompt` | string | No | Transform step output |
| `systemPromptMode` | enum | No | `replace` or `append` (overrides orchestration default) |
| `reasoningLevel` | enum | No | `low`, `medium`, or `high` |
| `loop` | object | No | Retry/check loop configuration |

### Parameters

Use `{{param.name}}` syntax in prompts to inject parameters:

```json
{
  "name": "translator",
  "userPrompt": "Translate '{{param.text}}' to {{param.targetLanguage}}.",
  "parameters": ["text", "targetLanguage"]
}
```

Pass parameters via CLI:

```bash
-param text="Hello world" -param targetLanguage=French
```

### Dependencies

Steps automatically receive outputs from their dependencies:

```json
{
  "name": "step-a",
  "dependsOn": [],
  "userPrompt": "Generate a list of topics."
},
{
  "name": "step-b",
  "dependsOn": ["step-a"],
  "userPrompt": "Expand on: {{step-a.output}}"
}
```

When `step-b` runs, it receives `step-a`'s output as context.

### Handler Prompts

Transform data between steps using handler prompts:

```json
{
  "name": "analyzer",
  "dependsOn": ["data-fetcher"],
  "inputHandlerPrompt": "Extract only the numerical data from the input.",
  "outputHandlerPrompt": "Format the analysis as a bullet-point summary.",
  "userPrompt": "Analyze the data."
}
```

### Loop Configuration

Implement quality control by retrying steps until criteria are met:

```json
{
  "name": "quality-checker",
  "dependsOn": ["writer"],
  "systemPrompt": "Review the content. If it meets all criteria, respond with APPROVED.",
  "userPrompt": "Check the content for accuracy and completeness.",
  "loop": {
    "target": "writer",
    "maxIterations": 3,
    "exitPattern": "APPROVED"
  }
}
```

The `quality-checker` step will re-run `writer` with feedback until `APPROVED` appears in the response or `maxIterations` is reached.

## Template Expressions

Orchestra uses `{{expression}}` syntax for dynamic values in prompts, URLs, headers, templates, and command arguments. All expressions are case-insensitive and whitespace-tolerant (e.g., `{{ orchestration.NAME }}` works).

### Expression Namespaces

| Namespace | Syntax | Description |
|-----------|--------|-------------|
| Parameters | `{{param.name}}` | User-supplied parameters passed at runtime |
| Variables | `{{vars.name}}` | User-defined orchestration variables (with recursive expansion) |
| Orchestration | `{{orchestration.property}}` | Built-in orchestration metadata |
| Step | `{{step.property}}` | Current step metadata |
| Environment | `{{env.VAR_NAME}}` | OS environment variable value |
| Step Output | `{{stepName.output}}` | Output content from a completed step |
| Step Raw Output | `{{stepName.rawOutput}}` | Raw (unprocessed) output from a completed step |
| Step Files | `{{stepName.files}}` | JSON array of all file paths saved by a step via `orchestra_save_file` |
| Step File (indexed) | `{{stepName.files[N]}}` | Path of the Nth file (0-based) saved by a step via `orchestra_save_file` |

### Orchestration Metadata Properties

| Property | Description | Example Value |
|----------|-------------|---------------|
| `{{orchestration.name}}` | Orchestration name | `"deployment-pipeline"` |
| `{{orchestration.version}}` | Orchestration version | `"2.1.0"` |
| `{{orchestration.runId}}` | Unique run identifier | `"abc123-def456"` |
| `{{orchestration.startedAt}}` | Run start time (ISO 8601) | `"2025-06-15T10:30:00+00:00"` |

### Step Metadata Properties

| Property | Description | Example Value |
|----------|-------------|---------------|
| `{{step.name}}` | Current step's name | `"security-scan"` |
| `{{step.type}}` | Current step's type | `"Prompt"`, `"Command"`, `"Transform"`, `"Http"` |

### Example

```json
{
  "userPrompt": "{{orchestration.name}} v{{orchestration.version}} [Run: {{orchestration.runId}}]\nStep: {{step.name}} ({{step.type}})\nAPI Key: {{env.API_KEY}}\nTopic: {{param.topic}}\nPrevious result: {{research.output}}"
}
```

## Variables

Variables let you define reusable values at the orchestration level. They are declared in the top-level `variables` object and referenced via `{{vars.name}}` in any step.

### Basic Usage

```json
{
  "name": "my-pipeline",
  "variables": {
    "appName": "customer-portal",
    "apiEndpoint": "https://api.example.com/v2"
  },
  "steps": [
    {
      "name": "fetch-data",
      "type": "Http",
      "url": "{{vars.apiEndpoint}}/projects/{{param.project}}/summary"
    }
  ]
}
```

### Recursive Expansion

Variable values can contain other template expressions, which are resolved when the variable is used:

```json
{
  "variables": {
    "baseDir": "/data/{{param.env}}",
    "outputDir": "{{vars.baseDir}}/reports",
    "logPrefix": "[{{orchestration.name}}:{{orchestration.runId}}]"
  }
}
```

In this example:
- `{{vars.outputDir}}` resolves to `{{vars.baseDir}}/reports`, then `baseDir` resolves to `/data/prod/reports` (if `env=prod`)
- `{{vars.logPrefix}}` resolves to `[my-pipeline:abc123]` using live orchestration metadata
- Variables can reference `{{param.*}}`, `{{orchestration.*}}`, `{{step.*}}`, `{{env.*}}`, `{{stepName.output}}`, `{{stepName.files}}`, `{{stepName.files[N]}}`, and other `{{vars.*}}`

### Circular Reference Protection

Circular variable references (e.g., `a` references `b` which references `a`) are detected and left unresolved to prevent infinite loops:

```json
{
  "variables": {
    "a": "{{vars.b}}",
    "b": "{{vars.a}}"
  }
}
```

Using `{{vars.a}}` will resolve to `{{vars.a}}` (left as-is) rather than causing an error.

### Unknown Variables

References to undefined variables are left as-is in the output (e.g., `{{vars.nonexistent}}` remains `{{vars.nonexistent}}`).

## Environment Variables

Access OS environment variables directly in templates using the `{{env.VAR_NAME}}` syntax:

```json
{
  "variables": {
    "connectionString": "Server={{env.DB_HOST}};Database={{env.DB_NAME}}"
  },
  "steps": [
    {
      "name": "deploy",
      "type": "Command",
      "command": "docker",
      "arguments": ["push", "{{env.CONTAINER_REGISTRY}}/{{vars.appName}}:latest"]
    }
  ]
}
```

Key behaviors:
- References to undefined environment variables are left as-is (e.g., `{{env.MISSING}}` remains `{{env.MISSING}}`)
- Environment variable names are passed through as-is (case-sensitive on Linux, case-insensitive on Windows)
- Can be used inside variable values for recursive expansion (e.g., a `vars` value containing `{{env.DB_HOST}}`)

## MCP Integration

Orchestra supports [Model Context Protocol](https://modelcontextprotocol.io/) servers for extending LLM capabilities with tools.

### MCP Types

- **Local**: Local process communicating via stdio
- **Remote**: Remote HTTP endpoint

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
      "name": "fetch",
      "type": "local",
      "command": "uvx",
      "arguments": ["mcp-server-fetch"]
    },
    {
      "name": "graph",
      "type": "local",
      "command": "dotnet",
      "arguments": ["run", "--project", "src/Orchestra.Mcp.Graph/Orchestra.Mcp.Graph.csproj"],
      "workingDirectory": "P:/Github/Orchestra"
    }
  ]
}
```

### Inline MCP Definitions

Define MCPs directly in the orchestration:

```json
{
  "name": "my-orchestration",
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
      "name": "researcher",
      "mcps": ["web-fetch"],
      "userPrompt": "Fetch and summarize https://example.com"
    }
  ]
}
```

### Built-in Microsoft Graph MCP

Orchestra includes `Orchestra.Mcp.Graph` with tools for Microsoft 365:

| Category | Tools |
|----------|-------|
| Calendar | `get_upcoming_meetings`, `get_recent_meetings`, `get_todays_meetings`, `search_calendar` |
| Mail | Email reading and management |
| Chat | Teams chat integration |
| Teams | Teams and channels management |
| User | User profile information |

## Triggers

Automate orchestration execution with triggers.

### Scheduler Trigger

Run on a schedule using cron expressions or intervals:

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

Or with a simple interval:

```json
{
  "trigger": {
    "type": "scheduler",
    "enabled": true,
    "intervalSeconds": 3600
  }
}
```

### Webhook Trigger

Execute via HTTP POST:

```json
{
  "trigger": {
    "type": "webhook",
    "enabled": true,
    "secret": "your-hmac-secret",
    "maxConcurrent": 5,
    "inputHandlerPrompt": "Extract 'topic' and 'audience' from the JSON payload."
  }
}
```

### Email Trigger

Poll an Outlook folder for new emails:

```json
{
  "trigger": {
    "type": "Email",
    "enabled": true,
    "folderPath": "Inbox/ActionItems",
    "pollIntervalSeconds": 60,
    "subjectContains": "Action Required",
    "senderContains": "@company.com",
    "inputHandlerPrompt": "Extract the action item from the email body."
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

## System Prompt Modes

Control how system prompts interact with the SDK's built-in prompts.

### Modes

- **`append`** (default): Your system prompt is added to the SDK's default system prompt
- **`replace`**: Your system prompt completely replaces the SDK's default

### Orchestration-Level Default

Set a default for all steps:

```json
{
  "name": "my-orchestration",
  "defaultSystemPromptMode": "replace",
  "steps": [...]
}
```

### Step-Level Override

Override the default for specific steps:

```json
{
  "defaultSystemPromptMode": "replace",
  "steps": [
    {
      "name": "strict-translator",
      "systemPrompt": "You are a translator. Only output the translation.",
      "userPrompt": "Translate: {{text}}"
    },
    {
      "name": "helpful-assistant",
      "systemPrompt": "Focus on being helpful.",
      "systemPromptMode": "append",
      "userPrompt": "Help me with: {{task}}"
    }
  ]
}
```

In this example:
- `strict-translator` uses `replace` (from orchestration default)
- `helpful-assistant` uses `append` (step-level override)

## IPromptFormatter

Customize how prompts and context are formatted by implementing `IPromptFormatter`.

### Interface

```csharp
public interface IPromptFormatter
{
    string FormatDependencyOutputs(IReadOnlyDictionary<string, string> dependencyOutputs);
    
    string BuildUserPrompt(
        string userPrompt,
        string dependencyOutputs,
        string? loopFeedback = null,
        string? inputHandlerPrompt = null);
    
    string BuildTransformationSystemPrompt(string handlerInstructions);
    
    string WrapContentForTransformation(string content);
}
```

### Default Behavior

The `DefaultPromptFormatter`:
- Formats multiple dependency outputs with markdown headers and separators
- Includes loop feedback when retrying steps
- Wraps content in `<INPUT_CONTENT>` tags for transformations

### Custom Implementation

```csharp
public class XmlPromptFormatter : IPromptFormatter
{
    public string FormatDependencyOutputs(IReadOnlyDictionary<string, string> deps)
    {
        var sb = new StringBuilder("<dependencies>");
        foreach (var (name, output) in deps)
        {
            sb.Append($"<step name=\"{name}\">{output}</step>");
        }
        sb.Append("</dependencies>");
        return sb.ToString();
    }
    
    // Implement other methods...
}

// Register in DI
services.AddSingleton<IPromptFormatter, XmlPromptFormatter>();
```

## Programmatic Usage

### Basic Setup

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orchestra.Engine;
using Orchestra.Copilot;

// Parse configuration
var mcps = OrchestrationParser.ParseMcpFile("mcp.json");
var orchestration = OrchestrationParser.ParseOrchestrationFile("orchestration.json", mcps);

// Configure services
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();
builder.Services.AddSingleton<IOrchestrationReporter, NullOrchestrationReporter>();
builder.Services.AddSingleton<IScheduler, OrchestrationScheduler>();
builder.Services.AddSingleton<OrchestrationExecutor>();

var host = builder.Build();

// Execute
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

### Custom Reporter

Implement `IOrchestrationReporter` for custom progress reporting:

```csharp
public class MyReporter : IOrchestrationReporter
{
    public void ReportStepStarted(string stepName) 
        => Console.WriteLine($"Starting: {stepName}");
    
    public void ReportStepCompleted(string stepName, AgentResult result)
        => Console.WriteLine($"Completed: {stepName}");
    
    // Implement other methods...
}
```

### Custom Run Store

Implement `IRunStore` to persist orchestration runs:

```csharp
public class DatabaseRunStore : IRunStore
{
    public Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken ct)
    {
        // Save to database
    }
    
    public Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(
        string orchestrationName, int? limit, CancellationToken ct)
    {
        // Query database
    }
    
    // Implement other methods...
}
```

## Architecture

```
+----------------------------------------------------------+
|                   Orchestration JSON                      |
|  (name, description, steps[], mcps[], trigger)           |
+----------------------------------------------------------+
                            |
                            v
+----------------------------------------------------------+
|                  OrchestrationParser                      |
|  - Parses JSON into Orchestration objects                |
|  - Resolves MCP references                               |
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
|  - Handles loops (retry with feedback)                   |
+----------------------------------------------------------+
                            |
                            v
+----------------------------------------------------------+
|                    PromptExecutor                         |
|  - Builds prompts with dependency context                |
|  - Applies input/output transformations                  |
|  - Uses IPromptFormatter for formatting                  |
+----------------------------------------------------------+
                            |
                            v
+----------------------------------------------------------+
|                     AgentBuilder                          |
|  - Abstract builder for LLM agents                       |
|  - Implementation: CopilotAgentBuilder (GitHub Copilot)  |
+----------------------------------------------------------+
```

## Examples

See the `examples/` folder for complete orchestration examples:

| Example | Description |
|---------|-------------|
| `deployment-pipeline.json` | Deployment pipeline with variables, metadata, and mixed step types |
| `variables-and-metadata.json` | Variables, orchestration metadata, and step metadata expressions |
| `weather-roads-seattle.json` | Parallel weather monitoring |
| `command-build-and-analyze.json` | Command steps with build and git analysis |
| `system-prompt-mode-example.json` | System prompt mode demonstration |
| `advanced-combined-features.json` | Full pipeline with loops and MCPs |
| `email-trigger-example.json` | Email-triggered processing |
| `webhook-triggered-notification.json` | Webhook event handling |
| `subagents-research-team.json` | Multi-agent orchestration with subagents |

## CLI Reference

```bash
dotnet run --project playground/Hosting/Orchestra.Playground.Copilot \
  -orchestration <path>     # Path to orchestration JSON (required)
  -mcp <path>               # Path to MCP configuration JSON (required)
  -param key=value          # Parameters (repeatable)
  -print                    # Print final output
```

## License

[Add your license here]
