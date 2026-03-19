# Project improvement and feature recommendations

**Session ID:** ses_2fad1fdadffexeif9Tv7naF4Uk
**Created:** 3/19/2026, 1:19:54 AM
**Updated:** 3/19/2026, 3:56:13 AM

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

## Assistant (Plan · claude-opus-4.6 · 499.8s)



This is a massive set of changes. Let me start by exploring the current codebase structure in detail to understand the exact files and patterns I'll need to work with, then build a comprehensive plan.

**Tool: task**

**Input:**
```json
{
  "description": "Explore engine core structure",
  "prompt": "Explore the Orchestra project at P:\\Github\\Orchestra in READ-ONLY mode. I need detailed information about:\n\n1. The full file listing of src/Orchestra.Engine/ - every file and its purpose\n2. The OrchestrationExecutor.cs - full content (how steps are executed, dependency resolution, the DAG execution)\n3. The PromptExecutor.cs - full content (how individual prompts are executed)\n4. The AgentBuilder and IAgent interfaces - full content\n5. The Orchestration model - full content\n6. The OrchestrationStep model - full content\n7. The OrchestrationScheduler - full content\n8. All serialization/parser files\n\nReturn the FULL content of each file with line numbers. This is critical for planning.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2fa629750ffey225gF4nbp3Zgs (for resuming to continue this task if needed)

<task_result>
Here is the complete, detailed report of every file in `src/Orchestra.Engine/` with full contents and analysis.

---

# Orchestra.Engine -- Complete File Listing and Analysis

## Project Structure Overview

```
src/Orchestra.Engine/
  Orchestra.Engine.csproj
  Agent/
    AgentBuilder.cs
    AgentEvent.cs
    AgentEventType.cs
    AgentResult.cs
    AgentTask.cs
    IAgent.cs
    Subagent.cs
  Formatting/
    DefaultPromptFormatter.cs
    IPromptFormatter.cs
  Mcp/
    LocalMcp.cs
    Mcp.cs
    McpType.cs
    RemoteMcp.cs
  Orchestration/
    Orchestration.cs
    OrchestrationResult.cs
    Executor/
      AgentEventProcessor.cs
      ExecutionContext.cs
      ExecutionResult.cs
      ExecutionStatus.cs
      Executor.cs
      OrchestrationExecutor.cs
      PromptExecutor.cs
    Scheduler/
      IScheduler.cs
      OrchestrationScheduler.cs
      Schedule.cs
      ScheduleEntry.cs
    Steps/
      LoopConfig.cs
      OrchestrationStep.cs
      OrchestrationStepType.cs
      PromptOrchestrationStep.cs
      ReasoningLevel.cs
      SystemPromptMode.cs
  Reporting/
    AvailableModelInfo.cs
    IOrchestrationReporter.cs
    ModelMismatchInfo.cs
    NullOrchestrationReporter.cs
  Serialization/
    OrchestrationParser.cs
  Storage/
    IRunStore.cs
    NullRunStore.cs
    OrchestrationRunRecord.cs
    StepRunRecord.cs
  Triggers/
    EmailTriggerConfig.cs
    LoopTriggerConfig.cs
    SchedulerTriggerConfig.cs
    TriggerConfig.cs
    TriggerStatus.cs
    TriggerType.cs
    WebhookTriggerConfig.cs
```

**Total: 48 files** (1 csproj + 47 .cs files)

---

## 1. FILE-BY-FILE PURPOSE INDEX

| # | File (relative to `src/Orchestra.Engine/`) | Lines | Purpose |
|---|---|---|---|
| 1 | `Orchestra.Engine.csproj` | 11 | Project file. Targets default SDK, only dependency is `Microsoft.Extensions.Logging.Abstractions`. |
| 2 | `Agent/IAgent.cs` | 6 | Core agent interface -- single method `SendAsync(prompt)` returning `AgentTask`. |
| 3 | `Agent/AgentBuilder.cs` | 56 | Abstract fluent builder for constructing `IAgent` instances with model, system prompt, MCPs, subagents, reasoning level, system prompt mode. |
| 4 | `Agent/AgentTask.cs` | 22 | Wraps a `ChannelReader<AgentEvent>` + `Task<AgentResult>`, implements `IAsyncEnumerable<AgentEvent>` for streaming. |
| 5 | `Agent/AgentResult.cs` | 37 | Final result from agent: content, actual model, selected model, usage stats, available models. |
| 6 | `Agent/AgentEvent.cs` | 82 | Event emitted during agent execution: message deltas, reasoning deltas, tool calls, subagent events, errors. |
| 7 | `Agent/AgentEventType.cs` | 23 | Enum: MessageDelta, Message, ReasoningDelta, Reasoning, ToolExecutionStart/Complete, SessionIdle, Error, SessionStart, ModelChange, Usage, and 5 subagent event types. |
| 8 | `Agent/Subagent.cs` | 57 | Subagent definition: name, display name, description, prompt, tool restrictions, MCP servers, inference flag. |
| 9 | `Formatting/IPromptFormatter.cs` | 45 | Interface for formatting dependency outputs, building user prompts, building transformation system prompts, wrapping content for transformation. |
| 10 | `Formatting/DefaultPromptFormatter.cs` | 113 | Default markdown-style formatter: joins dependency outputs with `---` separators, builds user prompts with context sections, builds transformation system prompts with strict rules. |
| 11 | `Mcp/Mcp.cs` | 7 | Base MCP class: Name + Type. |
| 12 | `Mcp/McpType.cs` | 7 | Enum: Local, Remote. |
| 13 | `Mcp/LocalMcp.cs` | 8 | Local MCP: command, arguments, optional working directory (stdio-based). |
| 14 | `Mcp/RemoteMcp.cs` | 7 | Remote MCP: endpoint URL + headers dictionary (HTTP-based). |
| 15 | `Orchestration/Orchestration.cs` | 40 | Top-level orchestration model: name, description, steps, version, optional trigger, inline MCPs, default system prompt mode. |
| 16 | `Orchestration/OrchestrationResult.cs` | 38 | Result from running an orchestration. Contains terminal step results (steps no other step depends on) and all step results. Status is Failed if any terminal step failed/skipped. |
| 17 | `Orchestration/Executor/Executor.cs` | 6 | Abstract base `Executor<TStep>` with single method `ExecuteAsync`. |
| 18 | `Orchestration/Executor/ExecutionStatus.cs` | 11 | Enum: Pending, Running, Succeeded, Failed, Skipped, Cancelled. |
| 19 | `Orchestration/Executor/ExecutionResult.cs` | 82 | Result of a single step: content, status, error, raw content, raw dependency outputs, prompt sent, actual model, token usage, execution trace. Factory methods: `Succeeded`, `Failed`, `Skipped`. |
| 20 | `Orchestration/Executor/ExecutionContext.cs` | 100 | Shared mutable context during orchestration: stores step results (ConcurrentDictionary), loop feedback, provides dependency output retrieval, checks for failed dependencies. |
| 21 | `Orchestration/Executor/OrchestrationExecutor.cs` | 497 | **Core orchestration executor.** DAG-based parallel execution using TaskCompletionSource per step, dependency propagation via reverse dependency map, loop handling, parameter validation, step timeout, run record persistence. |
| 22 | `Orchestration/Executor/PromptExecutor.cs` | 201 | **Prompt step executor.** Builds user prompt with parameter injection + dependency outputs + loop feedback, creates agent via builder, streams events through AgentEventProcessor, handles output handler post-processing, builds execution trace. |
| 23 | `Orchestration/Executor/AgentEventProcessor.cs` | 292 | Processes agent event stream: collects reasoning, tool calls (with pending/complete correlation), response segments. Builds `StepExecutionTrace` from collected data. Reports events to `IOrchestrationReporter`. |
| 24 | `Orchestration/Scheduler/IScheduler.cs` | 6 | Interface: `Schedule Schedule(Orchestration)`. |
| 25 | `Orchestration/Scheduler/OrchestrationScheduler.cs` | 87 | **Kahn's algorithm** topological sort: validates DAG (detects cycles, missing deps, duplicate names), produces layered schedule where each layer is a set of parallelizable steps. |
| 26 | `Orchestration/Scheduler/Schedule.cs` | 6 | Schedule model: array of `ScheduleEntry`. |
| 27 | `Orchestration/Scheduler/ScheduleEntry.cs` | 6 | A single layer: array of `OrchestrationStep` that can run in parallel. |
| 28 | `Orchestration/Steps/OrchestrationStep.cs` | 9 | Abstract base step: name, type, dependsOn, parameters. |
| 29 | `Orchestration/Steps/OrchestrationStepType.cs` | 6 | Enum: Prompt (only type currently). |
| 30 | `Orchestration/Steps/PromptOrchestrationStep.cs` | 40 | Concrete prompt step: system prompt, user prompt, input/output handler prompts, model, reasoning level, system prompt mode, MCPs, timeout, loop config, subagents. |
| 31 | `Orchestration/Steps/LoopConfig.cs` | 28 | Loop configuration: target step name, max iterations (1-10), exit pattern (case-insensitive string match). |
| 32 | `Orchestration/Steps/ReasoningLevel.cs` | 8 | Enum: Low, Medium, High. |
| 33 | `Orchestration/Steps/SystemPromptMode.cs` | 7 | Enum: Append, Replace. |
| 34 | `Reporting/IOrchestrationReporter.cs` | 28 | Event reporting interface: 19 methods covering session, content deltas, reasoning deltas, tool execution, step lifecycle, model mismatch, loops, subagent events. |
| 35 | `Reporting/NullOrchestrationReporter.cs` | 30 | No-op implementation of all 19 reporter methods. |
| 36 | `Reporting/ModelMismatchInfo.cs` | 12 | Model mismatch data: configured vs actual model, prompt mode, reasoning level, MCP servers, available models. |
| 37 | `Reporting/AvailableModelInfo.cs` | 9 | Model info: ID, name, billing multiplier, supported reasoning efforts. |
| 38 | `Serialization/OrchestrationParser.cs` | 323 | **JSON parser** with 3 custom `JsonConverter`s: `OrchestrationStepConverter` (polymorphic step deserialization), `McpConverter` (Local/Remote dispatch), `TriggerConfigConverter` (Scheduler/Loop/Webhook/Email dispatch). Also handles MCP resolution (inline + external, external wins). |
| 39 | `Storage/IRunStore.cs` | 39 | Persistence interface: save, list (by name, all, by trigger), get, delete run records. |
| 40 | `Storage/NullRunStore.cs` | 27 | No-op store that discards all records. |
| 41 | `Storage/OrchestrationRunRecord.cs` | 50 | Run record: run ID, orchestration name, timing, status, version, triggered by, parameters, trigger ID, step records (final + all iterations), final content. |
| 42 | `Storage/StepRunRecord.cs` | 174 | Step record + `TokenUsage` + `StepExecutionTrace` + `ToolCallRecord`. The trace model captures: system prompt, user prompt (raw + processed), reasoning, tool calls (with call ID, MCP server, arguments, success, result, timing), response segments, final response, output handler result. |
| 43 | `Triggers/TriggerConfig.cs` | 23 | Abstract base trigger: type, enabled flag, optional input handler prompt (LLM-based input transformation). |
| 44 | `Triggers/TriggerType.cs` | 25 | Enum: Scheduler, Loop, Webhook, Email. |
| 45 | `Triggers/TriggerStatus.cs` | 25 | Enum: Idle, Waiting, Running, Paused, Completed, Error. |
| 46 | `Triggers/SchedulerTriggerConfig.cs` | 25 | Cron or interval-based scheduling with optional max runs. |
| 47 | `Triggers/LoopTriggerConfig.cs` | 23 | Auto-re-run trigger: delay, max iterations, continue on failure. |
| 48 | `Triggers/WebhookTriggerConfig.cs` | 19 | HTTP webhook trigger: optional HMAC secret, max concurrent. |
| 49 | `Triggers/EmailTriggerConfig.cs` | 37 | Email trigger: folder path, poll interval, max items, subject/sender filters. |

---

## 2. OrchestrationExecutor.cs -- FULL CONTENT (497 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs`

```csharp
// Line 1-497 (full file shown above in the read output)
```

### Key Architecture:

**DAG Execution Model:**
- The executor does NOT use the scheduler's layered output at runtime. It calls `_scheduler.Schedule()` purely for **validation** (cycle detection, missing deps, duplicate names) on line 44.
- Actual execution uses a **reactive push-based model**: each step, upon completion, checks its dependents and launches any whose deps are now all satisfied (lines 134-144).

**Parallel Execution:**
- A `TaskCompletionSource<ExecutionResult>` is created per step (lines 65-69).
- All zero-dependency steps are launched immediately (lines 149-156).
- Steps with dependencies are launched by their parent steps upon completion (lines 135-144).
- `Task.WhenAll` on all TCS tasks waits for everything to finish (line 159).

**Dependency Skip Propagation:**
- If a dependency failed/skipped, the dependent step is skipped with a reason (lines 243-257 in `ExecuteOrSkipStepAsync`).

**Loop Handling (lines 302-389):**
- After a "checker" step runs, if its output does NOT contain `ExitPattern`, it re-runs the `Target` step with feedback, then re-runs the checker.
- Feedback is injected via `context.SetLoopFeedback()`.
- Loop records are stored with iteration keys like `"stepName:iteration-N"`.

**Step Timeout:**
- Per-step timeout via `CancellationTokenSource.CreateLinkedTokenSource` (lines 266-272).
- Timeout vs. global cancellation is differentiated in the catch block (lines 289-295).

**Parameter Validation (lines 202-225):**
- Collects all parameters required by all steps, reports which steps need each missing parameter.

**Run Persistence (lines 172-198):**
- Builds `OrchestrationRunRecord` with all step records and saves via `IRunStore`.

---

## 3. PromptExecutor.cs -- FULL CONTENT (201 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\PromptExecutor.cs`

### Key Flow:

1. **Capture raw dependency outputs** before building prompt (line 30)
2. **Inject parameters** into user prompt via `{{paramName}}` template syntax (line 33, lines 145-160)
3. **Create AgentEventProcessor** for streaming event collection (line 36)
4. **Build user prompt** with dependency outputs + loop feedback + input handler (lines 135-143)
5. **Build agent** via fluent `AgentBuilder` with model, system prompt, MCPs, subagents, reasoning level, system prompt mode (lines 48-56)
6. **Stream execution**: `agent.SendAsync()` returns `AgentTask` (IAsyncEnumerable), events processed by `AgentEventProcessor` (lines 58-61)
7. **Get final result** from `task.GetResultAsync()` (line 63)
8. **Output handler**: If `OutputHandlerPrompt` is set, runs a second LLM call to transform the output (lines 80-85, lines 162-184). This second agent uses `SystemPromptMode.Replace` and no MCPs.
9. **Build execution trace** with system prompt, raw user prompt, processed user prompt, response, output handler result (lines 99-104)
10. **Return** `ExecutionResult.Succeeded` with all metadata (lines 109-116)

---

## 4. AgentBuilder and IAgent -- FULL CONTENT

**`IAgent.cs` (6 lines):**
```csharp
namespace Orchestra.Engine;
public interface IAgent
{
    AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default);
}
```
Single method. Returns `AgentTask` which is both an `IAsyncEnumerable<AgentEvent>` (for streaming) and provides `GetResultAsync()` (for the final result).

**`AgentBuilder.cs` (56 lines):**
Abstract class with fluent API:
- `WithModel(string)` -- LLM model identifier
- `WithSystemPrompt(string)` -- system prompt
- `WithMcp(params Mcp[])` -- MCP servers
- `WithSubagents(params Subagent[])` -- delegatable subagents
- `WithReasoningLevel(ReasoningLevel?)` -- Low/Medium/High
- `WithSystemPromptMode(SystemPromptMode?)` -- Append/Replace
- `WithReporter(IOrchestrationReporter)` -- event reporter
- `BuildAgentAsync(CancellationToken)` -- abstract factory method

This is the **extension point** for different LLM backends (e.g., Copilot SDK). Concrete implementations live outside the engine.

---

## 5. Orchestration.cs -- FULL CONTENT (40 lines)

```csharp
namespace Orchestra.Engine;
public class Orchestration
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required OrchestrationStep[] Steps { get; init; }
    public string Version { get; init; } = "1.0.0";
    public TriggerConfig? Trigger { get; init; }
    public Mcp[] Mcps { get; init; } = [];
    public SystemPromptMode? DefaultSystemPromptMode { get; init; }
}
```

Key design: Orchestration is a **declarative DAG** of steps with optional trigger, inline MCPs, and a default system prompt mode. Steps define their own dependencies.

---

## 6. OrchestrationStep.cs -- FULL CONTENT (9 lines)

```csharp
namespace Orchestra.Engine;
public abstract class OrchestrationStep
{
    public required string Name { get; init; }
    public required OrchestrationStepType Type { get; init; }
    public required string[] DependsOn { get; init; }
    public string[] Parameters { get; init; } = [];
}
```

**PromptOrchestrationStep.cs** extends this (40 lines) with:
- `SystemPrompt`, `UserPrompt` (required)
- `InputHandlerPrompt`, `OutputHandlerPrompt` (optional LLM pre/post-processing)
- `Model` (required)
- `ReasoningLevel?`, `SystemPromptMode?`
- `Mcp[]` (resolved at parse time from `McpNames`)
- `TimeoutSeconds?`
- `LoopConfig?` (target step, max iterations, exit pattern)
- `Subagent[]` (for multi-agent delegation)

---

## 7. OrchestrationScheduler.cs -- FULL CONTENT (87 lines)

Implements **Kahn's topological sort algorithm**:

1. Build in-degree map and adjacency list (lines 17-37)
2. Validates: duplicate step names, missing dependency references
3. Seed queue with zero-dependency steps (lines 44-48)
4. If no zero-dependency steps exist, throw "circular dependency" (lines 50-51)
5. Layer-by-layer: drain current ready queue into a `ScheduleEntry` (parallel layer), reduce in-degree of dependents, enqueue newly ready (lines 55-80)
6. If `scheduled != steps.Length`, throw "circular dependency" (lines 82-83)

Output: `Schedule` with `ScheduleEntry[]` -- each entry is one parallelizable layer.

**Note:** As mentioned above, the `OrchestrationExecutor` uses this only for validation, not for actual scheduling. Runtime uses reactive push-based launching.

---

## 8. Serialization/Parser Files -- FULL CONTENT

### `OrchestrationParser.cs` (323 lines)

**Public API:**
- `ParseOrchestration(json, availableMcps)` -- full parse + MCP resolution
- `ParseOrchestrationFile(path, availableMcps)` -- file-based variant
- `ParseOrchestrationMetadataOnly(json)` -- parse without MCP resolution (for folder scans)
- `ParseOrchestrationFileMetadataOnly(path)` -- file-based variant
- `ParseMcps(json)` / `ParseMcpFile(path)` -- parse MCP config documents

**JSON options (lines 8-18):**
- `camelCase` naming policy
- 3 custom converters + `JsonStringEnumConverter`

**MCP Resolution (lines 73-120):**
- Merges inline orchestration MCPs with external MCPs (external wins on name conflict)
- Resolves step-level `McpNames` to `Mcp[]` objects
- Also resolves subagent-level `McpNames`

**`OrchestrationStepConverter` (lines 166-254):**
- Reads `type` discriminator
- Currently only `Prompt` type supported
- Deserializes all PromptOrchestrationStep properties including nested `LoopConfig` and `Subagent[]`

**`McpConverter` (lines 122-164):**
- Reads `type` discriminator (Local/Remote)
- Local: command, arguments, workingDirectory
- Remote: endpoint, headers

**`TriggerConfigConverter` (lines 256-322):**
- Reads `type` discriminator (Scheduler/Loop/Webhook/Email)
- Each type has its own properties
- All share `enabled` and `inputHandlerPrompt`

---

## Supporting Model Files (Complete Contents Already Provided Above)

| File | Key Content |
|---|---|
| `ExecutionContext.cs` (100 lines) | Thread-safe context with `ConcurrentDictionary` for results and loop feedback. Methods: `AddResult`, `GetResult`, `ClearResult`, `HasAnyDependencyFailed`, `GetDependencyOutputs`, `GetRawDependencyOutputs`, `SetLoopFeedback`, `ConsumeLoopFeedback`. |
| `ExecutionResult.cs` (82 lines) | Immutable result with factory methods. Tracks content, raw content, dependency outputs, prompt sent, actual model, usage, trace. |
| `AgentEventProcessor.cs` (292 lines) | Streams `AgentEvent`s, correlates tool call start/complete by `ToolCallId`, collects reasoning, response segments. Builds `StepExecutionTrace`. |
| `StepRunRecord.cs` (174 lines) | Contains 3 classes: `StepRunRecord` (run persistence), `TokenUsage` (input/output tokens), `StepExecutionTrace` (system prompt, raw/processed user prompt, reasoning, tool calls, response segments, final response, output handler result), `ToolCallRecord` (call ID, MCP server, tool name, arguments, success, result, error, timing). |
| `AgentTask.cs` (22 lines) | `IAsyncEnumerable<AgentEvent>` wrapper over `ChannelReader<AgentEvent>` + `Task<AgentResult>`. |
| `DefaultPromptFormatter.cs` (113 lines) | Markdown formatting: single dep = raw content, multiple deps = `## Output from 'X':` with `---` separators. User prompt built with `Context from previous steps:` section. Loop feedback as `Feedback from previous attempt:` section. Transformation uses XML-like `<INPUT_CONTENT>` tags. |

---

## Architectural Summary

The engine is a **provider-agnostic orchestration framework**:

1. **Declarative JSON** defines orchestrations with steps, dependencies, MCPs, triggers
2. **OrchestrationParser** deserializes JSON with polymorphic type converters
3. **OrchestrationScheduler** validates the DAG via Kahn's algorithm
4. **OrchestrationExecutor** runs the DAG with reactive parallel execution (not layered)
5. **PromptExecutor** handles individual step execution: parameter injection, prompt building, agent invocation, output handling, trace collection
6. **AgentBuilder/IAgent** is the abstraction boundary -- concrete LLM backends implement these
7. **IOrchestrationReporter** provides real-time event streaming (content deltas, tool calls, subagent events)
8. **IRunStore** provides persistence for execution history
9. **IPromptFormatter** allows customizing how prompts are assembled

The only external dependency is `Microsoft.Extensions.Logging.Abstractions`. Everything else is abstracted via interfaces, making the engine pluggable for different LLM providers, storage backends, and UI integrations.
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Explore host layer structure",
  "prompt": "Explore the Orchestra project at P:\\Github\\Orchestra in READ-ONLY mode. I need detailed information about:\n\n1. The full file listing of src/Orchestra.Host/ - every file\n2. Full content of ExecutionApi.cs\n3. Full content of SseReporter.cs\n4. Full content of ServiceCollectionExtensions.cs\n5. Full content of EndpointRouteBuilderExtensions.cs\n6. Full content of TriggerManager.cs\n7. Full content of FileSystemRunStore.cs\n8. Full content of RunsApi.cs\n9. Full content of OrchestrationsApi.cs\n10. Full content of WebhooksApi.cs\n11. Full content of TriggersApi.cs\n12. Full content of UtilityApi.cs\n\nReturn the FULL content of each file with line numbers.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2fa628ce6ffeSucgiDu9GsTWqq (for resuming to continue this task if needed)

<task_result>
Here is the complete information you requested:

---

## 1. Full File Listing of `src/Orchestra.Host/`

The directory contains the following source files (excluding `bin/`, `obj/` build artifacts):

### Root
- `P:\Github\Orchestra\src\Orchestra.Host\Orchestra.Host.csproj`

### Api/
- `P:\Github\Orchestra\src\Orchestra.Host\Api\DefaultExecutionCallback.cs`
- `P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs` (504 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs` (503 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\Api\RunsApi.cs` (380 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs` (350 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\Api\TriggersApi.cs` (168 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\Api\UtilityApi.cs` (131 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs` (107 lines)

### Extensions/
- `P:\Github\Orchestra\src\Orchestra.Host\Extensions\EndpointRouteBuilderExtensions.cs` (122 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\Extensions\ServiceCollectionExtensions.cs` (167 lines)

### Hosting/
- `P:\Github\Orchestra\src\Orchestra.Host\Hosting\OrchestrationHostOptions.cs`

### Logging/
- `P:\Github\Orchestra\src\Orchestra.Host\Logging\FileLogging.cs`

### Persistence/
- `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs` (443 lines)

### Registry/
- `P:\Github\Orchestra\src\Orchestra.Host\Registry\OrchestrationRegistry.cs`

### Triggers/
- `P:\Github\Orchestra\src\Orchestra.Host\Triggers\ITriggerExecutionCallback.cs`
- `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs` (895 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerTypes.cs`

---

## 2. ExecutionApi.cs (504 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs`

This file defines SSE (Server-Sent Events) streaming endpoints for orchestration execution. It contains:
- `GET /api/orchestrations/{id}/run` -- Runs an orchestration and streams events via SSE. Creates an `SseReporter`, tracks the execution in `activeExecutions`/`activeExecutionInfos`, parses optional `params` from query string, executes the orchestration in a background task, and streams SSE events (replay + future) to the client.
- `GET /api/execution/{executionId}/attach` -- Attaches to an already-running execution's SSE stream. Replays accumulated events and then streams future events.
- `SaveCancelledRunAsync` (private) -- Persists a cancelled run record by parsing accumulated SSE events to determine which steps completed, were cancelled, were in-progress, or were skipped.
- `SaveFailedRunAsync` (private) -- Persists a failed run record similarly, tracking completed vs. failed steps.

Full content was returned above (lines 1-504).

---

## 3. SseReporter.cs (350 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs`

This file defines:
- `SseEvent` record -- A simple `(Type, Data)` pair representing an SSE event.
- `SseReporter` class -- Implements `IOrchestrationReporter`. Supports multiple concurrent subscribers via `Channel<SseEvent>`. Late-joining subscribers get a replay of all accumulated events. Methods include:
  - `Subscribe()` / `Unsubscribe()` -- Manage subscriber channels
  - `Complete()` -- Marks the reporter as done, completing all subscriber channels
  - `ReportSessionStarted`, `ReportModelChange`, `ReportUsage`, `ReportContentDelta`, `ReportReasoningDelta`, `ReportToolExecutionStarted/Completed`, `ReportStepError/Cancelled/Completed/Skipped/Started`, `ReportStepTrace`, `ReportStepOutput`, `ReportModelMismatch`, `ReportLoopIteration`, `ReportSubagentSelected/Started/Completed/Failed/Deselected`, `ReportOrchestrationDone/Cancelled/Error`, `ReportStatusChange` -- All write structured JSON events via the private `Write` method.

Full content was returned above (lines 1-350).

---

## 4. ServiceCollectionExtensions.cs (167 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Extensions\ServiceCollectionExtensions.cs`

This file defines two extension classes:
- `ServiceCollectionExtensions` -- `AddOrchestraHost()` registers all Orchestra Host services: options, engine services (`IScheduler`), trigger execution callback, `FileSystemRunStore`, active execution tracking dictionaries, `OrchestrationRegistry`, and `TriggerManager` as a hosted background service. Also includes `AddTriggerExecutionCallback<T>()` overloads and a private `TryAddSingleton` helper.
- `ServiceProviderExtensions` -- `InitializeOrchestraHost()` loads persisted orchestrations, scans for orchestration files, and registers JSON-defined triggers after the host is built.

Full content was returned above (lines 1-167).

---

## 5. EndpointRouteBuilderExtensions.cs (122 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Extensions\EndpointRouteBuilderExtensions.cs`

Defines extension methods for mapping Orchestra Host API endpoints:
- `DefaultJsonOptions` -- Static `JsonSerializerOptions` with camelCase naming.
- `MapOrchestraHostEndpoints()` -- Maps ALL endpoint groups (orchestrations, triggers, webhooks, runs, execution, utility).
- Individual mapping methods: `MapOrchestrationsEndpoints()`, `MapTriggersEndpoints()`, `MapWebhooksEndpoints()`, `MapRunsEndpoints()`, `MapExecutionEndpoints()`, `MapUtilityEndpoints()` -- Each maps only its respective API group.

Full content was returned above (lines 1-122).

---

## 6. TriggerManager.cs (895 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Triggers\TriggerManager.cs`

A `BackgroundService` that manages all trigger registrations and fires orchestrations. Key members:
- **State:** `_triggers` (concurrent dictionary of registrations), `_activeExecutions`, `_activeExecutionInfos`, builder/scheduler/logger dependencies, runs/triggers directories, run store, execution callback.
- **Public API:** `GetAllTriggers()`, `GetActiveExecutions()`, `GetActiveExecution()`, `GetTrigger()`, `RegisterTrigger()`, `RemoveTrigger()`, `SetTriggerEnabled()`, `FireWebhookTriggerAsync()`, `FireTriggerAsync()`, `RunOrchestrationAsync()`, `CancelExecution()`, `LoadPersistedTriggers()`, `ScanForJsonTriggers()`.
- **ExecuteAsync (BackgroundService):** Loads persisted triggers on startup, then polls every 1 second checking scheduler triggers.
- **ExecuteOrchestrationAsync (private):** Full orchestration execution pipeline including input handler prompt transformation via LLM, reporter creation via callback, execution tracking, history persistence, and loop/webhook/scheduler post-execution handling.
- **Helpers:** `PersistTriggerOverride()`, `GetSidecarPath()`, `GenerateTriggerId()`, `CalculateNextFireTime()` (with Cronos cron support), `CloneTriggerConfigWithEnabled()`, `DeserializeTriggerConfig()`.
- **Structured Logging:** 14 `[LoggerMessage]` partial methods using source generation.

Full content was returned above (lines 1-895).

---

## 7. FileSystemRunStore.cs (443 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Persistence\FileSystemRunStore.cs`

Implements `IRunStore` with a file-system-backed storage layout:
```
{rootPath}/executions/{orchestration-name}/{name}_{version}_{trigger}_{timestamp}_{execution-id}/
    orchestration.json, run.json, {step}-inputs.json, {step}-outputs.json, {step}-result.json, result.md
```

Key features:
- In-memory index (`ConcurrentDictionary`) for fast lookups, lazy-loaded on first access via `EnsureIndexLoadedAsync()`.
- `SaveRunAsync()` -- Writes orchestration copy, full run record, per-step input/output/result files, and a `result.md` summary.
- Query methods: `ListRunsAsync()`, `ListAllRunsAsync()`, `ListRunsByTriggerAsync()`, `GetRunAsync()`, `DeleteRunAsync()`, `GetRunSummariesAsync()` (two overloads).

Also defines supporting types: `RunIndex`, `StepInputsRecord`, `StepOutputsRecord`, `StepResultRecord`.

Full content was returned above (lines 1-443).

---

## 8. RunsApi.cs (380 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Api\RunsApi.cs`

Defines endpoints for run history and active executions:
- `GET /api/history` -- Recent executions (running + completed summaries, default limit 15).
- `GET /api/history/all` -- All executions with pagination (offset/limit).
- `GET /api/history/{orchestrationName}/{runId}` -- Full execution details including step traces, tool calls, usage.
- `DELETE /api/history/{orchestrationName}/{runId}` -- Delete a specific execution record.
- `GET /api/active` -- All active (running) orchestrations plus pending/waiting triggers.
- `POST /api/active/{executionId}/cancel` -- Cancel a running execution.

Full content was returned above (lines 1-380).

---

## 9. OrchestrationsApi.cs (503 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Api\OrchestrationsApi.cs`

Defines orchestration CRUD endpoints:
- `GET /api/orchestrations` -- List all registered orchestrations with metadata, steps, triggers, parameters, models.
- `GET /api/orchestrations/{id}` -- Get a specific orchestration with full step details, execution schedule layers, MCP info.
- `POST /api/orchestrations` -- Add orchestrations from file paths (auto-detects `mcp.json`).
- `POST /api/orchestrations/json` -- Add an orchestration from pasted JSON content.
- `DELETE /api/orchestrations/{id}` -- Remove an orchestration and its trigger.
- `POST /api/orchestrations/{id}/enable` -- Enable an orchestration's trigger.
- `POST /api/orchestrations/{id}/disable` -- Disable an orchestration's trigger.
- `POST /api/orchestrations/scan` -- Scan a folder for orchestration JSON files.

Also defines request DTOs: `AddOrchestrationsRequest`, `AddJsonRequest`, `FolderScanRequest`.

Helper methods: `FormatTriggerInfo()`, `FormatTriggerInfoWithWebhook()` (includes webhook invocation details), `SanitizePath()`.

Full content was returned above (lines 1-503).

---

## 10. WebhooksApi.cs (107 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs`

Defines webhook receiver endpoints:
- `POST /api/webhooks/{id}` -- Receives a webhook call, validates the secret (from `X-Webhook-Secret` header or `secret` query param), parses parameters from the JSON body, and fires the webhook trigger via `TriggerManager.FireWebhookTriggerAsync()`.
- `POST /api/webhooks/{id}/validate` -- Validates the webhook secret without firing the trigger.

Full content was returned above (lines 1-107).

---

## 11. TriggersApi.cs (168 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Api\TriggersApi.cs`

Defines trigger management endpoints:
- `GET /api/triggers` -- List all triggers with status, timing, webhook URLs.
- `GET /api/triggers/{id}` -- Get a specific trigger with full config details.
- `POST /api/triggers/{id}/enable` -- Enable a trigger.
- `POST /api/triggers/{id}/disable` -- Disable a trigger.
- `POST /api/triggers/{id}/fire` -- Manually fire a trigger with optional parameters.
- `DELETE /api/triggers/{id}` -- Remove a trigger.

Helper: `FormatTriggerConfig()` -- Formats config for scheduler, loop, webhook, and email trigger types.

Full content was returned above (lines 1-168).

---

## 12. UtilityApi.cs (131 lines)

**Path:** `P:\Github\Orchestra\src\Orchestra.Host\Api\UtilityApi.cs`

Defines utility endpoints:
- `GET /api/models` -- Returns a hardcoded list of available AI models (GPT-4.1, GPT-4.1 Mini/Nano, GPT-4o, GPT-4o Mini, o3-mini, o4-mini, Claude Sonnet 4, Claude Opus 4, Claude 3.5 Sonnet, Claude Opus 4.5, Gemini 2.0 Flash).
- `GET /api/status` -- Server status including orchestration count, active triggers, running executions, and data path.
- `GET /api/health` -- Simple health check returning `{ status: "healthy", timestamp }`.
- `GET /api/mcps` -- Lists all MCP servers used across all registered orchestrations, including type/endpoint/command details and which orchestrations use each MCP.

Full content was returned above (lines 1-131).
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Explore Copilot and tests",
  "prompt": "Explore the Orchestra project at P:\\Github\\Orchestra in READ-ONLY mode. I need detailed information about:\n\n1. Full file listing of src/Orchestra.Copilot/ - every file\n2. Full content of CopilotAgent.cs\n3. Full content of CopilotAgentBuilder.cs\n4. Full content of CopilotSessionHandler.cs\n5. Full file listing of all test projects\n6. A sampling of key test files to understand testing patterns (at least 3-4 test files)\n7. Full content of all .csproj files in the solution\n8. Full content of Directory.Packages.props\n9. Full content of the solution file (.sln)\n\nReturn the FULL content of each file with line numbers.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2fa6280f0ffeCy7tA2ZAryz9Ef (for resuming to continue this task if needed)

<task_result>
I now have all the information. Here is the complete report:

---

# Orchestra Project -- Full Exploration Report

## 1. Full File Listing of `src/Orchestra.Copilot/` (Source Files Only)

The `src/Orchestra.Copilot/` directory contains **3 source files** and **1 project file**:

| File | Path |
|------|------|
| `CopilotAgent.cs` | `P:\Github\Orchestra\src\Orchestra.Copilot\CopilotAgent.cs` |
| `CopilotAgentBuilder.cs` | `P:\Github\Orchestra\src\Orchestra.Copilot\CopilotAgentBuilder.cs` |
| `CopilotSessionHandler.cs` | `P:\Github\Orchestra\src\Orchestra.Copilot\CopilotSessionHandler.cs` |
| `Orchestra.Copilot.csproj` | `P:\Github\Orchestra\src\Orchestra.Copilot\Orchestra.Copilot.csproj` |

Additionally, `bin/` and `obj/` contain build artifacts including the Copilot CLI binaries (`copilot.exe` versions 0.0.403 and 0.0.420 for `win32-x64`).

---

## 2. Full Content of `CopilotAgent.cs`

**Path:** `P:\Github\Orchestra\src\Orchestra.Copilot\CopilotAgent.cs` (355 lines)

```csharp
1: using System.Threading.Channels;
2: using GitHub.Copilot.SDK;
3: using Microsoft.Extensions.Logging;
4: using Orchestra.Engine;
5: 
6: namespace Orchestra.Copilot;
7: 
8: public partial class CopilotAgent : IAgent
9: {
10: 	private readonly CopilotClient _client;
11: 	private readonly string _model;
12: 	private readonly string? _systemPrompt;
13: 	private readonly Mcp[] _mcps;
14: 	private readonly Subagent[] _subagents;
15: 	private readonly ReasoningLevel? _reasoningLevel;
16: 	private readonly SystemPromptMode? _systemPromptMode;
17: 	private readonly IOrchestrationReporter _reporter;
18: 	private readonly ILogger<CopilotAgent> _logger;
19: 
20: 	internal CopilotAgent(
21: 			CopilotClient client,
22: 			string model,
23: 			string? systemPrompt,
24: 			Mcp[] mcps,
25: 			Subagent[] subagents,
26: 			ReasoningLevel? reasoningLevel,
27: 			SystemPromptMode? systemPromptMode,
28: 			IOrchestrationReporter reporter,
29: 			ILogger<CopilotAgent> logger)
30: 	{
31: 		_client = client;
32: 		_model = model;
33: 		_systemPrompt = systemPrompt;
34: 		_mcps = mcps;
35: 		_subagents = subagents;
36: 		_reasoningLevel = reasoningLevel;
37: 		_systemPromptMode = systemPromptMode;
38: 		_reporter = reporter;
39: 		_logger = logger;
40: 	}
41: 
42: 	public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
43: 	{
44: 		var channel = Channel.CreateUnbounded<AgentEvent>();
45: 		var resultTask = RunSessionAsync(prompt, channel.Writer, cancellationToken);
46: 		return new AgentTask(channel.Reader, resultTask);
47: 	}
48: 
49: 	private async Task<AgentResult> RunSessionAsync(
50: 			string prompt,
51: 			ChannelWriter<AgentEvent> writer,
52: 			CancellationToken cancellationToken)
53: 	{
54: 		try
55: 		{
56: 			var config = BuildSessionConfig();
57: 			LogMcpConfiguration();
58: 
59: 			await using var session = await _client.CreateSessionAsync(config, cancellationToken);
60: 
61: 			var done = new TaskCompletionSource();
62: 			var handler = new CopilotSessionHandler(writer, _reporter, _model, done);
63: 
64: 			session.On(handler.HandleEvent);
65: 
66: 			await session.SendAsync(new MessageOptions { Prompt = prompt }, cancellationToken);
67: 
68: 			using var registration = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
69: 			await done.Task;
70: 
71: 			// Handle model mismatch detection and reporting
72: 			var availableModels = await CheckModelMismatchAsync(handler.ActualModel, cancellationToken);
73: 
74: 			return new AgentResult
75: 			{
76: 				Content = handler.FinalContent ?? string.Empty,
77: 				SelectedModel = handler.SelectedModel,
78: 				ActualModel = handler.ActualModel,
79: 				Usage = handler.Usage,
80: 				AvailableModels = availableModels,
81: 			};
82: 		}
83: 		finally
84: 		{
85: 			writer.TryComplete();
86: 		}
87: 	}
88: 
89: 	private SessionConfig BuildSessionConfig()
90: 	{
91: 		var config = new SessionConfig
92: 		{
93: 			Model = _model,
94: 			Streaming = true,
95: 			OnPermissionRequest = PermissionHandler.ApproveAll,
96: 		};
97: 
98: 		if (_reasoningLevel is not null)
99: 		{
100: 			config.ReasoningEffort = _reasoningLevel.Value.ToString().ToLowerInvariant();
101: 		}
102: 
103: 		if (_systemPrompt is not null)
104: 		{
105: 			config.SystemMessage = new SystemMessageConfig
106: 			{
107: 				Content = _systemPrompt,
108: 			};
109: 
110: 			if (_systemPromptMode is not null)
111: 			{
112: 				config.SystemMessage.Mode = _systemPromptMode.Value == SystemPromptMode.Append
113: 					? SystemMessageMode.Append
114: 					: SystemMessageMode.Replace;
115: 			}
116: 		}
117: 
118: 		if (_mcps.Length > 0)
119: 		{
120: 			config.McpServers = BuildMcpServers();
121: 		}
122: 
123: 		if (_subagents.Length > 0)
124: 		{
125: 			config.CustomAgents = BuildCustomAgents();
126: 		}
127: 
128: 		return config;
129: 	}
130: 
131: 	private void LogMcpConfiguration()
132: 	{
133: 		LogMcpCount(_mcps.Length);
134: 
135: 		if (_mcps.Length > 0)
136: 		{
137: 			foreach (var mcp in _mcps)
138: 			{
139: 				switch (mcp)
140: 				{
141: 					case LocalMcp local:
142: 						LogLocalMcpServer(mcp.Name, local.Command, string.Join(", ", local.Arguments), local.WorkingDirectory);
143: 						break;
144: 					case RemoteMcp remote:
145: 						LogRemoteMcpServer(mcp.Name, remote.Endpoint);
146: 						break;
147: 				}
148: 			}
149: 		}
150: 		else
151: 		{
152: 			LogNoMcpsConfigured();
153: 		}
154: 	}
155: 
156: 	private async Task<IReadOnlyList<AvailableModelInfo>?> CheckModelMismatchAsync(
157: 		string? actualModel,
158: 		CancellationToken cancellationToken)
159: 	{
160: 		if (actualModel is null || string.Equals(actualModel, _model, StringComparison.OrdinalIgnoreCase))
161: 			return null;
162: 
163: 		IReadOnlyList<AvailableModelInfo>? availableModels = null;
164: 
165: 		try
166: 		{
167: 			var models = await _client.ListModelsAsync(cancellationToken);
168: 			availableModels = models
169: 				.OrderBy(m => m.Id)
170: 				.Select(m => new AvailableModelInfo
171: 				{
172: 					Id = m.Id,
173: 					Name = m.Name,
174: 					BillingMultiplier = m.Billing?.Multiplier,
175: 					ReasoningEfforts = m.SupportedReasoningEfforts is { Count: > 0 }
176: 						? [.. m.SupportedReasoningEfforts]
177: 						: null,
178: 				})
179: 				.ToList();
180: 		}
181: 		catch
182: 		{
183: 			// Unable to list models - continue without them
184: 		}
185: 
186: 		_reporter.ReportModelMismatch(new ModelMismatchInfo
187: 		{
188: 			ConfiguredModel = _model,
189: 			ActualModel = actualModel,
190: 			SystemPromptMode = _systemPromptMode?.ToString() ?? "(SDK default)",
191: 			ReasoningLevel = _reasoningLevel?.ToString() ?? "(none)",
192: 			SystemPromptPreview = _systemPrompt is not null
193: 				? $"{_systemPrompt[..Math.Min(_systemPrompt.Length, 80)]}..."
194: 				: "(none)",
195: 			McpServers = _mcps.Length > 0
196: 				? _mcps.Select(m => m.Name).ToArray()
197: 				: null,
198: 			AvailableModels = availableModels,
199: 		});
200: 
201: 		return availableModels;
202: 	}
203: 
204: 	private Dictionary<string, object> BuildMcpServers()
205: 	{
206: 		var servers = new Dictionary<string, object>();
207: 
208: 		foreach (var mcp in _mcps)
209: 		{
210: 			switch (mcp)
211: 			{
212: 				case LocalMcp local:
213: 					servers[mcp.Name] = new McpLocalServerConfig
214: 					{
215: 						Command = local.Command,
216: 						Args = [.. local.Arguments],
217: 						Cwd = local.WorkingDirectory,
218: 					};
219: 					break;
220: 
221: 				case RemoteMcp remote:
222: 					servers[mcp.Name] = new McpRemoteServerConfig
223: 					{
224: 						Url = remote.Endpoint,
225: 						Headers = remote.Headers,
226: 					};
227: 					break;
228: 			}
229: 		}
230: 
231: 		return servers;
232: 	}
233: 
234: 	private List<CustomAgentConfig> BuildCustomAgents()
235: 	{
236: 		var customAgents = new List<CustomAgentConfig>();
237: 
238: 		foreach (var subagent in _subagents)
239: 		{
240: 			var config = new CustomAgentConfig
241: 			{
242: 				Name = subagent.Name,
243: 				Prompt = subagent.Prompt,
244: 			};
245: 
246: 			if (subagent.DisplayName is not null)
247: 				config.DisplayName = subagent.DisplayName;
248: 
249: 			if (subagent.Description is not null)
250: 				config.Description = subagent.Description;
251: 
252: 			if (subagent.Tools is { Length: > 0 })
253: 				config.Tools = [.. subagent.Tools];
254: 
255: 			if (!subagent.Infer)
256: 				config.Infer = false;
257: 
258: 			// Add MCP servers specific to this subagent
259: 			if (subagent.Mcps.Length > 0)
260: 			{
261: 				config.McpServers = BuildMcpServersFor(subagent.Mcps);
262: 			}
263: 
264: 			customAgents.Add(config);
265: 		}
266: 
267: 		LogSubagentConfiguration();
268: 		return customAgents;
269: 	}
270: 
271: 	private Dictionary<string, object> BuildMcpServersFor(Mcp[] mcps)
272: 	{
273: 		var servers = new Dictionary<string, object>();
274: 
275: 		foreach (var mcp in mcps)
276: 		{
277: 			switch (mcp)
278: 			{
279: 				case LocalMcp local:
280: 					servers[mcp.Name] = new McpLocalServerConfig
281: 					{
282: 						Command = local.Command,
283: 						Args = [.. local.Arguments],
284: 						Cwd = local.WorkingDirectory,
285: 					};
286: 					break;
287: 
288: 				case RemoteMcp remote:
289: 					servers[mcp.Name] = new McpRemoteServerConfig
290: 					{
291: 						Url = remote.Endpoint,
292: 						Headers = remote.Headers,
293: 					};
294: 					break;
295: 			}
296: 		}
297: 
298: 		return servers;
299: 	}
300: 
301: 	private void LogSubagentConfiguration()
302: 	{
303: 		LogSubagentCount(_subagents.Length);
304: 
305: 		foreach (var subagent in _subagents)
306: 		{
307: 			LogSubagentDetails(
308: 				subagent.Name,
309: 				subagent.DisplayName ?? "(none)",
310: 				subagent.Tools is { Length: > 0 } ? string.Join(", ", subagent.Tools) : "all",
311: 				subagent.Mcps.Length,
312: 				subagent.Infer);
313: 		}
314: 	}
315: 
316: 	#region Source-Generated Logging
317: 
318: 	[LoggerMessage(
319: 			EventId = 1,
320: 			Level = LogLevel.Debug,
321: 			Message = "Agent has {McpCount} MCPs configured")]
322: 	private partial void LogMcpCount(int mcpCount);
323: 
324: 	[LoggerMessage(
325: 			EventId = 2,
326: 			Level = LogLevel.Debug,
327: 			Message = "Configuring local MCP server '{Name}': Command={Command}, Args=[{Args}], Cwd={WorkingDirectory}")]
328: 	private partial void LogLocalMcpServer(string name, string command, string args, string? workingDirectory);
329: 
330: 	[LoggerMessage(
331: 			EventId = 3,
332: 			Level = LogLevel.Debug,
333: 			Message = "Configuring remote MCP server '{Name}': Url={Url}")]
334: 	private partial void LogRemoteMcpServer(string name, string url);
335: 
336: 	[LoggerMessage(
337: 			EventId = 4,
338: 			Level = LogLevel.Debug,
339: 			Message = "No MCPs configured for this agent")]
340: 	private partial void LogNoMcpsConfigured();
341: 
342: 	[LoggerMessage(
343: 			EventId = 5,
344: 			Level = LogLevel.Debug,
345: 			Message = "Agent has {SubagentCount} subagents configured")]
346: 	private partial void LogSubagentCount(int subagentCount);
347: 
348: 	[LoggerMessage(
349: 			EventId = 6,
350: 			Level = LogLevel.Debug,
351: 			Message = "Configuring subagent '{Name}': DisplayName={DisplayName}, Tools=[{Tools}], McpCount={McpCount}, Infer={Infer}")]
352: 	private partial void LogSubagentDetails(string name, string displayName, string tools, int mcpCount, bool infer);
353: 
354: 	#endregion
355: }
```

**Key observations:**
- Implements `IAgent` from `Orchestra.Engine`
- Uses `partial class` for source-generated logging via `[LoggerMessage]`
- Wraps the GitHub Copilot SDK (`CopilotClient`, `SessionConfig`)
- Streams events via `System.Threading.Channels` (`Channel<AgentEvent>`)
- Supports MCP servers (local and remote), subagents, reasoning levels, system prompt modes
- Detects model mismatch between requested and actual model, reports via `IOrchestrationReporter`

---

## 3. Full Content of `CopilotAgentBuilder.cs`

**Path:** `P:\Github\Orchestra\src\Orchestra.Copilot\CopilotAgentBuilder.cs` (53 lines)

```csharp
1: using GitHub.Copilot.SDK;
2: using Microsoft.Extensions.Logging;
3: using Microsoft.Extensions.Logging.Abstractions;
4: using Orchestra.Engine;
5: 
6: namespace Orchestra.Copilot;
7: 
8: public class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
9: {
10: 	private CopilotClient _client = new ();
11: 	private readonly ILoggerFactory _loggerFactory;
12: 
13: 	public CopilotAgentBuilder(ILoggerFactory? loggerFactory = null)
14: 	{
15: 		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
16: 	}
17: 
18: 	public override async Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
19: 	{
20: 		ArgumentException.ThrowIfNullOrWhiteSpace(Model, nameof(Model));
21: 
22: 		// Capture state immediately to avoid race conditions with concurrent builder usage
23: 		var model = Model;
24: 		var systemPrompt = SystemPrompt;
25: 		var mcps = Mcps;
26: 		var subagents = Subagents;
27: 		var reasoningLevel = ReasoningLevel;
28: 		var systemPromptMode = SystemPromptMode;
29: 		var reporter = Reporter;
30: 
31: 		await _client.StartAsync(cancellationToken);
32: 
33: 		return new CopilotAgent(
34: 			client: _client,
35: 			model: model,
36: 			systemPrompt: systemPrompt,
37: 			mcps: mcps,
38: 			subagents: subagents,
39: 			reasoningLevel: reasoningLevel,
40: 			systemPromptMode: systemPromptMode,
41: 			reporter: reporter,
42: 			logger: _loggerFactory.CreateLogger<CopilotAgent>()
43: 		);
44: 	}
45: 
46: 	public async ValueTask DisposeAsync()
47: 	{
48: 		await _client.StopAsync();
49: 		await _client.DisposeAsync();
50: 
51: 		GC.SuppressFinalize(this);
52: 	}
53: }
```

**Key observations:**
- Extends `AgentBuilder` (from `Orchestra.Engine`) and implements `IAsyncDisposable`
- Builder pattern: captures state snapshot before building to avoid race conditions
- Manages the `CopilotClient` lifecycle (start/stop/dispose)
- Validates that `Model` is set before building

---

## 4. Full Content of `CopilotSessionHandler.cs`

**Path:** `P:\Github\Orchestra\src\Orchestra.Copilot\CopilotSessionHandler.cs` (306 lines)

```csharp
1: using System.Text.Json;
2: using System.Threading.Channels;
3: using GitHub.Copilot.SDK;
4: using Orchestra.Engine;
5: 
6: namespace Orchestra.Copilot;
7: 
8: /// <summary>
9: /// Handles Copilot SDK session events and translates them to engine-agnostic AgentEvents.
10: /// Extracted from CopilotAgent to reduce complexity and improve testability.
11: /// </summary>
12: internal sealed class CopilotSessionHandler
13: {
14: 	private readonly ChannelWriter<AgentEvent> _writer;
15: 	private readonly IOrchestrationReporter _reporter;
16: 	private readonly string _requestedModel;
17: 	private readonly TaskCompletionSource _done;
18: 	private readonly Dictionary<string, string> _toolCallNames = [];
19: 
20: 	private string? _finalContent;
21: 	private string? _selectedModel;
22: 	private string? _actualModel;
23: 	private AgentUsage? _usage;
24: 
25: 	public CopilotSessionHandler(
26: 		ChannelWriter<AgentEvent> writer,
27: 		IOrchestrationReporter reporter,
28: 		string requestedModel,
29: 		TaskCompletionSource done)
30: 	{
31: 		_writer = writer;
32: 		_reporter = reporter;
33: 		_requestedModel = requestedModel;
34: 		_done = done;
35: 	}
36: 
37: 	public string? FinalContent => _finalContent;
38: 	public string? SelectedModel => _selectedModel;
39: 	public string? ActualModel => _actualModel;
40: 	public AgentUsage? Usage => _usage;
41: 
42: 	/// <summary>
43: 	/// Handles a session event from the Copilot SDK.
44: 	/// </summary>
45: 	public void HandleEvent(SessionEvent evt)
46: 	{
47: 		switch (evt)
48: 		{
49: 			case SessionStartEvent start:
50: 				HandleSessionStart(start);
51: 				break;
52: 
53: 			case SessionModelChangeEvent modelChange:
54: 				HandleModelChange(modelChange);
55: 				break;
56: 
57: 			case AssistantUsageEvent usageEvt:
58: 				HandleUsage(usageEvt);
59: 				break;
60: 
61: 			case AssistantMessageDeltaEvent delta:
62: 				HandleMessageDelta(delta);
63: 				break;
64: 
65: 			case AssistantReasoningDeltaEvent reasoningDelta:
66: 				HandleReasoningDelta(reasoningDelta);
67: 				break;
68: 
69: 			case AssistantMessageEvent msg:
70: 				HandleMessage(msg);
71: 				break;
72: 
73: 			case AssistantReasoningEvent reasoning:
74: 				HandleReasoning(reasoning);
75: 				break;
76: 
77: 			case ToolExecutionStartEvent toolStart:
78: 				HandleToolExecutionStart(toolStart);
79: 				break;
80: 
81: 			case ToolExecutionCompleteEvent toolComplete:
82: 				HandleToolExecutionComplete(toolComplete);
83: 				break;
84: 
85: 			case SubagentSelectedEvent subagentSelected:
86: 				HandleSubagentSelected(subagentSelected);
87: 				break;
88: 
89: 			case SubagentStartedEvent subagentStarted:
90: 				HandleSubagentStarted(subagentStarted);
91: 				break;
92: 
93: 			case SubagentCompletedEvent subagentCompleted:
94: 				HandleSubagentCompleted(subagentCompleted);
95: 				break;
96: 
97: 			case SubagentFailedEvent subagentFailed:
98: 				HandleSubagentFailed(subagentFailed);
99: 				break;
100: 
101: 			case SubagentDeselectedEvent:
102: 				HandleSubagentDeselected();
103: 				break;
104: 
105: 			case SessionErrorEvent err:
106: 				HandleError(err);
107: 				break;
108: 
109: 			case SessionIdleEvent:
110: 				HandleIdle();
111: 				break;
112: 		}
113: 	}
114: 
115: 	private void HandleSessionStart(SessionStartEvent start)
116: 	{
117: 		_selectedModel = start.Data.SelectedModel;
118: 		_reporter.ReportSessionStarted(_requestedModel, _selectedModel);
119: 		_writer.TryWrite(new AgentEvent
120: 		{
121: 			Type = AgentEventType.SessionStart,
122: 			Model = _selectedModel,
123: 		});
124: 	}
125: 
126: 	private void HandleModelChange(SessionModelChangeEvent modelChange)
127: 	{
128: 		_reporter.ReportModelChange(modelChange.Data.PreviousModel, modelChange.Data.NewModel);
129: 		_writer.TryWrite(new AgentEvent
130: 		{
131: 			Type = AgentEventType.ModelChange,
132: 			Model = modelChange.Data.NewModel,
133: 			PreviousModel = modelChange.Data.PreviousModel,
134: 		});
135: 	}
136: 
137: 	private void HandleUsage(AssistantUsageEvent usageEvt)
138: 	{
139: 		_actualModel = usageEvt.Data.Model;
140: 		_usage = new AgentUsage
141: 		{
142: 			InputTokens = usageEvt.Data.InputTokens,
143: 			OutputTokens = usageEvt.Data.OutputTokens,
144: 			CacheReadTokens = usageEvt.Data.CacheReadTokens,
145: 			CacheWriteTokens = usageEvt.Data.CacheWriteTokens,
146: 			Cost = usageEvt.Data.Cost,
147: 			Duration = usageEvt.Data.Duration,
148: 		};
149: 		_writer.TryWrite(new AgentEvent
150: 		{
151: 			Type = AgentEventType.Usage,
152: 			Model = _actualModel,
153: 			Usage = _usage,
154: 		});
155: 	}
156: 
157: 	private void HandleMessageDelta(AssistantMessageDeltaEvent delta)
158: 	{
159: 		_writer.TryWrite(new AgentEvent
160: 		{
161: 			Type = AgentEventType.MessageDelta,
162: 			Content = delta.Data.DeltaContent,
163: 		});
164: 	}
165: 
166: 	private void HandleReasoningDelta(AssistantReasoningDeltaEvent reasoningDelta)
167: 	{
168: 		_writer.TryWrite(new AgentEvent
169: 		{
170: 			Type = AgentEventType.ReasoningDelta,
171: 			Content = reasoningDelta.Data.DeltaContent,
172: 		});
173: 	}
174: 
175: 	private void HandleMessage(AssistantMessageEvent msg)
176: 	{
177: 		_finalContent = msg.Data.Content;
178: 		_writer.TryWrite(new AgentEvent
179: 		{
180: 			Type = AgentEventType.Message,
181: 			Content = msg.Data.Content,
182: 		});
183: 	}
184: 
185: 	private void HandleReasoning(AssistantReasoningEvent reasoning)
186: 	{
187: 		_writer.TryWrite(new AgentEvent
188: 		{
189: 			Type = AgentEventType.Reasoning,
190: 			Content = reasoning.Data.Content,
191: 		});
192: 	}
193: 
194: 	private void HandleToolExecutionStart(ToolExecutionStartEvent toolStart)
195: 	{
196: 		var toolName = toolStart.Data.McpToolName ?? toolStart.Data.ToolName;
197: 		if (toolStart.Data.ToolCallId is not null)
198: 			_toolCallNames[toolStart.Data.ToolCallId] = toolName;
199: 
200: 		string? serializedArgs = null;
201: 		if (toolStart.Data.Arguments is not null)
202: 		{
203: 			try { serializedArgs = JsonSerializer.Serialize(toolStart.Data.Arguments); }
204: 			catch { /* ignore serialization failures - arguments are optional for trace */ }
205: 		}
206: 
207: 		_writer.TryWrite(new AgentEvent
208: 		{
209: 			Type = AgentEventType.ToolExecutionStart,
210: 			ToolCallId = toolStart.Data.ToolCallId,
211: 			ToolName = toolName,
212: 			ToolArguments = serializedArgs,
213: 			McpServerName = toolStart.Data.McpServerName,
214: 		});
215: 	}
216: 
217: 	private void HandleToolExecutionComplete(ToolExecutionCompleteEvent toolComplete)
218: 	{
219: 		// Correlate tool name from start event via ToolCallId
220: 		string? toolName = null;
221: 		if (toolComplete.Data.ToolCallId is not null)
222: 			_toolCallNames.TryGetValue(toolComplete.Data.ToolCallId, out toolName);
223: 
224: 		_writer.TryWrite(new AgentEvent
225: 		{
226: 			Type = AgentEventType.ToolExecutionComplete,
227: 			ToolCallId = toolComplete.Data.ToolCallId,
228: 			ToolName = toolName,
229: 			ToolSuccess = toolComplete.Data.Success,
230: 			ToolResult = toolComplete.Data.Result?.Content ?? toolComplete.Data.Result?.DetailedContent,
231: 			ToolError = toolComplete.Data.Error?.Message,
232: 		});
233: 	}
234: 
235: 	private void HandleSubagentSelected(SubagentSelectedEvent subagentSelected)
236: 	{
237: 		_writer.TryWrite(new AgentEvent
238: 		{
239: 			Type = AgentEventType.SubagentSelected,
240: 			SubagentName = subagentSelected.Data.AgentName,
241: 			SubagentDisplayName = subagentSelected.Data.AgentDisplayName,
242: 			SubagentTools = subagentSelected.Data.Tools,
243: 		});
244: 	}
245: 
246: 	private void HandleSubagentStarted(SubagentStartedEvent subagentStarted)
247: 	{
248: 		_writer.TryWrite(new AgentEvent
249: 		{
250: 			Type = AgentEventType.SubagentStarted,
251: 			ToolCallId = subagentStarted.Data.ToolCallId,
252: 			SubagentName = subagentStarted.Data.AgentName,
253: 			SubagentDisplayName = subagentStarted.Data.AgentDisplayName,
254: 			SubagentDescription = subagentStarted.Data.AgentDescription,
255: 		});
256: 	}
257: 
258: 	private void HandleSubagentCompleted(SubagentCompletedEvent subagentCompleted)
259: 	{
260: 		_writer.TryWrite(new AgentEvent
261: 		{
262: 			Type = AgentEventType.SubagentCompleted,
263: 			ToolCallId = subagentCompleted.Data.ToolCallId,
264: 			SubagentName = subagentCompleted.Data.AgentName,
265: 			SubagentDisplayName = subagentCompleted.Data.AgentDisplayName,
266: 		});
267: 	}
268: 
269: 	private void HandleSubagentFailed(SubagentFailedEvent subagentFailed)
270: 	{
271: 		_writer.TryWrite(new AgentEvent
272: 		{
273: 			Type = AgentEventType.SubagentFailed,
274: 			ToolCallId = subagentFailed.Data.ToolCallId,
275: 			SubagentName = subagentFailed.Data.AgentName,
276: 			SubagentDisplayName = subagentFailed.Data.AgentDisplayName,
277: 			ErrorMessage = subagentFailed.Data.Error,
278: 		});
279: 	}
280: 
281: 	private void HandleSubagentDeselected()
282: 	{
283: 		_writer.TryWrite(new AgentEvent
284: 		{
285: 			Type = AgentEventType.SubagentDeselected,
286: 		});
287: 	}
288: 
289: 	private void HandleError(SessionErrorEvent err)
290: 	{
291: 		_writer.TryWrite(new AgentEvent
292: 		{
293: 			Type = AgentEventType.Error,
294: 			ErrorMessage = err.Data.Message,
295: 		});
296: 	}
297: 
298: 	private void HandleIdle()
299: 	{
300: 		_writer.TryWrite(new AgentEvent
301: 		{
302: 			Type = AgentEventType.SessionIdle,
303: 		});
304: 		_done.TrySetResult();
305: 	}
306: }
```

**Key observations:**
- Internal sealed class -- only accessible within the assembly (and test project via `InternalsVisibleTo`)
- Translates Copilot SDK `SessionEvent` types into engine-agnostic `AgentEvent` types
- Handles 15+ event types: session start, model change, usage, message/reasoning deltas, tool execution, subagent lifecycle, errors, idle
- Correlates tool call IDs between start and complete events via `_toolCallNames` dictionary
- Signals session completion via `TaskCompletionSource._done.TrySetResult()`

---

## 5. Full File Listing of All Test Projects

### 6 Test Projects total:

| Project | Path |
|---------|------|
| Orchestra.Engine.Tests | `P:\Github\Orchestra\tests\Orchestra.Engine.Tests\` |
| Orchestra.Copilot.Tests | `P:\Github\Orchestra\tests\Orchestra.Copilot.Tests\` |
| Orchestra.Host.Tests | `P:\Github\Orchestra\tests\Orchestra.Host.Tests\` |
| Orchestra.Portal.Tests | `P:\Github\Orchestra\tests\Orchestra.Portal.Tests\` |
| Orchestra.Portal.E2E | `P:\Github\Orchestra\tests\Orchestra.Portal.E2E\` |
| Orchestra.Mcp.Graph.Tests | `P:\Github\Orchestra\tests\Orchestra.Mcp.Graph.Tests\` |

### All Test Source Files (37 .cs files):

```
tests/Orchestra.Engine.Tests/
  Domain/ExecutionResultTests.cs
  Domain/OrchestrationResultTests.cs
  Domain/OrchestrationTests.cs
  Executor/AgentEventProcessorTests.cs
  Executor/OrchestrationExecutionContextTests.cs
  Executor/OrchestrationExecutorTests.cs
  Executor/PromptExecutorTests.cs
  Executor/TimeoutEnforcementTests.cs
  Formatting/DefaultPromptFormatterTests.cs
  Scheduler/OrchestrationSchedulerTests.cs
  Serialization/OrchestrationParserTests.cs
  Serialization/TimeoutParsingTests.cs
  Storage/NullRunStoreTests.cs
  TestHelpers/MockAgentBuilder.cs
  TestHelpers/TestOrchestrations.cs
  GlobalUsings.cs

tests/Orchestra.Copilot.Tests/
  CopilotAgentTests.cs
  CopilotAgentBuilderTests.cs
  CopilotSessionHandlerTests.cs
  GlobalUsings.cs

tests/Orchestra.Host.Tests/
  CronCalculationTests.cs
  FileLoggingTests.cs
  FileSystemRunStoreTests.cs
  HealthCheckTests.cs
  OrchestrationRegistryTests.cs
  TriggerTypesTests.cs

tests/Orchestra.Portal.Tests/
  PortalWebApplicationFactory.cs
  WebhookTriggerTests.cs

tests/Orchestra.Portal.E2E/
  PlaywrightTestBase.cs
  PortalServerFixture.cs
  WebhookTriggerUiTests.cs

tests/Orchestra.Mcp.Graph.Tests/
  Authentication/DualTokenProviderTests.cs
  Authentication/TokenCacheTests.cs
  GraphOptionsTests.cs
  GraphScopesTests.cs
  Services/GraphHelpersTests.cs
  Tools/CalendarToolsTests.cs
```

---

## 6. Sampling of Key Test Files

Four representative test files were read in full. Here is a summary of the testing patterns:

### Test File 1: `CopilotSessionHandlerTests.cs` (900 lines)
- **Pattern:** xUnit `[Fact]` tests with NSubstitute mocks and FluentAssertions
- **Structure:** Constructor-based test setup, `#region` grouping by feature
- **Approach:** Creates SDK event objects via static helper factory methods, feeds them to the handler, then verifies the `Channel<AgentEvent>` output and property mutations
- **Coverage:** Every SDK event type is tested (session start, model change, usage, message delta, reasoning delta, tool execution, subagent lifecycle, error, idle), plus full session flow integration tests

### Test File 2: `CopilotAgentBuilderTests.cs` (267 lines)
- **Pattern:** Fluent API testing - verifies builder method chaining returns `BeSameAs(builder)`
- **Coverage:** Validation (null/empty/whitespace model throws `ArgumentException`), constructor null safety, `DisposeAsync` idempotency, fluent API chaining

### Test File 3: `OrchestrationExecutorTests.cs` (530 lines)
- **Pattern:** Integration-style unit tests using `MockAgentBuilder` (custom test double) and NSubstitute for `IOrchestrationReporter`/`IRunStore`
- **Coverage:** Single step, linear chain, parallel steps, diamond DAG, complex DAG, error handling (downstream skipping), parameter validation, cycle detection, cancellation, run store persistence, terminal step identification

### Test File 4: `OrchestrationParserTests.cs` (990 lines)
- **Pattern:** JSON parsing tests using raw string literals (C# 11 `"""` syntax)
- **Coverage:** Full orchestration parsing, step field extraction, MCP resolution (inline/external/override/missing), trigger types (scheduler/webhook/loop/email), subagent parsing, metadata-only parsing, error handling (invalid JSON, missing type, unknown type)

### Common Testing Patterns Across the Project:
1. **Framework:** xUnit 2.9.2 with `[Fact]` attributes (no `[Theory]` observed in sampled files)
2. **Assertions:** FluentAssertions 7.0.0 (`Should().Be()`, `Should().HaveCount()`, `Should().ThrowAsync<>()`)
3. **Mocking:** NSubstitute 5.3.0 (`Substitute.For<T>()`, `Received()`, `Arg.Any<T>()`)
4. **Organization:** `#region` blocks for grouping related tests
5. **Naming:** `MethodName_Condition_ExpectedBehavior` convention
6. **Setup:** Constructor injection for shared state, static factory methods for test data

---

## 7. Full Content of All .csproj Files

### 7.1 Source Projects

**`P:\Github\Orchestra\src\Orchestra.Engine\Orchestra.Engine.csproj`** (11 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <RootNamespace>Orchestra.Engine</RootNamespace>
5:   </PropertyGroup>
6: 
7:   <ItemGroup>
8:     <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
9:   </ItemGroup>
10: 
11: </Project>
```

**`P:\Github\Orchestra\src\Orchestra.Copilot\Orchestra.Copilot.csproj`** (15 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <ItemGroup>
4:     <ProjectReference Include="..\Orchestra.Engine\Orchestra.Engine.csproj" />
5:   </ItemGroup>
6: 
7:   <ItemGroup>
8:     <PackageReference Include="GitHub.Copilot.SDK" />
9:   </ItemGroup>
10: 
11:   <ItemGroup>
12:     <InternalsVisibleTo Include="Orchestra.Copilot.Tests" />
13:   </ItemGroup>
14: 
15: </Project>
```

**`P:\Github\Orchestra\src\Orchestra.Host\Orchestra.Host.csproj`** (19 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <RootNamespace>Orchestra.Host</RootNamespace>
5:   </PropertyGroup>
6: 
7:   <ItemGroup>
8:     <ProjectReference Include="..\Orchestra.Engine\Orchestra.Engine.csproj" />
9:   </ItemGroup>
10: 
11:   <ItemGroup>
12:     <FrameworkReference Include="Microsoft.AspNetCore.App" />
13:   </ItemGroup>
14: 
15:   <ItemGroup>
16:     <PackageReference Include="Cronos" />
17:   </ItemGroup>
18: 
19: </Project>
```

**`P:\Github\Orchestra\src\Orchestra.Mcp.Graph\Orchestra.Mcp.Graph.csproj`** (15 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <OutputType>Exe</OutputType>
5:     <RootNamespace>Orchestra.Mcp.Graph</RootNamespace>
6:   </PropertyGroup>
7: 
8:   <ItemGroup>
9:     <PackageReference Include="ModelContextProtocol" />
10:     <PackageReference Include="Microsoft.Extensions.Hosting" />
11:     <PackageReference Include="Microsoft.Extensions.Http" />
12:     <PackageReference Include="Microsoft.Extensions.Options" />
13:   </ItemGroup>
14: 
15: </Project>
```

### 7.2 Playground Projects

**`P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot\Orchestra.Playground.Copilot.csproj`** (17 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <OutputType>Exe</OutputType>
5:     <RootNamespace>Orchestra.Playground.Copilot</RootNamespace>
6:   </PropertyGroup>
7: 
8:   <ItemGroup>
9:     <ProjectReference Include="..\..\..\src\Orchestra.Engine\Orchestra.Engine.csproj" />
10:     <ProjectReference Include="..\..\..\src\Orchestra.Copilot\Orchestra.Copilot.csproj" />
11:   </ItemGroup>
12: 
13:   <ItemGroup>
14:     <PackageReference Include="Microsoft.Extensions.Hosting" />
15:   </ItemGroup>
16: 
17: </Project>
```

**`P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Web\Orchestra.Playground.Copilot.Web.csproj`** (12 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk.Web">
2: 
3:   <PropertyGroup>
4:     <RootNamespace>Orchestra.Playground.Copilot.Web</RootNamespace>
5:   </PropertyGroup>
6: 
7:   <ItemGroup>
8:     <ProjectReference Include="..\..\..\src\Orchestra.Engine\Orchestra.Engine.csproj" />
9:     <ProjectReference Include="..\..\..\src\Orchestra.Copilot\Orchestra.Copilot.csproj" />
10:   </ItemGroup>
11: 
12: </Project>
```

**`P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Portal\Orchestra.Playground.Copilot.Portal.csproj`** (15 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk.Web">
2: 
3:   <PropertyGroup>
4:     <TargetFramework>net10.0-windows</TargetFramework>
5:     <RootNamespace>Orchestra.Playground.Copilot.Portal</RootNamespace>
6:     <UseWindowsForms>true</UseWindowsForms>
7:   </PropertyGroup>
8: 
9:   <ItemGroup>
10:     <ProjectReference Include="..\..\..\src\Orchestra.Engine\Orchestra.Engine.csproj" />
11:     <ProjectReference Include="..\..\..\src\Orchestra.Copilot\Orchestra.Copilot.csproj" />
12:     <ProjectReference Include="..\..\..\src\Orchestra.Host\Orchestra.Host.csproj" />
13:   </ItemGroup>
14: 
15: </Project>
```

**`P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Terminal\Orchestra.Playground.Copilot.Terminal.csproj`** (19 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <OutputType>Exe</OutputType>
5:     <RootNamespace>Orchestra.Playground.Copilot.Terminal</RootNamespace>
6:   </PropertyGroup>
7: 
8:   <ItemGroup>
9:     <ProjectReference Include="..\..\..\src\Orchestra.Engine\Orchestra.Engine.csproj" />
10:     <ProjectReference Include="..\..\..\src\Orchestra.Copilot\Orchestra.Copilot.csproj" />
11:     <ProjectReference Include="..\..\..\src\Orchestra.Host\Orchestra.Host.csproj" />
12:   </ItemGroup>
13: 
14:   <ItemGroup>
15:     <PackageReference Include="Microsoft.Extensions.Hosting" />
16:     <PackageReference Include="Spectre.Console" />
17:   </ItemGroup>
18: 
19: </Project>
```

### 7.3 Test Projects

**`P:\Github\Orchestra\tests\Orchestra.Engine.Tests\Orchestra.Engine.Tests.csproj`** (24 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <IsPackable>false</IsPackable>
5:     <IsTestProject>true</IsTestProject>
6:   </PropertyGroup>
7: 
8:   <ItemGroup>
9:     <PackageReference Include="Microsoft.NET.Test.Sdk" />
10:     <PackageReference Include="xunit" />
11:     <PackageReference Include="xunit.runner.visualstudio">
12:       <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
13:       <PrivateAssets>all</PrivateAssets>
14:     </PackageReference>
15:     <PackageReference Include="FluentAssertions" />
16:     <PackageReference Include="NSubstitute" />
17:     <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
18:   </ItemGroup>
19: 
20:   <ItemGroup>
21:     <ProjectReference Include="..\..\src\Orchestra.Engine\Orchestra.Engine.csproj" />
22:   </ItemGroup>
23: 
24: </Project>
```

**`P:\Github\Orchestra\tests\Orchestra.Copilot.Tests\Orchestra.Copilot.Tests.csproj`** (24 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <IsPackable>false</IsPackable>
5:     <IsTestProject>true</IsTestProject>
6:   </PropertyGroup>
7: 
8:   <ItemGroup>
9:     <PackageReference Include="Microsoft.NET.Test.Sdk" />
10:     <PackageReference Include="xunit" />
11:     <PackageReference Include="xunit.runner.visualstudio">
12:       <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
13:       <PrivateAssets>all</PrivateAssets>
14:     </PackageReference>
15:     <PackageReference Include="FluentAssertions" />
16:     <PackageReference Include="NSubstitute" />
17:     <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
18:   </ItemGroup>
19: 
20:   <ItemGroup>
21:     <ProjectReference Include="..\..\src\Orchestra.Copilot\Orchestra.Copilot.csproj" />
22:   </ItemGroup>
23: 
24: </Project>
```

**`P:\Github\Orchestra\tests\Orchestra.Host.Tests\Orchestra.Host.Tests.csproj`** (24 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <IsPackable>false</IsPackable>
5:     <IsTestProject>true</IsTestProject>
6:   </PropertyGroup>
7: 
8:   <ItemGroup>
9:     <PackageReference Include="Microsoft.NET.Test.Sdk" />
10:     <PackageReference Include="xunit" />
11:     <PackageReference Include="xunit.runner.visualstudio">
12:       <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
13:       <PrivateAssets>all</PrivateAssets>
14:     </PackageReference>
15:     <PackageReference Include="FluentAssertions" />
16:     <PackageReference Include="NSubstitute" />
17:     <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
18:   </ItemGroup>
19: 
20:   <ItemGroup>
21:     <ProjectReference Include="..\..\src\Orchestra.Host\Orchestra.Host.csproj" />
22:   </ItemGroup>
23: 
24: </Project>
```

**`P:\Github\Orchestra\tests\Orchestra.Portal.Tests\Orchestra.Portal.Tests.csproj`** (24 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <TargetFramework>net10.0-windows</TargetFramework>
5:     <IsPackable>false</IsPackable>
6:     <IsTestProject>true</IsTestProject>
7:   </PropertyGroup>
8: 
9:   <ItemGroup>
10:     <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
11:     <PackageReference Include="Microsoft.NET.Test.Sdk" />
12:     <PackageReference Include="xunit" />
13:     <PackageReference Include="xunit.runner.visualstudio">
14:       <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
15:       <PrivateAssets>all</PrivateAssets>
16:     </PackageReference>
17:     <PackageReference Include="FluentAssertions" />
18:   </ItemGroup>
19: 
20:   <ItemGroup>
21:     <ProjectReference Include="..\..\playground\Hosting\Orchestra.Playground.Copilot.Portal\Orchestra.Playground.Copilot.Portal.csproj" />
22:   </ItemGroup>
23: 
24: </Project>
```

**`P:\Github\Orchestra\tests\Orchestra.Portal.E2E\Orchestra.Portal.E2E.csproj`** (25 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <TargetFramework>net10.0-windows</TargetFramework>
5:     <IsPackable>false</IsPackable>
6:     <IsTestProject>true</IsTestProject>
7:   </PropertyGroup>
8: 
9:   <ItemGroup>
10:     <PackageReference Include="Microsoft.NET.Test.Sdk" />
11:     <PackageReference Include="Microsoft.Playwright" />
12:     <PackageReference Include="Microsoft.Playwright.Xunit" />
13:     <PackageReference Include="xunit" />
14:     <PackageReference Include="xunit.runner.visualstudio">
15:       <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
16:       <PrivateAssets>all</PrivateAssets>
17:     </PackageReference>
18:     <PackageReference Include="FluentAssertions" />
19:   </ItemGroup>
20: 
21:   <ItemGroup>
22:     <ProjectReference Include="..\..\playground\Hosting\Orchestra.Playground.Copilot.Portal\Orchestra.Playground.Copilot.Portal.csproj" />
23:   </ItemGroup>
24: 
25: </Project>
```

**`P:\Github\Orchestra\tests\Orchestra.Mcp.Graph.Tests\Orchestra.Mcp.Graph.Tests.csproj`** (23 lines)
```xml
1: <Project Sdk="Microsoft.NET.Sdk">
2: 
3:   <PropertyGroup>
4:     <IsPackable>false</IsPackable>
5:     <IsTestProject>true</IsTestProject>
6:   </PropertyGroup>
7: 
8:   <ItemGroup>
9:     <PackageReference Include="Microsoft.NET.Test.Sdk" />
10:     <PackageReference Include="xunit" />
11:     <PackageReference Include="xunit.runner.visualstudio">
12:       <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
13:       <PrivateAssets>all</PrivateAssets>
14:     </PackageReference>
15:     <PackageReference Include="FluentAssertions" />
16:     <PackageReference Include="NSubstitute" />
17:   </ItemGroup>
18: 
19:   <ItemGroup>
20:     <ProjectReference Include="..\..\src\Orchestra.Mcp.Graph\Orchestra.Mcp.Graph.csproj" />
21:   </ItemGroup>
22: 
23: </Project>
```

---

## 8. Full Content of `Directory.Packages.props`

**Path:** `P:\Github\Orchestra\Directory.Packages.props` (36 lines)

```xml
1: <Project>
2: 
3:   <PropertyGroup>
4:     <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
5:   </PropertyGroup>
6: 
7:   <!-- Shared dependency versions -->
8:   <ItemGroup>
9:     <!-- Core -->
10:     <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.2" />
11:     <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.2" />
12:     <PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.2" />
13:     <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.2" />
14: 
15:     <!-- LLM / MCP -->
16:     <PackageVersion Include="GitHub.Copilot.SDK" Version="0.1.29" />
17:     <PackageVersion Include="ModelContextProtocol" Version="0.2.0-preview.2" />
18: 
19:     <!-- Scheduling -->
20:     <PackageVersion Include="Cronos" Version="0.8.4" />
21: 
22:     <!-- UI -->
23:     <PackageVersion Include="Spectre.Console" Version="0.50.0" />
24: 
25:     <!-- Testing -->
26:     <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
27:     <PackageVersion Include="xunit" Version="2.9.2" />
28:     <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.0" />
29:     <PackageVersion Include="FluentAssertions" Version="7.0.0" />
30:     <PackageVersion Include="NSubstitute" Version="5.3.0" />
31:     <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0-preview.1.25120.3" />
32:     <PackageVersion Include="Microsoft.Playwright" Version="1.50.0" />
33:     <PackageVersion Include="Microsoft.Playwright.Xunit" Version="1.50.0" />
34:   </ItemGroup>
35: 
36: </Project>
```

Also relevant -- **`Directory.Build.props`** (9 lines):
```xml
1: <Project>
2: 
3:   <PropertyGroup>
4:     <TargetFramework>net10.0</TargetFramework>
5:     <ImplicitUsings>enable</ImplicitUsings>
6:     <Nullable>enable</Nullable>
7:   </PropertyGroup>
8: 
9: </Project>
```

---

## 9. Full Content of the Solution File

**Path:** `P:\Github\Orchestra\OrchestrationEngine.slnx` (22 lines)

Note: This is an `.slnx` file (XML-based solution format), not a traditional `.sln`.

```xml
1: <Solution>
2:   <Folder Name="/src/">
3:     <Project Path="src/Orchestra.Engine/Orchestra.Engine.csproj" />
4:     <Project Path="src/Orchestra.Copilot/Orchestra.Copilot.csproj" />
5:     <Project Path="src/Orchestra.Mcp.Graph/Orchestra.Mcp.Graph.csproj" />
6:     <Project Path="src/Orchestra.Host/Orchestra.Host.csproj" />
7:   </Folder>
8:   <Folder Name="/playground/">
9:     <Project Path="playground/Hosting/Orchestra.Playground.Copilot/Orchestra.Playground.Copilot.csproj" />
10:     <Project Path="playground/Hosting/Orchestra.Playground.Copilot.Web/Orchestra.Playground.Copilot.Web.csproj" />
11:     <Project Path="playground/Hosting/Orchestra.Playground.Copilot.Portal/Orchestra.Playground.Copilot.Portal.csproj" />
12:     <Project Path="playground/Hosting/Orchestra.Playground.Copilot.Terminal/Orchestra.Playground.Copilot.Terminal.csproj" />
13:   </Folder>
14:   <Folder Name="/tests/">
15:     <Project Path="tests/Orchestra.Portal.Tests/Orchestra.Portal.Tests.csproj" />
16:     <Project Path="tests/Orchestra.Portal.E2E/Orchestra.Portal.E2E.csproj" />
17:     <Project Path="tests/Orchestra.Engine.Tests/Orchestra.Engine.Tests.csproj" />
18:     <Project Path="tests/Orchestra.Mcp.Graph.Tests/Orchestra.Mcp.Graph.Tests.csproj" />
19:     <Project Path="tests/Orchestra.Copilot.Tests/Orchestra.Copilot.Tests.csproj" />
20:     <Project Path="tests/Orchestra.Host.Tests/Orchestra.Host.Tests.csproj" />
21:   </Folder>
22: </Solution>
```

---

## Architecture Summary

The Orchestra project is an **AI orchestration engine** targeting **.NET 10.0** with the following layered architecture:

```
Orchestra.Engine          -- Core engine: orchestration model, DAG execution, parsing, scheduling
  |
  +-- Orchestra.Copilot   -- GitHub Copilot SDK integration (agent implementation)
  +-- Orchestra.Host      -- ASP.NET Core hosting, triggers (cron/webhook/loop/email), file storage
  +-- Orchestra.Mcp.Graph -- Microsoft Graph MCP server (standalone exe)
```

**14 projects** total: 4 source libraries, 4 playground apps, 6 test projects. Uses central package management, source-generated logging, and channel-based streaming for agent events.
</task_result>
```

**Tool: task**

**Input:**
```json
{
  "description": "Explore frontend and playground",
  "prompt": "Explore the Orchestra project at P:\\Github\\Orchestra in READ-ONLY mode. I need detailed information about:\n\n1. Full content of playground/Hosting/Orchestra.Playground.Copilot.Portal/Program.cs\n2. Full content of playground/Hosting/Orchestra.Playground.Copilot.Portal/wwwroot/index.html (this is the React SPA)\n3. Full content of playground/Hosting/Orchestra.Playground.Copilot.Web/Program.cs (especially the embedded HTML/JS/CSS)\n4. Full content of playground/Hosting/Orchestra.Playground.Copilot.Web/wwwroot/index.html if it exists\n5. Full content of playground/Hosting/Orchestra.Playground.Copilot.Terminal/TerminalUI.cs\n6. Full file listing of all playground directories\n\nFocus especially on the frontend HTML/CSS/JS embedded in these files. Return as much content as possible with line numbers.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_2fa6270faffeIK6sezAHRgCEh1 (for resuming to continue this task if needed)

<task_result>
Here is a comprehensive summary of what has been done so far in the **Orchestra** project:

---

## What Has Been Done So Far

### Overview
**Orchestra** is a declarative orchestration engine for LLM workflows, built over approximately 6 weeks (Feb 6 - Mar 19, 2026) by Moaid Hathot, totaling **277 files** and **~51,500 lines of code** across 30 commits.

---

### Phase 1: Foundation (Feb 6-7) -- 5 commits
- **Initial commit** and project scaffolding
- Created the **Copilot skeleton** (`Orchestra.Copilot` library) -- the agent implementation that wraps GitHub Copilot's SDK
- Built the **playground skeleton** -- first console runner application
- Got a **working proof-of-concept** -- end-to-end orchestration execution with LLM calls

### Phase 2: Core Engine Features (Feb 7-10) -- 5 commits
- Added **more configurations** (model selection, parameters, system prompts)
- Built the **improvements** to the orchestration model (steps, dependencies, MCP support)
- Added **more features** (DAG scheduling with Kahn's algorithm, parameter injection with `{{paramName}}`, dependency output passing)
- Implemented the **loop/checker pattern** -- iterative step refinement where a checker step examines a target step's output and re-runs it with feedback until an exit pattern is found

### Phase 3: Web Experience (Feb 11-20) -- 4 commits
- Got orchestrations **kind-of-working** end-to-end with the web UI
- Made it **better** with improved UI/UX
- Built out the full **implementation** of the standalone Web playground (`Orchestra.Playground.Copilot.Web`) -- a 1,714-line `Program.cs` with embedded file browser, orchestration editor, trigger management, Mermaid DAG visualization, and SSE streaming execution
- The Web UI (`index.html`, 5,319 lines) is a vanilla JS SPA with dark GitHub-style theme, two modes (single file + folder mode), multi-model comparison runner, and live DAG rendering with status coloring

### Phase 4: MCP Graph Integration (Feb 26) -- 2 commits
- Built **Orchestra.Mcp.Graph** -- a full Microsoft Graph MCP server (17 files) providing tools for:
  - **Mail**: search, read, send, reply, move, flag emails
  - **Calendar**: list events, create, update, delete, find free/busy
  - **Teams Chat**: list chats, send messages
  - **Copilot**: interact with Microsoft 365 Copilot
  - **Users**: get user profile info
- Includes dual authentication (Azure CLI + OAuth with PKCE), token caching, and configurable scopes

### Phase 5: Major Refactoring & Shared Library (Mar 2) -- 5 commits
- Extracted the shared **Orchestra.Host** library (17 files) from the monolithic Web playground -- DI registration, API endpoints, trigger management, file-system persistence, orchestration registry
- Extracted the **Orchestra.Engine** library (47 files) -- the pure orchestration engine with no hosting dependencies
- The architecture became layered: `Engine` (core) -> `Copilot` (agent impl) -> `Host` (hosting/API) -> Playgrounds (UI shells)
- Significant refactoring of the executor to use **push-based dependency resolution** (steps launch immediately when all deps complete via reverse dependency map, rather than layer-by-layer execution)

### Phase 6: Improvements & Subagents (Mar 2-11) -- 2 commits
- Added **improvements** across the board (model support, reporting, error handling)
- Implemented **subagents** -- steps can now delegate to multiple named sub-agents with their own prompts, tools, MCP servers, and the ability to infer which subagent to use. Supports `SubagentSelected/Started/Completed/Failed/Deselected` event types

### Phase 7: Portal & Polish (Mar 17-19) -- 4 commits
- Added **.gitignore** rules
- Built the **Portal** playground (`Orchestra.Playground.Copilot.Portal`) -- the newest, cleanest web UI using the shared `Orchestra.Host` library. Only 144 lines of `Program.cs` plus a 5,245-line React SPA loaded via CDN (React 18, Babel standalone, Mermaid). Features live DAG visualization, execution trace panel (system prompt, user prompt, reasoning, tool calls, response segments, output handler results), MCPs browser, and status bar
- Built the **Terminal** playground (`Orchestra.Playground.Copilot.Terminal`) -- a Spectre.Console TUI with LazyGit-style keyboard-driven interface (1,456 lines), vim-style navigation (j/k), views for Dashboard, Orchestrations, Triggers, History, Active Executions
- Created comprehensive **documentation** site (Jekyll-based GitHub Pages):
  - `docs/index.md` -- landing page
  - `docs/getting-started.md` -- setup guide
  - `docs/engine.md` -- engine deep dive
  - `docs/host.md` -- hosting library docs
  - `docs/copilot.md` -- Copilot integration docs
  - `docs/api-reference.md` -- full API reference
  - GitHub Actions workflow for publishing docs
- Latest commit (today, Mar 19): Added **CI pipeline** (`.github/workflows/ci.yml`), **Dockerfile**, **central package management** (`Directory.Packages.props`, `Directory.Build.props`), **.editorconfig**, **step-level timeout enforcement** (new `timeoutSeconds` property on steps), **health check endpoint**, and several new test suites

---

### Test Coverage
The project has a substantial test suite across **6 test projects** with **37 test files**:

| Test Project | Tests Cover |
|---|---|
| `Orchestra.Engine.Tests` | Executor, scheduler, parser, prompt formatting, storage, domain models, timeout enforcement (~4,500 lines) |
| `Orchestra.Host.Tests` | Registry, file system run store, trigger types, cron calculation, file logging, health checks (~1,750 lines) |
| `Orchestra.Copilot.Tests` | Agent builder, agent, session handler (~1,560 lines) |
| `Orchestra.Mcp.Graph.Tests` | Authentication, graph helpers, calendar tools, scopes, options (~640 lines) |
| `Orchestra.Portal.Tests` | Webhook trigger integration tests (~370 lines) |
| `Orchestra.Portal.E2E` | Playwright-based end-to-end UI tests |

---

### Sample Orchestrations
11 sample orchestration JSON files plus 12 examples in the `examples/` directory covering:
- Content pipelines, research workflows, travel planning, stock analysis
- Email triggers, webhook triggers, scheduler triggers
- MCP tool integration, subagent delegation
- Input/output handler prompts, folder MCP auto-detection
- System prompt modes, combined advanced features

---

### What Remains Unfinished / In-Progress
- The latest commit message is just "working" -- suggesting active development
- The `main` branch is 1 commit ahead of `origin/main`
- The E2E Playwright tests exist but are likely still being set up
- The feature branches (`feature/moaid/new-new-experience`, `feature/moaid/new-new-new-experience`, `feature/new-experience`) represent earlier iterations that have been superseded by `main`
</task_result>
```

---

## Assistant (Plan · claude-opus-4.6 · 9.5s)

Now let me look at a few more specific files that are critical for understanding the implementation details of some features.

**Tool: read**

**Input:**
```json
{
  "filePath": "P:\\Github\\Orchestra\\src\\Orchestra.Engine\\Orchestration\\Executor\\OrchestrationExecutor.cs"
}
```

**Output:**
```
<path>P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationExecutor.cs</path>
<type>file</type>
<content>1: using System.Collections.Concurrent;
2: using Microsoft.Extensions.Logging;
3: 
4: namespace Orchestra.Engine;
5: 
6: public partial class OrchestrationExecutor
7: {
8: 	private readonly IScheduler _scheduler;
9: 	private readonly AgentBuilder _agentBuilder;
10: 	private readonly IOrchestrationReporter _reporter;
11: 	private readonly IPromptFormatter _promptFormatter;
12: 	private readonly ILoggerFactory _loggerFactory;
13: 	private readonly ILogger<OrchestrationExecutor> _logger;
14: 	private readonly IRunStore _runStore;
15: 
16: 	public OrchestrationExecutor(
17: 		IScheduler scheduler,
18: 		AgentBuilder agentBuilder,
19: 		IOrchestrationReporter reporter,
20: 		ILoggerFactory loggerFactory,
21: 		IPromptFormatter? promptFormatter = null,
22: 		IRunStore? runStore = null)
23: 	{
24: 		_scheduler = scheduler;
25: 		_agentBuilder = agentBuilder;
26: 		_reporter = reporter;
27: 		_promptFormatter = promptFormatter ?? DefaultPromptFormatter.Instance;
28: 		_loggerFactory = loggerFactory;
29: 		_logger = loggerFactory.CreateLogger<OrchestrationExecutor>();
30: 		_runStore = runStore ?? NullRunStore.Instance;
31: 	}
32: 
33: 	public async Task<OrchestrationResult> ExecuteAsync(
34: 		Orchestration orchestration,
35: 		Dictionary<string, string>? parameters = null,
36: 		string? triggerId = null,
37: 		CancellationToken cancellationToken = default)
38: 	{
39: 		LogStartingOrchestration(orchestration.Name);
40: 
41: 		ValidateParameters(orchestration, parameters);
42: 
43: 		// Scheduler validates the DAG (detects cycles, missing deps)
44: 		_ = _scheduler.Schedule(orchestration);
45: 
46: 		var runId = Guid.NewGuid().ToString("N")[..12];
47: 		var runStartedAt = DateTimeOffset.UtcNow;
48: 		var effectiveParams = parameters ?? [];
49: 
50: 		var context = new OrchestrationExecutionContext
51: 		{
52: 			Parameters = effectiveParams,
53: 			DefaultSystemPromptMode = orchestration.DefaultSystemPromptMode,
54: 		};
55: 		var executor = new PromptExecutor(_agentBuilder, _reporter, _promptFormatter, _loggerFactory.CreateLogger<PromptExecutor>());
56: 		var stepResults = new ConcurrentDictionary<string, ExecutionResult>();
57: 		var stepRecords = new ConcurrentDictionary<string, StepRunRecord>();
58: 		var allStepRecords = new ConcurrentDictionary<string, StepRunRecord>();
59: 
60: 		// Build step lookup and dependency graph
61: 		var allSteps = orchestration.Steps
62: 			.ToDictionary(s => s.Name, s => (PromptOrchestrationStep)s);
63: 
64: 		// Track completion via TaskCompletionSource per step
65: 		var completionSources = new Dictionary<string, TaskCompletionSource<ExecutionResult>>();
66: 		foreach (var step in orchestration.Steps)
67: 		{
68: 			completionSources[step.Name] = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
69: 		}
70: 
71: 		// Build reverse dependency map: step -> list of steps that depend on it
72: 		var dependents = new Dictionary<string, List<string>>();
73: 		foreach (var step in orchestration.Steps)
74: 		{
75: 			dependents[step.Name] = [];
76: 		}
77: 		foreach (var step in orchestration.Steps)
78: 		{
79: 			foreach (var dep in step.DependsOn)
80: 			{
81: 				dependents[dep].Add(step.Name);
82: 			}
83: 		}
84: 
85: 		// Launch a step when all its dependencies are complete
86: 		void TryLaunchStep(string stepName)
87: 		{
88: 			_ = Task.Run(async () =>
89: 			{
90: 				var step = allSteps[stepName];
91: 				var stepStartedAt = DateTimeOffset.UtcNow;
92: 
93: 				try
94: 				{
95: 					var result = await ExecuteOrSkipStepAsync(step, executor, context, stepResults, cancellationToken);
96: 
97: 					context.AddResult(step.Name, result);
98: 					stepResults[step.Name] = result;
99: 
100: 					var record = BuildStepRecord(step, result, effectiveParams, stepStartedAt);
101: 					stepRecords[step.Name] = record;
102: 					allStepRecords[step.Name] = record;
103: 
104: 					// Handle loop if configured
105: 					if (step.Loop is not null && result.Status == ExecutionStatus.Succeeded)
106: 					{
107: 						await HandleLoopAsync(step, allSteps, executor, context, stepResults, stepRecords, allStepRecords, effectiveParams, cancellationToken);
108: 					}
109: 
110: 					completionSources[step.Name].TrySetResult(stepResults[step.Name]);
111: 				}
112: 				catch (OperationCanceledException)
113: 				{
114: 					var cancelled = ExecutionResult.Failed("Cancelled");
115: 					context.AddResult(step.Name, cancelled);
116: 					stepResults[step.Name] = cancelled;
117: 					var record = BuildStepRecord(step, cancelled, effectiveParams, stepStartedAt);
118: 					stepRecords[step.Name] = record;
119: 					allStepRecords[step.Name] = record;
120: 					_reporter.ReportStepCancelled(step.Name);
121: 					completionSources[step.Name].TrySetResult(cancelled);
122: 				}
123: 				catch (Exception ex)
124: 				{
125: 					var failed = ExecutionResult.Failed(ex.Message);
126: 					context.AddResult(step.Name, failed);
127: 					stepResults[step.Name] = failed;
128: 					var record = BuildStepRecord(step, failed, effectiveParams, stepStartedAt);
129: 					stepRecords[step.Name] = record;
130: 					allStepRecords[step.Name] = record;
131: 					completionSources[step.Name].TrySetResult(failed);
132: 				}
133: 
134: 				// After this step completes, check all dependents — launch any that are now ready
135: 				foreach (var dependent in dependents[stepName])
136: 				{
137: 					var allDepsComplete = allSteps[dependent].DependsOn
138: 						.All(dep => stepResults.ContainsKey(dep));
139: 
140: 					if (allDepsComplete)
141: 					{
142: 						TryLaunchStep(dependent);
143: 					}
144: 				}
145: 			}); // Don't pass cancellationToken to Task.Run - let step handle cancellation internally
146: 		}
147: 
148: 		// Start all steps that have zero dependencies
149: 		foreach (var step in orchestration.Steps)
150: 		{
151: 			if (step.DependsOn.Length == 0)
152: 			{
153: 				LogLaunchingStep(step.Name);
154: 				TryLaunchStep(step.Name);
155: 			}
156: 		}
157: 
158: 		// Wait for all steps to complete
159: 		await Task.WhenAll(completionSources.Values.Select(tcs => tcs.Task));
160: 
161: 		var orchestrationResult = OrchestrationResult.From(orchestration, stepResults);
162: 
163: 		if (orchestrationResult.Status == ExecutionStatus.Succeeded)
164: 		{
165: 			LogOrchestrationSucceeded(orchestration.Name);
166: 		}
167: 		else
168: 		{
169: 			LogOrchestrationFailed(orchestration.Name);
170: 		}
171: 
172: 		// Build and persist the run record
173: 		var runCompletedAt = DateTimeOffset.UtcNow;
174: 		var finalContent = BuildFinalContent(orchestrationResult);
175: 
176: 		var runRecord = new OrchestrationRunRecord
177: 		{
178: 			RunId = runId,
179: 			OrchestrationName = orchestration.Name,
180: 			StartedAt = runStartedAt,
181: 			CompletedAt = runCompletedAt,
182: 			Status = orchestrationResult.Status,
183: 			Parameters = effectiveParams,
184: 			TriggerId = triggerId,
185: 			StepRecords = stepRecords,
186: 			AllStepRecords = allStepRecords,
187: 			FinalContent = finalContent,
188: 		};
189: 
190: 		try
191: 		{
192: 			await _runStore.SaveRunAsync(runRecord, cancellationToken);
193: 		}
194: 		catch (Exception ex)
195: 		{
196: 			LogRunStoreSaveFailed(ex, orchestration.Name, runId);
197: 		}
198: 
199: 		return orchestrationResult;
200: 	}
201: 
202: 	private static void ValidateParameters(Orchestration orchestration, Dictionary<string, string>? parameters)
203: 	{
204: 		var requiredByStep = orchestration.Steps
205: 			.SelectMany(step => step.Parameters.Select(param => (param, step.Name)))
206: 			.GroupBy(x => x.param)
207: 			.ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToArray());
208: 
209: 		if (requiredByStep.Count == 0)
210: 			return;
211: 
212: 		var missing = parameters is null
213: 			? requiredByStep.Keys.ToArray()
214: 			: requiredByStep.Keys.Except(parameters.Keys).ToArray();
215: 
216: 		if (missing.Length > 0)
217: 		{
218: 			var details = string.Join("; ", missing.Select(p =>
219: 				$"'{p}' (required by: {string.Join(", ", requiredByStep[p])})"));
220: 
221: 			throw new InvalidOperationException(
222: 				$"Missing required parameters: {details}. " +
223: 				$"Provide them via -param key=value.");
224: 		}
225: 	}
226: 
227: 	private async Task<ExecutionResult> ExecuteOrSkipStepAsync(
228: 		PromptOrchestrationStep step,
229: 		PromptExecutor executor,
230: 		OrchestrationExecutionContext context,
231: 		ConcurrentDictionary<string, ExecutionResult> stepResults,
232: 		CancellationToken cancellationToken)
233: 	{
234: 		// Check for cancellation before starting
235: 		if (cancellationToken.IsCancellationRequested)
236: 		{
237: 			LogStepCancelledBeforeStart(step.Name);
238: 			_reporter.ReportStepSkipped(step.Name, "Cancelled");
239: 			return ExecutionResult.Failed("Cancelled");
240: 		}
241: 
242: 		// Check if any dependency failed or was skipped
243: 		var shouldSkip = context.HasAnyDependencyFailed(step.DependsOn);
244: 		var failedDeps = shouldSkip
245: 			? step.DependsOn
246: 				.Where(dep => stepResults.TryGetValue(dep, out var r) &&
247: 					r.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped)
248: 				.ToArray()
249: 			: [];
250: 
251: 		if (shouldSkip)
252: 		{
253: 			var reason = $"Skipped because dependencies failed or were skipped: [{string.Join(", ", failedDeps)}]";
254: 			LogSkippingStep(step.Name, reason);
255: 			_reporter.ReportStepSkipped(step.Name, reason);
256: 			return ExecutionResult.Skipped(reason);
257: 		}
258: 
259: 		LogRunningStep(step.Name);
260: 		_reporter.ReportStepStarted(step.Name);
261: 
262: 		// Apply per-step timeout if configured
263: 		CancellationTokenSource? timeoutCts = null;
264: 		var effectiveToken = cancellationToken;
265: 
266: 		if (step.TimeoutSeconds is > 0)
267: 		{
268: 			timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
269: 			timeoutCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds.Value));
270: 			effectiveToken = timeoutCts.Token;
271: 			LogStepTimeout(step.Name, step.TimeoutSeconds.Value);
272: 		}
273: 
274: 		try
275: 		{
276: 			var result = await executor.ExecuteAsync(step, context, effectiveToken);
277: 
278: 			if (result.Status == ExecutionStatus.Succeeded)
279: 			{
280: 				LogStepSucceeded(step.Name);
281: 			}
282: 			else
283: 			{
284: 				LogStepFailed(step.Name, result.ErrorMessage);
285: 			}
286: 
287: 			return result;
288: 		}
289: 		catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
290: 		{
291: 			var message = $"Step timed out after {step.TimeoutSeconds} seconds.";
292: 			LogStepTimedOut(step.Name, step.TimeoutSeconds!.Value);
293: 			_reporter.ReportStepError(step.Name, message);
294: 			return ExecutionResult.Failed(message);
295: 		}
296: 		finally
297: 		{
298: 			timeoutCts?.Dispose();
299: 		}
300: 	}
301: 
302: 	private async Task HandleLoopAsync(
303: 		PromptOrchestrationStep checkerStep,
304: 		Dictionary<string, PromptOrchestrationStep> allStepsByName,
305: 		PromptExecutor executor,
306: 		OrchestrationExecutionContext context,
307: 		ConcurrentDictionary<string, ExecutionResult> stepResults,
308: 		ConcurrentDictionary<string, StepRunRecord> stepRecords,
309: 		ConcurrentDictionary<string, StepRunRecord> allStepRecords,
310: 		Dictionary<string, string> effectiveParams,
311: 		CancellationToken cancellationToken)
312: 	{
313: 		var loop = checkerStep.Loop!;
314: 
315: 		if (!allStepsByName.TryGetValue(loop.Target, out var targetStep))
316: 		{
317: 			LogLoopTargetNotFound(loop.Target, checkerStep.Name);
318: 			return;
319: 		}
320: 
321: 		for (var iteration = 1; iteration <= loop.MaxIterations; iteration++)
322: 		{
323: 			// Check exit condition on the checker's current result
324: 			var checkerResult = context.GetResult(checkerStep.Name);
325: 
326: 			if (checkerResult.Content.Contains(loop.ExitPattern, StringComparison.OrdinalIgnoreCase))
327: 			{
328: 				LogLoopExitConditionMet(checkerStep.Name, iteration - 1);
329: 				return;
330: 			}
331: 
332: 			LogLoopIteration(checkerStep.Name, iteration, loop.MaxIterations, loop.Target);
333: 			_reporter.ReportLoopIteration(checkerStep.Name, loop.Target, iteration, loop.MaxIterations);
334: 
335: 			// Inject checker's feedback into the target step's context
336: 			context.SetLoopFeedback(loop.Target, checkerResult.Content);
337: 			context.ClearResult(loop.Target);
338: 
339: 			// Re-execute the target step
340: 			var targetStartedAt = DateTimeOffset.UtcNow;
341: 			_reporter.ReportStepStarted(loop.Target);
342: 			var targetResult = await executor.ExecuteAsync(targetStep, context, cancellationToken);
343: 
344: 			context.AddResult(loop.Target, targetResult);
345: 			stepResults[loop.Target] = targetResult;
346: 
347: 			var targetRecord = BuildStepRecord(targetStep, targetResult, effectiveParams, targetStartedAt, iteration);
348: 			stepRecords[loop.Target] = targetRecord;
349: 			allStepRecords[$"{loop.Target}:iteration-{iteration}"] = targetRecord;
350: 
351: 			if (targetResult.Status != ExecutionStatus.Succeeded)
352: 			{
353: 				LogLoopTargetFailed(loop.Target, iteration);
354: 				return;
355: 			}
356: 
357: 			// Re-execute the checker step
358: 			context.ClearResult(checkerStep.Name);
359: 
360: 			var checkerStartedAt = DateTimeOffset.UtcNow;
361: 			_reporter.ReportStepStarted(checkerStep.Name);
362: 			var newCheckerResult = await executor.ExecuteAsync(checkerStep, context, cancellationToken);
363: 
364: 			context.AddResult(checkerStep.Name, newCheckerResult);
365: 			stepResults[checkerStep.Name] = newCheckerResult;
366: 
367: 			var checkerRecord = BuildStepRecord(checkerStep, newCheckerResult, effectiveParams, checkerStartedAt, iteration);
368: 			stepRecords[checkerStep.Name] = checkerRecord;
369: 			allStepRecords[$"{checkerStep.Name}:iteration-{iteration}"] = checkerRecord;
370: 
371: 			if (newCheckerResult.Status != ExecutionStatus.Succeeded)
372: 			{
373: 				LogLoopCheckerFailed(checkerStep.Name, iteration);
374: 				return;
375: 			}
376: 		}
377: 
378: 		// Check exit condition one final time after exhausting all iterations
379: 		var finalResult = context.GetResult(checkerStep.Name);
380: 
381: 		if (finalResult.Content.Contains(loop.ExitPattern, StringComparison.OrdinalIgnoreCase))
382: 		{
383: 			LogLoopExitConditionMet(checkerStep.Name, loop.MaxIterations);
384: 		}
385: 		else
386: 		{
387: 			LogLoopExhausted(checkerStep.Name, loop.MaxIterations);
388: 		}
389: 	}
390: 
391: 	private static StepRunRecord BuildStepRecord(
392: 		PromptOrchestrationStep step,
393: 		ExecutionResult result,
394: 		Dictionary<string, string> allParams,
395: 		DateTimeOffset startedAt,
396: 		int? loopIteration = null)
397: 	{
398: 		// Extract only the parameters relevant to this step
399: 		var stepParams = new Dictionary<string, string>();
400: 		foreach (var paramName in step.Parameters)
401: 		{
402: 			if (allParams.TryGetValue(paramName, out var value))
403: 			{
404: 				stepParams[paramName] = value;
405: 			}
406: 		}
407: 
408: 		return new StepRunRecord
409: 		{
410: 			StepName = step.Name,
411: 			Status = result.Status,
412: 			StartedAt = startedAt,
413: 			CompletedAt = DateTimeOffset.UtcNow,
414: 			Content = result.Content,
415: 			RawContent = result.RawContent,
416: 			ErrorMessage = result.ErrorMessage,
417: 			Parameters = stepParams,
418: 			LoopIteration = loopIteration,
419: 			RawDependencyOutputs = result.RawDependencyOutputs,
420: 			PromptSent = result.PromptSent,
421: 			ActualModel = result.ActualModel,
422: 			Usage = result.Usage,
423: 			Trace = result.Trace,
424: 		};
425: 	}
426: 
427: 	private static string BuildFinalContent(OrchestrationResult orchestrationResult)
428: 	{
429: 		if (orchestrationResult.Results.Count == 1)
430: 		{
431: 			return orchestrationResult.Results.Values.First().Content;
432: 		}
433: 
434: 		return string.Join("\n\n---\n\n",
435: 			orchestrationResult.Results
436: 				.Where(kv => kv.Value.Status == ExecutionStatus.Succeeded)
437: 				.Select(kv => $"## {kv.Key}\n{kv.Value.Content}"));
438: 	}
439: 
440: 	#region Source-Generated Logging
441: 
442: 	[LoggerMessage(Level = LogLevel.Information, Message = "Starting orchestration '{Name}'...")]
443: 	private partial void LogStartingOrchestration(string name);
444: 
445: 	[LoggerMessage(Level = LogLevel.Information, Message = "Launching step '{StepName}' (no dependencies)")]
446: 	private partial void LogLaunchingStep(string stepName);
447: 
448: 	[LoggerMessage(Level = LogLevel.Information, Message = "Orchestration '{Name}' completed successfully.")]
449: 	private partial void LogOrchestrationSucceeded(string name);
450: 
451: 	[LoggerMessage(Level = LogLevel.Warning, Message = "Orchestration '{Name}' completed with failures.")]
452: 	private partial void LogOrchestrationFailed(string name);
453: 
454: 	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to save run record for orchestration '{Name}', run '{RunId}'.")]
455: 	private partial void LogRunStoreSaveFailed(Exception ex, string name, string runId);
456: 
457: 	[LoggerMessage(Level = LogLevel.Warning, Message = "Step '{StepName}' cancelled before starting.")]
458: 	private partial void LogStepCancelledBeforeStart(string stepName);
459: 
460: 	[LoggerMessage(Level = LogLevel.Warning, Message = "Skipping step '{StepName}': {Reason}")]
461: 	private partial void LogSkippingStep(string stepName, string reason);
462: 
463: 	[LoggerMessage(Level = LogLevel.Information, Message = "Running step '{StepName}'...")]
464: 	private partial void LogRunningStep(string stepName);
465: 
466: 	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' completed successfully.")]
467: 	private partial void LogStepSucceeded(string stepName);
468: 
469: 	[LoggerMessage(Level = LogLevel.Error, Message = "Step '{StepName}' failed: {Error}")]
470: 	private partial void LogStepFailed(string stepName, string? error);
471: 
472: 	[LoggerMessage(Level = LogLevel.Warning, Message = "Loop target '{Target}' not found for checker '{Checker}', skipping loop.")]
473: 	private partial void LogLoopTargetNotFound(string target, string checker);
474: 
475: 	[LoggerMessage(Level = LogLevel.Information, Message = "[{Checker}] Loop exit condition met after {Iterations} iteration(s).")]
476: 	private partial void LogLoopExitConditionMet(string checker, int iterations);
477: 
478: 	[LoggerMessage(Level = LogLevel.Information, Message = "[{Checker}] Loop iteration {Iteration}/{MaxIterations} — re-running '{Target}' with feedback.")]
479: 	private partial void LogLoopIteration(string checker, int iteration, int maxIterations, string target);
480: 
481: 	[LoggerMessage(Level = LogLevel.Warning, Message = "[{Target}] Failed during loop iteration {Iteration}, stopping loop.")]
482: 	private partial void LogLoopTargetFailed(string target, int iteration);
483: 
484: 	[LoggerMessage(Level = LogLevel.Warning, Message = "[{Checker}] Failed during loop iteration {Iteration}, stopping loop.")]
485: 	private partial void LogLoopCheckerFailed(string checker, int iteration);
486: 
487: 	[LoggerMessage(Level = LogLevel.Warning, Message = "[{Checker}] Loop exhausted {MaxIterations} iterations without meeting exit condition. Using last result.")]
488: 	private partial void LogLoopExhausted(string checker, int maxIterations);
489: 
490: 	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' has a timeout of {TimeoutSeconds} seconds.")]
491: 	private partial void LogStepTimeout(string stepName, int timeoutSeconds);
492: 
493: 	[LoggerMessage(Level = LogLevel.Warning, Message = "Step '{StepName}' timed out after {TimeoutSeconds} seconds.")]
494: 	private partial void LogStepTimedOut(string stepName, int timeoutSeconds);
495: 
496: 	#endregion
497: }

(End of file - total 497 lines)
</content>
```

**Tool: read**

**Input:**
```json
{
  "filePath": "P:\\Github\\Orchestra\\src\\Orchestra.Engine\\Orchestration\\Executor\\PromptExecutor.cs"
}
```

**Output:**
```
<path>P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\PromptExecutor.cs</path>
<type>file</type>
<content>1: using Microsoft.Extensions.Logging;
2: 
3: namespace Orchestra.Engine;
4: 
5: public partial class PromptExecutor : Executor<PromptOrchestrationStep>
6: {
7: 	private readonly AgentBuilder _agentBuilder;
8: 	private readonly IOrchestrationReporter _reporter;
9: 	private readonly IPromptFormatter _formatter;
10: 	private readonly ILogger<PromptExecutor> _logger;
11: 
12: 	public PromptExecutor(
13: 		AgentBuilder agentBuilder,
14: 		IOrchestrationReporter reporter,
15: 		IPromptFormatter formatter,
16: 		ILogger<PromptExecutor> logger)
17: 	{
18: 		_agentBuilder = agentBuilder;
19: 		_reporter = reporter;
20: 		_formatter = formatter;
21: 		_logger = logger;
22: 	}
23: 
24: 	public override async Task<ExecutionResult> ExecuteAsync(
25: 		PromptOrchestrationStep step,
26: 		OrchestrationExecutionContext context,
27: 		CancellationToken cancellationToken = default)
28: 	{
29: 		// Capture raw dependency outputs before building the prompt
30: 		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);
31: 
32: 		// Get the raw user prompt before input handler processing
33: 		var userPromptRaw = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);
34: 
35: 		// Create event processor to handle agent events and collect trace data
36: 		var eventProcessor = new AgentEventProcessor(_reporter, step.Name);
37: 
38: 		try
39: 		{
40: 			// Build the user prompt, incorporating dependency outputs and parameters
41: 			var userPrompt = BuildUserPrompt(step, context);
42: 
43: 			// Log step MCPs for debugging
44: 			LogStepMcps(step.Name, step.Mcps.Length, string.Join(", ", step.Mcps.Select(m => m.Name)));
45: 			LogStepMcpNames(step.Name, string.Join(", ", step.McpNames));
46: 
47: 			// Build and run the agent
48: 			var agent = await _agentBuilder
49: 				.WithModel(step.Model)
50: 				.WithSystemPrompt(step.SystemPrompt)
51: 				.WithMcp(step.Mcps)
52: 				.WithSubagents(step.Subagents)
53: 				.WithReasoningLevel(step.ReasoningLevel)
54: 				.WithSystemPromptMode(step.SystemPromptMode ?? context.DefaultSystemPromptMode)
55: 				.WithReporter(_reporter)
56: 				.BuildAgentAsync(cancellationToken);
57: 
58: 			var task = agent.SendAsync(userPrompt, cancellationToken);
59: 
60: 			// Process all agent events, collecting trace data
61: 			await eventProcessor.ProcessEventsAsync(task, cancellationToken);
62: 
63: 			var result = await task.GetResultAsync();
64: 
65: 			// Report model and usage metadata if available
66: 			if (result.ActualModel is not null)
67: 			{
68: 				_reporter.ReportStepCompleted(step.Name, result);
69: 			}
70: 			if (result.Usage is not null && result.ActualModel is not null)
71: 			{
72: 				_reporter.ReportUsage(step.Name, result.ActualModel, result.Usage);
73: 			}
74: 
75: 			var content = result.Content;
76: 			string? rawContent = null;
77: 			string? outputHandlerResult = null;
78: 
79: 			// Apply output handler if specified
80: 			if (step.OutputHandlerPrompt is not null)
81: 			{
82: 				rawContent = content;
83: 				content = await RunHandlerAsync(step.OutputHandlerPrompt, content, step.Model, cancellationToken);
84: 				outputHandlerResult = content;
85: 			}
86: 
87: 			// Convert usage to our TokenUsage type
88: 			TokenUsage? tokenUsage = null;
89: 			if (result.Usage is not null)
90: 			{
91: 				tokenUsage = new TokenUsage
92: 				{
93: 					InputTokens = (int)(result.Usage.InputTokens ?? 0),
94: 					OutputTokens = (int)(result.Usage.OutputTokens ?? 0)
95: 				};
96: 			}
97: 
98: 			// Build the execution trace from collected data
99: 			var trace = eventProcessor.BuildTrace(
100: 				step.SystemPrompt,
101: 				userPromptRaw,
102: 				userPrompt,
103: 				rawContent ?? result.Content,
104: 				outputHandlerResult);
105: 
106: 			// Report the step trace for live trace viewing
107: 			_reporter.ReportStepTrace(step.Name, trace);
108: 
109: 			return ExecutionResult.Succeeded(
110: 				content,
111: 				rawContent,
112: 				rawDependencyOutputs,
113: 				userPrompt,
114: 				result.ActualModel,
115: 				tokenUsage,
116: 				trace);
117: 		}
118: 		catch (OperationCanceledException)
119: 		{
120: 			throw; // Let cancellation propagate to the caller for timeout handling
121: 		}
122: 		catch (Exception ex)
123: 		{
124: 			// Build partial trace even on failure
125: 			var trace = eventProcessor.BuildPartialTrace(step.SystemPrompt, userPromptRaw);
126: 
127: 			// Report the partial trace for live trace viewing
128: 			_reporter.ReportStepTrace(step.Name, trace);
129: 
130: 			_reporter.ReportStepError(step.Name, ex.Message);
131: 			return ExecutionResult.Failed(ex.Message, rawDependencyOutputs, trace: trace);
132: 		}
133: 	}
134: 
135: 	private string BuildUserPrompt(PromptOrchestrationStep step, OrchestrationExecutionContext context)
136: 	{
137: 		var userPrompt = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);
138: 		var dependencyOutputsDict = context.GetDependencyOutputs(step.DependsOn);
139: 		var dependencyOutputs = _formatter.FormatDependencyOutputs(dependencyOutputsDict);
140: 		var loopFeedback = context.ConsumeLoopFeedback(step.Name);
141: 
142: 		return _formatter.BuildUserPrompt(userPrompt, dependencyOutputs, loopFeedback, step.InputHandlerPrompt);
143: 	}
144: 
145: 	private static string InjectParameters(string prompt, string[] parameterNames, Dictionary<string, string> parameters)
146: 	{
147: 		if (parameterNames.Length == 0 || parameters.Count == 0)
148: 			return prompt;
149: 
150: 		var result = prompt;
151: 		foreach (var name in parameterNames)
152: 		{
153: 			if (parameters.TryGetValue(name, out var value))
154: 			{
155: 				result = result.Replace($"{{{{{name}}}}}", value);
156: 			}
157: 		}
158: 
159: 		return result;
160: 	}
161: 
162: 	private async Task<string> RunHandlerAsync(
163: 		string handlerPrompt,
164: 		string content,
165: 		string model,
166: 		CancellationToken cancellationToken)
167: 	{
168: 		var systemPrompt = _formatter.BuildTransformationSystemPrompt(handlerPrompt);
169: 
170: 		var agent = await _agentBuilder
171: 			.WithModel(model)
172: 			.WithSystemPrompt(systemPrompt)
173: 			.WithSystemPromptMode(SystemPromptMode.Replace)
174: 			.WithMcp()
175: 			.WithReporter(_reporter)
176: 			.BuildAgentAsync(cancellationToken);
177: 
178: 		var wrappedContent = _formatter.WrapContentForTransformation(content);
179: 
180: 		var task = agent.SendAsync(wrappedContent, cancellationToken);
181: 		var result = await task.GetResultAsync();
182: 
183: 		return result.Content;
184: 	}
185: 
186: 	#region Source-Generated Logging
187: 
188: 	[LoggerMessage(
189: 		EventId = 1,
190: 		Level = LogLevel.Debug,
191: 		Message = "Step '{StepName}' has {McpCount} MCPs: [{McpNames}]")]
192: 	private partial void LogStepMcps(string stepName, int mcpCount, string mcpNames);
193: 
194: 	[LoggerMessage(
195: 		EventId = 2,
196: 		Level = LogLevel.Debug,
197: 		Message = "Step '{StepName}' McpNames configuration: [{McpNames}]")]
198: 	private partial void LogStepMcpNames(string stepName, string mcpNames);
199: 
200: 	#endregion
201: }

(End of file - total 201 lines)
</content>
```

**Tool: read**

**Input:**
```json
{
  "filePath": "P:\\Github\\Orchestra\\src\\Orchestra.Host\\Api\\SseReporter.cs"
}
```

**Output:**
```
<path>P:\Github\Orchestra\src\Orchestra.Host\Api\SseReporter.cs</path>
<type>file</type>
<content>1: using System.Text.Json;
2: using System.Threading.Channels;
3: using Orchestra.Engine;
4: 
5: namespace Orchestra.Host.Api;
6: 
7: /// <summary>
8: /// Represents an SSE event with type and JSON data.
9: /// </summary>
10: public record SseEvent(string Type, string Data);
11: 
12: /// <summary>
13: /// An IOrchestrationReporter that writes structured SSE events to multiple subscribers.
14: /// Supports late-joining subscribers by replaying accumulated events.
15: /// Each execution creates its own instance tied to a specific orchestration run.
16: /// </summary>
17: public class SseReporter : IOrchestrationReporter
18: {
19: 	private static readonly JsonSerializerOptions s_jsonOptions = new()
20: 	{
21: 		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
22: 		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
23: 	};
24: 
25: 	private readonly object _lock = new();
26: 	private readonly List<SseEvent> _accumulatedEvents = [];
27: 	private readonly List<Channel<SseEvent>> _subscribers = [];
28: 	private bool _isCompleted;
29: 
30: 	/// <summary>
31: 	/// Gets all accumulated events (for replay to late-joining subscribers).
32: 	/// </summary>
33: 	public IReadOnlyList<SseEvent> AccumulatedEvents
34: 	{
35: 		get
36: 		{
37: 			lock (_lock)
38: 			{
39: 				return _accumulatedEvents.ToList();
40: 			}
41: 		}
42: 	}
43: 
44: 	/// <summary>
45: 	/// Whether the reporter has completed (orchestration finished).
46: 	/// </summary>
47: 	public bool IsCompleted
48: 	{
49: 		get
50: 		{
51: 			lock (_lock)
52: 			{
53: 				return _isCompleted;
54: 			}
55: 		}
56: 	}
57: 
58: 	/// <summary>
59: 	/// Callback invoked when a step starts. Parameters: stepName
60: 	/// </summary>
61: 	public Action<string>? OnStepStarted { get; set; }
62: 
63: 	/// <summary>
64: 	/// Callback invoked when a step completes. Parameters: stepName
65: 	/// </summary>
66: 	public Action<string>? OnStepCompleted { get; set; }
67: 
68: 	/// <summary>
69: 	/// Creates a new subscriber channel that will receive future events.
70: 	/// Call this to attach a new SSE client to the execution.
71: 	/// </summary>
72: 	/// <returns>A tuple of (accumulated events to replay, channel for future events)</returns>
73: 	public (IReadOnlyList<SseEvent> Replay, ChannelReader<SseEvent> Future) Subscribe()
74: 	{
75: 		var channel = Channel.CreateUnbounded<SseEvent>(
76: 			new UnboundedChannelOptions { SingleReader = true });
77: 
78: 		lock (_lock)
79: 		{
80: 			if (_isCompleted)
81: 			{
82: 				// Already done - just return accumulated events and a completed channel
83: 				channel.Writer.TryComplete();
84: 				return (_accumulatedEvents.ToList(), channel.Reader);
85: 			}
86: 
87: 			_subscribers.Add(channel);
88: 			return (_accumulatedEvents.ToList(), channel.Reader);
89: 		}
90: 	}
91: 
92: 	/// <summary>
93: 	/// Unsubscribes a channel (e.g., when client disconnects).
94: 	/// </summary>
95: 	public void Unsubscribe(ChannelReader<SseEvent> reader)
96: 	{
97: 		lock (_lock)
98: 		{
99: 			_subscribers.RemoveAll(ch => ch.Reader == reader);
100: 		}
101: 	}
102: 
103: 	/// <summary>
104: 	/// Legacy property for backward compatibility with existing code.
105: 	/// Creates a new subscriber and returns its reader.
106: 	/// </summary>
107: 	public ChannelReader<SseEvent> Events
108: 	{
109: 		get
110: 		{
111: 			var (_, future) = Subscribe();
112: 			return future;
113: 		}
114: 	}
115: 
116: 	public void Complete()
117: 	{
118: 		lock (_lock)
119: 		{
120: 			_isCompleted = true;
121: 			foreach (var channel in _subscribers)
122: 			{
123: 				channel.Writer.TryComplete();
124: 			}
125: 			_subscribers.Clear();
126: 		}
127: 	}
128: 
129: 	public void ReportSessionStarted(string requestedModel, string? selectedModel)
130: 	{
131: 		Write("session-started", new { requestedModel, selectedModel });
132: 	}
133: 
134: 	public void ReportModelChange(string? previousModel, string newModel)
135: 	{
136: 		Write("model-change", new { previousModel, newModel });
137: 	}
138: 
139: 	public void ReportUsage(string stepName, string model, AgentUsage usage)
140: 	{
141: 		Write("usage", new
142: 		{
143: 			stepName,
144: 			model,
145: 			inputTokens = usage.InputTokens,
146: 			outputTokens = usage.OutputTokens,
147: 			cacheReadTokens = usage.CacheReadTokens,
148: 			cacheWriteTokens = usage.CacheWriteTokens,
149: 			cost = usage.Cost,
150: 			duration = usage.Duration,
151: 		});
152: 	}
153: 
154: 	public void ReportContentDelta(string stepName, string chunk)
155: 	{
156: 		Write("content-delta", new { stepName, chunk });
157: 	}
158: 
159: 	public void ReportReasoningDelta(string stepName, string chunk)
160: 	{
161: 		Write("reasoning-delta", new { stepName, chunk });
162: 	}
163: 
164: 	public void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer)
165: 	{
166: 		Write("tool-started", new { stepName, toolName, arguments, mcpServer });
167: 	}
168: 
169: 	public void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error)
170: 	{
171: 		Write("tool-completed", new { stepName, toolName, success, result, error });
172: 	}
173: 
174: 	public void ReportStepError(string stepName, string errorMessage)
175: 	{
176: 		Write("step-error", new { stepName, error = errorMessage });
177: 	}
178: 
179: 	/// <summary>
180: 	/// Reports that a step was cancelled (not failed).
181: 	/// </summary>
182: 	public void ReportStepCancelled(string stepName)
183: 	{
184: 		Write("step-cancelled", new { stepName });
185: 	}
186: 
187: 	public void ReportStepCompleted(string stepName, AgentResult result)
188: 	{
189: 		Write("step-completed", new
190: 		{
191: 			stepName,
192: 			actualModel = result.ActualModel,
193: 			selectedModel = result.SelectedModel,
194: 			contentPreview = result.Content.Length > 500
195: 				? result.Content[..500] + "..."
196: 				: result.Content,
197: 		});
198: 		OnStepCompleted?.Invoke(stepName);
199: 	}
200: 
201: 	public void ReportStepTrace(string stepName, StepExecutionTrace trace)
202: 	{
203: 		Write("step-trace", new
204: 		{
205: 			stepName,
206: 			systemPrompt = trace.SystemPrompt,
207: 			userPromptRaw = trace.UserPromptRaw,
208: 			userPromptProcessed = trace.UserPromptProcessed,
209: 			reasoning = trace.Reasoning,
210: 			toolCalls = trace.ToolCalls.Select(tc => new
211: 			{
212: 				callId = tc.CallId,
213: 				mcpServer = tc.McpServer,
214: 				toolName = tc.ToolName,
215: 				arguments = tc.Arguments,
216: 				success = tc.Success,
217: 				result = tc.Result,
218: 				error = tc.Error,
219: 				startedAt = tc.StartedAt?.ToString("o"),
220: 				completedAt = tc.CompletedAt?.ToString("o")
221: 			}).ToArray(),
222: 			responseSegments = trace.ResponseSegments,
223: 			finalResponse = trace.FinalResponse,
224: 			outputHandlerResult = trace.OutputHandlerResult
225: 		});
226: 	}
227: 
228: 	public void ReportModelMismatch(ModelMismatchInfo mismatch)
229: 	{
230: 		Write("model-mismatch", new
231: 		{
232: 			configuredModel = mismatch.ConfiguredModel,
233: 			actualModel = mismatch.ActualModel,
234: 			systemPromptMode = mismatch.SystemPromptMode,
235: 			reasoningLevel = mismatch.ReasoningLevel,
236: 		});
237: 	}
238: 
239: 	public void ReportStepOutput(string stepName, string content)
240: 	{
241: 		Write("step-output", new { stepName, content });
242: 	}
243: 
244: 	public void ReportStepSkipped(string stepName, string reason)
245: 	{
246: 		Write("step-skipped", new { stepName, reason });
247: 	}
248: 
249: 	public void ReportStepStarted(string stepName)
250: 	{
251: 		Write("step-started", new { stepName });
252: 		OnStepStarted?.Invoke(stepName);
253: 	}
254: 
255: 	public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations)
256: 	{
257: 		Write("loop-iteration", new { checkerStepName, targetStepName, iteration, maxIterations });
258: 	}
259: 
260: 	public void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools)
261: 	{
262: 		Write("subagent-selected", new { stepName, agentName, displayName, tools });
263: 	}
264: 
265: 	public void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description)
266: 	{
267: 		Write("subagent-started", new { stepName, toolCallId, agentName, displayName, description });
268: 	}
269: 
270: 	public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName)
271: 	{
272: 		Write("subagent-completed", new { stepName, toolCallId, agentName, displayName });
273: 	}
274: 
275: 	public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error)
276: 	{
277: 		Write("subagent-failed", new { stepName, toolCallId, agentName, displayName, error });
278: 	}
279: 
280: 	public void ReportSubagentDeselected(string stepName)
281: 	{
282: 		Write("subagent-deselected", new { stepName });
283: 	}
284: 
285: 	/// <summary>
286: 	/// Reports the final orchestration result.
287: 	/// Not part of IOrchestrationReporter - called directly by the execution endpoint.
288: 	/// </summary>
289: 	public void ReportOrchestrationDone(OrchestrationResult orchestrationResult)
290: 	{
291: 		var results = orchestrationResult.StepResults.ToDictionary(
292: 			kv => kv.Key,
293: 			kv => new
294: 			{
295: 				status = kv.Value.Status.ToString(),
296: 				contentPreview = kv.Value.Content.Length > 1000
297: 					? kv.Value.Content[..1000] + "..."
298: 					: kv.Value.Content,
299: 				error = kv.Value.ErrorMessage,
300: 			});
301: 
302: 		Write("orchestration-done", new
303: 		{
304: 			status = orchestrationResult.Status.ToString(),
305: 			results,
306: 		});
307: 	}
308: 
309: 	/// <summary>
310: 	/// Reports that the orchestration was cancelled.
311: 	/// </summary>
312: 	public void ReportOrchestrationCancelled()
313: 	{
314: 		Write("orchestration-cancelled", new { status = "Cancelled" });
315: 	}
316: 
317: 	/// <summary>
318: 	/// Reports that the orchestration failed with an error.
319: 	/// </summary>
320: 	public void ReportOrchestrationError(string errorMessage)
321: 	{
322: 		Write("orchestration-error", new { status = "Failed", error = errorMessage });
323: 	}
324: 
325: 	/// <summary>
326: 	/// Reports a status change for the orchestration (e.g., "Cancelling").
327: 	/// </summary>
328: 	public void ReportStatusChange(string status)
329: 	{
330: 		Write("status-changed", new { status });
331: 	}
332: 
333: 	private void Write(string eventType, object data)
334: 	{
335: 		var json = JsonSerializer.Serialize(data, s_jsonOptions);
336: 		var evt = new SseEvent(eventType, json);
337: 
338: 		lock (_lock)
339: 		{
340: 			if (_isCompleted)
341: 				return;
342: 
343: 			_accumulatedEvents.Add(evt);
344: 			foreach (var channel in _subscribers)
345: 			{
346: 				channel.Writer.TryWrite(evt);
347: 			}
348: 		}
349: 	}
350: }

(End of file - total 350 lines)
</content>
```

---

## Assistant (Plan · claude-opus-4.6 · 9.0s)

**Tool: read**

**Input:**
```json
{
  "filePath": "P:\\Github\\Orchestra\\src\\Orchestra.Host\\Api\\ExecutionApi.cs"
}
```

**Output:**
```
<path>P:\Github\Orchestra\src\Orchestra.Host\Api\ExecutionApi.cs</path>
<type>file</type>
<content>1: using System.Collections.Concurrent;
2: using System.Text.Json;
3: using Microsoft.AspNetCore.Builder;
4: using Microsoft.AspNetCore.Http;
5: using Microsoft.AspNetCore.Routing;
6: using Microsoft.Extensions.DependencyInjection;
7: using Microsoft.Extensions.Logging;
8: using Orchestra.Engine;
9: using Orchestra.Host.Persistence;
10: using Orchestra.Host.Registry;
11: using Orchestra.Host.Triggers;
12: 
13: namespace Orchestra.Host.Api;
14: 
15: /// <summary>
16: /// API endpoints for execution streaming via SSE.
17: /// </summary>
18: public static class ExecutionApi
19: {
20: 	/// <summary>
21: 	/// Maps execution streaming endpoints.
22: 	/// </summary>
23: 	public static IEndpointRouteBuilder MapExecutionApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
24: 	{
25: 		// GET /api/orchestrations/{id}/run - Run an orchestration (SSE)
26: 		// NOTE: Must be GET for EventSource compatibility (SSE clients only support GET)
27: 		endpoints.MapGet("/api/orchestrations/{id}/run", async (
28: 			HttpContext httpContext,
29: 			string id,
30: 			OrchestrationRegistry registry,
31: 			AgentBuilder agentBuilder,
32: 			IScheduler scheduler,
33: 			ILoggerFactory loggerFactory,
34: 			FileSystemRunStore runStore,
35: 			ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
36: 			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
37: 		{
38: 			var entry = registry.Get(id);
39: 			if (entry is null)
40: 			{
41: 				httpContext.Response.StatusCode = 404;
42: 				await httpContext.Response.WriteAsJsonAsync(new { error = $"Orchestration '{id}' not found." });
43: 				return;
44: 			}
45: 
46: 			// Parse optional parameters from query string (EventSource can't send body)
47: 			Dictionary<string, string>? parameters = null;
48: 			var paramsQuery = httpContext.Request.Query["params"].FirstOrDefault();
49: 			if (!string.IsNullOrEmpty(paramsQuery))
50: 			{
51: 				try
52: 				{
53: 					var paramsEl = JsonSerializer.Deserialize<JsonElement>(paramsQuery, jsonOptions);
54: 					if (paramsEl.ValueKind == JsonValueKind.Object)
55: 					{
56: 						parameters = new Dictionary<string, string>();
57: 						foreach (var prop in paramsEl.EnumerateObject())
58: 							parameters[prop.Name] = prop.Value.GetString() ?? "";
59: 					}
60: 				}
61: 				catch { /* Invalid parameters JSON */ }
62: 			}
63: 
64: 			// Set up SSE response
65: 			httpContext.Response.ContentType = "text/event-stream";
66: 			httpContext.Response.Headers.CacheControl = "no-cache";
67: 			httpContext.Response.Headers.Connection = "keep-alive";
68: 			await httpContext.Response.Body.FlushAsync();
69: 
70: 			// Generate execution ID and create reporter
71: 			var executionId = Guid.NewGuid().ToString("N")[..12];
72: 			var reporter = new SseReporter();
73: 			var cts = new CancellationTokenSource();
74: 
75: 			activeExecutions[executionId] = cts;
76: 			var executionInfo = new ActiveExecutionInfo
77: 			{
78: 				ExecutionId = executionId,
79: 				OrchestrationId = id,
80: 				OrchestrationName = entry.Orchestration.Name,
81: 				StartedAt = DateTimeOffset.UtcNow,
82: 				TriggeredBy = "manual",
83: 				CancellationTokenSource = cts,
84: 				Reporter = reporter,
85: 				Parameters = parameters,
86: 				TotalSteps = entry.Orchestration.Steps.Length
87: 			};
88: 			activeExecutionInfos[executionId] = executionInfo;
89: 
90: 			// Set up progress callbacks
91: 			reporter.OnStepStarted = (stepName) =>
92: 			{
93: 				executionInfo.CurrentStep = stepName;
94: 			};
95: 			reporter.OnStepCompleted = (stepName) =>
96: 			{
97: 				executionInfo.CompletedSteps++;
98: 				executionInfo.CurrentStep = null;
99: 			};
100: 
101: 			// Send execution-started event
102: 			await httpContext.Response.WriteAsync($"event: execution-started\n");
103: 			await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { executionId }, jsonOptions)}\n\n");
104: 			await httpContext.Response.Body.FlushAsync();
105: 
106: 			var executor = new OrchestrationExecutor(scheduler, agentBuilder, reporter, loggerFactory, runStore: runStore);
107: 			var cancellationToken = cts.Token;
108: 			var runId = executionId;
109: 			var runStartedAt = DateTimeOffset.UtcNow;
110: 
111: 			// Execute in background
112: 			var executionTask = Task.Run(async () =>
113: 			{
114: 				try
115: 				{
116: 					var result = await executor.ExecuteAsync(
117: 						entry.Orchestration,
118: 						parameters,
119: 						cancellationToken: cancellationToken);
120: 
121: 					if (cancellationToken.IsCancellationRequested)
122: 					{
123: 						reporter.ReportOrchestrationCancelled();
124: 						executionInfo.Status = "Cancelled";
125: 						await SaveCancelledRunAsync(runStore, entry, runId, runStartedAt, parameters, reporter);
126: 						return;
127: 					}
128: 
129: 					foreach (var (stepName, stepResult) in result.StepResults)
130: 					{
131: 						if (stepResult.Status == ExecutionStatus.Succeeded)
132: 							reporter.ReportStepOutput(stepName, stepResult.Content);
133: 					}
134: 
135: 					reporter.ReportOrchestrationDone(result);
136: 					executionInfo.Status = "Completed";
137: 				}
138: 				catch (OperationCanceledException)
139: 				{
140: 					reporter.ReportOrchestrationCancelled();
141: 					executionInfo.Status = "Cancelled";
142: 					await SaveCancelledRunAsync(runStore, entry, runId, runStartedAt, parameters, reporter);
143: 				}
144: 				catch (Exception ex)
145: 				{
146: 					reporter.ReportStepError("orchestration", ex.Message);
147: 					reporter.ReportOrchestrationError(ex.Message);
148: 					executionInfo.Status = "Failed";
149: 					await SaveFailedRunAsync(runStore, entry, runId, runStartedAt, parameters, reporter, ex.Message);
150: 				}
151: 				finally
152: 				{
153: 					reporter.Complete();
154: 					_ = Task.Run(async () =>
155: 					{
156: 						await Task.Delay(TimeSpan.FromSeconds(5));
157: 						activeExecutions.TryRemove(executionId, out _);
158: 						activeExecutionInfos.TryRemove(executionId, out _);
159: 						cts.Dispose();
160: 					});
161: 				}
162: 			}, CancellationToken.None);
163: 
164: 			// Subscribe and stream SSE events to this client
165: 			var (replay, futureEvents) = reporter.Subscribe();
166: 			var sseToken = httpContext.RequestAborted;
167: 
168: 			// Replay any events that happened before we subscribed
169: 			foreach (var evt in replay)
170: 			{
171: 				await httpContext.Response.WriteAsync($"event: {evt.Type}\n", sseToken);
172: 				await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", sseToken);
173: 			}
174: 			await httpContext.Response.Body.FlushAsync(sseToken);
175: 
176: 			// Stream future events until client disconnects OR orchestration completes
177: 			try
178: 			{
179: 				await foreach (var evt in futureEvents.ReadAllAsync(sseToken))
180: 				{
181: 					await httpContext.Response.WriteAsync($"event: {evt.Type}\n", sseToken);
182: 					await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", sseToken);
183: 					await httpContext.Response.Body.FlushAsync(sseToken);
184: 				}
185: 			}
186: 			catch (OperationCanceledException)
187: 			{
188: 				reporter.Unsubscribe(futureEvents);
189: 			}
190: 
191: 			if (!sseToken.IsCancellationRequested)
192: 			{
193: 				await executionTask;
194: 			}
195: 		});
196: 
197: 		// GET /api/execution/{executionId}/attach - Attach to a running execution's SSE stream
198: 		endpoints.MapGet("/api/execution/{executionId}/attach", async (
199: 			string executionId,
200: 			HttpContext httpContext,
201: 			ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos) =>
202: 		{
203: 			if (!activeExecutionInfos.TryGetValue(executionId, out var info))
204: 			{
205: 				httpContext.Response.StatusCode = 404;
206: 				await httpContext.Response.WriteAsJsonAsync(new { error = $"No active execution with ID '{executionId}'" });
207: 				return;
208: 			}
209: 
210: 			if (info.Reporter is not SseReporter sseReporter)
211: 			{
212: 				httpContext.Response.StatusCode = 500;
213: 				await httpContext.Response.WriteAsJsonAsync(new { error = "Execution reporter is not an SseReporter" });
214: 				return;
215: 			}
216: 
217: 			// Set up SSE response
218: 			httpContext.Response.ContentType = "text/event-stream";
219: 			httpContext.Response.Headers.CacheControl = "no-cache";
220: 			httpContext.Response.Headers.Connection = "keep-alive";
221: 			await httpContext.Response.Body.FlushAsync();
222: 
223: 			var cancellationToken = httpContext.RequestAborted;
224: 
225: 			// Send current execution info
226: 			await httpContext.Response.WriteAsync($"event: execution-info\n");
227: 			await httpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(new
228: 			{
229: 				executionId = info.ExecutionId,
230: 				orchestrationId = info.OrchestrationId,
231: 				orchestrationName = info.OrchestrationName,
232: 				startedAt = info.StartedAt,
233: 				triggeredBy = info.TriggeredBy,
234: 				status = info.Status,
235: 				parameters = info.Parameters
236: 			}, jsonOptions)}\n\n");
237: 			await httpContext.Response.Body.FlushAsync();
238: 
239: 			// Subscribe to the reporter
240: 			var (replay, futureEvents) = sseReporter.Subscribe();
241: 
242: 			// Replay accumulated events
243: 			foreach (var evt in replay)
244: 			{
245: 				await httpContext.Response.WriteAsync($"event: {evt.Type}\n", cancellationToken);
246: 				await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", cancellationToken);
247: 			}
248: 			await httpContext.Response.Body.FlushAsync(cancellationToken);
249: 
250: 			// If already completed, we're done
251: 			if (sseReporter.IsCompleted)
252: 			{
253: 				return;
254: 			}
255: 
256: 			// Stream future events
257: 			try
258: 			{
259: 				await foreach (var evt in futureEvents.ReadAllAsync(cancellationToken))
260: 				{
261: 					await httpContext.Response.WriteAsync($"event: {evt.Type}\n", cancellationToken);
262: 					await httpContext.Response.WriteAsync($"data: {evt.Data}\n\n", cancellationToken);
263: 					await httpContext.Response.Body.FlushAsync(cancellationToken);
264: 				}
265: 			}
266: 			catch (OperationCanceledException)
267: 			{
268: 				sseReporter.Unsubscribe(futureEvents);
269: 			}
270: 		});
271: 
272: 		return endpoints;
273: 	}
274: 
275: 	private static async Task SaveCancelledRunAsync(
276: 		FileSystemRunStore store,
277: 		OrchestrationEntry entry,
278: 		string runId,
279: 		DateTimeOffset startTime,
280: 		Dictionary<string, string>? parameters,
281: 		SseReporter reporter)
282: 	{
283: 		var completedAt = DateTimeOffset.UtcNow;
284: 		var stepRecords = new Dictionary<string, StepRunRecord>();
285: 		var allStepRecords = new Dictionary<string, StepRunRecord>();
286: 		var summary = new System.Text.StringBuilder();
287: 		summary.AppendLine("Orchestration was cancelled.");
288: 
289: 		// Parse accumulated events to build step records
290: 		var stepsStarted = new HashSet<string>();
291: 		var stepsCompleted = new HashSet<string>();
292: 		var stepsCancelled = new HashSet<string>();
293: 		var stepErrors = new Dictionary<string, string>();
294: 
295: 		foreach (var evt in reporter.AccumulatedEvents)
296: 		{
297: 			try
298: 			{
299: 				var data = JsonSerializer.Deserialize<JsonElement>(evt.Data);
300: 				switch (evt.Type)
301: 				{
302: 					case "step-started":
303: 						if (data.TryGetProperty("stepName", out var startedName))
304: 							stepsStarted.Add(startedName.GetString() ?? "");
305: 						break;
306: 					case "step-completed":
307: 						if (data.TryGetProperty("stepName", out var completedName))
308: 							stepsCompleted.Add(completedName.GetString() ?? "");
309: 						break;
310: 					case "step-cancelled":
311: 						if (data.TryGetProperty("stepName", out var cancelledName))
312: 							stepsCancelled.Add(cancelledName.GetString() ?? "");
313: 						break;
314: 					case "step-error":
315: 						if (data.TryGetProperty("stepName", out var errorStepName) &&
316: 							data.TryGetProperty("error", out var errorMsg))
317: 							stepErrors[errorStepName.GetString() ?? ""] = errorMsg.GetString() ?? "";
318: 						break;
319: 				}
320: 			}
321: 			catch { /* Ignore parse errors */ }
322: 		}
323: 
324: 		// Build step records for ALL steps
325: 		foreach (var step in entry.Orchestration.Steps)
326: 		{
327: 			var stepName = step.Name;
328: 			ExecutionStatus status;
329: 			string? errorMessage = null;
330: 			string content = "";
331: 
332: 			if (stepsCompleted.Contains(stepName))
333: 			{
334: 				status = ExecutionStatus.Succeeded;
335: 			}
336: 			else if (stepsCancelled.Contains(stepName))
337: 			{
338: 				status = ExecutionStatus.Cancelled;
339: 				content = "[Cancelled]";
340: 				errorMessage = "Cancelled";
341: 			}
342: 			else if (stepErrors.TryGetValue(stepName, out var err))
343: 			{
344: 				status = ExecutionStatus.Failed;
345: 				errorMessage = err;
346: 			}
347: 			else if (stepsStarted.Contains(stepName))
348: 			{
349: 				status = ExecutionStatus.Cancelled;
350: 				content = "[Cancelled while in progress]";
351: 				errorMessage = "Cancelled while in progress";
352: 			}
353: 			else
354: 			{
355: 				status = ExecutionStatus.Skipped;
356: 				content = "[Skipped - orchestration cancelled]";
357: 			}
358: 
359: 			var stepRecord = new StepRunRecord
360: 			{
361: 				StepName = stepName,
362: 				Status = status,
363: 				StartedAt = stepsStarted.Contains(stepName) ? startTime : completedAt,
364: 				CompletedAt = completedAt,
365: 				Content = content,
366: 				ErrorMessage = errorMessage
367: 			};
368: 
369: 			stepRecords[stepName] = stepRecord;
370: 			allStepRecords[stepName] = stepRecord;
371: 		}
372: 
373: 		if (stepsCompleted.Count > 0)
374: 			summary.AppendLine($"Completed steps: {string.Join(", ", stepsCompleted)}");
375: 		if (stepsCancelled.Count > 0)
376: 			summary.AppendLine($"Cancelled steps: {string.Join(", ", stepsCancelled)}");
377: 		var inProgress = stepsStarted.Except(stepsCompleted).Except(stepsCancelled).ToList();
378: 		if (inProgress.Count > 0)
379: 			summary.AppendLine($"In-progress steps when cancelled: {string.Join(", ", inProgress)}");
380: 		var skipped = entry.Orchestration.Steps.Select(s => s.Name).Except(stepsStarted).ToList();
381: 		if (skipped.Count > 0)
382: 			summary.AppendLine($"Skipped steps: {string.Join(", ", skipped)}");
383: 
384: 		var record = new OrchestrationRunRecord
385: 		{
386: 			RunId = runId,
387: 			OrchestrationName = entry.Orchestration.Name,
388: 			StartedAt = startTime,
389: 			CompletedAt = completedAt,
390: 			Status = ExecutionStatus.Cancelled,
391: 			Parameters = parameters ?? new Dictionary<string, string>(),
392: 			TriggeredBy = "manual",
393: 			StepRecords = stepRecords,
394: 			AllStepRecords = allStepRecords,
395: 			FinalContent = summary.ToString()
396: 		};
397: 
398: 		try
399: 		{
400: 			await store.SaveRunAsync(record, entry.Orchestration);
401: 		}
402: 		catch (Exception ex)
403: 		{
404: 			Console.WriteLine($"Failed to save cancelled run record: {ex.Message}");
405: 		}
406: 	}
407: 
408: 	private static async Task SaveFailedRunAsync(
409: 		FileSystemRunStore store,
410: 		OrchestrationEntry entry,
411: 		string runId,
412: 		DateTimeOffset startTime,
413: 		Dictionary<string, string>? parameters,
414: 		SseReporter reporter,
415: 		string errorMessage)
416: 	{
417: 		var completedAt = DateTimeOffset.UtcNow;
418: 		var stepRecords = new Dictionary<string, StepRunRecord>();
419: 		var allStepRecords = new Dictionary<string, StepRunRecord>();
420: 		var summary = new System.Text.StringBuilder();
421: 		summary.AppendLine($"Orchestration failed: {errorMessage}");
422: 
423: 		var stepsStarted = new HashSet<string>();
424: 		var stepsCompleted = new HashSet<string>();
425: 		var stepErrors = new Dictionary<string, string>();
426: 
427: 		foreach (var evt in reporter.AccumulatedEvents)
428: 		{
429: 			try
430: 			{
431: 				var data = JsonSerializer.Deserialize<JsonElement>(evt.Data);
432: 				switch (evt.Type)
433: 				{
434: 					case "step-started":
435: 						if (data.TryGetProperty("stepName", out var startedName))
436: 							stepsStarted.Add(startedName.GetString() ?? "");
437: 						break;
438: 					case "step-completed":
439: 						if (data.TryGetProperty("stepName", out var completedName))
440: 							stepsCompleted.Add(completedName.GetString() ?? "");
441: 						break;
442: 					case "step-error":
443: 						if (data.TryGetProperty("stepName", out var errorStepName) &&
444: 							data.TryGetProperty("error", out var errorMsg))
445: 							stepErrors[errorStepName.GetString() ?? ""] = errorMsg.GetString() ?? "";
446: 						break;
447: 				}
448: 			}
449: 			catch { /* Ignore parse errors */ }
450: 		}
451: 
452: 		foreach (var stepName in stepsStarted)
453: 		{
454: 			var status = stepsCompleted.Contains(stepName)
455: 				? ExecutionStatus.Succeeded
456: 				: stepErrors.ContainsKey(stepName)
457: 					? ExecutionStatus.Failed
458: 					: ExecutionStatus.Failed;
459: 			var stepError = stepErrors.GetValueOrDefault(stepName);
460: 
461: 			var stepRecord = new StepRunRecord
462: 			{
463: 				StepName = stepName,
464: 				Status = status,
465: 				StartedAt = startTime,
466: 				CompletedAt = completedAt,
467: 				Content = status == ExecutionStatus.Failed ? "[Failed]" : "",
468: 				ErrorMessage = stepError
469: 			};
470: 
471: 			stepRecords[stepName] = stepRecord;
472: 			allStepRecords[stepName] = stepRecord;
473: 		}
474: 
475: 		if (stepsCompleted.Count > 0)
476: 			summary.AppendLine($"Completed steps: {string.Join(", ", stepsCompleted)}");
477: 		var failedSteps = stepErrors.Keys.ToList();
478: 		if (failedSteps.Count > 0)
479: 			summary.AppendLine($"Failed steps: {string.Join(", ", failedSteps)}");
480: 
481: 		var record = new OrchestrationRunRecord
482: 		{
483: 			RunId = runId,
484: 			OrchestrationName = entry.Orchestration.Name,
485: 			StartedAt = startTime,
486: 			CompletedAt = completedAt,
487: 			Status = ExecutionStatus.Failed,
488: 			Parameters = parameters ?? new Dictionary<string, string>(),
489: 			TriggeredBy = "manual",
490: 			StepRecords = stepRecords,
491: 			AllStepRecords = allStepRecords,
492: 			FinalContent = summary.ToString()
493: 		};
494: 
495: 		try
496: 		{
497: 			await store.SaveRunAsync(record, entry.Orchestration);
498: 		}
499: 		catch (Exception ex)
500: 		{
501: 			Console.WriteLine($"Failed to save failed run record: {ex.Message}");
502: 		}
503: 	}
504: }

(End of file - total 504 lines)
</content>
```

**Tool: read**

**Input:**
```json
{
  "filePath": "P:\\Github\\Orchestra\\src\\Orchestra.Engine\\Agent\\AgentBuilder.cs"
}
```

**Output:**
```
<path>P:\Github\Orchestra\src\Orchestra.Engine\Agent\AgentBuilder.cs</path>
<type>file</type>
<content>1: namespace Orchestra.Engine;
2: 
3: public abstract class AgentBuilder
4: {
5: 	protected string? Model { get; private set; }
6: 	protected string? SystemPrompt { get; private set; }
7: 	protected Mcp[] Mcps { get; private set; } = [];
8: 	protected Subagent[] Subagents { get; private set; } = [];
9: 	protected ReasoningLevel? ReasoningLevel { get; private set; }
10: 	protected SystemPromptMode? SystemPromptMode { get; private set; }
11: 	protected IOrchestrationReporter Reporter { get; private set; } = NullOrchestrationReporter.Instance;
12: 
13: 	public AgentBuilder WithModel(string model)
14: 	{
15: 		Model = model;
16: 		return this;
17: 	}
18: 
19: 	public AgentBuilder WithSystemPrompt(string systemPrompt)
20: 	{
21: 		SystemPrompt = systemPrompt;
22: 		return this;
23: 	}
24: 
25: 	public AgentBuilder WithMcp(params Mcp[] mcps)
26: 	{
27: 		Mcps = mcps;
28: 		return this;
29: 	}
30: 
31: 	public AgentBuilder WithSubagents(params Subagent[] subagents)
32: 	{
33: 		Subagents = subagents;
34: 		return this;
35: 	}
36: 
37: 	public AgentBuilder WithReasoningLevel(ReasoningLevel? reasoningLevel)
38: 	{
39: 		ReasoningLevel = reasoningLevel;
40: 		return this;
41: 	}
42: 
43: 	public AgentBuilder WithSystemPromptMode(SystemPromptMode? systemPromptMode)
44: 	{
45: 		SystemPromptMode = systemPromptMode;
46: 		return this;
47: 	}
48: 
49: 	public AgentBuilder WithReporter(IOrchestrationReporter reporter)
50: 	{
51: 		Reporter = reporter;
52: 		return this;
53: 	}
54: 
55: 	public abstract Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default);
56: }

(End of file - total 56 lines)
</content>
```

**Tool: read**

**Input:**
```json
{
  "filePath": "P:\\Github\\Orchestra\\src\\Orchestra.Engine\\Orchestration\\Steps\\OrchestrationStepType.cs"
}
```

**Output:**
```
<path>P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Steps\OrchestrationStepType.cs</path>
<type>file</type>
<content>1: namespace Orchestra.Engine;
2: 
3: public enum OrchestrationStepType
4: {
5: 	Prompt,
6: }

(End of file - total 6 lines)
</content>
```

---

## Assistant (Plan · claude-opus-4.6 · 463.7s)

Now I have a thorough understanding of the codebase. Let me ask some clarifying questions before finalizing the plan.

**Tool: question**

**Input:**
```json
{
  "questions": [
    {
      "question": "For the crash recovery/checkpoint system, you mentioned 'can this be a plugin without changing the core engine?' - The current engine has IRunStore as an abstraction. Should I design the checkpoint system as a new interface (e.g., ICheckpointStore) that the executor can optionally use, or should it be built on top of IRunStore? The former requires a small change to the engine (adding an optional dependency), while the latter requires no engine changes but is more limited.",
      "header": "Checkpoint architecture",
      "options": [
        {
          "label": "New ICheckpointStore interface (Recommended)",
          "description": "Add an optional ICheckpointStore to the executor constructor. Small engine change but much cleaner design. Supports resume-from-checkpoint natively."
        },
        {
          "label": "Build on IRunStore only",
          "description": "No engine changes. Checkpoint data stored as run records. More limited - can only replay from scratch, not resume mid-execution."
        },
        {
          "label": "Middleware/interceptor pattern",
          "description": "Wrap the executor in a checkpoint-aware decorator. Zero engine changes but adds complexity in the host layer."
        }
      ]
    },
    {
      "question": "For the plugin system (custom step types), the current architecture has only `PromptOrchestrationStep` (type: Prompt). Adding new step types (HTTP, Code, Transform) requires changes to OrchestrationStepType enum, the parser, and the executor. Should I design this as a full extensibility system with step type registration, or keep it simpler with a few built-in step types?",
      "header": "Plugin system scope",
      "options": [
        {
          "label": "Full extensibility (Recommended)",
          "description": "IStepExecutor<TStep> registration pattern. Third parties can register custom step types with their own executors. Requires parser extensibility too."
        },
        {
          "label": "Built-in step types only",
          "description": "Add HTTP, Code, Transform as built-in types in the engine. Simpler, no plugin infrastructure needed, but not extensible."
        },
        {
          "label": "Both - built-in + extensible",
          "description": "Built-in common types plus a plugin registration mechanism for custom ones. Most flexible but largest scope."
        }
      ]
    },
    {
      "question": "For the Visual Orchestration Builder, this is a significant frontend feature. Given the current state (Portal uses React via CDN with Babel standalone transpilation), should I build it into the Portal, create it as a standalone page, or first fix the Portal's build system?",
      "header": "Visual builder location",
      "options": [
        {
          "label": "Build into Portal + fix build (Recommended)",
          "description": "Fix the Portal to use a proper build step (Vite/esbuild), then add the visual builder there. Best long-term approach."
        },
        {
          "label": "Standalone page in Portal",
          "description": "Add as a new page in the existing Portal without fixing the build system. Faster but compounds the Babel issue."
        },
        {
          "label": "New separate frontend project",
          "description": "Create a new dedicated frontend app for the visual builder. Clean but adds another project to maintain."
        }
      ]
    },
    {
      "question": "For responsive design and accessibility, should I apply these changes to all three frontends (Web Playground, Portal, Terminal) or focus on one? The Portal is the newest and cleanest, while the Web Playground is the oldest with the most features.",
      "header": "UI improvements scope",
      "options": [
        {
          "label": "Portal only (Recommended)",
          "description": "Focus all UI improvements on the Portal as the 'production' frontend. It uses React and is the most maintainable."
        },
        {
          "label": "Both web frontends",
          "description": "Apply to both Portal and Web Playground. More work but covers all web users."
        },
        {
          "label": "All three",
          "description": "Include Terminal UI accessibility improvements too. Most comprehensive but largest scope."
        }
      ]
    },
    {
      "question": "For the retry policies, should retries be configurable per-step in the JSON orchestration definition, or should they be a global engine-level setting?",
      "header": "Retry configuration",
      "options": [
        {
          "label": "Per-step in JSON (Recommended)",
          "description": "Add optional retryPolicy to each step in the orchestration JSON (maxRetries, backoffSeconds, retryOn). Most flexible."
        },
        {
          "label": "Global engine setting",
          "description": "A single retry policy applied to all LLM calls. Simpler but less flexible."
        },
        {
          "label": "Both per-step and global",
          "description": "Global default that can be overridden per-step. Most flexible but more complex configuration."
        }
      ]
    }
  ]
}
```

**Output:**
```
User has answered your questions: "For the crash recovery/checkpoint system, you mentioned 'can this be a plugin without changing the core engine?' - The current engine has IRunStore as an abstraction. Should I design the checkpoint system as a new interface (e.g., ICheckpointStore) that the executor can optionally use, or should it be built on top of IRunStore? The former requires a small change to the engine (adding an optional dependency), while the latter requires no engine changes but is more limited."="New ICheckpointStore interface (Recommended)", "For the plugin system (custom step types), the current architecture has only `PromptOrchestrationStep` (type: Prompt). Adding new step types (HTTP, Code, Transform) requires changes to OrchestrationStepType enum, the parser, and the executor. Should I design this as a full extensibility system with step type registration, or keep it simpler with a few built-in step types?"="Full extensibility (Recommended)", "For the Visual Orchestration Builder, this is a significant frontend feature. Given the current state (Portal uses React via CDN with Babel standalone transpilation), should I build it into the Portal, create it as a standalone page, or first fix the Portal's build system?"="Build into Portal + fix build (Recommended)", "For responsive design and accessibility, should I apply these changes to all three frontends (Web Playground, Portal, Terminal) or focus on one? The Portal is the newest and cleanest, while the Web Playground is the oldest with the most features."="Portal only (Recommended)", "For the retry policies, should retries be configurable per-step in the JSON orchestration definition, or should they be a global engine-level setting?"="Both per-step and global". You can now continue with the user's answers in mind.
```

---

## Assistant (Plan · claude-opus-4.6 · 271.4s)

**Tool: question**

**Input:**
```json
{
  "questions": [
    {
      "header": "Versioning storage",
      "options": [
        {
          "description": "Store version metadata as JSON sidecar files. Consistent with existing file-based approach.",
          "label": "File-based sidecar (Recommended)"
        },
        {
          "description": "Use git-based versioning. Most powerful but adds git as a dependency.",
          "label": "Git integration"
        },
        {
          "description": "Version hash stored in the run record. Lightweight - just tracks which version was used for each run.",
          "label": "Run record only"
        }
      ],
      "question": "For orchestration versioning, should versions be stored as file-based sidecar metadata, or should I integrate with git, or keep it lightweight by just tracking version hashes in run records?"
    },
    {
      "header": "Portal build tool",
      "options": [
        {
          "description": "Fast, minimal config, built-in React/JSX support, HMR. Perfect for this use case.",
          "label": "Vite (Recommended)"
        },
        {
          "description": "Zero-config bundler, extremely fast. Good for simple builds but less ecosystem.",
          "label": "esbuild"
        },
        {
          "description": "Most mature, most config required. Heavy for this use case.",
          "label": "webpack"
        }
      ],
      "question": "For fixing the Portal build system (replacing Babel standalone), which build tool should I use?"
    },
    {
      "header": "Webhook enhancement scope",
      "options": [
        {
          "description": "Synchronous mode that waits for result and returns it. Per-webhook response templates. Both options in one.",
          "label": "Sync response + templates (Recommended)"
        },
        {
          "description": "Only add the ability for webhooks to return the orchestration result synchronously instead of 200 OK.",
          "label": "Sync response mode only"
        },
        {
          "description": "Add callback URL support so the result is POSTed to a URL when done.",
          "label": "Outbound webhook callbacks"
        }
      ],
      "question": "For webhook/API trigger enhancements, what's the priority? Should I focus on response transformation (returning LLM output as the webhook response), callback URLs, or both?"
    }
  ]
}
```

**Output:**
```
User has answered your questions: "For orchestration versioning, should versions be stored as file-based sidecar metadata, or should I integrate with git, or keep it lightweight by just tracking version hashes in run records?"="Remind me what you mean by versioning? can we force version in the orcehstration json? explain it to me please", "For fixing the Portal build system (replacing Babel standalone), which build tool should I use?"="Vite (Recommended)", "For webhook/API trigger enhancements, what's the priority? Should I focus on response transformation (returning LLM output as the webhook response), callback URLs, or both?"="Sync response + templates (Recommended)". You can now continue with the user's answers in mind.
```

---

## Assistant (Plan · claude-opus-4.6 · 149.6s)

Good question on versioning. Let me clarify:

**Orchestration Versioning** - Currently the `Orchestration` model has a `Version` property (defaults to `"1.0.0"`) that's set in the JSON file. But there's no tracking of *when* that version changed or *what* changed between versions. The problems are:

1. **No history**: If you edit an orchestration JSON and change it from v1.0.0 to v2.0.0, the old definition is lost forever. You can't see what v1.0.0 looked like.
2. **No run-to-version linkage**: Run records store the orchestration name but not a snapshot of the exact definition that was used. If you change the orchestration after a run, you can't reproduce it.
3. **No diff view**: You can't compare what changed between versions.

What I'd propose:

- **Keep the `version` field in JSON** (as it is today) - the author manually sets it
- **Snapshot the orchestration definition** with each run record (actually `FileSystemRunStore` already saves `orchestration.json` alongside each run - so this partly exists)
- **Add a version history file** (sidecar) that tracks when the version field changed and stores the hash of the definition at that point
- **Add a UI panel** in the Portal to browse version history and diff orchestration definitions
- **Add a "pin to version"** concept for triggers so they run a specific snapshot even if the file changes

The key question is whether you want this to be a full version control system or something lighter. The lightest version would be: just store a content hash when the orchestration is registered/updated, keep a history of those hashes with timestamps, and allow viewing the orchestration definition from any past run.

**Tool: question**

**Input:**
```json
{
  "questions": [
    {
      "header": "Versioning approach",
      "options": [
        {
          "description": "Store content hash + definition snapshot on each registration/update. History of versions with timestamps. Diff view in UI. Lightweight but useful.",
          "label": "Hash + snapshot history (Recommended)"
        },
        {
          "description": "Full history with semantic versioning enforcement, migration support between versions, branching. Much more complex.",
          "label": "Full version control"
        },
        {
          "description": "Just track which definition was used per run (already partly done). No explicit version history tracking.",
          "label": "Per-run snapshots only"
        }
      ],
      "question": "Now that I've explained the versioning concept, which level of version tracking do you want?"
    }
  ]
}
```

**Output:**
```
User has answered your questions: "Now that I've explained the versioning concept, which level of version tracking do you want?"="Hash + snapshot history (Recommended)". You can now continue with the user's answers in mind.
```

---

## Assistant (Plan · claude-opus-4.6 · 9.2s)

Now I have all the information I need. Let me build the comprehensive implementation plan.

**Tool: todowrite**

**Input:**
```json
{
  "todos": [
    {
      "content": "Phase 1: Engine Core Improvements (retry policies, orchestration timeout, checkpoint system, plugin system)",
      "status": "pending",
      "priority": "high"
    },
    {
      "content": "Phase 2: Host Layer Fixes (SSE memory, thread safety, graceful shutdown, problem details, Console.WriteLine replacement)",
      "status": "pending",
      "priority": "high"
    },
    {
      "content": "Phase 3: Webhook & Trigger Enhancements (sync response, response templates)",
      "status": "pending",
      "priority": "medium"
    },
    {
      "content": "Phase 4: Orchestration Versioning (hash + snapshot history)",
      "status": "pending",
      "priority": "medium"
    },
    {
      "content": "Phase 5: Portal Build System (Vite migration)",
      "status": "pending",
      "priority": "medium"
    },
    {
      "content": "Phase 6: Portal UI/UX (responsive design, accessibility, search/filtering, offline resilience, visual builder)",
      "status": "pending",
      "priority": "medium"
    },
    {
      "content": "Phase 7: Test Coverage Improvements",
      "status": "pending",
      "priority": "medium"
    }
  ]
}
```

**Output:**
```
[
  {
    "content": "Phase 1: Engine Core Improvements (retry policies, orchestration timeout, checkpoint system, plugin system)",
    "status": "pending",
    "priority": "high"
  },
  {
    "content": "Phase 2: Host Layer Fixes (SSE memory, thread safety, graceful shutdown, problem details, Console.WriteLine replacement)",
    "status": "pending",
    "priority": "high"
  },
  {
    "content": "Phase 3: Webhook & Trigger Enhancements (sync response, response templates)",
    "status": "pending",
    "priority": "medium"
  },
  {
    "content": "Phase 4: Orchestration Versioning (hash + snapshot history)",
    "status": "pending",
    "priority": "medium"
  },
  {
    "content": "Phase 5: Portal Build System (Vite migration)",
    "status": "pending",
    "priority": "medium"
  },
  {
    "content": "Phase 6: Portal UI/UX (responsive design, accessibility, search/filtering, offline resilience, visual builder)",
    "status": "pending",
    "priority": "medium"
  },
  {
    "content": "Phase 7: Test Coverage Improvements",
    "status": "pending",
    "priority": "medium"
  }
]
```

---

## Assistant (Plan · claude-opus-4.6 · 132.1s)

Here is the full implementation plan organized into 7 phases. Each phase lists every file to create/modify with the specific changes.

---

# Implementation Plan

## Phase 1: Engine Core Improvements

### 1.1 Retry Policies for LLM Calls

**New files:**
- `src/Orchestra.Engine/Orchestration/Steps/RetryPolicy.cs` - Model class with `MaxRetries` (int, default 0), `BackoffSeconds` (double, default 2.0), `BackoffMultiplier` (double, default 2.0), `RetryOnTimeout` (bool, default true)
- `src/Orchestra.Engine/Orchestration/Executor/RetryPolicyDefaults.cs` - Static class holding global defaults

**Modified files:**
- `src/Orchestra.Engine/Orchestration/Steps/PromptOrchestrationStep.cs` - Add `RetryPolicy? Retry { get; init; }` property
- `src/Orchestra.Engine/Orchestration/Orchestration.cs` - Add `RetryPolicy? DefaultRetryPolicy { get; init; }` for global default
- `src/Orchestra.Engine/Orchestration/Executor/PromptExecutor.cs` - Wrap `agent.SendAsync()` call in a retry loop:
  ```
  var effectiveRetry = step.Retry ?? context.DefaultRetryPolicy;
  for (var attempt = 0; attempt <= (effectiveRetry?.MaxRetries ?? 0); attempt++) {
      try { /* existing execution */ break; }
      catch (Exception ex) when (attempt < effectiveRetry.MaxRetries && ShouldRetry(ex, effectiveRetry)) {
          await Task.Delay(CalculateBackoff(attempt, effectiveRetry));
          reporter.ReportStepRetry(stepName, attempt+1, maxRetries, ex.Message);
      }
  }
  ```
- `src/Orchestra.Engine/Orchestration/Executor/ExecutionContext.cs` - Add `RetryPolicy? DefaultRetryPolicy` property
- `src/Orchestra.Engine/Orchestration/Executor/OrchestrationExecutor.cs` - Pass `orchestration.DefaultRetryPolicy` to context
- `src/Orchestra.Engine/Reporting/IOrchestrationReporter.cs` - Add `ReportStepRetry(string stepName, int attempt, int maxAttempts, string error)` method
- `src/Orchestra.Engine/Reporting/NullOrchestrationReporter.cs` - Add no-op implementation
- `src/Orchestra.Engine/Serialization/OrchestrationParser.cs` - Parse `retry` object in step JSON and `defaultRetryPolicy` at orchestration level
- `src/Orchestra.Host/Api/SseReporter.cs` - Add `ReportStepRetry` writing `"step-retry"` SSE event

**JSON example:**
```json
{
  "name": "my-orchestration",
  "defaultRetryPolicy": { "maxRetries": 2, "backoffSeconds": 3.0 },
  "steps": [{
    "retry": { "maxRetries": 3, "backoffSeconds": 1.0, "backoffMultiplier": 2.0, "retryOnTimeout": true }
  }]
}
```

### 1.2 Orchestration-Level Timeout

**Modified files:**
- `src/Orchestra.Engine/Orchestration/Orchestration.cs` - Add `int? TimeoutSeconds { get; init; }` (default null = 3600 seconds applied by executor)
- `src/Orchestra.Engine/Orchestration/Executor/OrchestrationExecutor.cs`:
  - At the start of `ExecuteAsync`, create a `CancellationTokenSource` with orchestration-level timeout:
    ```
    var orchestrationTimeout = orchestration.TimeoutSeconds ?? 3600; // 1 hour default
    using var orchestrationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    orchestrationCts.CancelAfter(TimeSpan.FromSeconds(orchestrationTimeout));
    ```
  - Pass `orchestrationCts.Token` to all step executions instead of `cancellationToken`
  - Differentiate orchestration timeout from external cancellation in the catch block
  - Add `LogOrchestrationTimedOut` logging method
- `src/Orchestra.Engine/Serialization/OrchestrationParser.cs` - Parse `timeoutSeconds` at orchestration level
- `src/Orchestra.Engine/Reporting/IOrchestrationReporter.cs` - Already has `ReportOrchestrationError`; will use that for timeout reporting

### 1.3 Checkpoint System (ICheckpointStore)

**New files:**
- `src/Orchestra.Engine/Storage/ICheckpointStore.cs` - Interface:
  ```csharp
  public interface ICheckpointStore
  {
      Task SaveCheckpointAsync(string runId, string stepName, ExecutionResult result, CancellationToken ct);
      Task<Dictionary<string, ExecutionResult>?> LoadCheckpointsAsync(string runId, CancellationToken ct);
      Task ClearCheckpointsAsync(string runId, CancellationToken ct);
  }
  ```
- `src/Orchestra.Engine/Storage/NullCheckpointStore.cs` - No-op implementation
- `src/Orchestra.Host/Persistence/FileSystemCheckpointStore.cs` - File-based implementation storing checkpoints as `{rootPath}/checkpoints/{runId}/{stepName}.json`

**Modified files:**
- `src/Orchestra.Engine/Orchestration/Executor/OrchestrationExecutor.cs`:
  - Add `ICheckpointStore _checkpointStore` as optional constructor parameter (defaults to `NullCheckpointStore.Instance`)
  - After each step succeeds, call `_checkpointStore.SaveCheckpointAsync(runId, step.Name, result)`
  - Add new `ResumeAsync` method that loads checkpoints, pre-populates `context` and `stepResults`, then only runs steps not yet completed
  - The resume logic: load checkpoints -> for each completed step, set TCS result directly -> launch remaining steps normally
- `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs` - Register `FileSystemCheckpointStore` as `ICheckpointStore`
- `src/Orchestra.Host/Api/ExecutionApi.cs` - Add `resumeRunId` query parameter to the execution endpoint; if provided, call `executor.ResumeAsync` instead of `ExecuteAsync`
- `src/Orchestra.Host/Api/RunsApi.cs` - Add `POST /api/history/{orchestrationName}/{runId}/retry` endpoint that re-executes a failed run with the same parameters; add `POST /api/history/{orchestrationName}/{runId}/resume` for resume from checkpoint

### 1.4 Plugin System (Extensible Step Types)

**New files:**
- `src/Orchestra.Engine/Orchestration/Executor/IStepExecutor.cs` - Interface:
  ```csharp
  public interface IStepExecutor<TStep> where TStep : OrchestrationStep
  {
      Task<ExecutionResult> ExecuteAsync(TStep step, OrchestrationExecutionContext context, CancellationToken ct);
  }
  ```
- `src/Orchestra.Engine/Orchestration/Executor/StepExecutorRegistry.cs` - Registry class:
  ```csharp
  public class StepExecutorRegistry
  {
      private readonly Dictionary<OrchestrationStepType, Func<OrchestrationStep, OrchestrationExecutionContext, CancellationToken, Task<ExecutionResult>>> _executors = new();
      public void Register<TStep>(OrchestrationStepType type, IStepExecutor<TStep> executor) where TStep : OrchestrationStep;
      public Task<ExecutionResult> ExecuteStepAsync(OrchestrationStep step, OrchestrationExecutionContext context, CancellationToken ct);
  }
  ```
- `src/Orchestra.Engine/Orchestration/Steps/HttpOrchestrationStep.cs` - HTTP call step: URL, method, headers, body template, expected status codes, response extraction (JSONPath or regex)
- `src/Orchestra.Engine/Orchestration/Steps/TransformOrchestrationStep.cs` - Data transformation step: input mapping, transformation expression (regex, template), output format
- `src/Orchestra.Engine/Orchestration/Executor/HttpStepExecutor.cs` - Built-in HTTP step executor using `HttpClient`
- `src/Orchestra.Engine/Orchestration/Executor/TransformStepExecutor.cs` - Built-in transform step executor
- `src/Orchestra.Engine/Serialization/IStepTypeParser.cs` - Interface for custom step type deserialization
- `src/Orchestra.Engine/Serialization/StepTypeParserRegistry.cs` - Registry for step type parsers

**Modified files:**
- `src/Orchestra.Engine/Orchestration/Steps/OrchestrationStepType.cs` - Add `Http`, `Transform` enum values
- `src/Orchestra.Engine/Orchestration/Executor/OrchestrationExecutor.cs`:
  - Accept `StepExecutorRegistry` as constructor parameter
  - In `ExecuteOrSkipStepAsync`, dispatch to the registry instead of directly to `PromptExecutor`
  - `PromptExecutor` registers itself for `OrchestrationStepType.Prompt`
- `src/Orchestra.Engine/Serialization/OrchestrationParser.cs`:
  - Use `StepTypeParserRegistry` for step deserialization
  - Register built-in parsers for Prompt, Http, Transform
  - Allow external parsers to be registered via the registry
- `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs` - Register `StepExecutorRegistry` with built-in executors

---

## Phase 2: Host Layer Fixes

### 2.1 Fix Unbounded SSE Memory Growth

**Modified files:**
- `src/Orchestra.Host/Api/SseReporter.cs`:
  - Change `_accumulatedEvents` from `List<SseEvent>` to a bounded circular buffer (max 10,000 events)
  - Change subscriber channels from `Channel.CreateUnbounded` to `Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest })`
  - Add `MaxSubscribers` constant (default 50) and reject new subscriptions beyond that
  - Implement `IDisposable`: clear `_accumulatedEvents`, complete all channels, clear `_subscribers`
  - Add a heartbeat mechanism: `WriteHeartbeat()` called periodically to detect dead connections
- `src/Orchestra.Host/Api/ExecutionApi.cs`:
  - Replace fire-and-forget cleanup `_ = Task.Run(async () => { await Task.Delay(5000); ... })` with a tracked cleanup task
  - Add periodic SSE keepalive (`event: ping\n\n`) every 30 seconds
  - Dispose `SseReporter` after execution completes and cleanup delay passes

### 2.2 Fix Thread Safety Issues

**Modified files:**
- `src/Orchestra.Host/Persistence/FileSystemRunStore.cs`:
  - Replace `ConcurrentDictionary<string, List<RunIndex>>` with `ConcurrentDictionary<string, ConcurrentBag<RunIndex>>` OR use explicit locking when mutating `List<RunIndex>` values
  - Specifically lock around `indices.Add()` (line ~148), `indices.Remove()` (line ~237), and `indices.Insert()` (line ~331)
  - Better approach: wrap the list in a thread-safe wrapper class with `lock` internally

### 2.3 Graceful Shutdown

**Modified files:**
- `src/Orchestra.Host/Triggers/TriggerManager.cs`:
  - In `StopAsync`, enumerate all active executions and cancel their `CancellationTokenSource`
  - Wait (with timeout) for in-flight executions to complete
  - Add `_activeExecutionTasks` tracking to properly await fire-and-forget tasks
  - Replace `_ = ExecuteAndHandleCompletionAsync(...)` with tracked task registration
- `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs`:
  - Register an `IHostedService` that subscribes to `IHostApplicationLifetime.ApplicationStopping` and cancels all active executions
  - OR: Add this logic to `TriggerManager.StopAsync` since it's already a `BackgroundService`

### 2.4 Problem Details for API Errors

**New files:**
- `src/Orchestra.Host/Api/ProblemDetailsMiddleware.cs` - Global exception handler that returns RFC 7807 Problem Details

**Modified files:**
- `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs` - Add `builder.Services.AddProblemDetails()`
- `src/Orchestra.Host/Extensions/EndpointRouteBuilderExtensions.cs` - Add `app.UseExceptionHandler()` and `app.UseStatusCodePages()`
- `src/Orchestra.Host/Api/ExecutionApi.cs`:
  - Replace `httpContext.Response.StatusCode = 404` + manual JSON write with `TypedResults.Problem()` or `TypedResults.NotFound()`
  - Replace silent `catch { }` blocks with logging + proper error responses
- `src/Orchestra.Host/Api/OrchestrationsApi.cs`:
  - Replace `Results.BadRequest(new { error = "..." })` with `TypedResults.Problem(detail: "...", statusCode: 400)`
  - Replace `Results.NotFound()` with `TypedResults.Problem(detail: "...", statusCode: 404)`
- `src/Orchestra.Host/Api/WebhooksApi.cs` - Replace silent catch blocks with logged errors + Problem Details
- `src/Orchestra.Host/Api/TriggersApi.cs` - Same treatment
- `src/Orchestra.Host/Api/RunsApi.cs` - Same treatment

### 2.5 Replace Console.WriteLine with ILogger

**Modified files:**
- `src/Orchestra.Host/Api/ExecutionApi.cs`:
  - Line 404: Replace `Console.WriteLine($"Failed to save cancelled run record: {ex.Message}")` with a `[LoggerMessage]` call
  - Line 501: Replace `Console.WriteLine($"Failed to save failed run record: {ex.Message}")` with a `[LoggerMessage]` call
  - Since `ExecutionApi` is a static class, need to receive `ILogger` via DI and pass it through, OR convert the save methods to accept an `ILogger` parameter

---

## Phase 3: Webhook & API Trigger Enhancements

### 3.1 Synchronous Webhook Response

**New files:**
- `src/Orchestra.Engine/Triggers/WebhookResponseConfig.cs` - Model: `WaitForResult` (bool), `ResponseTemplate` (string, Handlebars-like `{{stepName.output}}`), `TimeoutSeconds` (int, default 300)

**Modified files:**
- `src/Orchestra.Engine/Triggers/WebhookTriggerConfig.cs` - Add `WebhookResponseConfig? Response { get; init; }`
- `src/Orchestra.Host/Api/WebhooksApi.cs`:
  - If `Response.WaitForResult` is true, await the orchestration execution instead of fire-and-forget
  - Apply `ResponseTemplate` to transform the orchestration result into the HTTP response body
  - Return the result as JSON (or templated string) with appropriate status code
  - Add timeout handling for synchronous mode
- `src/Orchestra.Host/Triggers/TriggerManager.cs` - Support sync webhook execution path that returns the result
- `src/Orchestra.Engine/Serialization/OrchestrationParser.cs` - Parse `response` config in webhook trigger

### 3.2 Response Templates

The `ResponseTemplate` field uses simple `{{stepName.output}}` syntax. Implementation in `WebhooksApi.cs`:
```csharp
var response = template;
foreach (var (stepName, result) in orchestrationResult.StepResults)
    response = response.Replace($"{{{{{stepName}.output}}}}", result.Content);
```

---

## Phase 4: Orchestration Versioning

### 4.1 Version History Tracking

**New files:**
- `src/Orchestra.Host/Registry/OrchestrationVersion.cs` - Model: `VersionId` (string, content hash), `Version` (string, from JSON), `Timestamp` (DateTimeOffset), `ContentHash` (SHA256 of JSON)
- `src/Orchestra.Host/Registry/VersionHistory.cs` - Model: `OrchestrationId` (string), `Versions` (List<OrchestrationVersion>), file path to the definition at that version

**Modified files:**
- `src/Orchestra.Host/Registry/OrchestrationRegistry.cs`:
  - When an orchestration is registered or updated, compute SHA256 of the JSON content
  - Compare with the last known hash; if different, append a new `OrchestrationVersion` to the history
  - Store the version history as `{dataPath}/versions/{orchestrationId}.json`
  - Store version snapshots as `{dataPath}/versions/{orchestrationId}/{hash}.json`
  - Add `GetVersionHistory(string id)` method
  - Add `GetVersionSnapshot(string id, string versionId)` method
- `src/Orchestra.Host/Api/OrchestrationsApi.cs`:
  - Add `GET /api/orchestrations/{id}/versions` - Returns version history
  - Add `GET /api/orchestrations/{id}/versions/{versionId}` - Returns specific version snapshot
  - Add `GET /api/orchestrations/{id}/versions/{v1}/diff/{v2}` - Returns diff between two versions (simple line-by-line diff)
- `src/Orchestra.Engine/Storage/OrchestrationRunRecord.cs` - Add `string? ContentHash { get; init; }` to track which version was used

---

## Phase 5: Portal Build System (Vite Migration)

### 5.1 Set Up Vite + React Build

**New files in `playground/Hosting/Orchestra.Playground.Copilot.Portal/`:**
- `portal-ui/package.json` - Vite + React 18 + TypeScript dependencies
- `portal-ui/vite.config.ts` - Vite config with proxy to API server
- `portal-ui/tsconfig.json` - TypeScript config
- `portal-ui/index.html` - Entry HTML
- `portal-ui/src/main.tsx` - React entry point
- `portal-ui/src/App.tsx` - Main app component (migrated from inline JSX)
- `portal-ui/src/components/` - Break out components from the monolithic index.html:
  - `Sidebar.tsx`, `OrchestrationsGrid.tsx`, `OrchestrationCard.tsx`
  - `ExecutionModal.tsx`, `HistoryModal.tsx`, `ViewerModal.tsx`
  - `AddModal.tsx`, `RunModal.tsx`, `McpsModal.tsx`
  - `StatusBar.tsx`, `SearchBar.tsx`
  - `DagVisualization.tsx` (Mermaid wrapper)
- `portal-ui/src/styles/` - Extract CSS into modules
- `portal-ui/src/hooks/` - Custom hooks for polling, SSE, etc.
- `portal-ui/src/types/` - TypeScript type definitions

**Modified files:**
- `playground/Hosting/Orchestra.Playground.Copilot.Portal/wwwroot/index.html` - Replace with Vite build output (or serve from Vite dev server in development)
- `playground/Hosting/Orchestra.Playground.Copilot.Portal/Program.cs` - Add SPA fallback configuration for Vite build
- `playground/Hosting/Orchestra.Playground.Copilot.Portal/Orchestra.Playground.Copilot.Portal.csproj` - Add build step to run `npm run build` and copy output to wwwroot

---

## Phase 6: Portal UI/UX Improvements

### 6.1 Responsive Design

**Files to modify (in the new Vite component structure):**
- All component CSS files - Add media queries at 3 breakpoints:
  - Mobile: `max-width: 768px` - Collapse sidebar to hamburger menu, stack cards vertically, full-screen modals
  - Tablet: `max-width: 1024px` - Narrow sidebar, 2-column card grid
  - Desktop: `min-width: 1025px` - Full sidebar, auto-fill card grid
- `Sidebar.tsx` - Add hamburger menu toggle, overlay mode on mobile
- All modal components - Full-screen on mobile, centered on desktop
- `OrchestrationsGrid.tsx` - Responsive grid with `minmax(280px, 1fr)`

### 6.2 Accessibility

**All new component files will include:**
- ARIA labels on all interactive elements (buttons, inputs, links)
- `role="dialog"` + `aria-modal="true"` + `aria-labelledby` on all modals
- Focus trapping in modals (use a `useFocusTrap` hook)
- Escape key to close modals
- `role="search"` on search inputs
- `aria-hidden="true"` on decorative SVG icons
- Skip navigation link
- Minimum 44x44px touch targets
- WCAG AA contrast ratios (fix Portal's 7px/9px text - minimum 12px)
- `<noscript>` fallback
- Keyboard navigation for card grid (arrow keys)
- Announce status changes to screen readers via `aria-live` regions

### 6.3 Search and Filtering

**New components:**
- `portal-ui/src/components/SearchBar.tsx` - Global search with `Ctrl+K` shortcut, filters orchestrations by name/description
- `portal-ui/src/components/HistoryFilter.tsx` - Filter history by status (succeeded/failed/cancelled), date range, orchestration name
- `portal-ui/src/components/ExecutionSearch.tsx` - Search within execution trace (step names, tool calls, content)

**Modified components:**
- `HistoryModal.tsx` - Integrate `HistoryFilter`
- `App.tsx` - Integrate `SearchBar` with global keyboard shortcut
- `ExecutionModal.tsx` - Add search/filter for step trace

### 6.4 Offline Resilience

**New files:**
- `portal-ui/public/sw.js` - Service worker for static asset caching (cache-first strategy for JS/CSS, network-first for API calls)
- `portal-ui/src/hooks/useOnlineStatus.ts` - Hook wrapping `navigator.onLine` + event listeners

**Modified files:**
- `portal-ui/src/main.tsx` - Register service worker
- `portal-ui/src/App.tsx` - Show offline banner when disconnected
- `portal-ui/index.html` - Add `<noscript>` tag
- `portal-ui/package.json` - Mermaid.js as npm dependency (bundled, not CDN)

### 6.5 Visual Orchestration Builder

**New files:**
- `portal-ui/src/components/builder/` - New component directory:
  - `OrchestrationBuilder.tsx` - Main builder component with canvas area
  - `StepNode.tsx` - Draggable step node component
  - `StepEditor.tsx` - Side panel for editing step properties (name, prompt, model, MCPs, etc.)
  - `ConnectionLine.tsx` - SVG line between connected steps
  - `BuilderToolbar.tsx` - Toolbar with add step, delete, undo/redo, export JSON, import JSON
  - `BuilderCanvas.tsx` - Canvas with pan/zoom, drop target for steps
  - `StepPalette.tsx` - Palette of available step types (Prompt, HTTP, Transform + any registered plugin types)
  - `JsonPreview.tsx` - Live JSON preview panel with syntax highlighting
  - `useBuilderState.ts` - State management hook (steps array, connections, undo/redo stack)
  - `builderUtils.ts` - Utility functions for DAG validation, layout calculation, JSON serialization/deserialization

**Design:**
- Two-way sync: editing in the visual builder updates JSON, editing JSON updates the visual builder
- Drag from palette to canvas to add steps
- Drag between step connectors to create dependencies
- Click a step to open the property editor
- Real-time DAG validation (cycle detection, missing deps) with visual indicators
- Export button generates the orchestration JSON
- Import button loads existing JSON into the builder
- "Run" button that sends the built orchestration to the execution API

---

## Phase 7: Test Coverage Improvements

### 7.1 New Test Files

**Engine tests:**
- `tests/Orchestra.Engine.Tests/Executor/RetryPolicyTests.cs` - Tests for retry logic (successful retry, max retries exceeded, backoff calculation, timeout retry, no retry on non-retryable errors)
- `tests/Orchestra.Engine.Tests/Executor/OrchestrationTimeoutTests.cs` - Tests for orchestration-level timeout (timeout fires, default timeout, step timeout vs orchestration timeout)
- `tests/Orchestra.Engine.Tests/Executor/CheckpointTests.cs` - Tests for checkpoint save/load/clear, resume from checkpoint
- `tests/Orchestra.Engine.Tests/Executor/StepExecutorRegistryTests.cs` - Tests for plugin registration, dispatch, missing executor error
- `tests/Orchestra.Engine.Tests/Executor/HttpStepExecutorTests.cs` - Tests for HTTP step execution (GET/POST, headers, status codes, response extraction)
- `tests/Orchestra.Engine.Tests/Executor/TransformStepExecutorTests.cs` - Tests for transform step
- `tests/Orchestra.Engine.Tests/Serialization/RetryPolicyParsingTests.cs` - JSON parsing of retry policies
- `tests/Orchestra.Engine.Tests/Serialization/HttpStepParsingTests.cs` - JSON parsing of HTTP steps
- `tests/Orchestra.Engine.Tests/Serialization/TransformStepParsingTests.cs` - JSON parsing of Transform steps

**Host tests:**
- `tests/Orchestra.Host.Tests/SseReporterTests.cs` - Bounded buffer, max subscribers, disposal, heartbeat
- `tests/Orchestra.Host.Tests/ThreadSafetyTests.cs` - Concurrent access to FileSystemRunStore
- `tests/Orchestra.Host.Tests/GracefulShutdownTests.cs` - Active execution cancellation on shutdown
- `tests/Orchestra.Host.Tests/ProblemDetailsTests.cs` - Error response format validation
- `tests/Orchestra.Host.Tests/WebhookSyncResponseTests.cs` - Synchronous webhook response tests
- `tests/Orchestra.Host.Tests/OrchestrationVersioningTests.cs` - Version history tracking, hash computation, snapshot storage
- `tests/Orchestra.Host.Tests/CheckpointStoreTests.cs` - File-based checkpoint store

**Integration tests:**
- `tests/Orchestra.Portal.Tests/RetryEndToEndTests.cs` - Full retry flow through API
- `tests/Orchestra.Portal.Tests/CheckpointResumeTests.cs` - Resume from checkpoint through API
- `tests/Orchestra.Portal.Tests/VersionHistoryTests.cs` - Version tracking through API

**E2E tests:**
- `tests/Orchestra.Portal.E2E/VisualBuilderTests.cs` - Playwright tests for the visual builder
- `tests/Orchestra.Portal.E2E/ResponsiveDesignTests.cs` - Mobile viewport tests
- `tests/Orchestra.Portal.E2E/AccessibilityTests.cs` - Automated accessibility checks (axe-core via Playwright)
- `tests/Orchestra.Portal.E2E/SearchFilterTests.cs` - Search and filter functionality

---

## Dependency & Execution Order

```
Phase 1.1 (Retry) ─┐
Phase 1.2 (Timeout) ─┼── Can run in parallel ──> Phase 2 (Host fixes)
Phase 1.3 (Checkpoint)─┤                          │
Phase 1.4 (Plugin)  ─┘                          ├─> Phase 3 (Webhooks)
                                                 ├─> Phase 4 (Versioning)
                                                 └─> Phase 5 (Vite) ──> Phase 6 (UI/UX)
                                                                           └─> Phase 7 (Tests)
```

Phases 1.1-1.4 can be done in parallel since they touch different areas of the engine. Phase 2 should follow because host fixes need to accommodate the new engine features. Phase 5 (Vite) must precede Phase 6 (UI/UX) since the UI improvements go into the new component structure. Phase 7 (tests) runs last to cover everything.

---

## Estimated Scope

| Phase | New Files | Modified Files | Estimated Lines |
|-------|-----------|----------------|-----------------|
| 1.1 Retry | 2 | 9 | ~300 |
| 1.2 Timeout | 0 | 3 | ~50 |
| 1.3 Checkpoint | 3 | 4 | ~350 |
| 1.4 Plugin | 7 | 4 | ~600 |
| 2.x Host Fixes | 1 | 8 | ~400 |
| 3.x Webhooks | 1 | 4 | ~250 |
| 4.x Versioning | 2 | 3 | ~350 |
| 5.x Vite | ~20 | 3 | ~2000 |
| 6.x UI/UX | ~15 | 5 | ~3000 |
| 7.x Tests | ~15 | 0 | ~3000 |
| **Total** | **~66** | **~43** | **~10,300** |

---

This is a very large implementation. Should I proceed with execution starting from Phase 1?

---

## User

---

## Assistant (Compaction · claude-opus-4.6 · 79.8s)



## Goal

The user wants to implement a comprehensive set of improvements to the **Orchestra** project (located at `P:\Github\Orchestra`), a declarative orchestration engine for LLM workflows built in .NET 10.0/C#. The improvements span engine core features, host-layer fixes, UI/UX, and testing.

## Instructions

The user approved the following features for implementation, with specific architectural choices made via Q&A:

### Features to implement (in order of phases):

**Phase 1: Engine Core Improvements**
1. **Retry policies for LLM calls** — Both per-step and global default. Add `RetryPolicy` model (maxRetries, backoffSeconds, backoffMultiplier, retryOnTimeout). Per-step `retry` property on `PromptOrchestrationStep`, global `defaultRetryPolicy` on `Orchestration`. Wrap agent execution in retry loop in `PromptExecutor.cs`.
2. **Orchestration-level timeout** — Add `TimeoutSeconds` property to `Orchestration` model. Default is 3600 seconds (1 hour). Create linked CancellationTokenSource in executor.
3. **Crash recovery/checkpoint system** — New `ICheckpointStore` interface in the engine (user chose this over IRunStore-only or middleware approaches). `FileSystemCheckpointStore` implementation in Host. Save checkpoints after each step succeeds. Add `ResumeAsync` method to executor. Add resume/retry API endpoints.
4. **Plugin system for custom step types** — Full extensibility with `IStepExecutor<TStep>` registration pattern (user chose this over built-in-only). New `StepExecutorRegistry`. Built-in HTTP and Transform step types. Parser extensibility via `IStepTypeParser`/`StepTypeParserRegistry`.

**Phase 2: Host Layer Fixes**
5. **Fix unbounded SSE memory growth** — Bounded circular buffer for accumulated events (max 10,000), bounded channels for subscribers (1,000 with DropOldest), max 50 subscribers, implement IDisposable, add heartbeat/keepalive.
6. **Fix thread safety issues** — `FileSystemRunStore._indexByOrchestration` uses ConcurrentDictionary wrapping non-thread-safe `List<RunIndex>`. Add proper locking.
7. **Graceful shutdown** — Cancel all active executions in `TriggerManager.StopAsync`, track fire-and-forget tasks, await in-flight executions with timeout.
8. **Problem Details for API errors** — RFC 7807 Problem Details. Global exception handler middleware. Replace all ad-hoc error objects with `TypedResults.Problem()`. Replace silent `catch { }` blocks with logging.
9. **Replace Console.WriteLine with ILogger** — Two instances in `ExecutionApi.cs` lines 404 and 501.

**Phase 3: Webhook & Trigger Enhancements**
10. **Synchronous webhook response + response templates** — `WebhookResponseConfig` with `WaitForResult`, `ResponseTemplate` (using `{{stepName.output}}` syntax), `TimeoutSeconds`.

**Phase 4: Orchestration Versioning**
11. **Hash + snapshot history** — SHA256 content hash on registration/update, version history sidecar files, definition snapshots, diff view API endpoints. Track content hash in run records.

**Phase 5: Portal Build System**
12. **Vite migration** — Replace Babel Standalone with Vite + React 18 + TypeScript. Break monolithic 5,245-line `index.html` into proper component structure under `portal-ui/` directory.

**Phase 6: Portal UI/UX**
13. **Responsive design** — Portal only (user's choice). Three breakpoints: mobile ≤768px, tablet ≤1024px, desktop. Collapsible sidebar, responsive grid, full-screen modals on mobile.
14. **Accessibility** — ARIA labels, focus trapping in modals, Escape key handling, skip navigation, 44x44px touch targets, WCAG AA contrast, `aria-live` regions.
15. **Search and filtering** — Global search with Ctrl+K, history filtering by status/date/name, execution trace search.
16. **Offline resilience** — Service worker for static assets, `useOnlineStatus` hook, offline banner, bundle CDN deps locally, `<noscript>` tag.
17. **Visual Orchestration Builder** — Drag-and-drop DAG builder with step palette, connection lines, property editor, live JSON preview, two-way sync, DAG validation, export/import.

**Phase 7: Test Coverage**
18. **~15 new test files** covering all new features (retry, timeout, checkpoint, plugin, SSE bounds, thread safety, graceful shutdown, problem details, webhooks, versioning, visual builder E2E, responsive E2E, accessibility E2E).

### Execution order:
Phases 1.1-1.4 can run in parallel → Phase 2 → Phases 3, 4, 5 in parallel → Phase 6 (after 5) → Phase 7 (last)

### Estimated scope: ~66 new files, ~43 modified files, ~10,300 lines of code.

## Discoveries

### Project Architecture (4 library layers):
- **`Orchestra.Engine`** — Core library with zero external deps (only `Microsoft.Extensions.Logging.Abstractions`). Contains: domain models, DAG scheduler (Kahn's algorithm), `OrchestrationExecutor` (reactive push-based parallel execution via TaskCompletionSource per step), abstract `AgentBuilder`/`IAgent` interfaces, `IRunStore`/`IOrchestrationReporter` abstractions.
- **`Orchestra.Copilot`** — GitHub Copilot SDK integration. Implements `CopilotAgentBuilder`/`CopilotAgent` with streaming via Channels.
- **`Orchestra.Host`** — ASP.NET Core hosting: REST API endpoints, `OrchestrationRegistry`, `TriggerManager` (BackgroundService), `FileSystemRunStore`, `SseReporter`.
- **`Orchestra.Mcp.Graph`** — Standalone stdio MCP server for Microsoft 365 Graph API.

### Key architectural details:
- The executor does NOT use the scheduler's layered output at runtime — it uses reactive push-based dependency resolution (each step, on completion, checks its dependents and launches any whose deps are all satisfied).
- `PromptOrchestrationStep` is the only step type currently (type: `Prompt`). The `OrchestrationStepType` enum has only one value.
- `AgentBuilder` is a mutable abstract class with fluent API — shared as singleton. `CopilotAgentBuilder.BuildAgentAsync()` captures state snapshot to avoid race conditions.
- All persistence is file-based (no database). Run storage uses JSON files in `{rootPath}/executions/{name}/{name}_{version}_{trigger}_{timestamp}_{id}/`.
- The `SseReporter` accumulates ALL events in an unbounded `List<SseEvent>` and uses `Channel.CreateUnbounded<SseEvent>()` for subscribers — both unbounded.
- `FileSystemRunStore._indexByOrchestration` wraps non-thread-safe `List<RunIndex>` in `ConcurrentDictionary` — concurrent mutations are unsafe.
- Two `Console.WriteLine` calls in `ExecutionApi.cs` at lines 404 and 501 bypass structured logging.
- Fire-and-forget cleanup uses `_ = Task.Run(async () => { await Task.Delay(5000); ... })` pattern in `ExecutionApi.cs` lines 154-159 and `TriggerManager.cs` lines 650-654.

### Testing patterns:
- xUnit 2.9.2 with `[Fact]` attributes
- FluentAssertions 7.0.0 for assertions
- NSubstitute 5.3.0 for mocking
- Source-generated logging with `[LoggerMessage]` attributes used extensively
- Central package management via `Directory.Packages.props`
- Solution uses `.slnx` format (XML-based)
- Target framework: `net10.0`

### Frontend details:
- Portal uses React 18 + Babel Standalone via CDN (in-browser transpilation) — ~5,245 lines in single `index.html`
- Web Playground uses vanilla JS — ~7,033 lines in `wwwroot/index.html` + `Program.cs`
- Terminal uses Spectre.Console
- Portal project targets `net10.0-windows` with `UseWindowsForms=true`
- No media queries, no ARIA labels, no responsive design in either web frontend
- Mermaid.js loaded from CDN for DAG visualization

## Accomplished

- ✅ **Complete codebase exploration** — All source files across all 4 library projects, 4 playground projects, and 6 test projects have been read and analyzed
- ✅ **Security audit** — Critical issues identified (no auth, path traversal, broken HMAC, no rate limiting)
- ✅ **Error handling audit** — No retry mechanisms, inconsistent error responses, silent catch blocks, no dead letter queues
- ✅ **Scalability audit** — Memory growth risks, fake pagination, no concurrent execution limits, file I/O issues
- ✅ **UI/UX audit** — Accessibility, responsive design, loading states, offline handling, search/filter gaps
- ✅ **Comprehensive improvement recommendations** delivered to user
- ✅ **Detailed implementation plan** created with 7 phases, file-by-file changes, and execution order
- ✅ **All architectural decisions** resolved via Q&A with user
- ❌ **No code has been written yet** — User asked to proceed with Phase 1, then requested this summary before continuing

### What to do next:
Start implementing Phase 1 (Engine Core Improvements), beginning with any of 1.1-1.4 as they can run in parallel. Then proceed through phases 2-7 in order.

## Relevant files / directories

### Source Projects (to be modified):
- `P:\Github\Orchestra\src\Orchestra.Engine\` — Core engine (47 .cs files)
  - `Orchestration/Orchestration.cs` — Top-level model (add TimeoutSeconds, DefaultRetryPolicy)
  - `Orchestration/Executor/OrchestrationExecutor.cs` — Core executor (497 lines) — major changes for retry, timeout, checkpoint, plugin dispatch
  - `Orchestration/Executor/PromptExecutor.cs` — Prompt execution (201 lines) — add retry loop
  - `Orchestration/Executor/ExecutionContext.cs` — Shared context (100 lines) — add DefaultRetryPolicy
  - `Orchestration/Executor/Executor.cs` — Abstract base executor
  - `Orchestration/Executor/ExecutionResult.cs` — Step result model
  - `Orchestration/Steps/PromptOrchestrationStep.cs` — Prompt step model (add Retry property)
  - `Orchestration/Steps/OrchestrationStepType.cs` — Enum with only `Prompt` (add Http, Transform)
  - `Orchestration/Steps/OrchestrationStep.cs` — Abstract base step
  - `Serialization/OrchestrationParser.cs` — JSON parser (323 lines) — parse new step types, retry, timeout
  - `Reporting/IOrchestrationReporter.cs` — Reporter interface (add ReportStepRetry)
  - `Reporting/NullOrchestrationReporter.cs` — No-op reporter
  - `Storage/IRunStore.cs` — Run store interface
  - `Storage/OrchestrationRunRecord.cs` — Run record model (add ContentHash)
  - `Agent/AgentBuilder.cs` — Abstract fluent builder (56 lines)
  - `Agent/IAgent.cs` — Agent interface (6 lines)
- `P:\Github\Orchestra\src\Orchestra.Host\` — Hosting layer (17 files)
  - `Api/SseReporter.cs` — SSE reporter (350 lines) — bound memory, add heartbeat, IDisposable
  - `Api/ExecutionApi.cs` — Execution endpoints (504 lines) — fix Console.WriteLine, add resume/retry, fix cleanup
  - `Api/OrchestrationsApi.cs` — CRUD endpoints (503 lines) — Problem Details, version history endpoints
  - `Api/WebhooksApi.cs` — Webhook receiver (107 lines) — sync response, templates
  - `Api/RunsApi.cs` — History endpoints (380 lines) — Problem Details, retry/resume endpoints
  - `Api/TriggersApi.cs` — Trigger endpoints (168 lines) — Problem Details
  - `Api/UtilityApi.cs` — Utility endpoints (131 lines)
  - `Extensions/ServiceCollectionExtensions.cs` — DI registration (167 lines) — register new services
  - `Extensions/EndpointRouteBuilderExtensions.cs` — Endpoint mapping (122 lines) — add exception handler
  - `Persistence/FileSystemRunStore.cs` — File storage (443 lines) — fix thread safety
  - `Registry/OrchestrationRegistry.cs` — Registry — add versioning
  - `Triggers/TriggerManager.cs` — Background service (895 lines) — graceful shutdown, track tasks
- `P:\Github\Orchestra\src\Orchestra.Copilot\` — Copilot integration (3 files, mostly unmodified)

### Playground Projects:
- `P:\Github\Orchestra\playground\Hosting\Orchestra.Playground.Copilot.Portal\` — Portal app (main UI target)
  - `wwwroot/index.html` — 5,245-line React SPA to be replaced with Vite build
  - `Program.cs` — Portal entry point (~144 lines)
  - `portal-ui/` — NEW directory for Vite project (Phase 5)

### Test Projects:
- `P:\Github\Orchestra\tests\Orchestra.Engine.Tests\` — Engine unit tests
- `P:\Github\Orchestra\tests\Orchestra.Host.Tests\` — Host unit tests
- `P:\Github\Orchestra\tests\Orchestra.Portal.Tests\` — Integration tests
- `P:\Github\Orchestra\tests\Orchestra.Portal.E2E\` — Playwright E2E tests

### Configuration Files:
- `P:\Github\Orchestra\Directory.Packages.props` — Central package versions (36 lines)
- `P:\Github\Orchestra\Directory.Build.props` — Shared build properties (9 lines)
- `P:\Github\Orchestra\OrchestrationEngine.slnx` — Solution file (22 lines, .slnx format)

### Example Orchestrations:
- `P:\Github\Orchestra\examples\` — 13 example JSON orchestrations (useful for testing new features)

---

## User

---

## Assistant (Plan · claude-opus-4.6 · 464.3s)



Based on my context, we've completed a thorough codebase exploration and audit, made all architectural decisions, and have a detailed implementation plan ready. **No code has been written yet.** The next step is to begin implementing Phase 1: Engine Core Improvements.

However, I'm currently in **plan/read-only mode**, so I cannot make any code changes. Let me confirm the implementation order and check if anything needs clarification before we start writing code.

Here's where we stand:

## Ready to Implement

### Phase 1 (Engine Core) — 4 parallel workstreams:

| # | Feature | New Files | Modified Files | Est. LOC |
|---|---------|-----------|----------------|----------|
| 1.1 | Retry policies | 2 new | 4 modified | ~250 |
| 1.2 | Orchestration timeout | 0 new | 3 modified | ~80 |
| 1.3 | Checkpoint/crash recovery | 3 new (engine) + 1 new (host) | 3 modified | ~500 |
| 1.4 | Plugin system for custom steps | ~8 new | 5 modified | ~700 |

### Implementation approach for Phase 1:
1. **1.1 + 1.2 together** — Retry and timeout are closely related (both touch `PromptExecutor`, `ExecutionContext`, `Orchestration` model, and the parser). Doing them together avoids merge conflicts.
2. **1.3 next** — Checkpoint system adds `ICheckpointStore` interface to engine + `FileSystemCheckpointStore` in host, hooks into the executor after each step.
3. **1.4 last in Phase 1** — Plugin system is the largest change, adding `IStepExecutor<TStep>`, `StepExecutorRegistry`, new step types (HTTP, Transform), and parser extensibility.

### Then Phases 2-7 sequentially as planned.

**Tool: question**

**Input:**
```json
{
  "questions": [
    {
      "question": "Ready to start implementing? I'll begin with Phase 1.1 + 1.2 (retry policies + orchestration timeout) since they share many of the same files.",
      "header": "Start implementation?",
      "options": [
        {
          "label": "Yes, start Phase 1.1 + 1.2",
          "description": "Begin implementing retry policies and orchestration timeout together"
        },
        {
          "label": "Yes, but start with 1.3",
          "description": "Begin with checkpoint/crash recovery instead"
        },
        {
          "label": "Yes, but start with 1.4",
          "description": "Begin with the plugin system instead"
        },
        {
          "label": "Review the plan first",
          "description": "I want to review or adjust the plan before any code is written"
        }
      ]
    }
  ]
}
```

**Output:**
```
User has answered your questions: "Ready to start implementing? I'll begin with Phase 1.1 + 1.2 (retry policies + orchestration timeout) since they share many of the same files."="Yes, start Phase 1.1 + 1.2". You can now continue with the user's answers in mind.
```

---

## Assistant (Plan · claude-opus-4.6)

Let me start by reading the current state of the key files I'll need to modify for retry policies and orchestration timeout, then begin implementation.

**Tool: todowrite**

**Input:**
```json
{}
```

---

