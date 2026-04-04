---
layout: default
title: API Reference
nav_order: 6
---

# API Reference

Complete REST API reference for Orchestra.Host endpoints.

## Base URL

All endpoints are relative to your host's base URL (e.g., `http://localhost:5000`).

## Authentication

Orchestra does not include built-in authentication. Implement authentication middleware in your ASP.NET Core application as needed.

---

## Orchestrations

### List Orchestrations

```http
GET /api/orchestrations
```

**Response:**
```json
{
  "count": 1,
  "orchestrations": [
    {
      "id": "research-assistant-a1b2c3d4",
      "name": "research-assistant",
      "description": "Research a topic and generate a summary",
      "version": "1.0",
      "path": "/orchestrations/research-assistant.json",
      "mcpPath": "/orchestrations/mcp.json",
      "stepCount": 2,
      "steps": [...],
      "parameters": ["topic"],
      "hasParameters": true,
      "trigger": {
        "type": "scheduler",
        "enabled": true,
        "cron": "0 9 * * *",
        "intervalSeconds": null,
        "maxRuns": null
      },
      "triggerType": "scheduler",
      "enabled": true,
      "isActive": false,
      "runCount": 15,
      "models": ["claude-opus-4.5"]
    }
  ]
}
```

### Get Orchestration

```http
GET /api/orchestrations/{id}
```

**Response:**
```json
{
  "id": "research-assistant-a1b2c3d4",
  "path": "/orchestrations/research-assistant.json",
  "mcpPath": "/orchestrations/mcp.json",
  "name": "research-assistant",
  "description": "Research a topic and generate a summary",
  "version": "1.0",
  "steps": [
    {
      "name": "research",
      "type": "Prompt",
      "dependsOn": [],
      "parameters": ["topic"],
      "model": "claude-opus-4.5",
      "systemPrompt": "You are a researcher...",
      "userPrompt": "Research: {{topic}}"
    }
  ],
  "layers": [
    {
      "layer": 1,
      "steps": ["research", "analyze"]
    },
    {
      "layer": 2,
      "steps": ["summarize"]
    }
  ],
  "parameters": ["topic"],
  "trigger": null,
  "mcps": []
}
```

### Register Orchestrations

```http
POST /api/orchestrations
Content-Type: application/json
```

**Request:**
```json
{
  "paths": ["/path/to/orchestration.json"],
  "mcpPath": "/path/to/mcp.json"
}
```

**Response:**
```json
{
  "addedCount": 1,
  "added": [
    {
      "id": "my-workflow-e5f6g7h8",
      "name": "my-workflow",
      "path": "/path/to/orchestration.json"
    }
  ],
  "errors": []
}
```

### Register from JSON

```http
POST /api/orchestrations/json
Content-Type: application/json
```

**Request:**
```json
{
  "json": "{\"name\":\"test\",\"version\":\"1.0\",\"steps\":[...]}",
  "mcpJson": "{\"mcps\":[...]}"
}
```

### Delete Orchestration

```http
DELETE /api/orchestrations/{id}
```

**Response:**
```json
{
  "removed": true,
  "id": "my-workflow-e5f6g7h8"
}
```

### Enable Trigger

```http
POST /api/orchestrations/{id}/enable
```

### Disable Trigger

```http
POST /api/orchestrations/{id}/disable
```

### Scan Directory

```http
POST /api/orchestrations/scan
Content-Type: application/json
```

**Request:**
```json
{
  "directory": "/path/to/orchestrations"
}
```

---

## Execution

### Run Orchestration (SSE)

```http
GET /api/orchestrations/{id}/run?params={"topic":"AI"}
Accept: text/event-stream
```

**Query Parameters:** Parameters are passed as a JSON string in the `params` query parameter (required for EventSource compatibility).

**Response:** Server-Sent Events stream

```
event: execution-started
data: {"executionId":"a1b2c3d4e5f6"}

event: session-started
data: {"requestedModel":"claude-opus-4.5","selectedModel":"claude-opus-4.5"}

event: step-started
data: {"stepName":"research"}

event: content-delta
data: {"stepName":"research","chunk":"The field of"}

event: content-delta
data: {"stepName":"research","chunk":" artificial intelligence"}

event: tool-started
data: {"stepName":"research","toolName":"web_search","arguments":"{\"query\":\"AI trends\"}","mcpServer":"web-search"}

event: tool-completed
data: {"stepName":"research","toolName":"web_search","success":true,"result":"[search results]","error":null}

event: step-completed
data: {"stepName":"research","actualModel":"claude-opus-4.5","selectedModel":"claude-opus-4.5","contentPreview":"Full research content..."}

event: step-output
data: {"stepName":"research","content":"Full research content..."}

event: orchestration-done
data: {"status":"Succeeded","results":{"research":{"status":"Succeeded","contentPreview":"...","error":null}}}
```

### Attach to Execution

```http
GET /api/execution/{executionId}/attach
Accept: text/event-stream
```

Attaches to an existing execution stream. Events that occurred before attachment are replayed.

---

## Triggers

### List Triggers

```http
GET /api/triggers
```

**Response:**
```json
[
  {
    "id": "trigger-abc123",
    "orchestrationId": "research-assistant-a1b2c3d4",
    "orchestrationName": "research-assistant",
    "type": "scheduler",
    "enabled": true,
    "status": "idle",
    "lastFired": "2024-01-15T09:00:00Z",
    "nextFire": "2024-01-15T10:00:00Z",
    "runCount": 15,
    "config": {
      "cron": "0 * * * *"
    }
  }
]
```

### Get Trigger

```http
GET /api/triggers/{id}
```

### Enable Trigger

```http
POST /api/triggers/{id}/enable
```

### Disable Trigger

```http
POST /api/triggers/{id}/disable
```

### Fire Trigger

```http
POST /api/triggers/{id}/fire
Content-Type: application/json
```

**Request (optional):**
```json
{
  "parameters": {
    "key": "value"
  }
}
```

### Delete Trigger

```http
DELETE /api/triggers/{id}
```

---

## Webhooks

### Fire Webhook

```http
POST /api/webhooks/{id}
Content-Type: application/json
X-Webhook-Signature: sha256=<signature>
```

**Request:** Any JSON payload (passed as parameters)

**Signature:** HMAC-SHA256 of the request body using the webhook secret.

### Validate Webhook

```http
POST /api/webhooks/{id}/validate
Content-Type: application/json
X-Webhook-Signature: sha256=<signature>
```

**Response:**
```json
{
  "valid": true
}
```

---

## History

### Get Recent Runs

```http
GET /api/history?limit=10
```

**Response:**
```json
{
  "count": 2,
  "runs": [
    {
      "runId": "a1b2c3d4e5f6",
      "executionId": "a1b2c3d4e5f6",
      "orchestrationName": "research-assistant",
      "version": "1.0",
      "triggeredBy": "manual",
      "startedAt": "2024-01-15T10:30:00Z",
      "completedAt": "2024-01-15T10:30:45Z",
      "durationSeconds": 45.12,
      "status": "Succeeded",
      "isActive": false
    }
  ]
}
```

### Get All Runs

```http
GET /api/history/all?offset=0&limit=100
```

**Query Parameters:**
- `offset`: Number of records to skip (default: 0)
- `limit`: Number of records to return (default: 100)

### Get Run Details

```http
GET /api/history/{orchestrationName}/{runId}
```

**Response:**
```json
{
  "runId": "a1b2c3d4e5f6",
  "orchestrationName": "research-assistant",
  "version": "1.0",
  "triggeredBy": "manual",
  "status": "Succeeded",
  "startedAt": "2024-01-15T10:30:00Z",
  "completedAt": "2024-01-15T10:30:45Z",
  "durationSeconds": 45.12,
  "parameters": {
    "topic": "AI"
  },
  "finalContent": "Summary of results...",
  "steps": [
    {
      "name": "research",
      "status": "Succeeded",
      "startedAt": "2024-01-15T10:30:00Z",
      "completedAt": "2024-01-15T10:30:30Z",
      "durationSeconds": 30.0,
      "content": "Research results...",
      "rawContent": "Raw research results...",
      "promptSent": "Research: AI",
      "actualModel": "claude-opus-4.5",
      "usage": {
        "inputTokens": 1500,
        "outputTokens": 2000,
        "totalTokens": 3500
      },
      "errorMessage": null,
      "trace": {
        "systemPrompt": "You are a researcher...",
        "userPromptRaw": "Research: AI",
        "userPromptProcessed": "Research: AI",
        "reasoning": "Let me analyze...",
        "toolCalls": [
          {
            "callId": "call_123",
            "mcpServer": "web-search",
            "toolName": "web_search",
            "arguments": "{\"query\":\"AI\"}",
            "success": true,
            "result": "[results]",
            "error": null,
            "startedAt": "2024-01-15T10:30:05Z",
            "completedAt": "2024-01-15T10:30:08Z"
          }
        ],
        "responseSegments": ["Research results..."],
        "finalResponse": "Research results...",
        "outputHandlerResult": null
      }
    }
  ]
}
```

### Delete Run

```http
DELETE /api/history/{orchestrationName}/{runId}
```

---

## Active Executions

### Get Active Executions

```http
GET /api/active
```

**Response:**
```json
{
  "running": [
    {
      "executionId": "a1b2c3d4e5f6",
      "orchestrationId": "research-assistant-abc123",
      "orchestrationName": "research-assistant",
      "startedAt": "2024-01-15T10:30:00Z",
      "triggeredBy": "manual",
      "source": "manual",
      "status": "Running",
      "parameters": {"topic": "AI"},
      "totalSteps": 3,
      "completedSteps": 1,
      "currentStep": "analyze"
    }
  ],
  "pending": [
    {
      "orchestrationId": "daily-report-def456",
      "orchestrationName": "daily-report",
      "nextFireTime": "2024-01-15T11:00:00Z",
      "status": "waiting",
      "triggerType": "scheduler",
      "source": "pending"
    }
  ],
  "totalRunning": 1,
  "totalPending": 1
}
```

### Cancel Execution

```http
POST /api/active/{executionId}/cancel
```

---

## Utility

### List Available Models

```http
GET /api/models
```

**Response:**
```json
{
  "models": [
    {
      "id": "claude-opus-4.5",
      "name": "Claude Opus 4.5",
      "provider": "Anthropic"
    },
    {
      "id": "gpt-4o",
      "name": "GPT-4o",
      "provider": "OpenAI"
    }
  ]
}
```

### List MCPs

```http
GET /api/mcps
```

**Response:**
```json
{
  "count": 2,
  "mcps": [
    {
      "name": "filesystem",
      "type": "Local",
      "command": "npx",
      "arguments": ["-y", "@anthropic/mcp-server-filesystem", "."],
      "usedByCount": 2,
      "usedBy": ["research-assistant-abc123", "code-reviewer-def456"]
    },
    {
      "name": "web-search",
      "type": "Remote",
      "endpoint": "https://mcp.example.com/search",
      "usedByCount": 1,
      "usedBy": ["research-assistant-abc123"]
    }
  ]
}
```

### Server Status

```http
GET /api/status
```

**Response:**
```json
{
  "status": "running",
  "version": "1.0.0",
  "orchestrationCount": 5,
  "activeTriggers": 3,
  "runningExecutions": 1,
  "dataPath": "/data"
}
```

---

## MCP Server Endpoints

Orchestra exposes orchestrations to external AI agents via Model Context Protocol (MCP) server endpoints using Streamable HTTP transport.

### Data Plane (`/mcp/data`)

Enabled by default. Provides orchestration discovery and invocation.

**Tools:**

| Tool | Description |
|------|-------------|
| `ListOrchestrations` | List and filter orchestrations by tags or name pattern |
| `InvokeOrchestration` | Invoke an orchestration by ID (async or sync mode) |
| `GetOrchestrationStatus` | Check execution status and results by execution ID |
| `CancelOrchestration` | Cancel a running execution |

**InvokeOrchestration Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `orchestrationId` | string | required | Orchestration ID to invoke |
| `parameters` | string (JSON) | `null` | JSON object with parameter key-value pairs |
| `mode` | string | `"async"` | `"async"` (returns immediately) or `"sync"` (blocks until done) |
| `timeoutSeconds` | int | `300` | Sync mode timeout in seconds |
| `metadata` | string (JSON) | `null` | Optional tracking metadata (e.g., correlation IDs) |
| `parentExecutionId` | string | `null` | Parent execution ID for nested invocations |

**Nesting:**

Orchestrations can invoke other orchestrations via the MCP data plane. Nesting is tracked automatically:
- `parentExecutionId`, `rootExecutionId`, and `depth` are included in status responses
- Configurable maximum nesting depth (default: 5)
- Child cancellation tokens are linked to parent tokens

### Control Plane (`/mcp/control`)

Disabled by default. Enable via configuration:

```csharp
builder.Services.AddOrchestraMcpServer(options =>
{
    options.ControlPlaneEnabled = true;
});
```

**Tools:**

| Tool | Description |
|------|-------------|
| `GetOrchestrationDetails` | Get full orchestration details including steps |
| `RegisterOrchestration` | Register an orchestration from a file path |
| `RemoveOrchestration` | Remove a registered orchestration |
| `ScanDirectory` | Scan a directory for orchestration files |
| `ListTags` | List all tags with counts |
| `AddTags` | Add tags to an orchestration |
| `RemoveTag` | Remove a tag from an orchestration |
| `ListProfiles` | List all profiles |
| `CreateProfile` | Create a new profile |
| `DeleteProfile` | Delete a profile |
| `ActivateProfile` | Activate a profile |
| `DeactivateProfile` | Deactivate a profile |
| `ListTriggers` | List all triggers |
| `EnableTrigger` | Enable a trigger |
| `DisableTrigger` | Disable a trigger |
| `ListRuns` | List recent run history |
| `GetRun` | Get full run details |

### Configuration

```csharp
builder.Services.AddOrchestraMcpServer(options =>
{
    options.DataPlaneEnabled = true;          // default
    options.DataPlaneRoute = "/mcp/data";     // default
    options.ControlPlaneEnabled = false;      // default
    options.ControlPlaneRoute = "/mcp/control"; // default
    options.MaxNestingDepth = 5;              // default
});
```

### Connecting

Any MCP-compatible client can connect. From within an orchestration, use `{{server.url}}`:

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

---

## Error Responses

All endpoints may return error responses:

```json
{
  "error": "Orchestration not found",
  "details": "No orchestration with ID 'invalid-id' exists"
}
```

**Status Codes:**
- `200 OK` - Success
- `201 Created` - Resource created
- `204 No Content` - Success with no response body
- `400 Bad Request` - Invalid request
- `404 Not Found` - Resource not found
- `500 Internal Server Error` - Server error
