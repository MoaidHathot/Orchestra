---
layout: default
title: Orchestra.Host
nav_order: 4
---

# Orchestra.Host

Orchestra.Host is an ASP.NET Core hosting library that provides a complete HTTP API and infrastructure for running, managing, and monitoring AI orchestrations.

## Overview

Orchestra.Host bridges the Orchestra.Engine with web-based clients, providing:

- **REST API** for orchestration and trigger management
- **Real-time streaming** via Server-Sent Events (SSE)
- **Trigger system** for automated execution (scheduler, webhook, email, loop)
- **Run history** with detailed execution traces
- **File-based persistence** for orchestrations and runs

## Installation

```bash
dotnet add package Orchestra.Host
```

## Quick Start

```csharp
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register your AgentBuilder implementation
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

// Add Orchestra Host services
builder.Services.AddOrchestraHost(options =>
{
    options.DataPath = "./data";
    options.Scan = new ScanConfig
    {
        Directory = "./orchestrations",
        Watch = true,
    };
});

var app = builder.Build();

// Initialize and map endpoints
app.Services.InitializeOrchestraHost();
app.MapOrchestraHostEndpoints();

app.Run();
```

## Configuration

### OrchestrationHostOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DataPath` | `string` | `%LOCALAPPDATA%/OrchestraHost` | Root path for all Orchestra data |
| `Scan` | `ScanConfig?` | `null` | Configuration for auto-scanning and watching a directory for orchestration and profile files |
| `HostBaseUrl` | `string?` | `null` | Base URL for generating run detail links |
| `LoadPersistedOrchestrations` | `bool` | `true` | Load saved orchestrations on startup |
| `LoadPersistedTriggers` | `bool` | `true` | Load saved trigger states on startup |
| `RegisterJsonTriggers` | `bool` | `true` | Register triggers defined in orchestration JSON |
| `Hooks` | `HookDefinition[]` | `[]` | Global hooks applied to every orchestration executed by the host |

### Global Hooks

Global hooks let you apply the same lifecycle automation to all orchestrations run by the host. They use the same shape as inline orchestration hooks and are loaded from `orchestra.json`.

```json
{
  "hooks": [
    {
      "name": "archive-failures",
      "on": "orchestration.failure",
      "payload": {
        "detail": "compact",
        "steps": "failed",
        "includeRefs": true
      },
      "action": {
        "type": "script",
        "scriptFile": "hooks/archive-failure.ps1"
      }
    }
  ]
}
```

Relative `scriptFile` and `workingDirectory` paths resolve from the directory containing `orchestra.json`.

### Environment Variables

| Variable | Description |
|----------|-------------|
| `ORCHESTRA_PORTAL_DATA_PATH` | Override the data path |
| `ORCHESTRA_ORCHESTRATIONS_PATH` | Override the orchestrations scan path |

## API Endpoints

### MCP Server

Orchestra.Host includes a built-in MCP (Model Context Protocol) server that exposes orchestrations to external AI agents via Streamable HTTP transport.

#### Setup

```csharp
using Orchestra.Host.McpServer;

builder.Services.AddOrchestraMcpServer();

var app = builder.Build();
app.MapOrchestraMcpEndpoints(); // Maps /mcp/data
```

#### McpServerOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DataPlaneEnabled` | `bool` | `true` | Enable the data-plane MCP endpoint |
| `DataPlaneRoute` | `string` | `"/mcp/data"` | Route path for the data-plane endpoint |
| `ControlPlaneEnabled` | `bool` | `false` | Enable the control-plane MCP endpoint (opt-in) |
| `ControlPlaneRoute` | `string` | `"/mcp/control"` | Route path for the control-plane endpoint |

#### Data-Plane Tools

The data-plane MCP server provides four tools:

| Tool | Description |
|------|-------------|
| `ListOrchestrations` | List and filter orchestrations by tags or name pattern. Returns IDs, names, descriptions, parameter schemas. |
| `InvokeOrchestration` | Invoke an orchestration by ID with parameters. Supports `async` (default) and `sync` modes. |
| `GetOrchestrationStatus` | Check the status and result of an execution by execution ID. |
| `CancelOrchestration` | Cancel a running execution by execution ID. |

`InvokeOrchestration` supports two modes:
- **`async`** (default): Returns immediately with an `executionId`. Use `GetOrchestrationStatus` to poll for results.
- **`sync`**: Blocks until the orchestration completes or the timeout is reached (default: 300 seconds).

### Orchestrations

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/orchestrations` | List all registered orchestrations |
| `GET` | `/api/orchestrations/{id}` | Get orchestration details with schedule |
| `POST` | `/api/orchestrations` | Register orchestrations from file paths |
| `POST` | `/api/orchestrations/json` | Register orchestration from JSON content |
| `DELETE` | `/api/orchestrations/{id}` | Remove an orchestration |
| `POST` | `/api/orchestrations/{id}/enable` | Enable orchestration trigger |
| `POST` | `/api/orchestrations/{id}/disable` | Disable orchestration trigger |
| `POST` | `/api/orchestrations/scan` | Scan directory for orchestration files |

#### Register Orchestrations

```bash
curl -X POST http://localhost:5000/api/orchestrations \
  -H "Content-Type: application/json" \
  -d '{
    "paths": ["./orchestrations/my-workflow.json"],
    "mcpPath": "./mcp.json"
  }'
```

#### Register from JSON

```bash
curl -X POST http://localhost:5000/api/orchestrations/json \
  -H "Content-Type: application/json" \
  -d '{
    "json": "{\"name\":\"test\",\"version\":\"1.0\",\"steps\":[...]}",
    "mcpJson": "{\"mcps\":[...]}"
  }'
```

### Execution

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/orchestrations/{id}/run` | Execute orchestration with SSE streaming |
| `GET` | `/api/execution/{id}/attach` | Attach to running execution stream |

#### Execute Orchestration

```bash
# SSE stream of execution events
# Parameters are passed as a JSON string in the 'params' query parameter
curl -N "http://localhost:5000/api/orchestrations/{id}/run?params={\"topic\":\"AI\"}"
```

#### SSE Event Types

| Event | Data | Description |
|-------|------|-------------|
| `execution-started` | `{"executionId"}` | Execution started |
| `session-started` | `{"requestedModel", "selectedModel"}` | Agent session started |
| `model-change` | `{"previousModel", "newModel"}` | Model selection changed |
| `step-started` | `{"stepName"}` | Step execution began |
| `content-delta` | `{"stepName", "chunk"}` | Streaming content chunk |
| `reasoning-delta` | `{"stepName", "chunk"}` | Streaming reasoning chunk |
| `tool-started` | `{"stepName", "toolName", "arguments", "mcpServer"}` | Tool execution started |
| `tool-completed` | `{"stepName", "toolName", "success", "result", "error"}` | Tool execution completed |
| `step-completed` | `{"stepName", "actualModel", "selectedModel", "contentPreview"}` | Step finished |
| `step-output` | `{"stepName", "content"}` | Step final output |
| `step-error` | `{"stepName", "error"}` | Step error |
| `step-cancelled` | `{"stepName"}` | Step was cancelled |
| `step-skipped` | `{"stepName", "reason"}` | Step was skipped |
| `step-trace` | `{"stepName", "systemPrompt", "userPromptRaw", ...}` | Detailed step execution trace |
| `usage` | `{"stepName", "model", "inputTokens", "outputTokens", ...}` | Token usage information |
| `model-mismatch` | `{"configuredModel", "actualModel", ...}` | Requested model differs from actual |
| `loop-iteration` | `{"checkerStepName", "targetStepName", "iteration", "maxIterations"}` | Loop retry iteration |
| `subagent-selected` | `{"stepName", "agentName", "displayName", "tools"}` | Subagent was selected |
| `subagent-started` | `{"stepName", "toolCallId", "agentName", "displayName", "description"}` | Subagent execution started |
| `subagent-completed` | `{"stepName", "toolCallId", "agentName", "displayName"}` | Subagent execution completed |
| `subagent-failed` | `{"stepName", "toolCallId", "agentName", "displayName", "error"}` | Subagent execution failed |
| `subagent-deselected` | `{"stepName"}` | Subagent was deselected |
| `orchestration-done` | `{"status", "results"}` | Orchestration finished successfully |
| `orchestration-cancelled` | `{"status"}` | Orchestration was cancelled |
| `orchestration-error` | `{"status", "error"}` | Orchestration failed |
| `status-changed` | `{"status"}` | Execution status changed (e.g., "Cancelling") |

### Triggers

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/triggers` | List all triggers |
| `GET` | `/api/triggers/{id}` | Get trigger details |
| `POST` | `/api/triggers/{id}/enable` | Enable a trigger |
| `POST` | `/api/triggers/{id}/disable` | Disable a trigger |
| `POST` | `/api/triggers/{id}/fire` | Manually fire a trigger |
| `DELETE` | `/api/triggers/{id}` | Remove a trigger |

### Webhooks

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/webhooks/{id}` | Fire webhook trigger |
| `POST` | `/api/webhooks/{id}/validate` | Validate webhook secret |

#### Webhook with HMAC Validation

```bash
# Generate signature
SIGNATURE=$(echo -n "$PAYLOAD" | openssl dgst -sha256 -hmac "$SECRET" | cut -d' ' -f2)

curl -X POST http://localhost:5000/api/webhooks/{id} \
  -H "Content-Type: application/json" \
  -H "X-Webhook-Signature: sha256=$SIGNATURE" \
  -d "$PAYLOAD"
```

### History

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/history` | Get recent execution summaries |
| `GET` | `/api/history/all` | Get all executions (paginated) |
| `GET` | `/api/history/{name}/{runId}` | Get full execution details |
| `DELETE` | `/api/history/{name}/{runId}` | Delete execution record |

### Active Executions

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/active` | Get running and pending executions |
| `POST` | `/api/active/{executionId}/cancel` | Cancel running execution |

### Utility

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/mcps` | List MCPs used across orchestrations |
| `GET` | `/api/status` | Get server status |

## Trigger Types

### Scheduler Trigger

Runs orchestrations on a schedule using cron expressions or intervals.

```json
{
  "trigger": {
    "type": "scheduler",
    "cron": "0 9 * * MON-FRI",
    "enabled": true,
    "inputHandlerPrompt": "Generate today's date as context"
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

Fires when an HTTP POST is received with valid signature.

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

Re-runs the orchestration when it completes.

```json
{
  "trigger": {
    "type": "loop",
    "enabled": true
  }
}
```

### Email Trigger

Polls an Outlook mailbox for new emails.

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

## Custom Trigger Callback

Override the default execution callback to customize behavior:

```csharp
public class MyExecutionCallback : ITriggerExecutionCallback
{
    public IOrchestrationReporter CreateReporter()
    {
        return new MyCustomReporter();
    }
    
    public void OnExecutionStarted(ActiveExecutionInfo info) { }
    public void OnExecutionCompleted(ActiveExecutionInfo info) { }
    public void OnStepStarted(ActiveExecutionInfo info, string stepName) { }
    public void OnStepCompleted(ActiveExecutionInfo info, string stepName) { }
}

// Register before AddOrchestraHost
builder.Services.AddTriggerExecutionCallback<MyExecutionCallback>();
```

## Data Storage Layout

```
{DataPath}/
├── registered-orchestrations.json    # Persisted orchestration paths
├── triggers/                          # Persisted trigger states
│   └── {hash}.trigger.json
└── executions/                        # Run history
    └── {orchestration-name}/
        └── {name}_{version}_{trigger}_{timestamp}_{id}/
            ├── orchestration.json     # Copy of orchestration at execution
            ├── run.json               # Full OrchestrationRunRecord
            ├── {step-name}-inputs.json
            ├── {step-name}-outputs.json
            ├── {step-name}-result.json
            └── result.md              # Human-readable final output
```

## Selective Endpoint Mapping

Map only the endpoints you need:

```csharp
// Map all endpoints
app.MapOrchestraHostEndpoints();

// Or map specific groups
app.MapOrchestrationsEndpoints();  // /api/orchestrations
app.MapTriggersEndpoints();         // /api/triggers
app.MapWebhooksEndpoints();         // /api/webhooks
app.MapRunsEndpoints();             // /api/history, /api/active
app.MapExecutionEndpoints();        // /api/orchestrations/{id}/run, /api/execution/{id}/attach
app.MapUtilityEndpoints();          // /api/mcps, /api/status, /api/health
```

## Architecture

### Core Components

| Component | Description |
|-----------|-------------|
| `OrchestrationRegistry` | In-memory registry with disk persistence |
| `TriggerManager` | Background service managing all triggers |
| `FileSystemRunStore` | File-based persistence for run history |
| `SseReporter` | SSE streaming with event replay for late joiners |

### Request Flow

```
Client Request
      │
      ▼
API Endpoint (OrchestrationsApi, TriggersApi, etc.)
      │
      ▼
OrchestrationRegistry (lookup orchestration)
      │
      ▼
TriggerManager / Direct Execution
      │
      ├──▶ Create SseReporter
      │
      ▼
OrchestrationExecutor (from Orchestra.Engine)
      │
      ├──▶ Stream events to SseReporter
      │
      ▼
FileSystemRunStore (persist run record)
      │
      ▼
SSE Response to Client
```

### Trigger Execution Flow

```
TriggerManager (BackgroundService)
      │
      ├──▶ Check scheduler triggers every second
      ├──▶ Listen for webhook/email events
      │
      ▼
Trigger Fires
      │
      ▼
Parse orchestration file
      │
      ▼
Apply input handler (optional LLM transformation)
      │
      ▼
Create OrchestrationExecutor with callback's reporter
      │
      ▼
Execute orchestration
      │
      ▼
Update trigger state (next fire time, run count)
      │
      ▼
Persist run to FileSystemRunStore
```

## Dependencies

- **Target Framework**: .NET 10.0
- **Framework Reference**: `Microsoft.AspNetCore.App`
- **Project Reference**: `Orchestra.Engine`
