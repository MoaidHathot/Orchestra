# Project improvement and feature recommendations

**Session ID:** ses_2fad1fdadffexeif9Tv7naF4Uk
**Created:** 3/19/2026, 1:19:54 AM
**Updated:** 3/19/2026, 3:21:25 AM

---

## User

What improvements, new features, optimization, UI/UX features, abilities, customization, etc you would recommend for this project?

---

## Assistant (Build · claude-opus-4.6 · 225.8s)



I'll start by thoroughly exploring the codebase to understand the project's architecture, current features, and areas for improvement.

**Tool: task**

**Input:**
```json
{
  "description": "Explore project structure",
  "prompt": "Thoroughly explore the codebase at P:\\Github\\Orchestra. I need a comprehensive understanding of:\n\n1. What is this project? What does it do?\n2. The full directory structure (top-level and key subdirectories)\n3. The technology stack (languages, frameworks, libraries)\n4. Key configuration files (package.json, tsconfig, etc.)\n5. The architecture pattern used\n6. Main entry points and how the application starts\n7. Key features currently implemented\n\nRead the README, main config files, and explore the directory structure. Return a detailed summary of everything you find.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2fad1e026ffeSgPGg7zn75jddm (for resuming to continue this task if needed)

<task_result>


Based on my previous thorough exploration, I have already completed a comprehensive analysis of the entire Orchestra codebase. There are no remaining next steps for the exploration task — everything has been covered.

Here is the full summary of **what has been done so far in this project**:

---

## Orchestra — Project Summary

**Orchestra** is a **declarative orchestration engine for LLM workflows**, built in **C# / .NET 10.0** by Moaid Hathot (MIT license, Copyright 2026). It lets you define multi-step AI pipelines in JSON and execute them with dependency-based parallelism, MCP tool integration, quality control loops, and automatic triggers.

---

### Architecture (4 Library Layers)

1. **`Orchestra.Engine`** — The core library with zero external dependencies. Contains:
   - Domain models (`Orchestration`, `OrchestrationStep`, `PromptOrchestrationStep`)
   - DAG scheduler using Kahn's algorithm for topological sorting into parallel execution layers
   - `OrchestrationExecutor` — the main execution engine
   - Abstract `AgentBuilder` / `IAgent` interfaces (provider-agnostic)
   - MCP models (`LocalMcp`, `RemoteMcp`)
   - Trigger configs (scheduler, webhook, email, loop)
   - JSON serialization with polymorphic converters
   - Prompt formatting, reporting, and storage abstractions

2. **`Orchestra.Copilot`** — Concrete LLM integration using the **GitHub Copilot SDK** (`v0.1.29`). Implements `CopilotAgentBuilder` and `CopilotAgent` with session management, MCP server configuration, subagent support, and event streaming via Channels.

3. **`Orchestra.Host`** — ASP.NET Core hosting layer providing:
   - REST API endpoints (orchestrations, execution via SSE, triggers, webhooks, runs, utilities)
   - `OrchestrationRegistry` — in-memory + disk-persisted registry
   - `TriggerManager` — BackgroundService handling cron scheduling, webhooks, loops, and email polling
   - `FileSystemRunStore` for run history persistence
   - Easy integration via `AddOrchestraHost()` / `MapOrchestraHostEndpoints()` extension methods

4. **`Orchestra.Mcp.Graph`** — A standalone stdio-based MCP server for **Microsoft 365** (Graph API). Provides tools for Calendar, Mail, Chat, Teams, Users, and Copilot interactions with OAuth/Azure CLI authentication.

---

### Key Features Implemented

- **Declarative JSON pipelines** with `{{param}}` template syntax
- **DAG-based parallel execution** — steps run concurrently when dependencies allow
- **MCP tool integration** — local (stdio) and remote (HTTP) servers, inline or external, with merge/override semantics
- **Quality control loops** — checker steps retry target steps with feedback until an exit pattern matches
- **Input/output handler prompts** — LLM-powered data transformation between steps
- **4 trigger types**: Scheduler (cron + interval), Webhook (with HMAC secret), Email (Outlook folder polling), Loop (re-run on completion)
- **Subagent/multi-agent orchestration** — steps can define subagents with tool restrictions and auto-delegation
- **System prompt modes** (`append` / `replace`) at orchestration and step level
- **Reasoning level control** (`low` / `medium` / `high`)
- **Per-step timeouts**
- **SSE streaming** for real-time execution progress
- **Run history persistence** with traces, token usage, and model info
- **Model mismatch detection** and reporting
- **Orchestration validation** (cycle detection, missing dependencies, parameter checks)
- **Model comparison mode** — run the same orchestration with different model overrides

---

### 4 Playground Entry Points

1. **CLI** (`Orchestra.Playground.Copilot`) — Command-line runner for single orchestrations
2. **Web** (`Orchestra.Playground.Copilot.Web`) — Full-featured web app with SPA frontend (~1400+ lines in Program.cs), REST API, SSE execution streaming
3. **Portal** (`Orchestra.Playground.Copilot.Portal`) — Lightweight web app using the `Orchestra.Host` library pattern, also with SPA frontend
4. **Terminal** (`Orchestra.Playground.Copilot.Terminal`) — TUI app using Spectre.Console with keyboard-driven dashboard, orchestration management, trigger control, and execution history

---

### Testing

6 test projects covering:
- **Unit tests**: `Orchestra.Engine.Tests`, `Orchestra.Copilot.Tests`, `Orchestra.Host.Tests`, `Orchestra.Mcp.Graph.Tests`
- **Integration tests**: `Orchestra.Portal.Tests` (using `Microsoft.AspNetCore.Mvc.Testing`)
- **E2E tests**: `Orchestra.Portal.E2E` (using Microsoft.Playwright)

---

### Additional Assets

- **13 example orchestrations** in `examples/` (blog writing, code review, meal planning, travel planning, research, etc.)
- **Jekyll-based documentation site** in `docs/`
- **Dockerfile** for containerized deployment of the Web playground
- **Scripts** for running the portal with tunneling and executing tests

---

This is a mature, well-structured project with a clean separation of concerns between the engine core, LLM provider integration, hosting infrastructure, and multiple consumer applications. Is there anything specific you would like to dive deeper into or any next steps you would like to take?
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Explore core domain models",
  "prompt": "Explore the core domain models and business logic of the project at P:\\Github\\Orchestra. Look at:\n\n1. All model/entity definitions\n2. Database schemas or migrations\n3. Core services and their responsibilities\n4. API endpoints and controllers\n5. Any orchestration or workflow logic\n6. Integration points with external services (LLMs, APIs, etc.)\n\nSearch through src/ directories, look for models, entities, services, controllers, repositories. Return a detailed summary of the domain model and business logic.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2fad1d49dffedoAzijnzkfqtHy (for resuming to continue this task if needed)

<task_result>


Based on my previous thorough analysis, I have already completed a comprehensive exploration of the entire Orchestra codebase. There are no remaining next steps for the exploration — all source files across all 4 projects have been read and analyzed. Let me deliver the synthesized summary that was noted as not yet delivered.

---

# Orchestra Project — Complete Summary

## What Is Orchestra?

**Orchestra** is a **declarative orchestration engine for LLM workflows** built in .NET. It lets you define multi-step AI pipelines in JSON, which are then executed as a dependency-based DAG (directed acyclic graph) with parallel execution, MCP tool integration, quality-control loops, and automatic triggers. The LLM backend is currently **GitHub Copilot SDK**.

---

## Architecture (4 Projects)

| Project | Role |
|---|---|
| **Orchestra.Engine** | Core domain: models, DAG scheduling, execution, agent abstractions, formatting, serialization, triggers, storage interfaces |
| **Orchestra.Host** | ASP.NET hosting: REST APIs, registry, file-based persistence, trigger management (BackgroundService), SSE streaming |
| **Orchestra.Copilot** | GitHub Copilot SDK implementation of the abstract agent interfaces |
| **Orchestra.Mcp.Graph** | Standalone MCP server exposing Microsoft Graph API tools (Calendar, Mail, Chat, Teams, User) |

---

## Core Domain Model

- **Orchestration** — Root aggregate containing steps, MCP server references, trigger config, and a system prompt mode.
- **OrchestrationStep / PromptOrchestrationStep** — Each step has a system prompt, user prompt, model selection, optional input/output handler prompts, optional loop config, optional subagents, and MCP tool references. Steps declare dependencies on other steps by name.
- **LoopConfig** — Quality-control retry: a "checker" step can loop back to a "target" step with feedback, up to MaxIterations, exiting when an ExitPattern is matched.
- **Subagent** — Named sub-agents with their own prompts, tools, and MCPs that a step can delegate to.
- **Mcp (Local/Remote)** — MCP tool servers, either local (stdio command) or remote (HTTP endpoint).
- **TriggerConfig (4 types)** — Scheduler (cron/interval), Webhook (HMAC-secured), Email (polling), Loop (repeated execution). Each can have an InputHandlerPrompt (LLM-based payload transformation).

---

## Execution Flow

1. **Parse** — JSON orchestration file is deserialized via `OrchestrationParser` with custom polymorphic converters.
2. **Schedule** — `OrchestrationScheduler` validates the DAG using Kahn's algorithm (cycle detection, missing dependency checks), groups steps into parallel execution layers.
3. **Execute** — `OrchestrationExecutor` runs the DAG event-driven using `TaskCompletionSource` per step and a reverse dependency map. Steps within the same layer run in parallel.
4. **Per-Step** — `PromptExecutor` builds prompts (injecting dependency outputs, parameters, loop feedback), creates an agent via `AgentBuilder`, streams events through `AgentEventProcessor`, and optionally runs input/output handler LLM transformations.
5. **Loops** — If a checker step triggers a loop, the target and checker steps are re-executed with accumulated feedback until the exit pattern matches or max iterations is reached.
6. **Persist** — `FileSystemRunStore` saves the full run record (per-step inputs, outputs, traces, tool calls) as JSON files in a folder hierarchy.
7. **Stream** — `SseReporter` sends real-time SSE events to connected clients, with replay support for late joiners.

---

## Persistence

There is **no database**. Everything is file-based:
- **Run storage**: JSON files under `{rootPath}/executions/{orchestration-name}/{name}_{version}_{trigger}_{timestamp}_{id}/`
- **Registry**: `%LOCALAPPDATA%/OrchestraHost/registered-orchestrations.json`
- **Trigger overrides**: `.trigger.json` sidecar files next to orchestration files

---

## REST API Endpoints

| Route | Purpose |
|---|---|
| `GET/POST /api/orchestrations` | CRUD for orchestrations (list, get, add, delete, scan folder) |
| `GET /api/orchestrations/{id}/run` | SSE streaming execution |
| `GET /api/execution/{id}/attach` | Attach to a running execution's SSE stream |
| `*/api/triggers` | Trigger management (list, enable, disable, fire, delete) |
| `POST /api/webhooks/{id}` | Webhook receiver with optional HMAC validation |
| `*/api/history` | Run history (list, detail, delete) |
| `*/api/active` | Active executions (list, cancel) |
| `GET /api/models` | Available AI models |
| `GET /api/mcps` | All MCPs across orchestrations |
| `GET /api/status`, `/api/health` | Server status and health |

---

## External Integrations

1. **GitHub Copilot SDK** — The sole LLM backend. `CopilotAgentBuilder` / `CopilotAgent` / `CopilotSessionHandler` translate between the engine's abstract agent interfaces and the Copilot SDK.
2. **MCP Servers** — Arbitrary tool servers (local stdio or remote HTTP). The engine connects to them at execution time to give steps access to external tools.
3. **Microsoft Graph** — A dedicated MCP server (`Orchestra.Mcp.Graph`) exposing Calendar, Mail, Chat, Teams, and User tools via dual authentication (Azure CLI + OAuth).

---

## Key Design Decisions

- **Declarative JSON-first**: Orchestrations are plain JSON files, not code. This enables version control, sharing, and non-programmer authoring.
- **DAG-based parallelism**: Steps declare dependencies; independent steps run concurrently automatically.
- **Streaming-first**: All execution is streamed via SSE with 13 event types (message deltas, reasoning, tool calls, subagent events, etc.).
- **Agent abstraction**: The engine is backend-agnostic — `IAgent` / `AgentBuilder` can be implemented for any LLM provider, though only Copilot exists today.
- **Quality loops**: Built-in checker/target loop pattern for self-correcting AI outputs.
- **File-based persistence**: Simplicity over complexity — no database dependency.

---

That is the complete picture of what has been built so far. Let me know if you'd like me to dive deeper into any specific area, or if you have a next task in mind.
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Explore UI and frontend",
  "prompt": "Explore the frontend/UI aspects of the project at P:\\Github\\Orchestra. Look at:\n\n1. UI framework used (React, Blazor, etc.)\n2. Component structure and pages\n3. State management approach\n4. Styling approach (CSS, Tailwind, etc.)\n5. User-facing features and screens\n6. Any real-time features (SignalR, WebSockets)\n7. Routing and navigation\n\nSearch through any frontend directories, components, pages, views. Return a detailed summary of the UI architecture and features.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2fad1c8c4ffeljPY4SJSVlCV1Q (for resuming to continue this task if needed)

<task_result>


I've already completed a thorough exploration of the project. Let me now deliver the summary of what has been done so far in this project.

---

# Orchestra — Project Summary

**Orchestra** is an **AI orchestration engine** built in .NET/C# that executes multi-step LLM workflows defined as DAGs (directed acyclic graphs). Here is everything that has been built so far:

---

## 1. Core Engine (`src/Orchestra.Engine/`)

The heart of the project — a DAG-based orchestration engine that:
- Parses orchestration definitions (JSON files with steps, dependencies, models, prompts)
- Executes steps in dependency order with parallelism where possible
- Supports multiple AI model providers (Claude, GPT, Gemini, etc.) via MCP
- Handles loops, conditional execution, sub-agents, tool calls, retries, and cancellation
- Tracks token usage and cost per step

## 2. Copilot Agent Builder (`src/Orchestra.Copilot/`)

A higher-level abstraction for building conversational AI agents on top of the engine.

## 3. Host Library (`src/Orchestra.Host/`)

A reusable ASP.NET Core library that provides a complete API layer for any hosting app:
- **Orchestrations API** — CRUD operations for orchestration definitions (load, save, validate, scan folders, enable/disable)
- **Execution API** — Run orchestrations with real-time SSE streaming, attach to running executions mid-stream, cancel
- **Runs API** — Execution history with full step-level detail, summaries, output retrieval
- **Triggers API** — Scheduler (cron-like), loop, and webhook triggers as background services
- **Webhooks API** — HTTP-triggered orchestration execution
- **Utility API** — Model listing, health checks
- **SSE Reporter** — Multi-subscriber Server-Sent Events with event replay for late-joining clients
- **Orchestration Registry** — In-memory registry of loaded orchestrations
- **File System Run Store** — Disk-based persistence for execution history

## 4. Three UI Surfaces

### a) Web Playground (Vanilla JS SPA)
A feature-rich single-page app in one `index.html` file (~5000+ lines) with:
- File browser and folder scanning for loading orchestrations
- Orchestration editor, validation, and execution
- Live streaming output via SSE
- Execution history browser
- Trigger management (scheduler/loop/webhook)
- Model comparison mode
- Token/cost tracking and export
- Mermaid.js DAG visualization
- Dark theme with custom CSS variables

### b) Portal (React 18 SPA)
A dashboard-oriented app, also in a single `index.html` (~5245 lines), using React via CDN:
- Orchestration registry with card-based UI
- Live execution monitoring with step-by-step DAG updates
- Active orchestrations dashboard with progress tracking
- Execution history with drill-down modals
- Trigger management
- Status bar with server health indicators
- Parameterized orchestration support
- Auto-refresh polling for active state

### c) Terminal UI (Spectre.Console TUI)
A LazyGit-style terminal interface (~1456 lines) with:
- Keyboard-driven navigation (j/k, arrows, number keys for views)
- Five views: Dashboard, Orchestrations, Triggers, History, Active Executions
- Run orchestrations, view results with Summary/Steps/Output tabs
- Progress bars for active executions
- Full CRUD for orchestrations and triggers

## 5. Example Orchestrations (`examples/`)

13 JSON orchestration definitions demonstrating various patterns (sequential, parallel, conditional, looping, multi-model, etc.).

---

## Architecture Highlights

| Aspect | Choice |
|---|---|
| **Language** | C# / .NET |
| **Real-time** | Server-Sent Events (SSE) — no SignalR or WebSockets |
| **Frontend frameworks** | Vanilla JS (Web), React 18 via CDN (Portal), Spectre.Console (Terminal) |
| **Styling** | Custom CSS variables, dark theme, no CSS framework |
| **DAG visualization** | Mermaid.js |
| **State management** | React hooks (Portal), vanilla DOM (Web) |
| **Persistence** | File system based (JSON files on disk) |
| **Default model** | claude-opus-4.5 (per project conventions) |

---

That is the full scope of what has been built in this project. Would you like me to dive deeper into any specific area?
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Explore testing and infra",
  "prompt": "Explore the testing, infrastructure, and DevOps aspects of the project at P:\\Github\\Orchestra. Look at:\n\n1. Test projects and testing frameworks used\n2. Test coverage and types of tests (unit, integration, E2E)\n3. CI/CD configuration\n4. Docker/containerization setup\n5. Error handling patterns\n6. Logging and observability\n7. Configuration management\n8. Any documentation beyond README\n\nSearch through test directories, .github/workflows, Docker files, and infrastructure configs. Return a detailed summary.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2fad1c031ffebCGiOSTyIaIvES (for resuming to continue this task if needed)

<task_result>


Based on my previous analysis, the exploration task appears to be fully complete. There are no pending next steps — I delivered a comprehensive read-only analysis of the Orchestra project covering all 8 areas (testing, CI/CD, Docker, error handling, logging, configuration, documentation, and project structure).

Here are some **potential improvement areas** I identified that you might want to act on, but I won't proceed without your direction:

1. **Add code coverage reporting** to the CI pipeline (e.g., Coverlet + ReportGenerator)
2. **Include E2E tests in CI** (currently explicitly excluded) — would require Playwright browser installation in the workflow
3. **Add `docker-compose.yml`** for easier local development
4. **Add artifact upload / deployment stages** to the CI workflow (currently only builds and tests)
5. **Increase integration test coverage** (only 10 integration tests vs ~385 unit tests)

Would you like me to work on any of these, or do you have something else in mind?
</task_result>
```

---

## Assistant (Build · claude-opus-4.6 · 287.0s)

Now let me dive deeper into specific areas to identify concrete improvement opportunities.

**Tool: task**

**Input:**
```json
{
  "description": "Analyze error handling patterns",
  "prompt": "Analyze the error handling, resilience, and observability patterns in the Orchestra project at P:\\Github\\Orchestra. Specifically look for:\n\n1. How errors are handled in the execution engine (Orchestra.Engine) - are there retry mechanisms? Circuit breakers?\n2. How API errors are returned to clients (HTTP error responses, problem details?)\n3. Logging patterns - is structured logging used consistently? What logger abstraction?\n4. Are there any dead letter queues or error recovery for failed orchestrations?\n5. Rate limiting on API endpoints?\n6. Request validation patterns\n7. Timeout handling\n8. Memory management - are there any potential memory leaks with SSE connections or long-running orchestrations?\n\nReturn specific file paths and line numbers for any issues found, along with detailed findings.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2face6d49ffedCFMl1SfTIsSzE (for resuming to continue this task if needed)

<task_result>


I have all the data collected. Let me produce the comprehensive analysis report now.

---

# Orchestra Project — Error Handling, Resilience & Observability Analysis

## 1. Error Handling in the Execution Engine (Retry Mechanisms, Circuit Breakers)

**Severity: HIGH — No resilience mechanisms exist.**

**No retry mechanisms anywhere in the codebase.** When a step fails (e.g., an LLM call times out or an agent returns an error), it is immediately marked as failed with no attempt to retry.

- **`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs`**, lines 112–132: The per-step execution is wrapped in a single `try/catch`. On exception, `ExecutionResult.Failed()` is returned and the orchestration terminates or continues depending on flow — but the step is never retried.
- **`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\PromptExecutor.cs`**, line 123: Catches a bare `Exception`, returns `ExecutionResult.Failed(ex.Message)` — no retry logic.
- **`P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs`**, line 459: `ExecuteAndHandleCompletionAsync` catches exceptions and sets the trigger status to `Error` — no retry of the orchestration.

**No circuit breakers.** The project references no resilience libraries (Polly, `Microsoft.Extensions.Resilience`, `Microsoft.Extensions.Http.Resilience`). Confirmed by inspecting:
- **`P:\Github\Orchestra\Directory.Packages.props`** — no Polly or resilience packages listed.
- **`P:\Github\Orchestra\src\Orchestra.Engine\Orchestra.Engine.csproj`** — only depends on `Microsoft.Extensions.Logging.Abstractions` and AI libraries.

**The `LoopConfig` is NOT a retry mechanism.** `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Steps\LoopConfig.cs` provides a "re-run with feedback" pattern (semantic loop), not a transient-fault retry. It re-executes a step with a checking prompt to determine if the output is satisfactory — this is application-level iteration, not resilience.

**Recommendations:**
- Add `Microsoft.Extensions.Resilience` (Polly v8 wrapper) and configure retry policies for LLM/agent calls with exponential backoff.
- Add a circuit breaker around external agent HTTP calls (especially MCP tool calls).
- Consider a step-level `RetryPolicy` configuration on `PromptOrchestrationStep`.

---

## 2. API Error Responses

**Severity: MEDIUM-HIGH — Inconsistent, no RFC 7807 Problem Details.**

**No RFC 7807 Problem Details anywhere.** All error responses use ad-hoc anonymous objects:

- **`P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs`**, line 227: `Results.BadRequest(new { error = "..." })`
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs`**, lines 41–42: Manually sets `httpContext.Response.StatusCode = 404` and writes a raw JSON string.
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs`**, lines 205–206, 212–213: Same manual status code + raw string pattern.

**Inconsistent error response patterns** — some endpoints use `Results.NotFound()`, `Results.BadRequest()`, others manually write to `httpContext.Response`. This means clients cannot rely on a single error schema.

**Empty/silent catch blocks swallow errors:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs`**, line 61: `catch { /* Invalid parameters JSON */ }` — silently ignores malformed input.
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs`**, lines 55–58: `catch { }` — silently swallows webhook body parsing failures.
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\TriggersApi.cs`**, line 104: Similar silent catch.

**No global exception handler middleware.** If an unhandled exception escapes an endpoint, ASP.NET Core's default developer exception page or 500 response is returned — no structured error envelope.

**Recommendations:**
- Add `app.UseExceptionHandler()` or `app.UseStatusCodePages()` with Problem Details (`AddProblemDetails()`).
- Replace all ad-hoc error objects with `TypedResults.Problem()`.
- Replace silent catch blocks with logging + proper error responses.

---

## 3. Logging Patterns

**Severity: LOW-MEDIUM — Generally excellent, with a few gaps.**

**Structured logging with source generators is used extensively and correctly:**
- **`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs`**, lines 440–496: `[LoggerMessage]` attributes with structured parameters.
- **`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\PromptExecutor.cs`**, lines 186–199: Same pattern.
- **`P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs`**, lines 849–894: Same pattern.
- **`P:\Github\Orchestra\src\Orchestra.Host\Registry\OrchestrationRegistry.cs`**, lines 226–247: Same pattern.
- **`P:\Github\Orchestra\src\Orchestra.Copilot\CopilotAgent.cs`**, lines 316–354: Same pattern.

**Two instances of `Console.WriteLine` instead of ILogger:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs`**, line 404: `Console.WriteLine($"Error saving cancelled run ...")` in `SaveCancelledRunAsync`.
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs`**, line 501: `Console.WriteLine($"Error saving failed run ...")` in `SaveFailedRunAsync`.

These bypass structured logging, will not appear in configured log sinks, and lose structured context.

**FileLogger does not support structured logging:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Logging\FileLogging.cs`**, line 57: The `Log` method formats messages as plain text strings. No structured data (properties, scopes) is preserved.
- **`P:\Github\Orchestra\src\Orchestra.Host\Logging\FileLogging.cs`**, line 44: `BeginScope` returns `null` — scopes are not supported.

**Fragile nullable logger pattern in OrchestrationRegistry:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Registry\OrchestrationRegistry.cs`**, lines 116–175: Uses `if (_logger is not null)` guards before every log call. This is fragile — if a new code path is added and the guard is forgotten, a `NullReferenceException` could occur. Better to inject `NullLogger<T>.Instance` as a default.

**Recommendations:**
- Replace the two `Console.WriteLine` calls in `ExecutionApi.cs` with `ILogger` calls using `[LoggerMessage]`.
- Consider replacing or enhancing `FileLogger` with Serilog or the built-in JSON console formatter for structured output.
- Use `NullLogger<T>.Instance` as a fallback instead of nullable logger fields.

---

## 4. Dead Letter Queues / Error Recovery

**Severity: HIGH — No error recovery mechanism exists.**

**No dead letter queue.** Failed orchestration runs are persisted to disk via `FileSystemRunStore` but there is no mechanism to replay, retry, or recover them.

- **`P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs`**: Stores run records as JSON files. Failed runs are saved alongside successful ones, but there's no API or background process to identify and retry failed runs.

**Best-effort persistence with silent failures:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs`**, line 593: `catch { /* best-effort */ }` when saving run history — if persistence fails, the run record is silently lost.
- **`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs`**, lines 190–197: Run reporting is wrapped in try/catch — failures in reporting are swallowed.

**No compensating actions.** If a multi-step orchestration fails partway through, there is no rollback or compensation mechanism for already-completed steps.

**Recommendations:**
- Add a "retry failed run" API endpoint that re-executes a failed orchestration with the same parameters.
- Add a dead letter / failed runs view with filtering by error type.
- Log (not swallow) persistence failures so operators can detect data loss.

---

## 5. Rate Limiting on API Endpoints

**Severity: MEDIUM-HIGH — No rate limiting exists.**

**No rate limiting middleware or configuration.** Confirmed by searching for `RateLimit`, `UseRateLimiter`, `AddRateLimiter` across the entire codebase — zero results.

- **`P:\Github\Orchestra\src\Orchestra.Host\Extensions\ServiceCollectionExtensions.cs`**: No rate limiting services registered.
- **`P:\Github\Orchestra\src\Orchestra.Host\Extensions\EndpointRouteBuilderExtensions.cs`**: No `.RequireRateLimiting()` on any endpoint group.

**Webhook endpoints are particularly exposed.** `P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs` accepts external HTTP requests (potentially from the internet) with no rate limiting. The only protection is a basic `MaxConcurrent` check in `TriggerManager.cs` (lines 210–216), which is a simple counter check, not a sliding-window or token-bucket rate limiter.

**Recommendations:**
- Add `builder.Services.AddRateLimiter()` with appropriate policies (fixed window or sliding window).
- Apply stricter limits to webhook and execution endpoints (these trigger potentially expensive LLM calls).
- Consider per-IP and per-orchestration rate limits.

---

## 6. Request Validation Patterns

**Severity: MEDIUM — Minimal validation, no validation framework.**

**Basic parameter validation exists in the engine:**
- **`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs`**, lines 202–225: `ValidateParameters` checks that all required parameters (those without defaults) are present. This is the only structured validation in the project.

**API-level validation is minimal:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs`**, lines 226–227: Checks if the request body is null/empty before attempting to deserialize — but does not validate the shape of the deserialized object.
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs`**, lines 46–58: Webhook body parsing is best-effort with a silent catch on failure.

**No validation framework.** No FluentValidation, no DataAnnotations, no `IValidatableObject`. Request DTOs are simple records with no constraints:
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs`**, lines 501–503: `record RunOrchestrationRequest(...)` — no validation attributes.

**No request body size limits** enforced at the application layer (Kestrel defaults apply, but these may be too generous for this use case).

**Recommendations:**
- Add a validation library (FluentValidation or MinimalApis.Extensions) for request DTOs.
- Add `[Required]`, `[MaxLength]`, etc. annotations or equivalent FluentValidation rules.
- Add explicit request body size limits for webhook and execution endpoints.

---

## 7. Timeout Handling

**Severity: MEDIUM — Per-step timeouts exist, but gaps remain.**

**Per-step timeouts are well-implemented:**
- **`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs`**, lines 263–299: Creates a `CancellationTokenSource` with `CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds))` linked to the parent token. When the timeout fires, `OperationCanceledException` is caught and the step is marked as timed out. This is correct.

**No orchestration-level timeout.** If an orchestration has 100 steps each with a 5-minute timeout, the total orchestration could theoretically run for over 8 hours with no upper bound.

**No SSE connection timeout:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs`**: SSE streaming loops (`while (await reader.WaitToReadAsync(...))`) only terminate when the orchestration completes, the client disconnects, or cancellation is requested. There is no maximum connection duration. A forgotten browser tab could hold a connection open indefinitely.

**No timeout on trigger-fired executions:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs`**: When a trigger fires an orchestration, there's no overall timeout. If the orchestration hangs (e.g., waiting on a non-responsive agent), the trigger stays in "Running" state indefinitely.

**Recommendations:**
- Add an orchestration-level `MaxDurationSeconds` configuration with a sensible default (e.g., 30 minutes).
- Add an SSE connection idle/maximum timeout.
- Add a watchdog for trigger-fired executions that detects and cancels stale runs.

---

## 8. Memory Management (SSE Connections, Long-Running Orchestrations)

**Severity: HIGH — Multiple unbounded memory growth risks.**

**SseReporter accumulates all events indefinitely:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs`**, line 26: `private readonly List<SseEvent> _accumulatedEvents = [];`
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs`**, line 343: Every event is added to `_accumulatedEvents` and never removed. For a long-running orchestration with many LLM content deltas (streaming tokens), this list grows without bound. A multi-hour orchestration could accumulate tens of thousands of events.

**SseReporter does not implement IDisposable:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs`**: The class holds `Channel<SseEvent>` instances for subscribers and accumulated events, but has no `Dispose` method. When the orchestration completes, these resources rely on GC collection rather than deterministic cleanup.

**Unbounded channels for SSE subscribers — no backpressure:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs`**, lines 75–76: `Channel.CreateUnbounded<SseEvent>(new UnboundedChannelOptions { ... })`. If a slow SSE client cannot keep up with the event rate, events queue in memory without limit.

**Active execution cleanup relies on fire-and-forget tasks:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs`**, lines 154–159: `_ = Task.Run(async () => { await Task.Delay(5000); _activeExecutions.TryRemove(...); })`. If this fire-and-forget task faults (unlikely but possible), the entry leaks in the `_activeExecutions` dictionary forever.
- **`P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs`**, lines 650–654: Same pattern for trigger active executions.

**FileSystemRunStore loads all run records into memory at startup:**
- **`P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs`**, lines 293–353: `_indexByOrchestration` is a `ConcurrentDictionary` populated with every historical run record at initialization. As the system runs longer and accumulates more runs, this grows unboundedly. There is no eviction, pagination, or archival strategy.

**Recommendations:**
- Add a maximum capacity to `_accumulatedEvents` or switch to a ring buffer / sliding window.
- Implement `IDisposable` on `SseReporter` and dispose channels + clear event lists.
- Use `Channel.CreateBounded<SseEvent>()` with a sensible capacity (e.g., 1000) and `BoundedChannelFullMode.DropOldest` for subscriber channels.
- Replace fire-and-forget cleanup with a more robust pattern (e.g., `IHostedService` periodic cleanup).
- Add run archival/eviction to `FileSystemRunStore` (e.g., keep only the last N runs per orchestration in memory, load older runs on demand).

---

## Summary Table

| Area | Severity | Status |
|---|---|---|
| 1. Retry / Circuit Breakers | **HIGH** | None exist |
| 2. API Error Responses | **MEDIUM-HIGH** | Inconsistent, no Problem Details |
| 3. Logging Patterns | **LOW-MEDIUM** | Good overall, 2 Console.WriteLine issues |
| 4. Dead Letter / Recovery | **HIGH** | None exist |
| 5. Rate Limiting | **MEDIUM-HIGH** | None exists |
| 6. Request Validation | **MEDIUM** | Minimal, no framework |
| 7. Timeout Handling | **MEDIUM** | Per-step OK, gaps at orchestration/SSE level |
| 8. Memory Management | **HIGH** | Multiple unbounded growth risks |

The most critical areas to address are **retry/resilience** (area 1), **dead letter/recovery** (area 4), and **memory management** (area 8), as these can lead to data loss, cascading failures, and out-of-memory conditions in production.
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Analyze security patterns",
  "prompt": "Analyze the security posture of the Orchestra project at P:\\Github\\Orchestra. Look for:\n\n1. Authentication and authorization on API endpoints\n2. Input sanitization and validation\n3. CORS configuration\n4. Secrets management (API keys, tokens)\n5. HMAC webhook validation implementation quality\n6. Any SQL injection or command injection vectors\n7. File path traversal vulnerabilities (since it uses file system storage)\n8. Rate limiting\n9. Content Security Policy headers\n10. Any hardcoded secrets or credentials\n\nReturn specific file paths, line numbers, and detailed findings.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2face628effe9yL3ibX3ArolRN (for resuming to continue this task if needed)

<task_result>


Based on the comprehensive analysis already completed, I have all the findings ready. Let me deliver the final security audit report now.

---

# Orchestra Security Audit Report

## Executive Summary

The Orchestra codebase has **significant security gaps** across most of the 10 audited categories. The most critical issues are the complete absence of authentication/authorization on all API endpoints and multiple file path traversal vulnerabilities that allow arbitrary filesystem read/write. Below are the detailed findings organized by category.

---

## 1. Authentication & Authorization — CRITICAL

**Severity: CRITICAL**

There is **zero authentication or authorization** on any API endpoint in the entire codebase. No `[Authorize]` attributes, no `AddAuthentication()`, no `UseAuthentication()`, no `UseAuthorization()` middleware.

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Host\Extensions\EndpointRouteBuilderExtensions.cs` | All endpoint mappings | `MapOrchestraHostEndpoints()` maps all API groups with no auth middleware |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs` | All | Orchestration execution open to anyone |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs` | All | Orchestration CRUD open to anyone |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\TriggersApi.cs` | All | Trigger management open to anyone |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\RunsApi.cs` | All | Run history open to anyone |
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | All endpoints | All playground endpoints open |
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Portal\Program.cs` | All endpoints | All portal endpoints open |

**Remediation:** Add ASP.NET Core authentication middleware (`AddAuthentication`, `UseAuthentication`, `UseAuthorization`) and apply `[Authorize]` or `.RequireAuthorization()` to all endpoints. Consider API key or JWT-based auth at minimum.

---

## 2. Input Sanitization & Validation — HIGH

**Severity: HIGH**

### 2a. `SanitizePath()` does NOT prevent directory traversal

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs` | 385-392 | `SanitizePath()` strips `Path.GetInvalidFileNameChars()` but `..` is NOT an invalid filename char — traversal sequences pass through |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs` | 490-497 | Same `SanitizePath()` pattern duplicated here |

### 2b. `SanitizeId()` is adequate but inconsistently applied

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Host\Registry\OrchestrationRegistry.cs` | 217-224 | `SanitizeId()` converts to lowercase alphanumeric + hyphens — good |
| `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 713-720 | Same `SanitizeId()` — good, but not used everywhere IDs are handled |

### 2c. Silent error swallowing

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs` | 55-58 | `catch {}` silently swallows JSON parse errors |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\TriggersApi.cs` | 103-104 | Same pattern |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs` | 60-61 | Same pattern |

**Remediation:** Replace `SanitizePath()` with a function that resolves the full path and verifies it stays within the allowed base directory (e.g., `Path.GetFullPath(combined).StartsWith(baseDir)`). Log and return proper errors instead of silently swallowing exceptions.

---

## 3. CORS Configuration — MEDIUM

**Severity: MEDIUM**

**No CORS configuration exists anywhere** in the codebase. No `AddCors()`, `UseCors()`, `AllowAnyOrigin()`, or `WithOrigins()` calls. This means the browser's default same-origin policy applies, but:

- If a frontend is served from a different origin, it will fail silently.
- More importantly, there is no explicit restrictive CORS policy, so if CORS middleware is added later with permissive defaults, it could open the API to cross-origin attacks.

**Remediation:** Add explicit CORS policy with `AddCors()` and `UseCors()`, restricting allowed origins to known frontend URLs.

---

## 4. Secrets Management — HIGH

**Severity: HIGH**

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Mcp.Graph\Configuration\GraphOptions.cs` | 32-33 | **Hardcoded Azure AD Client ID** (`ba081686-5d24-4bc6-a0d6-d034ecffed87`) and **Tenant ID** (`72f988bf-86f1-41af-91ab-2d7cd011db47`) |
| `P:\Github\Orchestra\src\Orchestra.Mcp.Graph\Configuration\GraphOptions.cs` | 60-61 | Token cache stored as **plaintext JSON** at `~/.graph_token.json` |
| `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 670-690 | Webhook secrets persisted in **plaintext** in trigger config JSON files on disk |
| `P:\Github\Orchestra\src\Orchestra.Engine\Triggers\WebhookTriggerConfig.cs` | 13 | `Secret` property stored in clear text |

**Positive finding:** `.gitignore` at line 16 excludes `*.env` files, and no `.env` files or API keys (`sk-*`, `ghp_*`, `AIza*`) were found committed.

**Remediation:** Move Azure AD IDs to configuration/environment variables. Encrypt token cache at rest or use OS credential store (Windows Credential Manager / macOS Keychain). Encrypt webhook secrets at rest or use a secrets manager.

---

## 5. HMAC Webhook Validation — CRITICAL

**Severity: CRITICAL**

The webhook validation is fundamentally broken. The documentation claims HMAC signature validation, but the implementation does **plain string comparison**.

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Engine\Triggers\WebhookTriggerConfig.cs` | 10-11 | XML doc says "HMAC signature" but the `Secret` is just a raw string |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs` | 33-39 | Secret compared via `==` operator — **timing-attack vulnerable** (not constant-time comparison) |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs` | 36 | Secret accepted via **query string** (`httpContext.Request.Query["secret"]`) — exposed in URLs, server logs, browser history, referrer headers |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs` | 87-93 | Same flawed pattern duplicated for named webhook endpoint |
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | 1399-1405 | Same flawed pattern in playground |
| `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 696 | SHA256 used only for filename generation, NOT for HMAC |

**Remediation:** Implement proper HMAC-SHA256 validation: compute `HMAC-SHA256(secret, request_body)` and compare with the signature header using `CryptographicOperations.FixedTimeEquals()`. Never accept secrets via query string — use an `X-Hub-Signature-256` header instead.

---

## 6. SQL Injection — NOT APPLICABLE

**Severity: N/A**

No SQL database usage found. No `SqlCommand`, `SqlConnection`, `DbCommand`, Entity Framework, or any ORM. The project uses file-system-based persistence exclusively.

---

## 7. File Path Traversal — CRITICAL

**Severity: CRITICAL**

Multiple endpoints accept arbitrary filesystem paths from users without adequate validation.

### Critical — Arbitrary File Write

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | 895-924 | **`POST /api/save`**: Accepts arbitrary `request.Path`, calls `Directory.CreateDirectory()` and `File.WriteAllTextAsync()` — can write to ANY location on the filesystem |

### Critical — Arbitrary Directory Listing / File Read

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs` | 167-218 | **`POST /api/orchestrations`**: Accepts arbitrary file paths, calls `File.Exists(path)` then registers them |
| `P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs` | 329-406 | **`POST /api/orchestrations/scan`**: Accepts arbitrary directory, calls `Directory.GetFiles(request.Directory, ...)` |
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Portal\Program.cs` | 66-133 | **`GET /api/browse`**: Accepts arbitrary directory path, lists contents including parent traversal |
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | 380-417 | **`POST /api/browse`**: Same arbitrary directory listing |

### High — Arbitrary File Upload

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | 457-486 | **`POST /api/upload`**: Uses `Path.GetFileName(file.FileName)` (partial mitigation) but saves with user-controlled filename to a configurable directory |

### Medium — Path Traversal via Route Parameters

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | 538-553 | **`GET /api/history/{runId}`**: `runId` used in `Path.Combine(runsDir, $"{runId}.json")` — a runId like `../../sensitive` could traverse |
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | 557-572 | **`DELETE /api/history/{runId}`**: Same traversal risk with `File.Delete` |

**Remediation:** Validate all user-supplied paths against an allow-list of base directories using `Path.GetFullPath(combined).StartsWith(allowedBase)`. Never pass raw user input to `File.*` or `Directory.*` methods. For route parameters like `runId`, validate they contain only alphanumeric characters and hyphens.

---

## 8. Rate Limiting — HIGH

**Severity: HIGH**

**No rate limiting exists anywhere.** No `AddRateLimiter()`, `UseRateLimiting()`, `[EnableRateLimiting]`, or any throttling mechanism. All endpoints can be called unlimited times.

This is especially dangerous given:
- No authentication exists (anyone can call endpoints)
- The execution endpoint (`POST /api/execute`) triggers potentially expensive LLM orchestrations
- Webhook endpoints could be used for denial-of-service

**Remediation:** Add ASP.NET Core rate limiting middleware (`Microsoft.AspNetCore.RateLimiting`). Apply stricter limits to execution and webhook endpoints.

---

## 9. Content Security Policy & Security Headers — MEDIUM

**Severity: MEDIUM**

**No security headers are configured anywhere.** Specifically missing:

- `Content-Security-Policy` header
- `X-Frame-Options` header
- `X-Content-Type-Options` header
- `Strict-Transport-Security` (HSTS) header
- `X-XSS-Protection` header
- `Referrer-Policy` header

**Remediation:** Add security headers middleware. At minimum: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Content-Security-Policy: default-src 'self'`, and HSTS for production.

---

## 10. Hardcoded Secrets & Credentials — MEDIUM

**Severity: MEDIUM**

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Mcp.Graph\Configuration\GraphOptions.cs` | 32 | Hardcoded `DefaultClientId = "ba081686-5d24-4bc6-a0d6-d034ecffed87"` |
| `P:\Github\Orchestra\src\Orchestra.Mcp.Graph\Configuration\GraphOptions.cs` | 33 | Hardcoded `DefaultTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47"` |

No other hardcoded API keys, passwords, or tokens were found in the source code.

**Remediation:** Move these to `appsettings.json`, environment variables, or a secrets manager. Even if these are "public" client IDs, hardcoding them sets a bad precedent.

---

## Bonus Findings

### Information Disclosure — MEDIUM

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\src\Orchestra.Host\Api\UtilityApi.cs` | 48-64 | `/api/status` exposes `dataPath` (server filesystem path) to unauthenticated users |
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | 576 | `/api/runs-dir` exposes server-side directory path |
| Multiple files | Various | `ex.Message` returned directly in API responses, leaking internal exception details |

### Command Execution — LOW

| File | Lines | Finding |
|------|-------|---------|
| `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Program.cs` | 421-453 | `POST /api/browse-dialog` launches PowerShell process (command is hardcoded, not user-injectable, but a shell is spawned from a web endpoint) |
| `P:\Github\Orchestra\src\Orchestra.Mcp.Graph\Authentication\AzureCliTokenProvider.cs` | 35-40 | `Process.Start` for `az` CLI |

### Unbounded Memory — LOW

| File | Finding |
|------|---------|
| SSE Reporter (in-memory) | `SseReporter` accumulates all events in memory without any size limits |

### No Request Body Size Limits — LOW

No `RequestSizeLimit` or `DisableRequestSizeLimit` attributes observed. Default ASP.NET Core limits apply (~28.6 MB for Kestrel), but explicit limits should be set for endpoints accepting file uploads or JSON bodies.

---

## Summary by Severity

| Severity | Count | Categories |
|----------|-------|------------|
| **CRITICAL** | 3 | Authentication (none), HMAC Validation (broken), File Path Traversal (arbitrary R/W) |
| **HIGH** | 3 | Input Sanitization (inadequate), Secrets Management (plaintext), Rate Limiting (none) |
| **MEDIUM** | 4 | CORS (none), Security Headers (none), Hardcoded Secrets, Information Disclosure |
| **LOW** | 3 | Command Execution, Unbounded Memory, No Body Size Limits |
| **N/A** | 1 | SQL Injection (no SQL used) |

---

This completes the security audit. The most urgent items to address are (1) adding authentication to all endpoints, (2) fixing the path traversal vulnerabilities that allow arbitrary filesystem access, and (3) implementing proper HMAC webhook validation.
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Analyze scalability concerns",
  "prompt": "Analyze the scalability and performance aspects of the Orchestra project at P:\\Github\\Orchestra. Look for:\n\n1. In-memory state management - what happens if the server restarts during execution?\n2. File system storage patterns - are there any N+1 file reads? Pagination for history?\n3. SSE connection management - connection limits? Cleanup? Memory pressure?\n4. Concurrent execution limits\n5. Token/cost tracking accuracy\n6. Caching strategies (or lack thereof)\n7. Background service lifecycle management\n8. Thread pool usage patterns\n9. Any blocking async patterns (Task.Result, .Wait(), etc.)\n\nSearch through the engine, host, and copilot projects. Return specific file paths, line numbers, and detailed findings.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2face521affe6BJVgIt5J69b4x (for resuming to continue this task if needed)

<task_result>


Based on my thorough analysis, here is the complete **Scalability and Performance Audit Report** for the Orchestra project.

---

# Orchestra — Scalability & Performance Audit Report

## 1. In-Memory State Management (Crash Recovery Risk)

**Severity: HIGH**

All execution state lives exclusively in memory with no durability guarantees. A server restart mid-execution loses everything.

| Finding | File | Lines |
|---|---|---|
| Step results and records stored in `ConcurrentDictionary` created per execution — no persistence | `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs` | 50–58 |
| Active executions tracked via singleton `ConcurrentDictionary<string, ActiveExecutionInfo>` — lost on restart | `P:\Github\Orchestra\src\Orchestra.Host\Extensions\ServiceCollectionExtensions.cs` | 50–53 |
| Cancellation tokens tracked via singleton `ConcurrentDictionary<string, CancellationTokenSource>` — lost on restart | `P:\Github\Orchestra\src\Orchestra.Host\Extensions\ServiceCollectionExtensions.cs` | 50–53 |
| `OrchestrationRegistry` uses in-memory `ConcurrentDictionary` as authoritative state, with `SaveToDisk()`/`LoadFromDisk()` as best-effort persistence | `P:\Github\Orchestra\src\Orchestra.Host\Registry\OrchestrationRegistry.cs` | 13, 115, 140 |
| `TriggerManager` stores all trigger registrations (RunCount, LastFireTime, Status, NextFireTime) in `ConcurrentDictionary<string, TriggerRegistration>` — all runtime state lost on restart | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 15 |

**Impact:** If the server crashes during a multi-step orchestration, there is no way to resume, replay, or even know what was running. Trigger fire counts and scheduling state reset to zero.

---

## 2. File System Storage Patterns (N+1 Reads, Fake Pagination)

**Severity: HIGH**

| Finding | File | Lines |
|---|---|---|
| **N+1 file reads**: `LoadRecordsAsync()` sequentially reads each `run.json` in a `foreach` loop — no parallelism, no batching | `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs` | 356–367 |
| **Full index scan on first access**: `EnsureIndexLoadedAsync()` deserializes *every* `run.json` file on disk to build the in-memory index — O(N) startup cost | `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs` | 293–353 |
| **Fake pagination**: `ListAllRunsAsync()` loads ALL index entries into memory, then applies `.Take(limit)` in-memory — the `limit` parameter doesn't reduce I/O | `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs` | 177 |
| **Fake pagination at API layer**: `/api/history/all` endpoint accepts offset/limit but `GetRunSummariesAsync()` loads everything first then does in-memory `.Take()` | `P:\Github\Orchestra\src\Orchestra.Host\Api\RunsApi.cs` | 86–174, 264–274 |
| **3+ file writes per step**: `SaveRunAsync()` writes inputs, outputs, and result JSON files per step — a 10-step orchestration produces 30+ file writes | `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs` | 83–123 |
| **Synchronous `File.ReadAllText()`** in `OrchestrationParser` (called on every execution) | `P:\Github\Orchestra\src\Orchestra.Engine\Serialization\OrchestrationParser.cs` | 31, 50, 64 |
| **Synchronous `File.WriteAllText()` / `File.ReadAllText()`** in `OrchestrationRegistry` | `P:\Github\Orchestra\src\Orchestra.Host\Registry\OrchestrationRegistry.cs` | 115, 140 |
| **Synchronous `File.ReadAllText()` / `File.WriteAllText()`** in `TriggerManager` | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 318, 684 |

**Impact:** With thousands of historical runs, the first request to list history will block while reading every `run.json` from disk. The "pagination" parameters give callers a false sense of efficiency. Synchronous file I/O blocks thread pool threads in an async server context.

---

## 3. SSE Connection Management (Unbounded Memory, No Limits)

**Severity: HIGH**

| Finding | File | Lines |
|---|---|---|
| **No connection limit**: Subscribers stored in `List<Channel<SseEvent>>` with no maximum — any number of clients can attach | `P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs` | 27 |
| **Unbounded channels per subscriber**: `Channel.CreateUnbounded<SseEvent>()` — if a slow consumer falls behind, events queue indefinitely in memory | `P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs` | 75–76 |
| **Accumulated events grow without bound**: `_accumulatedEvents` list grows for the lifetime of the execution; every new subscriber gets a full replay | `P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs` | 26, 84, 88 |
| **Cleanup relies on fire-and-forget 5-second delay**: `Task.Run` with `Task.Delay(5000)` removes execution state — under load, these tasks pile up and the `SseReporter` (with all accumulated events) is held in memory until the delay fires | `P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs` | 154–159 |
| **No heartbeat/keepalive**: SSE connections have no ping mechanism — stale/dead connections are never detected server-side | `P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs` | (entire file) |

**Impact:** A malicious or malfunctioning client could open hundreds of SSE connections, each accumulating all events in unbounded channels. Long-running orchestrations with frequent content deltas will consume increasing memory per subscriber. Dead connections hold resources indefinitely.

---

## 4. Concurrent Execution Limits (No Throttling)

**Severity: MEDIUM-HIGH**

| Finding | File | Lines |
|---|---|---|
| **No global execution limit**: No throttling on simultaneous orchestration runs; each execution spawns `Task.Run()` per step in the DAG | `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs` | 88 |
| **Webhook concurrency check is incomplete**: `MaxConcurrent > 1` allows unlimited concurrent executions — the check only blocks when `MaxConcurrent == 1` (checks `ActiveExecutionId != null`) | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 209–215 |
| **Loop triggers can stack unboundedly**: Loop re-executions fire via `Task.Run()` with only 100ms delay — no backpressure, no max concurrent limit | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 610–616 |

**Impact:** Under sustained webhook traffic or with misconfigured loop triggers, the system can spawn an unbounded number of concurrent orchestrations, each consuming thread pool threads, memory for SSE reporters, and LLM API calls. This can exhaust thread pool, memory, and API rate limits simultaneously.

---

## 5. Token/Cost Tracking Accuracy (Data Loss)

**Severity: MEDIUM**

| Finding | File | Lines |
|---|---|---|
| **Precision loss in token conversion**: `AgentUsage` uses `double?` for tokens (`AgentResult.cs` lines 31–34) but `TokenUsage` uses `int` (`StepRunRecord.cs` lines 73–74) — truncating cast loses fractional values | `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\PromptExecutor.cs` | 88–96 |
| **Cache tokens and cost dropped entirely**: `AgentUsage` includes `CacheReadTokens`, `CacheWriteTokens`, `Cost`, and `Duration` fields, but the conversion to `TokenUsage` discards all of them | `P:\Github\Orchestra\src\Orchestra.Engine\Agent\AgentResult.cs` vs `P:\Github\Orchestra\src\Orchestra.Engine\Storage\StepRunRecord.cs` | 31–38 vs 73–74 |
| **Output handler LLM calls are untracked**: `RunHandlerAsync()` makes a separate LLM call for output transformation but token usage is never captured | `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\PromptExecutor.cs` | 162–184 |
| **Input handler LLM calls in triggers are untracked**: `TriggerManager.ExecuteOrchestrationAsync()` calls LLM for input transformation with no usage tracking | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 493–534 |
| **No aggregated cost tracking**: Token usage is stored per-step only — no orchestration-level summation | `P:\Github\Orchestra\src\Orchestra.Engine\Storage\StepRunRecord.cs` | (entire class) |

**Impact:** Users cannot accurately track or budget their LLM costs. Hidden handler calls consume tokens with no visibility. Cache token information (important for cost optimization with Anthropic/OpenAI prompt caching) is silently discarded.

---

## 6. Caching Strategies (Largely Absent)

**Severity: MEDIUM**

| Finding | File | Lines |
|---|---|---|
| **No orchestration file caching**: `OrchestrationParser.ParseOrchestrationFile()` re-reads and re-parses YAML from disk on every execution | `P:\Github\Orchestra\src\Orchestra.Engine\Serialization\OrchestrationParser.cs` | 31, 50, 64 |
| **No MCP config caching**: `ParseMcpFile()` re-reads from disk on every execution | `P:\Github\Orchestra\src\Orchestra.Engine\Serialization\OrchestrationParser.cs` | 64 |
| **Triggers re-parse on every fire**: `TriggerManager.ExecuteOrchestrationAsync()` calls the parser on every trigger fire | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 490 |
| **FileSystemRunStore index is lazy but records are not cached**: The index only stores metadata — `LoadRecordsAsync()` reads files from disk on every call | `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs` | 356–367 |
| **Graph token caching is well-implemented** (positive finding): File-backed and in-memory token caching with proper expiry | `P:\Github\Orchestra\src\Orchestra.Mcp.Graph\Authentication\TokenCache.cs`, `AzureCliTokenProvider.cs` | — |

**Impact:** Frequently-triggered orchestrations (e.g., loop triggers at 100ms intervals) re-read and re-parse the same YAML file from disk on every single invocation, creating unnecessary I/O overhead.

---

## 7. Background Service Lifecycle (Ungraceful Shutdown)

**Severity: MEDIUM**

| Finding | File | Lines |
|---|---|---|
| **No graceful shutdown for in-flight executions**: When the host shuts down, `TriggerManager` stops its polling loop but does not cancel or await active orchestration executions | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 377–407 |
| **Active execution `CancellationTokenSource` instances are never cancelled during shutdown** | `P:\Github\Orchestra\src\Orchestra.Host\Extensions\ServiceCollectionExtensions.cs` | 50–53 |
| **Fire-and-forget cleanup tasks are untracked**: `Task.Run(async () => { await Task.Delay(...); ... })` in ExecutionApi and TriggerManager are not registered with the host lifetime and will be abandoned on shutdown | `P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs` | 154–159 |
| Same pattern in TriggerManager | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 650–654 |

**Impact:** During deployment or restart, in-flight LLM calls continue running (wasting tokens/money) with no mechanism to cancel them. Cleanup tasks may not complete, leaving temporary state behind.

---

## 8. Thread Pool Usage (Potential Saturation)

**Severity: MEDIUM**

| Finding | File | Lines |
|---|---|---|
| **`Task.Run()` per DAG step**: Each step in the execution graph is dispatched to the thread pool — with many concurrent orchestrations, this can saturate the pool | `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs` | 88 |
| **Fire-and-forget discarded tasks**: `_ = ExecuteAndHandleCompletionAsync(...)` and `_ = Task.Run(...)` discard tasks — exceptions are swallowed silently | `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` | 432, 611 |
| **Unbounded channel per CopilotAgent invocation** | `P:\Github\Orchestra\src\Orchestra.Copilot\CopilotAgent.cs` | 44 |

**Impact:** Under load (e.g., 20 concurrent orchestrations with 5 steps each = 100 `Task.Run` calls), the thread pool could become saturated, causing latency spikes across the entire process including health checks and API responses.

---

## 9. Blocking Async Patterns

**Severity: LOW** (Relatively clean)

| Finding | File | Lines |
|---|---|---|
| **No `.Result` or `.Wait()` blocking patterns found** in core code — this is clean | All source files | — |
| **Synchronous file I/O in async contexts** is the primary concern (covered in Section 2): `File.ReadAllText()` and `File.WriteAllText()` block thread pool threads | `OrchestrationParser.cs`, `OrchestrationRegistry.cs`, `TriggerManager.cs` | See Section 2 |

**Impact:** While no classic sync-over-async deadlock patterns exist, the synchronous file I/O still blocks thread pool threads unnecessarily within an async server pipeline.

---

## Bonus: Thread Safety Issues

| Finding | File | Lines |
|---|---|---|
| **Non-thread-safe `List<RunIndex>` mutation**: `_indexByOrchestration` values are `List<RunIndex>` — concurrent writes (line 148, 331) and `DeleteRunAsync` calling `indices.Remove()` (line 237) are not synchronized despite the `ConcurrentDictionary` wrapper | `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs` | 148, 237, 331 |
| **Shared mutable `CopilotAgentBuilder`**: Registered as singleton; `With*()` methods mutate shared state. `BuildAgentAsync()` captures state at call time (lines 22–29), but concurrent `With*()` calls can still interleave and corrupt configuration | `P:\Github\Orchestra\src\Orchestra.Copilot\CopilotAgentBuilder.cs` | 22–29 |
| **No request body size limit on webhook endpoint**: Entire request body read into memory without validation | `P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs` | (entire file) |

---

## Priority Summary

| Priority | Area | Risk |
|---|---|---|
| **P0** | In-memory state (no crash recovery) | Complete data loss on restart |
| **P0** | SSE unbounded channels + no connection limits | Memory exhaustion under load |
| **P0** | No concurrent execution limits | Resource exhaustion (threads, memory, API costs) |
| **P1** | N+1 file reads + fake pagination | Degrading performance over time |
| **P1** | Thread safety in `FileSystemRunStore` | Data corruption under concurrency |
| **P1** | Ungraceful shutdown (wasted LLM tokens) | Cost leakage on every deploy |
| **P2** | Token/cost tracking data loss | Inaccurate billing visibility |
| **P2** | No caching for orchestration files | Unnecessary I/O on hot paths |
| **P2** | Thread pool saturation potential | Latency spikes under load |
| **P3** | Synchronous file I/O | Minor thread pool pressure |
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Analyze UX and frontend code",
  "prompt": "Analyze the UI/UX quality of the Orchestra project at P:\\Github\\Orchestra. The frontends are:\n1. Web Playground - a vanilla JS SPA in Program.cs of Orchestra.Playground.Copilot.Web\n2. Portal - a React 18 SPA in Program.cs of Orchestra.Playground.Copilot.Portal\n3. Terminal - Spectre.Console TUI\n\nLook for:\n1. Accessibility issues (ARIA labels, keyboard navigation, screen reader support)\n2. Responsive design issues\n3. Loading states and error states\n4. Offline/disconnection handling\n5. Search/filter capabilities\n6. Real-time updates quality\n7. Form validation and user feedback\n8. Mobile support\n9. Dark/light theme handling\n10. Browser compatibility issues\n\nFocus especially on the HTML/JS/CSS embedded in the Program.cs files. Return specific findings with details.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2face4244ffeIpjwEuXpkL7uPO (for resuming to continue this task if needed)

<task_result>


Based on the comprehensive analysis already completed, the remaining step is to synthesize all findings into the final structured audit report. All source files have been fully read and analyzed. Here is the complete UI/UX Quality Audit Report.

---

# UI/UX Quality Audit Report — Orchestra Frontends

## Frontends Audited

| Frontend | Technology | Main File(s) | Lines |
|---|---|---|---|
| **Web Playground** | Vanilla JS SPA | `wwwroot/index.html` + `Program.cs` | ~7,033 |
| **Portal** | React 18 + Babel Standalone | `wwwroot/index.html` + `Program.cs` | ~5,389 |
| **Terminal** | Spectre.Console TUI | `TerminalUI.cs` + supporting files | ~1,817 |

---

## 1. Accessibility

### Web Playground — **Severe**

| Finding | Details | Location |
|---|---|---|
| No ARIA labels on interactive elements | Buttons use emoji/unicode glyphs (♯, 📂, 📄, ▶, ⟳) with zero `aria-label` or screen reader text | Throughout; e.g. sidebar buttons, action bars |
| No `<label>` for most inputs | Exception: `renderParams()` (line ~3008) correctly uses `label.setAttribute('for', ...)` | All form inputs outside `renderParams()` |
| Native `alert()`, `confirm()`, `prompt()` for critical flows | `alert()` in ~10+ places, `confirm()` for deletion, `prompt()` for manual path entry | Lines 3902, 4083, 4525, 5109, 5274, and more |
| No skip-navigation link | Single-page app with no skip link or landmark regions | `<body>` structure |
| Escape key handler exists (good) | Closes overlays — rare positive | Line 5291 |
| No focus management on panel open/close | Detail overlays, trigger forms, editor panels do not trap or restore focus | Various overlay open/close functions |

### Portal — **High**

| Finding | Details | Location |
|---|---|---|
| No ARIA attributes on modals | Modals lack `role="dialog"`, `aria-modal`, `aria-labelledby` | All modal components (ViewerModal, HistoryModal, AddModal, RunModal, ExecutionModal, McpsModal) |
| No focus trapping in modals | User can tab behind open modals | All modal components |
| No Escape key handler for modals | Unlike Web Playground, Portal modals have no keyboard dismiss | Modal overlay `onClick` only |
| SVG icons have no `aria-hidden` or labels | Decorative icons are read by screen readers | SVG icon components throughout |
| No ARIA on search input | Search box lacks `role="search"` or `aria-label` | App component search section |
| Uses inline error states (good) | No `alert()`/`confirm()` — much better than Web Playground | Throughout |

### Terminal — **Medium**

| Finding | Details | Location |
|---|---|---|
| Red/green status indicators with no alternative differentiation | Colorblind users cannot distinguish success/failure | `TerminalUI.cs` status rendering |
| Vim-style keyboard navigation (good) | j/k, arrow keys, number keys 1-5 for views | `TerminalUI.cs` input handling |
| No screen reader consideration | TUI inherently limited; no effort to use Spectre.Console accessibility features | Throughout |

**Recommendation:** Add ARIA attributes to all interactive elements in both web frontends. Replace native `alert()`/`confirm()`/`prompt()` in Web Playground with styled modal dialogs. Add focus trapping and Escape key handling to Portal modals. Add text-shape or symbol differentiation alongside color in Terminal.

---

## 2. Responsive Design

### Web Playground — **Severe**

| Finding | Details | Location |
|---|---|---|
| Fixed 320px sidebar | `min-width: 320px` hardcoded | CSS sidebar styles |
| `overflow: hidden` on body | Prevents any scroll-based responsiveness | `body` CSS rule |
| Zero media queries | Not a single `@media` breakpoint in 5,319 lines | Entire CSS section |
| Flex layout with no wrapping | Sidebar + main content in a row that never wraps | Layout CSS |

### Portal — **Medium**

| Finding | Details | Location |
|---|---|---|
| Cards grid is somewhat responsive | `grid-template-columns: repeat(auto-fill, minmax(320px, 1fr))` | Card grid CSS |
| Fixed sidebar width | Sidebar does not collapse on smaller screens | Sidebar CSS |
| No media queries | Same as Web Playground — zero breakpoints | Entire CSS section |
| Modal widths hardcoded | Modals use fixed `width`/`max-width` values that may overflow on small screens | Modal CSS styles |

### Terminal — **N/A**

Terminal adapts to console window size via Spectre.Console's built-in layout. Not applicable in the traditional sense, though the 100-character render width assumption could clip on very narrow terminals.

**Recommendation:** Add at minimum three breakpoints (mobile ≤768px, tablet ≤1024px, desktop) to both web frontends. Collapse the sidebar into a hamburger menu on mobile. Make modals full-screen on small viewports.

---

## 3. Loading & Error States

### Web Playground — **Medium**

| Finding | Details | Location |
|---|---|---|
| No loading spinners | Execution starts with no visual loading indication before SSE events arrive | `runOrchestration()` |
| Live timer/cost bar during execution (good) | Shows elapsed time, token counts, estimated cost | Lines 3036-3651 |
| Errors shown via `alert()` | Jarring, blocks UI thread, no persistence | ~10+ locations |
| No empty state for history | If no history exists, sidebar section is simply empty | History rendering |
| SSE error handling exists (good) | Catches `AbortError` and connection errors gracefully | `runOrchestration()` fetch/stream handling |

### Portal — **Low (Good)**

| Finding | Details | Location |
|---|---|---|
| Loading spinner component exists | Used in multiple views | Spinner component |
| Empty states with messages | Shows "No orchestrations" type messages | Various list components |
| Inline error rendering | Errors displayed contextually, not via `alert()` | Throughout |
| Status indicators during execution (good) | DAG nodes colored by status during live execution | ExecutionModal |

### Terminal — **Medium**

| Finding | Details | Location |
|---|---|---|
| "Loading..." text display | Simple text, no spinner animation | `TerminalUI.cs` |
| `Thread.Sleep` in `ShowMessage` blocks UI | Error messages freeze the entire TUI | `ShowMessage()` method |
| Blocking `.GetAwaiter().GetResult()` | Could cause UI freezes during history load | `HandleHistoryInput`, `RenderHistory` |
| Auto-refresh every 500ms during execution (good) | Keeps view current | Render loop |

**Recommendation:** Add loading spinners to Web Playground. Replace `Thread.Sleep` with async delay in Terminal. Replace `alert()` with toast notifications in Web Playground.

---

## 4. Offline / Disconnection Handling

### Web Playground — **Severe**

| Finding | Details | Location |
|---|---|---|
| No offline detection | No `navigator.onLine` check, no `offline`/`online` event listeners | Absent |
| No service worker | No caching, no offline fallback | Absent |
| Mermaid.js loaded from CDN | If CDN is unreachable, DAG rendering fails silently | `<script>` tag in `<head>` |
| SSE connection loss shows error but no reconnection | If server goes down mid-execution, error is caught but no retry | `runOrchestration()` |
| No `<noscript>` fallback | Blank page if JS is disabled | `<body>` |

### Portal — **High**

| Finding | Details | Location |
|---|---|---|
| No offline detection | Same gap as Web Playground | Absent |
| No service worker | Same gap | Absent |
| React, ReactDOM, Babel loaded from CDN | If any CDN fails, entire app is broken — blank page | `<script>` tags in `<head>` |
| Mermaid.js from CDN | Same as Web Playground | `<script>` tag |
| StatusBar shows server connection state (good) | "Connected"/"Disconnected"/"Connecting" indicators for server and Outlook | StatusBar component |
| EventSource SSE has error handling (good) | Handles SSE connection errors | ExecutionModal |
| Polling continues on failure | 2s/5s/5s polling intervals retry automatically | useEffect hooks |

### Terminal — **Medium**

| Finding | Details | Location |
|---|---|---|
| No graceful server disconnect handling | If API server goes down, exceptions propagate | HTTP calls in TerminalUI.cs |
| 100-event buffer cap | Events beyond 100 are silently dropped | `TerminalOrchestrationReporter.cs` |

**Recommendation:** Add a service worker for static asset caching in both web frontends. Bundle critical CDN dependencies locally. Add `navigator.onLine` detection with a visible offline banner. Add `<noscript>` tags. Add reconnection logic for SSE streams.

---

## 5. Search & Filter

### Web Playground — **High**

| Finding | Details | Location |
|---|---|---|
| No orchestration search | Cannot search or filter orchestrations in sidebar | Sidebar rendering |
| Folder-mode file listing only | Implicit browse via folder scan, no text search | Folder browse functions |
| No history search/filter | Cannot search past runs by name, date, status | History panel |
| No step search in editor | Cannot search within orchestration steps | Editor panel |

### Portal — **Low (Good)**

| Finding | Details | Location |
|---|---|---|
| Search box with filtering (good) | Filters orchestrations by name in the main grid | App component |
| MCP server search (good) | Search box in McpsModal to filter MCP servers | McpsModal component |
| No history search | Cannot filter history by date, status, or name | HistoryModal |
| No execution event search | Cannot search within execution trace | ExecutionModal |

### Terminal — **High**

| Finding | Details | Location |
|---|---|---|
| No search functionality | Must scroll through lists manually | TerminalUI.cs |
| Tab completion for file paths (good) | Helps with path entry | File input handling |

**Recommendation:** Add a search/filter input to Web Playground's sidebar. Add history filtering by status/date to both web frontends. Add a search shortcut (Ctrl+K or /) for quick access.

---

## 6. Real-Time Updates

### Web Playground — **Low (Good)**

| Finding | Details | Location |
|---|---|---|
| SSE via fetch + ReadableStream (good) | POST-based SSE with manual buffer parsing | `runOrchestration()` |
| AbortController for cancellation (good) | Clean cancellation of running executions | `runOrchestration()` |
| Live timer and cost tracking (good) | Updates every second during execution | Timer interval code |
| No auto-refresh of history | Must manually reload to see new history entries | History panel |

### Portal — **Low (Good)**

| Finding | Details | Location |
|---|---|---|
| EventSource SSE for execution streaming (good) | Proper SSE with event type handling | ExecutionModal |
| Polling: active every 2s, history every 5s, status every 5s (good) | Keeps all views current | useEffect hooks |
| Live DAG coloring during execution (good) | Nodes change color based on step status | ExecutionModal DAG |
| Mermaid re-initialized on every render (concern) | Could cause performance issues with frequent updates | Lines 4915-4927, 5113-5125 |

### Terminal — **Medium**

| Finding | Details | Location |
|---|---|---|
| Auto-refresh every 500ms during active execution (good) | Keeps terminal view current | Render loop |
| Render throttling at 100ms (good) | Prevents excessive redraws | Render throttle logic |
| 100-event buffer cap | Older events lost in long executions | `TerminalOrchestrationReporter.cs` line ~30 |

**Recommendation:** Add auto-refresh or polling for history in Web Playground. Cache Mermaid instances in Portal instead of re-initializing. Increase or make configurable the Terminal event buffer.

---

## 7. Form Validation & Feedback

### Web Playground — **Medium**

| Finding | Details | Location |
|---|---|---|
| `/api/validate` endpoint with visual feedback (good) | Shows errors/warnings with color coding | Lines 3902-3943 |
| Editor has step validation (good) | Required fields checked on save | Editor save logic |
| Validation errors shown via `alert()` | Disruptive UX | Multiple locations |
| No inline field-level validation | Errors shown after submission, not during input | Forms |
| Trigger form has type-specific fields (good) | Shows/hides fields based on trigger type | Trigger form rendering |

### Portal — **Low (Good)**

| Finding | Details | Location |
|---|---|---|
| AddModal has JSON preview with validation (good) | Shows parsed orchestration before adding | AddModal component |
| RunModal has parameter form (good) | Dynamic parameter inputs based on orchestration definition | RunModal component |
| Inline error display (good) | No `alert()` usage | Throughout |
| No field-level validation | Same gap as Web Playground | Forms |

### Terminal — **Medium**

| Finding | Details | Location |
|---|---|---|
| Interactive console detection (good) | Checks `Console.IsInputRedirected` before starting TUI | `Program.cs` |
| Tab completion for paths (good) | Reduces input errors | File path input |
| Limited error feedback | Errors shown as text messages that block with `Thread.Sleep` | `ShowMessage()` |

**Recommendation:** Add inline field-level validation (red borders, helper text) to both web frontends. Replace `alert()` with inline error messages in Web Playground. Replace `Thread.Sleep` with async message display in Terminal.

---

## 8. Mobile Support

### Web Playground — **Severe**

| Finding | Details | Location |
|---|---|---|
| No viewport meta tag considerations | Has `<meta name="viewport">` but layout is not mobile-friendly | `<head>` |
| Fixed sidebar prevents mobile use | 320px sidebar + content area cannot fit on mobile screens | CSS layout |
| No touch-friendly targets | Buttons and links are small, no touch target sizing | Throughout |
| No mobile navigation pattern | No hamburger menu, no bottom nav, no swipe gestures | Absent |

### Portal — **High**

| Finding | Details | Location |
|---|---|---|
| Card grid wraps on smaller screens (partial) | `auto-fill` grid helps but sidebar doesn't collapse | Card grid CSS |
| Extremely small text | Card title: 10px, meta label: 7px, meta value: 9px — unusable on mobile | Card CSS styles |
| No touch target sizing | Buttons and interactive elements not sized for touch (minimum 44x44px) | Throughout |
| Fixed sidebar | Same issue as Web Playground | Sidebar CSS |

### Terminal — **N/A**

Not applicable — terminal applications are not used on mobile devices.

**Recommendation:** Implement a mobile-first responsive redesign for both web frontends. Minimum touch targets of 44x44px. Collapsible sidebar. Increase Portal's card text sizes to at minimum 12px for body text and 14px for titles.

---

## 9. Dark / Light Theme

### Web Playground — **High**

| Finding | Details | Location |
|---|---|---|
| Dark-only theme | CSS custom properties define only dark colors | CSS `:root` variables |
| No `prefers-color-scheme` media query | Does not respect OS theme preference | Absent |
| No theme toggle | No UI control to switch themes | Absent |
| Good contrast in dark mode (mostly) | Dark backgrounds with light text are generally readable | CSS color definitions |

### Portal — **High**

| Finding | Details | Location |
|---|---|---|
| Dark-only theme | Same pattern as Web Playground | CSS `:root` variables |
| No `prefers-color-scheme` media query | Same gap | Absent |
| No theme toggle | Same gap | Absent |
| Very low contrast on small text | 7px/9px text in muted colors on dark backgrounds — fails WCAG AA | Card meta styles |

### Terminal — **Medium**

| Finding | Details | Location |
|---|---|---|
| Uses Spectre.Console colors | Relies on terminal theme for base colors | Color usage in TerminalUI.cs |
| Hardcoded color choices (red/green/yellow/blue) | May clash with some terminal color schemes | Status coloring |
| No theme awareness | Does not detect or adapt to terminal light/dark theme | Throughout |

**Recommendation:** Implement a CSS custom property-based theming system with both dark and light themes. Add a `prefers-color-scheme` media query for automatic OS theme detection. Add a manual toggle. Ensure all text meets WCAG AA contrast ratios (4.5:1 for normal text, 3:1 for large text).

---

## 10. Browser Compatibility

### Web Playground — **Medium**

| Finding | Details | Location |
|---|---|---|
| Uses `fetch` + `ReadableStream` for SSE | Not supported in IE; good in all modern browsers | `runOrchestration()` |
| CSS custom properties (variables) | Not supported in IE11 | CSS `:root` |
| Template literals, arrow functions, async/await | Requires ES2017+ — no transpilation | JavaScript throughout |
| `AbortController` | Not in IE, limited older browser support | SSE handling |
| No polyfills | No fallbacks for older browsers | Absent |
| No `<noscript>` tag | Blank page without JS | `<body>` |

### Portal — **Severe (for production)**

| Finding | Details | Location |
|---|---|---|
| **Babel Standalone in-browser transpilation** | Development-only tool used in production; adds ~1MB parse overhead on every page load; `console` warnings from React dev mode | `<script>` tags |
| React 18 + ReactDOM from CDN | If `unpkg.com` is blocked or slow, entire app fails | `<script>` tags |
| JSX transpiled at runtime | Every page load re-parses ~4000 lines of JSX — significant performance hit | `<script type="text/babel">` |
| `console.log`/`console.error` in production code | Development artifacts left in | Various components |

### Terminal — **Low (Good)**

| Finding | Details | Location |
|---|---|---|
| .NET 8+ dependency | Requires compatible .NET runtime | `Program.cs` |
| Spectre.Console handles terminal compatibility | Well-tested library for cross-platform terminal rendering | Throughout |
| Windows/Linux/macOS support via .NET | Good cross-platform story | .NET runtime |

**Recommendation:** Bundle Portal's React/Babel with a proper build step (Vite, webpack, or at minimum pre-compiled JSX). Remove Babel Standalone from production. Add a build pipeline. For Web Playground, consider adding a minimal polyfill bundle or document minimum browser requirements.

---

## Comparative Summary

| Category | Web Playground | Portal | Terminal |
|---|---|---|---|
| 1. Accessibility | 🔴 Severe | 🟠 High | 🟡 Medium |
| 2. Responsive Design | 🔴 Severe | 🟡 Medium | N/A |
| 3. Loading/Error States | 🟡 Medium | 🟢 Low (Good) | 🟡 Medium |
| 4. Offline/Disconnection | 🔴 Severe | 🟠 High | 🟡 Medium |
| 5. Search/Filter | 🟠 High | 🟢 Low (Good) | 🟠 High |
| 6. Real-Time Updates | 🟢 Low (Good) | 🟢 Low (Good) | 🟡 Medium |
| 7. Form Validation | 🟡 Medium | 🟢 Low (Good) | 🟡 Medium |
| 8. Mobile Support | 🔴 Severe | 🟠 High | N/A |
| 9. Dark/Light Theme | 🟠 High | 🟠 High | 🟡 Medium |
| 10. Browser Compat | 🟡 Medium | 🔴 Severe (prod) | 🟢 Low (Good) |

### Top 5 Priority Fixes (Cross-Frontend)

1. **Portal: Remove Babel Standalone** — Ship pre-compiled JSX. This is the single biggest production-readiness issue. The entire app re-transpiles ~4000 lines of JSX on every page load.

2. **Both Web Frontends: Add ARIA attributes and keyboard navigation** — Neither frontend is usable with assistive technology. Add `role`, `aria-label`, `aria-modal`, focus trapping, and Escape key handling.

3. **Both Web Frontends: Add responsive breakpoints** — Neither frontend has a single `@media` query. Both are unusable below ~800px viewport width.

4. **Web Playground: Replace `alert()`/`confirm()`/`prompt()`** — These block the UI thread, cannot be styled, and break the user flow. Replace with inline modals/toasts.

5. **Both Web Frontends: Add offline resilience** — Bundle CDN dependencies locally, add a service worker for static asset caching, and add `navigator.onLine` detection with a visible banner.

---

## Security Concerns Noted During Audit

| Finding | Severity | Location |
|---|---|---|
| Mermaid.js `securityLevel: 'loose'` | Medium | Both Web Playground and Portal |
| Extensive `innerHTML` usage | Low (mitigated by `esc()`/`escHtml()`) | Web Playground |
| `onclick` handlers with `jsStr()` escaping | Low (potential edge cases) | Web Playground |
| Duplicate HTML escape functions (`esc()` and `escHtml()`) | Low (maintenance risk) | Web Playground |

---

This completes the full UI/UX quality audit across all three Orchestra frontends. Every finding includes the specific frontend, severity, and location. The recommendations are ordered by impact and effort to help prioritize remediation work.
</task_result>
```

---

## Assistant (Build · claude-opus-4.6 · 79.9s)

Now I have a comprehensive understanding of the project. Let me compile all findings into a structured recommendation report.

---

# Orchestra Project - Comprehensive Improvement Recommendations

Based on a thorough analysis of the entire codebase, here are my recommendations organized by category and priority.

---

## 1. SECURITY (Critical Priority)

### 1.1 Add Authentication & Authorization
There is **zero auth** on any API endpoint. Anyone with network access can execute orchestrations (triggering expensive LLM calls), read/write files, manage triggers, and view execution history.
- Add JWT or API key authentication via `AddAuthentication()` / `UseAuthorization()`
- Apply `.RequireAuthorization()` to all endpoint groups in `EndpointRouteBuilderExtensions.cs`

### 1.2 Fix Path Traversal Vulnerabilities
Multiple endpoints accept arbitrary filesystem paths. The `SanitizePath()` function strips invalid filename chars but `..` passes through.
- `POST /api/save` (`Web/Program.cs:895`) can **write to any location** on the filesystem
- `POST /api/browse`, `GET /api/browse` list arbitrary directories
- `GET /api/history/{runId}` allows traversal via the route parameter
- **Fix:** Validate all paths with `Path.GetFullPath(combined).StartsWith(allowedBase)`

### 1.3 Fix Broken HMAC Webhook Validation
The webhook "secret" validation (`WebhooksApi.cs:33-39`) uses plain `==` comparison on a query string parameter - vulnerable to timing attacks and URL logging.
- **Fix:** Implement proper `HMAC-SHA256(secret, body)` with `CryptographicOperations.FixedTimeEquals()`, using an `X-Hub-Signature-256` header instead of a query param

### 1.4 Add Rate Limiting
No rate limiting exists. Webhook and execution endpoints are particularly exposed since they trigger costly LLM calls.
- Add `Microsoft.AspNetCore.RateLimiting` with per-IP and per-endpoint policies

### 1.5 Add Security Headers
No `Content-Security-Policy`, `X-Frame-Options`, `X-Content-Type-Options`, `Strict-Transport-Security`, or `Referrer-Policy` headers are configured.

---

## 2. RESILIENCE & RELIABILITY (High Priority)

### 2.1 Add Retry Policies for LLM Calls
No retry mechanisms exist anywhere. A transient LLM API failure immediately fails the step with no recovery.
- Add `Microsoft.Extensions.Resilience` (Polly v8) with exponential backoff for agent calls
- Consider a step-level `RetryPolicy` config on `PromptOrchestrationStep`

### 2.2 Add Orchestration-Level Timeout
Per-step timeouts exist and work well (`OrchestrationExecutor.cs:263-299`), but there is no maximum orchestration duration. A 100-step orchestration could theoretically run for hours.
- Add `MaxDurationSeconds` to the `Orchestration` model

### 2.3 Add Crash Recovery / Checkpoint System
All execution state is in-memory. A server restart mid-execution loses everything with no way to resume.
- Persist step completion checkpoints to disk
- Add a "retry failed run" API endpoint
- Add a "resume from checkpoint" capability

### 2.4 Fix Graceful Shutdown
`TriggerManager` stops its loop on shutdown but doesn't cancel in-flight orchestrations. Active `CancellationTokenSource` instances are never cancelled, wasting LLM tokens during deploys.
- Register active executions with `IHostApplicationLifetime.ApplicationStopping`

---

## 3. MEMORY & PERFORMANCE (High Priority)

### 3.1 Fix Unbounded SSE Memory Growth
`SseReporter` accumulates **all events forever** in `_accumulatedEvents` (`SseReporter.cs:26`). Subscriber channels are unbounded. No connection limit exists.
- Switch to `Channel.CreateBounded<SseEvent>()` with `DropOldest`
- Add a maximum subscriber count
- Add SSE heartbeat/keepalive to detect dead connections
- Implement `IDisposable` on `SseReporter`

### 3.2 Fix File System Performance
- `FileSystemRunStore` loads **every** `run.json` into memory on first access (`FileSystemRunStore.cs:293-353`)
- Pagination is fake - loads all, then `.Take(limit)` in memory
- Synchronous `File.ReadAllText()` used in async contexts (`OrchestrationParser.cs`, `OrchestrationRegistry.cs`, `TriggerManager.cs`)
- **Fix:** Add true cursor-based pagination, lazy loading, and use `File.ReadAllTextAsync()`

### 3.3 Add Concurrent Execution Limits
No throttle on simultaneous orchestrations. Each step uses `Task.Run()`. Under load, this can saturate the thread pool.
- Add a configurable `MaxConcurrentOrchestrations` with a `SemaphoreSlim`

### 3.4 Fix Thread Safety Issues
`FileSystemRunStore._indexByOrchestration` uses `ConcurrentDictionary` wrapping non-thread-safe `List<RunIndex>` values. Concurrent writes can corrupt data.

---

## 4. UI/UX IMPROVEMENTS (Medium-High Priority)

### 4.1 Portal: Remove Babel Standalone
The Portal re-transpiles ~4000 lines of JSX **on every page load** using Babel Standalone in the browser. This adds ~1MB overhead and is explicitly a dev-only tool.
- Add a proper build step (Vite or esbuild) to pre-compile the React app

### 4.2 Both Web Frontends: Add Responsive Design
Neither frontend has a single `@media` query. Both are unusable below ~800px viewport width.
- Add breakpoints for mobile/tablet/desktop
- Collapse sidebar into hamburger menu on mobile
- Make modals full-screen on small viewports

### 4.3 Both Web Frontends: Accessibility
Neither frontend is usable with assistive technology.
- Add ARIA labels to all interactive elements (buttons use emoji glyphs like `♯`, `📂`, `▶` with zero `aria-label`)
- Add `role="dialog"`, `aria-modal`, focus trapping to Portal modals
- Replace `alert()`/`confirm()`/`prompt()` in Web Playground (~10+ locations) with styled modals/toasts

### 4.4 Add Dark/Light Theme Toggle
Both web frontends are dark-only. No `prefers-color-scheme` detection.
- Implement CSS custom property theming with both dark and light variants
- Respect OS preference with manual override

### 4.5 Add Search & Filtering
Web Playground has no search anywhere. Portal has basic name search but no history filtering.
- Add orchestration search to Web Playground sidebar
- Add history filtering by status, date, orchestration name
- Add a keyboard shortcut (`Ctrl+K` or `/`) for quick search

### 4.6 Add Offline Resilience
React, ReactDOM, Babel, and Mermaid.js are all loaded from CDN. If the CDN is unreachable, the app shows a blank page.
- Bundle critical dependencies locally
- Add `<noscript>` fallback

---

## 5. NEW FEATURES (Medium Priority)

### 5.1 Multi-Provider LLM Support
Currently only GitHub Copilot SDK is implemented. The engine has a clean `AgentBuilder`/`IAgent` abstraction.
- Add `Orchestra.OpenAI` for direct OpenAI/Azure OpenAI integration
- Add `Orchestra.Anthropic` for direct Anthropic API integration
- This removes the single-vendor dependency and allows users to choose providers

### 5.2 Orchestration Versioning
No version tracking for orchestration definitions. When a file changes, there's no history.
- Add version numbering with diff view
- Allow pinning executions to specific versions
- Track which version produced which run

### 5.3 Conditional Step Execution
Steps currently always execute if dependencies are met. There's no way to conditionally skip a step based on a previous step's output.
- Add `condition` expressions on steps (e.g., `"condition": "{{step1.output}} contains 'approved'"`)
- Add `if/else` branching in the DAG

### 5.4 Step Templates / Reusable Components
No mechanism to reuse common step patterns across orchestrations.
- Add a `$ref` or `template` mechanism for step definitions
- Allow shared prompt libraries

### 5.5 Cost Budgets & Guardrails
Token tracking exists but is incomplete (cache tokens dropped, handler calls untracked, no aggregation).
- Fix token tracking to capture all LLM calls including handlers
- Add per-orchestration cost budgets with automatic abort
- Add cost alerts/notifications

### 5.6 Orchestration Marketplace / Sharing
13 great examples exist but there's no mechanism to discover or share orchestrations.
- Add an import/export format with dependency bundling
- Consider a community gallery/registry

### 5.7 Visual Orchestration Builder
Orchestrations are JSON-only. A visual DAG editor would dramatically lower the barrier to entry.
- Add a drag-and-drop step builder with live DAG preview
- Two-way sync between visual editor and JSON
- Use the existing Mermaid DAG rendering as a starting point

### 5.8 Webhook & API Trigger Enhancements
- Add response transformation (return LLM output as webhook response, not just 200 OK)
- Add webhook retry with exponential backoff for outbound webhooks
- Add API key scoping per webhook

### 5.9 Execution Comparison & A/B Testing
The model comparison mode exists but is basic.
- Add side-by-side output comparison for the same orchestration with different models/prompts
- Add quality scoring and statistical tracking over time

### 5.10 Plugin System
- Allow custom step types (not just LLM prompts) - e.g., HTTP call steps, file transform steps, code execution steps
- Allow custom reporters/sinks

---

## 6. DEVELOPER EXPERIENCE (Medium Priority)

### 6.1 Add Problem Details for API Errors
Error responses are inconsistent - some use `Results.BadRequest(new { error })`, others manually write raw JSON. Silent `catch { }` blocks swallow errors in webhooks, triggers, and execution endpoints.
- Adopt RFC 7807 Problem Details with `AddProblemDetails()`
- Add global exception handler middleware

### 6.2 Replace Console.WriteLine with ILogger
Two `Console.WriteLine` calls in `ExecutionApi.cs` (lines 404, 501) bypass structured logging.

### 6.3 Add OpenAPI/Swagger Documentation
No API documentation exists. All endpoints are undocumented.
- Add `Swashbuckle` or `Microsoft.AspNetCore.OpenApi`
- Add XML doc comments to all endpoints

### 6.4 Add Configuration Validation
No startup validation of configuration. Missing or invalid config silently fails at runtime.
- Add `IValidateOptions<T>` for all option classes

### 6.5 Improve Test Coverage
- ~385 unit tests, only ~10 integration tests
- E2E tests exist but are excluded from CI
- Add integration tests for the full execution pipeline
- Enable E2E tests in CI with Playwright browser installation

---

## 7. INFRASTRUCTURE (Lower Priority)

### 7.1 Add Database Option
File-system-only storage limits scalability. Consider supporting SQLite or PostgreSQL as an alternative to `FileSystemRunStore`.

### 7.2 Add Distributed Execution Support
The engine is single-process only. For production workloads, consider:
- Queue-based step execution (e.g., via a message broker)
- Distributed locking for triggers

### 7.3 Add Observability
- Add OpenTelemetry tracing for end-to-end orchestration visibility
- Add metrics export (Prometheus/OTLP) for execution duration, token usage, error rates
- Add health check details for dependent services (LLM providers, MCP servers)

### 7.4 Add docker-compose.yml
Docker image exists but no compose file for easy local development with dependent services.

---

## Priority Matrix

| Priority | Category | Impact | Effort |
|----------|----------|--------|--------|
| **P0** | Authentication & Authorization | Critical | Medium |
| **P0** | Path Traversal Fixes | Critical | Low |
| **P0** | HMAC Webhook Fix | Critical | Low |
| **P0** | SSE Memory Bounds | High | Medium |
| **P1** | Retry Policies (Polly) | High | Medium |
| **P1** | Crash Recovery | High | High |
| **P1** | Rate Limiting | High | Low |
| **P1** | Portal Build Step | High | Medium |
| **P1** | File I/O Fixes | High | Medium |
| **P2** | Responsive Design | Medium | Medium |
| **P2** | Accessibility | Medium | Medium |
| **P2** | Multi-Provider LLM | Medium | High |
| **P2** | Visual Orchestration Builder | Medium | High |
| **P2** | Conditional Steps | Medium | Medium |
| **P3** | OpenAPI Docs | Low | Low |
| **P3** | Theme Toggle | Low | Low |
| **P3** | Orchestration Versioning | Low | Medium |
| **P3** | Database Option | Low | High |

The security fixes (P0) should be addressed immediately, followed by the resilience and memory management improvements (P1). The UI/UX and feature work (P2-P3) can be tackled incrementally.

---

## User

Implement the following:
- Retry policies for LLM Calls
- Orchestration-level timeout. Is configurable in json, default is very big, like 1 hour.
- crash-recovery and checkpoint system. Can this be as a "plugin". Without changing the core engine?
- Graceful shutdown
- Unbounded SSE memory growth
- Thread safety issues.
- Responive design.
- Accessibility as you suggested
- Search and Filtering
- Offline resilience
- Orchestration Versioning
- Visual Orchestration Builder
- Webhook & API Trigger enhancements
- Plugin system, specially other type of steps
- Problem details for API errors
- Replace Console.WriteLine with ILogger
- Improve test coverage
 

---

## Assistant (Plan · claude-opus-4.6)

---

