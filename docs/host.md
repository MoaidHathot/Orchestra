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

The host ships inside the **`Orchestra`** tool — `orchestra portal` runs it. There is no
`Orchestra.Host` package on NuGet; to embed the library, reference the project from a clone of
the repository.

```bash
orchestra portal                                   # dashboard, REST API, MCP endpoints
orchestra portal --urls http://localhost:5100
```

## Quick Start

Embedding the host in your own ASP.NET Core app:

```csharp
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Bind Kestrel to orchestra.json's `urls` unless something more explicit was supplied.
var orchestraConfig = OrchestraConfigLoader.Load();
builder.Configuration.ApplyOrchestraUrls(orchestraConfig);

builder.Services.AddOrchestraHost(options =>
{
    options.DataPath = "./data";

    // The workspace ROOT, not the orchestrations folder: the host looks for
    // `orchestrations/` and `profiles/` subdirectories inside it.
    options.Scan = new ScanConfig { Directory = ".", Watch = true };
});

// Register an AgentBuilder. Orchestra.Server and the CLI use a shared keyed registration for
// copilot + opencode so a step's `provider` field is honored; a single non-keyed builder
// routes every step to that one provider regardless of what the step asked for.
builder.Services.AddSingleton<AgentBuilder, MyAgentBuilder>();

var app = builder.Build();

await app.Services.InitializeOrchestraHostAsync();
app.UseOrchestraHostProblemDetails();
app.MapOrchestraHostEndpoints();

app.Run();
```

## Configuration

Almost everything the host does is configured through **`orchestra.json`**. Every property is
optional — Orchestra runs with no configuration file at all — and the file supports `//`
comments, trailing commas, and `${VAR}` / `"env:VAR"` environment expansion. A referenced
variable that is not set is a **hard error**, not an empty string, so a missing secret fails
loudly instead of silently producing a broken command line.

### Where `orchestra.json` is found

First match wins:

1. `ORCHESTRA_CONFIG_PATH` — an explicit file path.
2. **Project-local** — walking up from the working directory, checking `./orchestra.json` then
   `./.orchestra/orchestra.json` at each level, stopping once it has inspected a repository
   root (a directory containing `.git`). This is what `orchestra init` scaffolds.
3. `$XDG_CONFIG_HOME/Orchestra/orchestra.json`.
4. `%APPDATA%\Orchestra\orchestra.json` (Windows) or `~/.config/Orchestra/orchestra.json`.

Project-local beats the user-global locations, matching how `.editorconfig`, `global.json`, and
`Directory.Build.props` behave, so a workspace is self-contained while a personal config still
applies elsewhere. `orchestra.mcp.json` and `orchestra.services.json` are resolved **only** next
to whichever `orchestra.json` won: inside a workspace with its own `orchestra.json`, the
user-global sidecars are ignored (the host logs which file it skipped and why), so a scaffolded
project never starts your personal MCP servers or services. A user-global sidecar applies when
the user-global `orchestra.json` is the one in effect, or when no `orchestra.json` exists at all.
To use a global MCP or service inside a workspace, copy the sidecar next to that workspace's
`orchestra.json`.

Run **`orchestra doctor`** to see which file is actually in effect and whether it parses.

> A malformed `orchestra.json` is **not** fatal: the host logs a warning and continues on
> built-in defaults, so none of your settings apply and nothing obvious tells you. `doctor`
> reports it as a failure for exactly that reason.

### Global MCP startup

Execution hosts, including `orchestra portal`, **validate every global MCP before startup
completes**. Startup proceeds in this order:

1. Start managed services and wait for their configured readiness checks.
2. Connect each global MCP and perform `tools/list` where supported. This also checks backends
   behind another proxy that defers authentication until discovery.
3. Start serving the portal and executing orchestrations.

A connection or discovery failure aborts startup immediately, names the failing MCP, and
preserves its transport error or process stderr. Already-open MCP connections and managed
services are cleaned up. The CLI exits with a nonzero status. Local MCP launchers are started
serially, avoiding overlapping restores within this host's MCP startup phase.

Each backend gets a separate **60-second** connection-and-discovery budget. Configure it with
`mcpServer.backendStartupTimeoutSeconds` in `orchestra.json`; this is independent of tool-call
timeouts and the shorter per-step discovery probe. Increase it when discovery or interactive
sign-in needs longer; backend/SDK timeouts can still fail earlier. A successful startup cannot
guarantee a backend stays healthy, so the existing per-step checks still apply.

For startup failures:

- **Connection refused:** start the upstream server and check the configured endpoint's port.
- **HTTP 404 with `Mcp-Session-Id`:** the upstream rejected the session. Check whether it restarted
  during initialization and verify its MCP route. Restarting Orchestra creates fresh sessions.
- **NuGet package file in use:** another process holds the package file. Let its restore finish
  or stop the stale process, then restart Orchestra.

Unpinned `dnx` commands can resolve different package versions across machines or over time.
When comparing laptops, compare the effective configuration, .NET SDK, and resolved tool versions
as well as running processes and cache state. Pin package versions for reproducible launches.

### Editor support

`orchestra.schema.json` ships with the tool and describes every key with its default. Bind it
for completion and hover documentation:

```bash
orchestra schemas          # copies the schemas to ./.orchestra/schemas
```

```jsonc
{
  "$schema": "./.orchestra/schemas/orchestra.schema.json",
  "scan": { "directory": ".", "recursive": true }
}
```

`orchestra init` writes this reference for you. The schema sets `additionalProperties: false`,
so a typo'd key is flagged in the editor rather than silently ignored at runtime.

### Commonly used settings

| Key | Default | Purpose |
|---|---|---|
| `urls` | none | Kestrel binding. Applied unless `--urls`, `ASPNETCORE_URLS`, or `DOTNET_URLS` is set. |
| `hostBaseUrl` | none | Instance the CLI targets when neither `--server` nor `$ORCHESTRA_URL` is set. |
| `dataPath` | `%LOCALAPPDATA%/OrchestraHost` | Run history, registry, checkpoints. Relative to this file's directory. |
| `scan.directory` | none | Workspace **root** containing `orchestrations/` and `profiles/`. |
| `provider` / `defaultProvider` | `copilot` | Default agent provider for Prompt steps. |
| `defaultModel` | none | Default model for Prompt steps that set no `model` and belong to an orchestration with no `defaultModel`. Also used for trigger input-handler calls. |
| `defaultReasoningLevel` | none (provider default) | Default reasoning effort (`low` / `medium` / `high` / `xhigh` / `max`) for Prompt steps that set no `reasoningLevel` and belong to an orchestration with no `defaultReasoningLevel`. `xhigh`/`max` are only honoured by models that advertise them. |
| `defaultMaxConcurrency` | none (unbounded) | Cap on how many steps of a single run execute at the same time, for orchestrations that set no `maxConcurrency`. Approval steps and async Orchestration steps never occupy a slot. Values below 1 are ignored with a startup warning. |
| `enableScheduler` | `true` | Set `false` for an API-only host that never fires triggers itself. |
| `logLevel` | `Information` | Default minimum level for the console and the log file. Overrides `Logging:LogLevel:Default` from `appsettings.json`. |
| `logging.file` | on, `{dataPath}/logs`, 14 days | The host's rolling log file: one `orchestra-yyyy-MM-dd.log` per day, older files deleted past `retainedFiles`. Keys: `enabled`, `directory` (relative to `orchestra.json`), `level` (defaults to `logLevel`), `retainedFiles`. Read it with `orchestra logs [--follow]` or `GET /api/logs`. |
| `telemetry` | off | OpenTelemetry export (`otlpEndpoint`, `otlpProtocol`, `serviceName`, `traces`, `metrics`, `enabled`). See [Observability](#observability). |
| `retention` | unlimited | Run-history limits: `maxRunsPerOrchestration` (keep the newest N) and `maxRunAgeDays`. The host sweeps hourly; `orchestra runs prune [--dry-run]` applies or previews the same policy on demand. Favorited runs are always exempt. `orchestra init` scaffolds 200 runs / 30 days. |
| `hooks` | `[]` | Global lifecycle hooks applied to every orchestration. |

See `schemas/orchestra.schema.json` for the complete set, including `copilot`, `openCode`,
`agentPool`, `mcpServer`, `polling`, and `sse`.

### OrchestrationHostOptions

The runtime options object that `orchestra.json` is applied onto. These are also settable
programmatically when embedding the host.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DataPath` | `string` | `%LOCALAPPDATA%/OrchestraHost` | Root path for all Orchestra data |
| `Scan` | `ScanConfig?` | `null` | Workspace root to scan and optionally watch for orchestration and profile files |
| `HostBaseUrl` | `string?` | `null` | Base URL for generating run detail links |
| `LoadPersistedOrchestrations` | `bool` | `true` | Load saved orchestrations on startup |
| `LoadPersistedTriggers` | `bool` | `true` | Load saved trigger states on startup |
| `RegisterJsonTriggers` | `bool` | `true` | Register triggers defined in orchestration JSON |
| `StartExternalServices` | `bool` | `true` | Start processes declared in `orchestra.services.json` |
| `AutoResumeCheckpointsOnStartup` | `bool` | `true` | Resume checkpointed runs on startup |
| `EnableScheduler` | `bool` | `true` | Run the trigger scheduler |
| `ShutdownTimeoutSeconds` | `int` | `30` | Grace period for in-flight work on shutdown |
| `Hooks` | `HookDefinition[]` | `[]` | Global hooks applied to every orchestration executed by the host |

`LoadPersistedOrchestrations`, `LoadPersistedTriggers`, `RegisterJsonTriggers`, and
`StartExternalServices` are programmatic-only — they have no `orchestra.json` equivalent,
because each composition root (portal, server, CLI managed session) sets them deliberately.

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

### Copilot authentication

Set a host-wide GitHub token (and/or the SDK's logged-in-user flag) in `orchestra.json` so
Copilot auth is deterministic for servers/CI instead of relying solely on the bundled CLI's
stored credentials. `${VAR}` / `env:VAR` references are expanded when the config file loads,
so secrets can come from the environment. A per-step `githubToken` still overrides this for
that step's session.

```json
{
  "copilot": {
    "gitHubToken": "${GITHUB_TOKEN}",
    "useLoggedInUser": false
  }
}
```

### Copilot MCP-startup timeout

A Copilot session's `create`/`resume` call spawns the step's inline `type: local` MCP stdio
servers and performs their `initialize` handshake inside the SDK. If an MCP command never starts
(for example a bare shim that can't be resolved) or never answers `initialize`, that call would
otherwise block until the step is manually cancelled. `copilot.mcpStartupTimeoutSeconds` bounds it
and turns a stuck MCP into a clear, retryable failure (the CLI-swap loop can then try a fresh
worker). Omit the key to use the built-in default (120s — generous enough to absorb a first-run
`dnx`/NuGet package restore); set it to `0` to disable the guard.

```json
{
  "copilot": {
    "mcpStartupTimeoutSeconds": 120
  }
}
```

> Note: on Windows, bare shim commands such as `dnx`/`npx` (which are really `dnx.cmd`/`npx.cmd`)
> are automatically resolved to their full path before being handed to the provider runtime, so an
> inline `type: local` MCP with `"command": "dnx"` launches correctly. The timeout above is a
> secondary safety net for MCP servers that start but never complete their handshake.

### Environment Variables

| Variable | Description |
|----------|-------------|
| `ORCHESTRA_CONFIG_PATH` | Explicit `orchestra.json` path. Highest precedence in discovery. |
| `ORCHESTRA_DATA_PATH` | Override the data path (`Orchestra.Server`). |
| `ORCHESTRA_PORTAL_DATA_PATH` | Override the data path (portal). |
| `ORCHESTRA_ORCHESTRATIONS_PATH` | Override the workspace scan root. |
| `ORCHESTRA_ENABLE_SCHEDULER` | `false` runs an API-only host that never fires triggers. |
| `ORCHESTRA_URL` | Server URL used by CLI client commands. |
| `ORCHESTRA_COPILOT_CLI_PATH` | Use a pre-installed Copilot CLI instead of the managed download. |
| `ORCHESTRA_COPILOT_NPM_REGISTRY` | npm registry mirror for the Copilot CLI download (the only one tried when set; also honoured by the build). When unset, the registry from `~/.npmrc` / `npm_config_registry` is tried before `registry.npmjs.org`. |
| `ORCHESTRA_OPENCODE_PATH` | Path to the `opencode` binary when it is not on `PATH`. |
| `ASPNETCORE_URLS` / `DOTNET_URLS` | Standard ASP.NET bindings; both take precedence over `orchestra.json`'s `urls`. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_PROTOCOL` / `OTEL_SERVICE_NAME` | Standard OpenTelemetry variables; turn on OTLP export when `orchestra.json` has no `telemetry` block (see below). |

The engine also *sets* variables for `Script` steps: `ORCHESTRA_CONTROL_FILE`,
`ORCHESTRA_RUN_ID`, and `ORCHESTRA_STEP_NAME`.

### Observability

**Log file.** Every host writes one log file per day to `{dataPath}/logs/orchestra-yyyy-MM-dd.log`
and keeps 14 of them (`logging.file` in `orchestra.json` changes the directory, level or
retention, or turns it off). `orchestra logs [--follow] [--level Warning]` tails it and
`GET /api/logs` serves the same data to the portal. The CLI's throwaway management host never
writes one.

**Health.** `GET /api/health` runs real checks - data path writable, an agent provider
registered, scheduler on, log file, telemetry - and answers `200` with `status: healthy|degraded`
or `503` with `status: unhealthy` when runs could not be persisted. Each check is listed with a
detail line, so a `degraded` answer tells you what is missing.

**Traces and metrics.** The engine instruments every run with an `orchestra.run` span (tags:
orchestration, run id, trigger id, status) and every step with a nested `orchestra.step` span
(name, type, status, error category), and publishes an `Orchestra.Engine` meter:

| Instrument | Type | Tags |
|---|---|---|
| `orchestra.runs.started` / `orchestra.runs.completed` | counter | `orchestra.orchestration`, `orchestra.status` (completed) |
| `orchestra.runs.active` | up/down counter | `orchestra.orchestration` |
| `orchestra.run.duration` | histogram (s) | `orchestra.orchestration`, `orchestra.status` |
| `orchestra.steps.completed` | counter | `orchestra.orchestration`, `orchestra.step.type`, `orchestra.status` |
| `orchestra.step.duration` | histogram (s, includes retries and slot queueing) | `orchestra.orchestration`, `orchestra.step.type`, `orchestra.status` |
| `orchestra.steps.retries` | counter | `orchestra.orchestration`, `orchestra.step.type`, `orchestra.error.category` |

Nothing is exported until a collector is named:

```jsonc
"telemetry": {
  "otlpEndpoint": "http://localhost:4317",   // or OTEL_EXPORTER_OTLP_ENDPOINT
  "otlpProtocol": "grpc",                    // or "http/protobuf"
  "serviceName": "orchestra",
  "traces": true,
  "metrics": true
}
```

With an endpoint the host registers the OpenTelemetry SDK for the engine source/meter plus
ASP.NET Core request traces and metrics, and ships them over OTLP. Any OpenTelemetry-compatible
backend (Aspire dashboard, Jaeger, Grafana Tempo/Prometheus via the collector) works. Custom hosts
built on `Orchestra.Host` call `services.AddOrchestraTelemetry(orchestraConfig)`; hosts that
embed only the engine can subscribe an `ActivityListener`/`MeterListener` to
`OrchestraDiagnostics.SourceName` directly - the engine takes no OpenTelemetry dependency.

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
| `MaxNestingDepth` | `int` | `5` | Maximum orchestration-to-orchestration nesting depth |
| `DefaultOrchestraInvokeTimeoutSeconds` | `int` | `0` | Client-side transport timeout (seconds) applied to MCP requests targeting `/mcp/data` when the calling orchestration's `mcps[]` entry doesn't override it. **`0` = no transport timeout — server-side timeouts are authoritative**. Set to a positive value only if you want a belt-and-suspenders client-side cap; in that case make sure it is `>=` the largest sync `timeoutSeconds` your orchestrations use, or `invoke_orchestration` will return a `timeout-mismatch` error. |

> **Cancellation taxonomy.** Cancelled runs are recorded with a structured
> `CancellationDetails` (`cancellation` field on the run record + JSON projections).
> The `kind` enum distinguishes:
> - `External` — caller cancelled (REST `/api/active/{id}/cancel`, MCP
>   `cancel_orchestration` tool, or a parent run propagating cancel via the linked CTS).
>   The `detail` field carries the source string (`"REST /api/active/{id}/cancel"`,
>   `"mcp:cancel_orchestration: <reason>"`, `"propagated from parent <id> (step: <step>)"`).
> - `OrchestrationTimeout` — the orchestration's own `timeoutSeconds` fired.
> - `SyncInvokeTimeout` — the sync hard cap fired: either the `timeoutSeconds` passed to a
>   sync `invoke_orchestration` call, or the effective `timeoutSeconds` /
>   `defaultStepTimeoutSeconds` of the parent's `Orchestration` step (the launcher enforces
>   it on the step's behalf).
> - `McpRequestAborted` — the upstream MCP transport aborted its request to
>   `/mcp/data` before the engine completed (typically a stale `mcps[].timeoutSeconds`
>   smaller than the requested sync `timeoutSeconds`). Counted as a timeout
>   (`isTimeout: true`). Only recorded for children launched through the MCP
>   `invoke_orchestration` tool (`ChildLaunchRequest.Origin = McpTool`); a child launched by
>   an in-process `Orchestration` step whose parent token fires is recorded as `External`
>   with `"propagated from parent <id> (step: <step>)"` instead.
> - `OrchestrationComplete` — a step invoked the `orchestra_complete` engine tool.
> - `HostShutdown` — the host process was stopping.
> - `AwaitingInputTimeout` / `HostShutdownDuringWait` — outstanding human-in-the-loop wait.
> - `ConfigReload` — orchestration definition reloaded on disk while the run was active
>   (reserved; not yet wired by the file watcher).
>
> Every cancelled run also carries a `progress` summary inside `cancellation`:
> total/completed/cancelled/failed/skipped step counts, the most-recently-completed
> step name and timestamp, and the list of cancelled step names. Use it to answer
> "how far along was this run?" without scanning per-step records.

#### Data-Plane Tools

The data-plane MCP server provides the following tools:

| Tool | Description |
|------|-------------|
| `ListOrchestrations` | List and filter orchestrations by tags or name pattern. Returns IDs, names, descriptions, parameter schemas. |
| `InvokeOrchestration` | Invoke an orchestration by ID with parameters. Supports `async` (default) and `sync` modes. Sync mode accepts a `detail` parameter (`summary`/`compact`/`full`) controlling per-step content size. |
| `GetOrchestrationStatus` | Check the status and result of an execution by execution ID. Accepts a `detail` parameter; per-step results include `errorMessage`, `contentLength`, `truncated`, and `hasRawContent` metadata. |
| `GetOrchestrationStep` | Fetch full (or paginated, via `offset`/`length`) content of one step. Use after `GetOrchestrationStatus` reports `truncated: true`, or to drill into a failed child step's raw output. |
| `ListChildRuns` | List runs scoped to the caller's execution chain (auto-resolved from stamped headers when invoked from inside an orchestration; external callers must pass `parentExecutionId` or `rootExecutionId`). Supports `status` filter and `limit`/`offset` pagination. |
| `CancelOrchestration` | Cancel a running execution by execution ID. |
| `ListPendingInputs` / `RespondToInput` | Discover and respond to runs awaiting human input. |

`InvokeOrchestration` supports two modes:
- **`async`** (default): Returns immediately with an `executionId`. Use `GetOrchestrationStatus` to poll for results.
- **`sync`**: Blocks until the orchestration completes or the timeout is reached (default: 300 seconds). The sync response includes `stepResults` keyed by step name, each carrying `status`, truncated `content`, `errorMessage`, `savedFiles`, and truncation metadata.

##### Response detail levels

`GetOrchestrationStatus` and the sync `InvokeOrchestration` response both accept a `detail` parameter:

- `summary` — Step `content` and the top-level `summary` are omitted entirely; metadata (`contentLength`, `truncated`, `hasRawContent`, `errorMessage`, `savedFiles`) is preserved so the caller can decide whether to drill in.
- `compact` (default) — Content is truncated at ~8000 chars per step (16000 for the top-level summary) with the `... (truncated)` marker appended for human readability, plus the structured metadata fields.
- `full` — No truncation. Responses may be very large (a single run's step content can exceed 100 KB).

For runs whose content exceeds even `full`'s practical token budget, use `GetOrchestrationStep` with `offset`/`length` to page through the content of a specific step.

##### Header-based scope for `ListChildRuns`

The engine stamps `X-Orchestra-Parent-Execution-Id`, `X-Orchestra-Parent-Orchestration-Name`, `X-Orchestra-Parent-Step-Name`, and `X-Orchestra-Root-Execution-Id` on outbound MCP connections that target Orchestra's own endpoints. `ListChildRuns` auto-resolves its scope from those headers (preferring `Root-Execution-Id`, falling back to `Parent-Execution-Id` when the root isn't stamped) so a self-healing controller can enumerate its own attempts without needing to remember its own execution id. External callers (no headers) must pass `parentExecutionId` or `rootExecutionId` explicitly — the tool refuses to enumerate without a scope to avoid leaking unrelated runs.

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
    "paths": ["./orchestrations/my-workflow.json"]
  }'
```

#### Register from JSON

```bash
curl -X POST http://localhost:5000/api/orchestrations/json \
  -H "Content-Type: application/json" \
  -d '{
    "json": "{\"name\":\"test\",\"version\":\"1.0\",\"steps\":[...]}"
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
| `GET` | `/api/history/all` | Get all executions (paginated; `total` counts every match) |
| `GET` | `/api/history/search` | Search by name, run id, annotation, or run output (paginated) |
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
| `GET` | `/api/health` | Readiness probe with per-check detail; `503` when the data path is not writable |
| `GET` | `/api/logs` | Tail of the host's log file (`orchestra logs`) |

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

### Watcher Trigger

Subscribes the orchestration to a **watcher**: an in-process poller that runs a *fetcher*
(a command or a script) on a schedule, deduplicates the items it returns per subscriber and
launches one run per genuinely new item. Watchers are declared once in
`orchestra.watchers.json` (beside `orchestra.json`, like the MCP and services sidecars) and
referenced by name; a watcher only runs while at least one enabled orchestration subscribes
to it.

```yaml
trigger:
  type: watcher
  watcher: github-pr-comments      # a key in orchestra.watchers.json
  maxConcurrent: 4                 # parallel launches per tick (default 1)
  catchup: from-now                # skip the existing backlog (default); "all" dispatches it
  parameters:                      # item -> orchestration inputs
    comment: "{{item}}"            # whole item as JSON
    author: "{{item.user.login}}"  # nested field; missing paths render as the literal null
```

Small one-off watchers can be defined inline instead of in the library - `definition` takes
the same shape as a library entry plus a required `name`, and relative paths resolve against
the orchestration file's folder:

```yaml
trigger:
  type: watcher
  definition:
    name: local-inbox
    type: poll-script
    schedule: { intervalSeconds: 30 }
    fetch: { shell: pwsh, scriptFile: ./fetch.ps1, config: { folder: ./inbox } }
    dedup: { key: "{{item.path}}", mode: presence }
  parameters: { path: "{{item.path}}" }
```

Exactly one of `watcher` / `definition` is required; an unknown library name, an inline name
that collides with a library watcher, or a disabled watcher leaves the trigger registered in
the `Error` state with the reason in `lastError` - the host keeps running.

**`orchestra.watchers.json`** is a map of name to definition (schema:
`schemas/orchestra.watchers.schema.json`):

```jsonc
{
  "watchers": {
    "github-pr-comments": {
      "type": "poll-command",                       // or "poll-script"
      "schedule": { "intervalSeconds": 15 },        // or { "cron": "*/30 * * * * *" } (UTC; 5 or 6 fields)
      "fetch": {
        "command": "dnx",                           // poll-script: "shell" + "scriptFile" | "script"
        "arguments": ["Gh.Cli", "--", "pr-comments", "--json"],
        "timeoutSeconds": 60,
        "config": { "repo": "contoso/app" }        // passed verbatim to the fetcher
      },
      "items": { "jsonPath": "$.items[*]" },        // only for non-standard output shapes
      "dedup": { "key": "{{item.id}}", "mode": "fifo", "fifo": { "maxEntries": 10000, "ttlDays": 14 } },
      "failurePolicy": "warn"                       // warn | ignore | fail (stops this watcher only)
    }
  }
}
```

**Fetcher protocol (v1).** Every tick the fetcher receives a JSON document on stdin -
`{ "protocolVersion": "1", "watcher": "<name>", "cursor": <previous cursor or null>,
"subscribers": [...], "config": <fetch.config or null> }` - and must exit 0 with a JSON
document on stdout: `{ "protocolVersion": "1", "items": [...], "cursor": <opaque, optional>,
"diagnostics": { "message": "..." } }`. Empty or omitted `items` means nothing new. The
cursor is stored per watcher and sent back on the next tick; return only what changed since
it when the source supports that. Schemas: `schemas/watchers/v1/fetcher-input.schema.json`
and `fetcher-output.schema.json`; stdout larger than 16 MB, a non-zero exit, a timeout, or
output that fails the schema counts as a failed tick.

**Dedup.** Per (watcher, orchestration). `fifo` (default) remembers the most recent
`maxEntries` keys for `ttlDays`; `presence` tracks the keys currently visible at the source and
forgets one after it has been absent for `graceTicks` (default 3) ticks. Without `dedup.key`
the key is a hash of the whole item. A fetcher that returns its entire history every tick
under `fifo` eventually re-dispatches trimmed keys - the host logs a `FIFO trim rate`
warning when evictions exceed 10% of `maxEntries` per hour; switch to a cursor-aware fetcher
or `presence` mode.

**Failures and back-off.** After a failed tick the next one waits an extra
`min(2s x 2^(failures-1), 300s)`; the schedule stays the floor. `failurePolicy: fail` stops
that watcher's loop (subscriptions stay registered; it restarts when they change or the host
restarts).

**State** is written atomically to `.orchestra/watchers/` beside the `orchestra.json` in
effect (`<watcher>.state.json` shared cursor + seen keys, `<watcher>--<orchestration>.state.json`
per subscriber) and survives restarts. Loading the library is skipped in the `Testing`
environment or with the `skip-watchers=true` configuration key (inline definitions still
work). Watcher-launched runs report `triggeredBy: watcher:<name>` and the run origin
`watcher`. Use [`orchestra watcher test`](cli.md#watcher-test---run-a-watcher-fetcher-without-a-host) to run a fetcher once without
a host. Worked examples: `examples/watchers/folder-poll-pwsh` and `folder-poll-dotnet`.

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
├── orchestration-tags.json            # Host-managed orchestration tags
├── triggers/                          # Persisted trigger states
│   └── {hash}.trigger.json
├── annotations/                       # User curation for runs (sparse)
│   └── {orchestration-name}/
│       └── {runId}.json               # favorite / title / tags / note
└── executions/                        # Run history
    ├── .index.db                      # SQLite index over the runs below (derived, rebuildable)
    └── {orchestration-name}/
        └── {name}_{version}_{trigger}_{timestamp}_{id}/
            ├── orchestration.json     # Copy of orchestration at execution
            ├── run.json               # Full OrchestrationRunRecord
            ├── {step-name}-inputs.json
            ├── {step-name}-outputs.json
            ├── {step-name}-result.json
            └── result.md              # Human-readable final output
```

`executions/` is an immutable record. Mutable per-run state lives in parallel roots —
`annotations/` alongside `checkpoints/`, `pending/` and `temp/`. Favorited runs are exempt
from retention deletion; see the [run storage reference](run-storage.md).

`.index.db` is a SQLite projection of the runs beside it, used to answer history, search and
lineage queries without opening any `run.json`. It is derived and safe to delete — the host
rebuilds it, and discards it automatically if it is ever unreadable. See
[the run index](run-storage.md#the-run-index).

For a deep-dive on `run.json`'s structure, what each field contains, sizes you can expect,
and the design decisions behind how parent → child orchestration links are persisted (and
why we DON'T inline child step content into the parent's run record), see the
[run storage reference](run-storage.md).

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
