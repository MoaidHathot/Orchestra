---
layout: default
title: API Reference
nav_order: 7
---

# API Reference

Complete REST API reference for Orchestra.Host endpoints.

## Base URL

All endpoints are relative to your host's base URL (e.g., `http://localhost:5000`).

## Authentication

Orchestra does not include built-in authentication. Implement authentication middleware in your ASP.NET Core application as needed.

## Resolving Orchestrations: `{id}` Accepts ID or Name

Every endpoint that takes an `{id}` path parameter under `/api/orchestrations/{id}/...` accepts **either** the auto-generated registry ID (e.g. `research-assistant-a1b2c3d4`) **or** the orchestration's declared `name` field (e.g. `research-assistant`). When invoked by name, the response payload's `id` / `orchestrationId` field always echoes the canonical registry ID so callers can cache by it.

This applies to: `GET /{id}`, `GET /{id}/run`, `DELETE /{id}`, `POST /{id}/enable`, `POST /{id}/disable`, `GET/PUT/POST /{id}/tags`, `DELETE /{id}/tags/{tag}`, `GET/DELETE /{id}/versions`, `GET /{id}/versions/{hash}`, `GET /{id}/versions/{hash1}/diff/{hash2}`, and `GET /{id}/resume/{runId}`.

If multiple orchestrations share a `name` (rare), the lookup returns the first match; reference such orchestrations explicitly by registry ID.

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

**Query Parameters:**
- `limit`: Number of records to return (default: 15)
- `origins`: Comma-separated origin kinds (`manual`, `scheduler`, `loop`, `webhook`, `mcp`, `orchestration`, `retry`, `resume`)
- `roots`: `true` = roots only, `false` = children only, omitted = no scope filter
- `statuses`: Comma-separated `ExecutionStatus` names
- `favorites`: `true` = favorited runs only, `false` = unfavorited only
- `tags`: Comma-separated annotation tags. **OR semantics** — a run matches if it carries *any* of them

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
      "isActive": false,
      "favorite": true,
      "title": "Q1 evidence pack",
      "tags": ["connect", "keep"],
      "note": "Counts are unreliable — see caveats."
    }
  ]
}
```

Every history row carries the run's annotation. Unannotated runs report
`favorite: false`, `title: null`, `tags: []`, `note: null`.

### Get All Runs

```http
GET /api/history/all?offset=0&limit=100
```

**Query Parameters:**
- `offset`: Number of records to skip (default: 0)
- `limit`: Number of records to return (default: 100)
- Plus every filter listed under [Get Recent Runs](#get-recent-runs)

### Search Runs

```http
GET /api/history/search?query=connect
```

Substring, case-insensitive. Matches the orchestration name, the run id, **and the
run's annotation title, tags and note** — which is what makes machine-named runs
(ephemeral and self-healing) findable by the words a human would actually search for.

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
    },
    {
      "name": "invoke-child",
      "status": "Succeeded",
      "content": "Child final output",
      "childExecutionId": "child-exec-id-99",
      "childOrchestrationName": "child-orch",
      "childStatus": "succeeded"
    }
  ]
}
```

> **Note on `childExecutionId` / `childOrchestrationName` / `childStatus`**: these
> fields are only present on steps of type `Orchestration` (steps that invoked
> another orchestration). Portal renders them as a clickable badge that navigates
> to the child run's history view. They are omitted entirely for steps that did
> not launch a child orchestration.

### Delete Run

```http
DELETE /api/history/{orchestrationName}/{runId}
```

**Query Parameters:**
- `force`: Required (`true`) to delete a run marked as a favorite. Without it the
  request is rejected with `400`.

---

## Run Annotations

User-curated metadata attached to a run: **favorite**, **title**, **tags**, **note**.

Run records are immutable — `OrchestrationRunRecord` is entirely `init`-only and re-saving
one would duplicate its history index entry — so annotations live in their own store, keyed
by run id, and are merged into history projections at read time.

Two things annotations buy you:

- **Findability.** Machine-named runs (`ephemeral-efca835904b6-attempt-3`) carry no meaning.
  A title makes them searchable by the words you would actually type.
- **Durability.** Favorited runs are exempt from retention deletion, and are also excluded
  from the max-count ranking so they never consume another run's keep-slot.

Annotations are **sparse**: a record exists only for runs you have acted on. Emptying an
annotation deletes it.

Stored one file per annotated run at `{dataPath}/annotations/{orchestrationName}/{runId}.json`.

### List Annotations

```http
GET /api/history/annotations
GET /api/history/annotations?orphans=true
```

**Response:**
```json
{
  "count": 2,
  "orphanCount": 0,
  "annotations": [
    {
      "runId": "a1b2c3d4e5f6",
      "orchestrationName": "research-assistant",
      "favorite": true,
      "title": "Q1 evidence pack",
      "tags": ["connect", "keep"],
      "note": "Counts are unreliable.",
      "annotatedAt": "2024-01-15T10:31:00Z",
      "orphaned": false
    }
  ],
  "tags": [{ "tag": "connect", "count": 2 }]
}
```

An annotation is **orphaned** when its run no longer exists. Orphans are reported, never
silently deleted — a partially-loaded index must not be able to destroy curation.

### Prune Orphaned Annotations

```http
POST /api/history/annotations/prune
```

### Get / Set / Update / Remove an Annotation

```http
GET    /api/history/{orchestrationName}/{runId}/annotation
PUT    /api/history/{orchestrationName}/{runId}/annotation
PATCH  /api/history/{orchestrationName}/{runId}/annotation
DELETE /api/history/{orchestrationName}/{runId}/annotation
```

**Body** (`PUT` and `PATCH`):
```json
{
  "favorite": true,
  "title": "Q1 evidence pack",
  "tags": ["connect", "keep"],
  "note": "Counts are unreliable."
}
```

`PUT` replaces: omitted fields are cleared. `PATCH` merges: omitted fields are left
untouched, so setting a title cannot silently wipe tags. Passing an empty string clears
a field explicitly.

### Favorite Shortcuts

```http
POST   /api/history/{orchestrationName}/{runId}/favorite
DELETE /api/history/{orchestrationName}/{runId}/favorite
```

---

## Run Export

```http
GET /api/history/{orchestrationName}/{runId}/export?format=bundle
```

**Query Parameters:**
- `format`: `bundle` (default), `report`, or `data`

A run's artifacts live in **two** roots, which is the reason this endpoint exists:

| Root | Contains |
|---|---|
| `{dataPath}/executions/{orch}/{folder}/` | run record, per-step projections, `result.md` |
| `{dataPath}/temp/{orch}/{runId}/` | files written via `orchestra_save_file` |

The second is usually where the real deliverable is: a step that produces a large document
saves it and returns only a short summary inline, so copying the execution folder alone
gives you the summary and loses the document. Every export format pulls both in.

| `format` | Returns | Content type |
|---|---|---|
| `report` | The run's richest markdown — the largest saved `.md` artifact, else `finalContent` | `text/markdown` |
| `bundle` | Everything (below), zipped | `application/zip` |
| `data` | `steps/` only — fence-stripped, JSON-validated | `application/zip` |

`bundle` layout:

```
{orchestration}_{runId}_{timestamp}/
├── README.md            provenance, status warning, step table, parameters, token usage,
│                        the run's annotation, and any export warnings
├── run.json             full run record
├── orchestration.json   definition snapshot as it was at execution time
├── steps/{step}.json    per-step payloads (.txt when the step did not emit JSON)
├── files/{step}.{ext}   saved artifacts, GUID names resolved to the producing step
└── result.md            final content
```

The endpoint always streams, since HTTP cannot write into the caller's filesystem. The CLI
(`orchestra runs export`) expands the archive into a directory unless `--zip` is given.

Missing artifacts and unparseable step payloads are reported in the README's *Export
warnings* section rather than being silently dropped.

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
      "currentStep": "analyze",
      "parentExecutionId": "root-exec-id-xyz",
      "parentStepName": "invoke-research",
      "rootExecutionId": "root-exec-id-xyz",
      "nestingDepth": 1
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

> **Lineage fields** (`parentExecutionId`, `parentStepName`, `rootExecutionId`, `nestingDepth`)
> are present only for nested executions (i.e., when a parent orchestration launched
> this run via an `Orchestration` step or via the `invoke_orchestration` MCP tool).
> Top-level executions omit them. Observers can use these to render "running inside
> chain X" or to scope the data-plane `list_child_runs` tool to the caller's tree.

### Cancel Execution

```http
POST /api/active/{executionId}/cancel
```

---

## Utility

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
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "."],
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
