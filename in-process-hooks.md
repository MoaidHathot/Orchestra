# Offload orchestrator schedulers to hook service

**Session ID:** ses_1d6381bfbffe01jZf8TyEi3T2Z
**Created:** 5/14/2026, 8:57:06 PM
**Updated:** 5/14/2026, 11:17:02 PM

---

## User

Currently, for many things like tracking PR comments, etc there is no way to have hooks out of the box to call orchestrations.
Instead, we have this pattern of orchestrations that starts with a scheduler and only if there is new data they do something or call other orchestrations.

This works but it can spam the orchestration list (and history), and also less performant (new dedicated CLI instance/sessions, etc).

Now that we have the notion of "services" that orchestra loads on startup, I'm starting to think whether we can offload those schedulers to a different service. That service can use schedulers or whateber it needs, but invoke an orchestration only if there is real data via hook.

What I'm wondering and asking you to help with, is to think if this is a viable option, if it is better than the current workflow and how will it work. Should the service invoke hooks, or should it have a queue? How it knows which hook to call? the hook URL should be stable.
Also, can it be viable/easy to easily add new type of hooks or checks? like we need code to do that? every time change the logic of the service? although that is not ideal and beats the purpose of having json/yaml based orchestrations

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 7.4s)

_Thinking:_

 I need to dig into the codebase to understand how schedulers currently work and whether moving them to a dedicated service would be an improvement—specifically, I'm wondering about the mechanics of hook invocation, how the system would know which hooks to trigger, and whether the URLs would remain stable across changes.

**Tool: bash**

**Input:**
```json
{
  "command": "Get-ChildItem -LiteralPath \"P:\\Github\\Orchestra\" -Directory | Select-Object Name",
  "description": "List top-level directories of Orchestra repo"
}
```

**Output:**
```

[32;1mName[0m
[32;1m----[0m
.github
.opencode
artifacts
docs
examples
playground
schemas
scripts
skills
src
tests
utils


```

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 4.2s)

**Tool: bash**

**Input:**
```json
{
  "command": "Get-ChildItem -LiteralPath \"P:\\Github\\Orchestra\\src\" -Directory | Select-Object Name",
  "description": "List src directories"
}
```

**Output:**
```

[32;1mName[0m
[32;1m----[0m
Orchestra.Cli
Orchestra.Copilot
Orchestra.Engine
Orchestra.Host
Orchestra.ProcessHost
Orchestra.Server
Orchestra.Tool


```

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 220.5s)

**Tool: task**

**Input:**
```json
{
  "description": "Explore Orchestra services and hooks",
  "prompt": "I need a thorough understanding of how the Orchestra repo at P:\\Github\\Orchestra handles three concepts that are critical to my plan:\n\n1. **Services** - There's a \"services\" notion that Orchestra loads on startup. Find where services are defined, registered, loaded, configured (JSON/YAML?), their lifecycle, and what they can do. Look for files containing \"service\" / \"services\" in src/ (Orchestra.Engine, Orchestra.Host, Orchestra.Server, etc). Show me:\n   - The service contract/interface\n   - How services are discovered/loaded on startup\n   - Example services that exist\n   - How services can interact with orchestrations (can they trigger orchestrations? Through what mechanism?)\n   - Configuration format (JSON/YAML structure)\n\n2. **Hooks** - Orchestra has hooks that can invoke orchestrations. Find:\n   - How hooks are defined (URL? HTTP endpoint? config?)\n   - The hook handler/server code\n   - How a hook gets bound to an orchestration\n   - Hook URL stability — are hook URLs stable across restarts?\n   - Where hooks are stored / how to register a hook\n   - Existing examples of hooks in examples/ or docs/\n\n3. **Scheduler pattern for polling** - The user mentions a pattern where \"orchestrations start with a scheduler and only if there is new data they do something or call other orchestrations\" (e.g., PR comment tracking). Find concrete examples of this pattern in examples/ or tests/. Look for orchestrations with scheduler steps and conditional dispatch.\n\n4. **Orchestration invocation API** - How are orchestrations invoked programmatically from inside Orchestra (not external CLI)? Is there an in-process API a service can use to start an orchestration, or must it call back into the CLI / HTTP endpoint?\n\nReturn concrete file paths with line numbers (file:line) for each major piece, code snippets of the key contracts/interfaces (service contract, hook contract, orchestration invocation API), example service config and example hook config, and the existing scheduler pattern in an example. Be thorough — I am about to design a major architectural change and need accurate ground truth.",
  "subagent_type": "explore"
}
```

**Output:**
```
task_id: ses_1d637bc14ffeGJpbmlzT5qUpxW (for resuming to continue this task if needed)

<task_result>
I have comprehensive ground truth. Here is the consolidated report.

---

# Orchestra Architecture — Ground Truth for Services, Hooks, Scheduler-Polling, and Invocation

## TL;DR — Three Important Disambiguations

Orchestra uses the word "hook" and "service" in **three distinct ways**. Don't confuse them:

| Term | What it actually is | Project | Can it invoke an orchestration? |
|---|---|---|---|
| **"Service"** (`orchestra.services.json`) | External OS process or one-shot shell command managed alongside Orchestra (think docker-compose-lite). | `Orchestra.ProcessHost` | **No** — no API back into Orchestra. They're long-running OS processes. |
| **`CommandHook`** (in `orchestra.services.json`) | A one-shot shell command run at `beforeStart` / `afterStop` of the Orchestra host. | `Orchestra.ProcessHost` | **No** — they run before/after the host, not bound to orchestrations. |
| **`HookDefinition`** (in an orchestration's YAML/JSON) | Per-orchestration (or global) event handler that runs a script when an event fires (`step.success`, `orchestration.failure`, `step.awaitingInput`, etc.). | `Orchestra.Engine` | **Indirectly** — it runs a script. The script can call back into Orchestra via the MCP `invoke_orchestration` tool or `POST /api/webhooks/{id}`. |
| **Webhook trigger** | HTTP endpoint `POST /api/webhooks/{triggerId}` that fires a specific orchestration. | `Orchestra.Host` | **Yes** — this is the actual "hook that invokes an orchestration." |

The user's mental model "hook → invokes orchestration" most directly maps to **webhook triggers**, not `HookDefinition`. Both are documented below.

---

## 1. Services (`orchestra.services.json`)

### Contract / base type

`P:\Github\Orchestra\src\Orchestra.ProcessHost\Models\ServiceEntry.cs:7-33` — abstract base:

```csharp
public abstract class ServiceEntry
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string>? Env { get; init; }
}
```

Two concrete subtypes:

- **`ProcessService`** — long-running process, with `RestartPolicy` (`Never`/`OnFailure`/`Always`), `ReadinessCheck`, `ShutdownTimeoutSeconds`, `ForceKill`, `Required`. (`P:\Github\Orchestra\src\Orchestra.ProcessHost\Models\ProcessService.cs:7-38`)
- **`CommandHook`** — one-shot command, with `RunAt` (`BeforeStart` / `AfterStop`), `TimeoutSeconds`, `Required`. (`P:\Github\Orchestra\src\Orchestra.ProcessHost\Models\CommandHook.cs`)

These are discriminated by the JSON `"type"` field: `"process"` or `"command"`.

### Lifecycle / loader

- Discovery & loading: `P:\Github\Orchestra\src\Orchestra.Host\Extensions\ServiceCollectionExtensions.cs:387-417` (`InitializeOrchestraHostAsync`) calls `OrchestraConfigLoader.ResolveServiceConfigPath()` + `OrchestraConfigLoader.LoadServiceConfig(...)`, then hands the array to `OrchestraInfrastructureLifecycle.InitializeAsync(serviceManager, mcpManager, serviceEntries, globalMcps, shutdownToken)`.
- Manager: `P:\Github\Orchestra\src\Orchestra.ProcessHost\ServiceManager.cs:12-110` — `InitializeAsync(ServiceEntry[])`:
  1. Validates uniqueness.
  2. Runs all `beforeStart` `CommandHook`s **concurrently** (ServiceManager.cs:116-144); a failure of a `Required` hook throws `ServiceInitializationException` and aborts host startup.
  3. Starts all `ProcessService`s in **parallel** with readiness checks (ServiceManager.cs:174-215).
  4. For each, if `RestartPolicy != Never`, spawns a `RestartLoopAsync` (ServiceManager.cs:241-316) with exponential backoff capped at 30s.
- Shutdown: `ServiceManager.StopAsync` (line 422) cancels restart loops, stops processes in parallel, then runs `afterStop` hooks concurrently.
- Orphan handling: `ProcessTracker` records PIDs to `.orchestra.pids.json` so crashed-host orphans are cleaned up on next startup (`ServiceCollectionExtensions.cs:191-200`).

### Skipped in tests
`InitializeOrchestraHostAsync` skips the services file when env is `"Testing"` or config `"skip-services"=true` (`ServiceCollectionExtensions.cs:377-415`).

### Example config — `P:\Github\Orchestra\examples\orchestra.services.json:1-55`

```json
{
  "$schema": "../schemas/orchestra.services.schema.json",
  "services": [
    { "name": "migrate-db", "type": "command", "command": "dotnet",
      "arguments": ["ef","database","update"],
      "runAt": "beforeStart", "timeoutSeconds": 120, "required": true },
    { "name": "redis", "type": "process", "command": "redis-server",
      "arguments": ["--port","6379"],
      "restartPolicy": "onFailure",
      "readiness": { "stdoutPattern": "Ready to accept connections", "timeoutSeconds": 15 },
      "required": true },
    { "name": "dev-proxy", "type": "process", "command": "node",
      "arguments": ["proxy.js"], "workingDirectory": "P:/Github/MyProject/tools",
      "env": { "PORT": "8080" },
      "restartPolicy": "always",
      "readiness": { "healthCheckUrl": "http://localhost:8080/health" } },
    { "name": "cleanup-temp", "type": "command", "command": "dotnet",
      "arguments": ["script","cleanup.csx"], "runAt": "afterStop" }
  ]
}
```

JSON schema: `P:\Github\Orchestra\schemas\orchestra.services.schema.json`.

### Capabilities — and limits

- ✅ Start/stop external processes, run one-shot setup/teardown commands.
- ✅ Readiness gates (stdout regex or HTTP health probe).
- ✅ Restart policies with backoff, PID tracking, force-kill.
- ❌ **No API surface back into Orchestra.** A "service" cannot call into the engine to launch an orchestration. It's purely OS-process management. If you want that, the service must call `POST /api/webhooks/{id}` or the MCP `invoke_orchestration` tool externally — but at that point it's just another external HTTP/MCP client.

---

## 2a. Webhook Triggers (the "hook that fires an orchestration")

This is the mechanism you described. Webhook triggers live **on an orchestration definition** as part of its `trigger:` block.

### Contract

`P:\Github\Orchestra\src\Orchestra.Engine\Triggers\TriggerConfig.cs:7-29` (base) + `P:\Github\Orchestra\src\Orchestra.Engine\Triggers\WebhookTriggerConfig.cs:7-52`:

```csharp
public class WebhookTriggerConfig : TriggerConfig
{
    public string? Secret { get; init; }          // HMAC-SHA256 validation
    public int MaxConcurrent { get; init; } = 1;
    public WebhookResponseConfig? Response { get; init; }  // sync vs fire-and-forget
}
public class WebhookResponseConfig
{
    public bool WaitForResult { get; init; }
    public string? ResponseTemplate { get; init; }   // {{stepName.content}} placeholders
    public int TimeoutSeconds { get; init; } = 120;
}
// from base TriggerConfig:
//   bool Enabled, string? InputHandlerPrompt, string? InputHandlerModel
```

### How it's bound to an orchestration

The webhook lives **inside** the orchestration file under `trigger:`. Example: `P:\Github\Orchestra\examples\webhook-triggered-notification.json:34-39`:

```json
"trigger": {
  "type": "webhook",
  "enabled": true,
  "maxConcurrent": 5,
  "inputHandlerPrompt": "You will receive a raw JSON payload... extract eventType, eventData..."
}
```

When the host registers the orchestration's trigger, the webhook is **assigned the orchestration's registry ID** (no separate registration step). See `ServiceCollectionExtensions.cs:474-501` in `InitializeOrchestraHostAsync` where `triggerManager.RegisterTrigger(..., entry.Id, ...)` is called.

### Hook URL stability across restarts

URL format: `POST /api/webhooks/{triggerId}` (`P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs:22-25`).

The `triggerId` **is the registry ID**, generated deterministically (`P:\Github\Orchestra\src\Orchestra.Host\Registry\OrchestrationRegistry.cs:427-441`):

```csharp
public static string GenerateId(string name, string path)
{
    var hash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8].ToLowerInvariant();
    return $"{SanitizeId(name)}-{hash[..4]}";  // e.g. "icm-auto-acknowledge-a1b2"
}
```

**Stability rules:**
- ✅ **Stable across restarts** as long as the orchestration's `name` and **file path** don't change. The hash is over `path`.
- ⚠️ **Breaks** if the file is moved/renamed, the orchestration's name in the file changes, or the file is loaded from a different absolute path on different machines.
- ⚠️ Programmatically-registered orchestrations may use other code paths (e.g. line 139 uses `$"json-import:{name}"` as the path basis). Inspect call sites if you need full guarantees.

### Receiver handler

`P:\Github\Orchestra\src\Orchestra.Host\Api\WebhooksApi.cs:25-87`:
1. Look up trigger by id.
2. Buffer body for HMAC validation against `webhookConfig.Secret` via `X-Webhook-Signature` header (`WebhookSignatureValidator`).
3. Parse JSON body into `Dictionary<string,string>`.
4. Call `triggerManager.FireWebhookTriggerAsync(id, webhookParams)` (`TriggerManager.cs:280-321`).
5. Return either `{accepted, triggerId, executionId}` async, or the orchestration result inline if `Response.WaitForResult=true`.

### "Register a hook" without modifying the orchestration

The webhook is not a separate entity — to "register a new hook" you either:
- Add a `trigger: { type: webhook }` block to an orchestration file, or
- Programmatically register via `TriggerManager.RegisterTrigger(...)` with a `WebhookTriggerConfig`.

There is no separate "hook store." This is critical for your architectural plan.

---

## 2b. `HookDefinition` (per-orchestration event scripts)

Different concept: these run scripts in response to orchestration/step events. They cannot directly invoke an orchestration (no "fire orchestration X" action type), but the script they run is free to do so.

### Contract — `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Hooks\HookDefinition.cs:69-146`

```csharp
public class HookDefinition
{
    public string? Name { get; set; }
    public HookEventType On { get; set; }            // orchestration.success/.failure/.after,
                                                     // step.success/.failure/.after, step.awaitingInput
    public HookWhenFilter? When { get; set; }        // filter by step name+status
    public HookPayloadOptions Payload { get; set; }  // detail level, steps selection, includeRefs
    public required HookAction Action { get; set; }  // currently only `type: script`
    public HookFailurePolicy FailurePolicy { get; set; } = HookFailurePolicy.Warn;
}
public class HookAction
{
    public HookActionType Type { get; set; } = HookActionType.Script;
    public string? Shell { get; set; }       // pwsh by default
    public string? Script { get; set; }      // inline
    public string? ScriptFile { get; set; }  // path (resolved relative to orchestration file)
    public string[] Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = [];
    public bool IncludeStdErr { get; set; }
}
```

### Runtime

`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Hooks\HookRuntime.cs:28-100`: the engine fires hooks at event boundaries, builds a JSON payload (`BuildPayload`, line 127), and **runs the action via the same `ScriptStepExecutor`** the engine uses for `type: Script` steps — passing the payload on stdin (line 88). Failures are logged per `FailurePolicy`.

### Example — `P:\Github\Orchestra\examples\hooks-step-failure.yaml`

```yaml
hooks:
  - name: capture-build-or-deploy-failure
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
      scriptFile: ./hooks/write-hook-payload.ps1
      arguments: [./artifacts/step-failure-payload.json]
```

### Global hooks

`OrchestrationHostOptions.Hooks` (loaded from `orchestra.json`) is passed into both `TriggerManager` (`ServiceCollectionExtensions.cs:257`) and `ChildOrchestrationLauncher` (line 265) as `globalHooks`. Per-orchestration hooks merge with these.

### To invoke an orchestration from a hook script

The script can `curl -X POST http://localhost:.../api/webhooks/<id>` or use the Orchestra MCP `invoke_orchestration` tool. There's no first-class "fire orchestration" hook action — you'd add one or use a script.

---

## 3. Scheduler-Polling Pattern ("only act if there's new data")

Idiomatic pattern: **scheduler trigger** → **fetch** step → **gate** step that uses the `orchestra_complete` engine tool to short-circuit when nothing's new.

### Scheduler trigger contract — `P:\Github\Orchestra\src\Orchestra.Engine\Triggers\SchedulerTriggerConfig.cs:7-25`

```csharp
public class SchedulerTriggerConfig : TriggerConfig
{
    public string? Cron { get; init; }            // cron expression (preferred if set)
    public int? IntervalSeconds { get; init; }    // simple polling interval
    public int? MaxRuns { get; init; }            // null = unlimited
}
```

### Canonical example — `P:\Github\Orchestra\examples\icm-auto-acknowledge.yaml`

This is exactly the user's PR-comment-tracker pattern (polls IcM every 15 seconds, exits early if no unacknowledged incidents). Key shape:

```yaml
# scheduler trigger every 15s (lines 208-211)
trigger:
  type: scheduler
  enabled: true
  intervalSeconds: 15

steps:
  # (1) FETCH
  - name: fetch-active-incidents
    type: Command
    command: dnx
    arguments: [Icm.Cli, --, incidents, --acknowledged, "no", --states, ACTIVE, --severity, "3"]

  # (2) GATE — uses orchestra_complete engine tool to short-circuit
  - name: check-incidents
    type: Prompt
    dependsOn: [fetch-active-incidents]
    systemPrompt: >
      You have access to the orchestra_complete tool. If there are NO unacknowledged
      incidents, call orchestra_complete with status 'success' and reason. Otherwise
      output 'Proceeding with acknowledgment'.
    userPrompt: |
      Review: {{fetch-active-incidents.output}}
      If ALL acknowledged (or empty), call orchestra_complete. Otherwise output 'Proceeding'.

  # (3) ACT
  - name: acknowledge-incidents
    type: Prompt
    dependsOn: [check-incidents]
    mcps: [icm]
    # ...uses MCP tools to acknowledge

  # (4) FAN-OUT can be done either via subsequent steps, or by using
  #     `type: Orchestration` steps to call other orchestrations.
```

The gate primitive is the engine tool **`orchestra_complete`** — when called from a Prompt step, the engine terminates the run as Succeeded immediately, skipping all downstream steps. (Search hits in `EngineTools/` will show this; also see the `orchestra_set_status` tool referenced in the YAML.)

### Other scheduler-polling examples

- `examples/code-review-azure-devops.json` — every 600s, scans Azure DevOps PRs assigned to a reviewer; uses Transform step to filter.
- `examples/m365-todo-digest.json` — every 300s.
- `examples/weather-roads-seattle.json` — every 3600s.
- `examples/icm-acknowledge.json` — sibling JSON form, every 15s.

### Email-trigger polling

A different mechanism: `EmailTrigger` polls a mailbox with `pollIntervalSeconds` (see `playground/Hosting/Orchestra.Playground.Copilot/orchestrations/email-trigger-example.json:8`). Not the same trigger family as scheduler.

---

## 4. Orchestration Invocation API — In-Process

There **is** a first-class in-process invocation API: **`IChildOrchestrationLauncher`**.

### The contract

`P:\Github\Orchestra\src\Orchestra.Host\Services\ChildOrchestrationLauncher.cs:35-91` (class) and `ServiceCollectionExtensions.cs:188`:

```csharp
services.AddSingleton<IChildOrchestrationLauncher, ChildOrchestrationLauncher>();
```

Public method (line 93):
```csharp
Task<ChildOrchestrationHandle> LaunchAsync(
    ChildLaunchRequest request,
    CancellationToken cancellationToken = default);
```

`ChildLaunchRequest` (resolved from usages in the file): `OrchestrationId`, `Parameters`, `Mode` (`Sync` / `Async`), `TimeoutSeconds`, `TriggeredBy`, `ParentContext`, `UserMetadata`, `Reporter`, `TriggerId`, `OrchestrationPath` (override registry lookup), `PreExecutionParameterTransform`.

`ChildOrchestrationHandle` (line 301-309): `ExecutionId`, `OrchestrationId`, `OrchestrationName`, `Reporter`, `StartedAt`, `Completion` (a `Task<ChildOrchestrationResult>`).

### Three known callers — all in-process, all unified through the launcher

`ChildOrchestrationLauncher.cs:17-24` comment block:

> This class encapsulates the responsibilities that previously lived (duplicated) in
> `DataPlaneTools.InvokeOrchestration`, `TriggerManager.ExecuteOrchestrationCoreAsync`,
> and the manual SSE `/api/orchestrations/{id}/run` endpoint.

So an in-process service registered into the host's DI can simply:

```csharp
public class MyService(IChildOrchestrationLauncher launcher) {
    public async Task FireSomething() {
        var handle = await launcher.LaunchAsync(new ChildLaunchRequest {
            OrchestrationId = "my-orch",
            Parameters = new() { ["foo"] = "bar" },
            Mode = ChildLaunchMode.Async,
            TriggeredBy = "my-service",
        });
        // handle.ExecutionId, await handle.Completion for the result
    }
}
```

No CLI / no HTTP loopback needed.

### The `type: Orchestration` step (declarative in-process invocation)

`P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Steps\OrchestrationInvocationStep.cs:22-56`:

```yaml
- name: review-pr
  type: Orchestration
  orchestration: pr-code-reviewer       # supports {{template}} for dynamic dispatch
  parameters:                            # values support templates
    prData: "{{fetch-pr-metadata.output}}"
  mode: sync                             # sync (default) | async
  inputHandlerPrompt: "..."              # optional LLM param shaping
  timeoutSeconds: 14400
```

Executor: `P:\Github\Orchestra\src\Orchestra.Engine\Orchestration\Executor\OrchestrationStepExecutor.cs:13-117`. It just calls `_launcher.LaunchAsync(...)`.

### The MCP tool `invoke_orchestration`

`P:\Github\Orchestra\src\Orchestra.Host\McpServer\DataPlaneTools.cs:87-244` — exposed to LLM prompt steps and external MCP clients. Also delegates to the same `IChildOrchestrationLauncher`.

---

## Architectural Cheat-Sheet for Your Plan

If you want an **in-process plugin** (call it a "service" or "extension") that can both:
- Run alongside the host on startup, AND
- Trigger orchestrations,

…the existing `orchestra.services.json` is **not the right hook point** — that's for external OS processes. The clean DI-native paths are:

1. **Register your component as a hosted service** in the same DI container Orchestra builds. Inject `IChildOrchestrationLauncher`. Run a `BackgroundService` or subscribe to events.
2. **Define a webhook trigger** on each orchestration you want to address; call it via in-process HTTP or directly through the launcher with `TriggerId` set.
3. **For "poll-then-dispatch-if-new"**, the existing pattern is a scheduler-triggered orchestration with a gate step using `orchestra_complete`. You don't need new infrastructure for this — every example listed under §3 already does it.

Key file:line anchors:

| Concept | File | Lines |
|---|---|---|
| Service contract (base) | `src/Orchestra.ProcessHost/Models/ServiceEntry.cs` | 7-33 |
| Service config schema | `schemas/orchestra.services.schema.json` | whole file |
| Service example | `examples/orchestra.services.json` | 1-55 |
| Service loader | `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs` | 387-422 |
| ServiceManager lifecycle | `src/Orchestra.ProcessHost/ServiceManager.cs` | 51-110, 422-471 |
| HookDefinition contract | `src/Orchestra.Engine/Orchestration/Hooks/HookDefinition.cs` | 7-146 |
| HookRuntime executor | `src/Orchestra.Engine/Orchestration/Hooks/HookRuntime.cs` | 28-100 |
| Hook YAML example | `examples/hooks-step-failure.yaml` | 1-34 |
| Webhook trigger config | `src/Orchestra.Engine/Triggers/WebhookTriggerConfig.cs` | 7-52 |
| Webhook receiver | `src/Orchestra.Host/Api/WebhooksApi.cs` | 22-120 |
| Trigger-id (URL) generation | `src/Orchestra.Host/Registry/OrchestrationRegistry.cs` | 427-441 |
| Webhook example | `examples/webhook-triggered-notification.json` | 34-39 |
| Scheduler config | `src/Orchestra.Engine/Triggers/SchedulerTriggerConfig.cs` | 7-25 |
| Poll-then-gate pattern | `examples/icm-auto-acknowledge.yaml` | 40-60, 208-211 |
| `IChildOrchestrationLauncher` | `src/Orchestra.Host/Services/ChildOrchestrationLauncher.cs` | 35-314 |
| `type: Orchestration` step | `src/Orchestra.Engine/Orchestration/Steps/OrchestrationInvocationStep.cs` | 22-56 |
| Step executor (launcher caller) | `src/Orchestra.Engine/Orchestration/Executor/OrchestrationStepExecutor.cs` | 40-117 |
| MCP `invoke_orchestration` | `src/Orchestra.Host/McpServer/DataPlaneTools.cs` | 87-244 |
| Async-dispatch example | `examples/orchestration-step-async-dispatch.yaml` | 35-90 |
| Host wiring (DI) | `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs` | 31-278 |
</task_result>
```

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 954.9s)

_Thinking:_

 I'm working through whether this approach is viable and how it compares to the current setup. The architecture does support it—`IChildOrchestrationLauncher` already exists as an in-process API, though the existing `orchestra.services.json` is designed for OS processes rather than in-process plugins. The tradeoff is real: while the current scheduler pattern has drawbacks like cluttering the orchestration list, switching approaches involves its own considerations.

The core tension is around extensibility. Hard-coding watcher logic per check type defeats the whole point of a JSON-based system, so I'm thinking through whether to go with compiled C# plugins for maximum flexibility or a more generic polling watcher service that's configured via JSON with pluggable data sources.

I could use embedded scripts like PowerShell to handle the polling and data fetching, with the service just managing scheduling and hook firing. Or lean into webhook-based sources that genuinely receive push events instead of polling. The hybrid approach appeals to me most — a generic watcher host configured in JSON that reuses the Command step semantics to run a process, fetch data, compare against the last state, and fire the hook only when something actually changes.

I'm going to look at the existing playground and see if there are already in-process services I can build on, though I think I have enough context now to sketch out a solid plan. The key insight is that `orchestra.services.json` is designed for OS processes, but what we really need is an in-process plugin model — either a new extension pattern or extending the OS-process services to support an "Orchestra service" type that maps to a .NET class implementing `IOrchestraService`.

The verdict is clear: this is very viable since `IChildOrchestrationLauncher` already exists as a clean in-process API, and the current scheduler-orchestration pattern is both spammy and inefficient. I'm thinking a two-layer approach makes sense — in-process watcher plugins that do their own polling with internal state and only invoke orchestrations when there's genuinely new data, paired with generic config-driven watchers to avoid the "code per check" problem. For truly novel protocols, plugins would still be available, but most checks could be added via JSON config plus a small script similar to the Command step.

On the design side, I need to decide between direct `IChildOrchestrationLauncher` calls versus HTTP webhooks versus queue-based dispatch for hook invocation. I'm leaning toward a unified abstraction that publishes to an internal event bus with a default sink that calls `LaunchAsync`, optionally also firing HTTP for parity with external subscribers. The watcher config would specify the target orchestration, or orchestrations could declare a trigger that the watcher publishes events to. For extensibility, I'd have a generic "schedule plus command plus dedup plus dispatch" watcher for config-only cases, pluggable source types via DI, and per-source state persistence so we don't re-fire on restart.

Before I draft the full plan, I need to ask some focused questions about key tradeoffs.

Plan mode — analysis only, no edits. I delegated deep exploration and have the ground truth. Here's the situation and a proposed design, plus a few decisions I need from you.

# Findings from the codebase

A few facts that matter for this plan, with anchors:

- The existing `orchestra.services.json` (`src/Orchestra.ProcessHost/Models/ServiceEntry.cs:7-33`) is **OS-process** management — long-running processes + `beforeStart`/`afterStop` shell hooks. It has **no API back into Orchestra**, so it's not the right hook point for what you want.
- There IS already a clean in-process invocation API: `IChildOrchestrationLauncher.LaunchAsync(ChildLaunchRequest, ...)` (`src/Orchestra.Host/Services/ChildOrchestrationLauncher.cs:35-314`), unified across the three callers (`type: Orchestration` step, MCP `invoke_orchestration`, SSE `/api/orchestrations/{id}/run`). A new in-process component can call this directly — no CLI spawn, no HTTP loopback.
- Webhook URLs are stable: `POST /api/webhooks/{triggerId}` where `triggerId = "<sanitized-name>-<sha256(path)[:4]>"` (`src/Orchestra.Host/Registry/OrchestrationRegistry.cs:427-441`). Stable across restarts as long as orchestration name + file path don't change.
- The poll-then-gate pattern (e.g. `examples/icm-auto-acknowledge.yaml`) is the source of the pain you're describing: scheduler trigger every N seconds → fetch step → Prompt step that calls `orchestra_complete` to short-circuit when nothing changed. Each cycle is a real orchestration execution in the history.

# Diagnosis: is the current pattern actually a problem?

Yes, and you've named the costs correctly. Three real ones:

1. **Noise in the orchestration list/history** — every 15s poll is an execution record, even when 99% are no-ops.
2. **Per-poll cost** — even when the gate exits early, the engine spins up an execution, the fetch step runs, and a Prompt step invokes the LLM to decide "is there new data?". The LLM call alone can be more expensive than the fetch.
3. **State is awkward to keep** — "have I already seen this PR comment id?" lives in the data source or is re-derived every poll. There's no first-class "last seen" cursor.

What the current pattern does well, and that we should preserve:

- **Composability** — when something new happens, dispatch is just `type: Orchestration` to whatever handler you want.
- **All-in-config** — adding a new check is a JSON/YAML file, not a recompile.
- **Visibility** — the actual work is still a normal orchestration with full UI/history.

# Recommendation: yes, viable, and it's a real improvement — but the design has to preserve "config-only" extensibility for most cases

The right model is **in-process watchers** that own the polling/listening loop, keep their own cursor/state, and invoke orchestrations only when something new is observed. Conceptually three things:

```
┌──────────────────────────────────────────────────────────────────┐
│  Orchestra.Host (single process)                                 │
│                                                                  │
│   ┌─────────────────┐   ┌──────────────────┐   ┌───────────────┐ │
│   │ Watcher A       │   │ Watcher B        │   │ Watcher C     │ │
│   │ (schedule+cmd)  │   │ (schedule+cmd)   │   │ (custom .NET) │ │
│   │ cursor + dedup  │   │ cursor + dedup   │   │ push or poll  │ │
│   └────────┬────────┘   └────────┬─────────┘   └───────┬───────┘ │
│            │                     │                     │         │
│            └──────────────┬──────┴─────────────────────┘         │
│                           ▼                                      │
│                  ┌────────────────────┐                          │
│                  │  Event Dispatcher  │  filters, fan-out        │
│                  └─────────┬──────────┘                          │
│                            ▼                                     │
│      ┌─────────────────────┴────────────────────┐                │
│      ▼                                          ▼                │
│  IChildOrchestrationLauncher              POST /api/webhooks/…   │
│  (in-process, fast path)                  (external parity path) │
└──────────────────────────────────────────────────────────────────┘
```

Key design decisions, with my recommended answer for each:

### 1. In-process vs out-of-process

**In-process, hosted in `Orchestra.Host`.** That's the whole performance win — no separate CLI, no orchestration record per poll, share DI, log to the same sinks. The existing `orchestra.services.json` keeps its current meaning (OS processes); we add a parallel concept.

### 2. Hook invocation vs queue

**Direct invocation via `IChildOrchestrationLauncher` as the primary path; the existing webhook endpoint as a secondary path** (for parity if someone wants to subscribe externally to the same event). No queue is needed initially — the launcher already supports `Async` mode and returns a handle; the engine already serializes concurrency per orchestration via `MaxConcurrent`. If you later need cross-process durability, a queue is an additive change.

Why not a queue first: queues add an ops surface (storage, dead letters, replay) that the current execution model doesn't have. Skip it until something forces it.

### 3. How a watcher knows which orchestration to call

Two viable models. I recommend **(a)** for simplicity, with **(b)** as an opt-in for richer fan-out:

- **(a) Watcher config names the target** (orchestration id or name + parameter mapping).
- **(b) Watchers publish typed events to an in-process bus; orchestrations declare a new `trigger: { type: event, source: "github-pr-comments" }` and the host wires subscriptions.** This is the "config-only fan-out" superpower — adding a second handler is a new orchestration file, no watcher change.

(b) is genuinely valuable for PR comments etc., because you'll often want multiple orchestrations to react. I'd build (a) first and add (b) as a follow-up if needed, or build (b) directly if you're comfortable with one more abstraction.

### 4. Stable hook URL

Already solved by the existing `triggerId` derivation. Watchers should reference orchestrations by id (or by name+path so the id is derived) — same stability guarantees as webhooks today.

### 5. The hard one — adding new types of checks without writing code

This is the tension you flagged, and it's real. My proposal is a **tiered model**:

| Tier | What you write | Use cases |
|---|---|---|
| **Tier 0 — generic poll watcher (config only)** | A JSON entry with: schedule, fetch command, JSON path / regex for "new items", dedup key, target orchestration. | 80% of polling cases that today look like icm-auto-acknowledge: fetch from CLI, compare to last cursor, fire for each new item. |
| **Tier 1 — scripted watcher (config + small script)** | Same config, but the "fetch" is a PowerShell/dotnet-script file that returns events as JSON lines on stdout. State (cursor) handed in on stdin, returned on stdout. | Anything where the data shape needs munging or the source needs a multi-step query. Mirrors how `type: Script` works today. |
| **Tier 2 — typed watcher plugin (.NET code)** | Implement a small `IWatcherSource` and register it. | Push sources (SSE, SignalR, webhooks from external systems), or sources with bespoke auth/state that don't fit a CLI. |

Most cases the user is talking about (PR comments, IcM incidents, todos) are Tier 0 or Tier 1. So you get "JSON/YAML-only" extensibility for the common case, while still having an escape hatch.

### 6. State / dedup

A watcher must persist a small per-source state file (e.g. `.orchestra/watchers/<watcher-id>.state.json`) with a cursor or seen-id set. Without this, a host restart re-fires for every existing item. Storage layer is trivial; spec it once and reuse for all tiers.

### 7. Concurrency, failure, observability

- **Concurrency:** each watcher runs on its own timer; dispatch through `IChildOrchestrationLauncher` honors the target orchestration's `MaxConcurrent`. Backpressure is "drop or queue" — recommend "queue in-memory with a configurable max, then drop with a warning log."
- **Failure policy:** mirror `HookFailurePolicy` (`Warn` / `Fail` / `Ignore`). A watcher that errors on fetch should not crash the host.
- **Observability:** every dispatch produces an execution record (good — that's where the work lives). The polling itself should emit structured logs but **not** orchestration history records (this is the whole point). Add metrics: poll count, dispatch count, dedup-hit count, last-success timestamp per watcher.

### 8. Sample config (Tier 0/1) — strawman

```jsonc
// orchestra.watchers.json
{
  "$schema": "../schemas/orchestra.watchers.schema.json",
  "watchers": [
    {
      "name": "icm-active-incidents",
      "type": "poll-command",
      "schedule": { "intervalSeconds": 15 },
      "fetch": {
        "command": "dnx",
        "arguments": ["Icm.Cli", "--", "incidents", "--acknowledged", "no",
                      "--states", "ACTIVE", "--severity", "3", "--json"]
      },
      "items": { "jsonPath": "$.incidents[*]" },
      "dedup": { "key": "{{item.id}}", "store": "file" },
      "dispatch": {
        "orchestration": "icm-acknowledge-one",
        "mode": "async",
        "parameters": { "incidentId": "{{item.id}}", "severity": "{{item.severity}}" }
      },
      "failurePolicy": "warn"
    },
    {
      "name": "github-pr-comments",
      "type": "poll-script",
      "schedule": { "cron": "*/30 * * * * *" },
      "fetch": { "shell": "pwsh", "scriptFile": "./watchers/fetch-pr-comments.ps1" },
      "dedup": { "key": "{{item.commentId}}" },
      "dispatch": [
        { "orchestration": "pr-comment-triage", "mode": "async",
          "parameters": { "comment": "{{item}}" } }
      ]
    }
  ]
}
```

The shape mirrors the existing services/hook conventions so it's familiar.

### 9. Comparison to current state

| Dimension | Today (scheduler orchestrations) | Proposed (watchers) |
|---|---|---|
| History noise | 1 execution per poll, even no-ops | 1 execution per real event |
| Per-poll cost | Engine + LLM gate per poll | Plain HTTP/CLI fetch, no LLM |
| State / dedup | Re-derived from source each time, or hacked into source | First-class cursor/dedup |
| Add new check | New YAML file | New JSON entry (Tier 0/1) |
| Add exotic source | Awkward — push-style doesn't fit | Tier 2 plugin |
| Fan-out to multiple handlers | One orchestration calls many via `type: Orchestration` | Same, or event-bus model |
| Failure isolation | Polled orchestration shows failed in history | Logged + metric, no fake-failure executions |

# Things to decide before I write the implementation plan

I want explicit answers on these so I don't over- or under-build:

**Tool: question**

**Input:**
```json
{
  "questions": [
    {
      "header": "Naming / config scope",
      "question": "Should this live in a new `orchestra.watchers.json` (separate file, separate concept from OS-process services), or should it extend `orchestra.services.json` with a new `type: \"watcher\"` discriminator alongside `process` and `command`?",
      "options": [
        {
          "label": "New `orchestra.watchers.json` (Recommended)",
          "description": "Keeps OS-process services and in-process watchers conceptually distinct; cleaner schemas; easier to evolve independently."
        },
        {
          "label": "Extend `orchestra.services.json`",
          "description": "Single file, all 'background things' in one place; reuses existing loader pipeline; risks blurring two very different lifecycles."
        }
      ]
    },
    {
      "header": "Dispatch mechanism",
      "question": "How should watchers fire orchestrations? This is the 'hook vs queue' question.",
      "options": [
        {
          "label": "Direct in-process call to IChildOrchestrationLauncher (Recommended)",
          "description": "Lowest latency, no extra surface, watcher gets back a handle. Webhook endpoint remains available for external subscribers."
        },
        {
          "label": "Always go through the webhook endpoint (POST /api/webhooks/{id})",
          "description": "One uniform path, easier to reason about; pays HTTP cost in-process; loses the rich handle the launcher returns."
        },
        {
          "label": "In-memory queue + worker pool",
          "description": "Decouples poll from dispatch, supports backpressure and retries up front; adds complexity and a queue to operate."
        }
      ]
    },
    {
      "header": "Routing model",
      "question": "How does a watcher know which orchestration(s) to invoke?",
      "options": [
        {
          "label": "Watcher config names the target orchestration(s) directly (Recommended for v1)",
          "description": "Simplest, matches today's mental model. Fan-out done by listing multiple targets in `dispatch`."
        },
        {
          "label": "Event bus: watchers publish typed events; orchestrations declare `trigger: { type: event, source: ... }`",
          "description": "Best for many-to-many fan-out and decoupling, but adds a new trigger type and a routing layer."
        },
        {
          "label": "Both: support direct dispatch AND a future event-bus subscription",
          "description": "Build direct now; add event bus when a concrete need shows up."
        }
      ]
    },
    {
      "header": "Extensibility tiers to ship",
      "question": "Which tiers should be in scope for the first cut?",
      "options": [
        {
          "label": "Tier 0 (poll-command) + Tier 1 (poll-script) (Recommended)",
          "description": "Covers ~all current scheduler-orchestrations with no new code. Tier 2 plugins added when a push source actually shows up."
        },
        {
          "label": "Tier 0 only",
          "description": "Smallest surface area; forces awkward shell pipelines for anything non-trivial."
        },
        {
          "label": "All three tiers (including .NET IWatcherSource plugin contract)",
          "description": "Maximum flexibility from day one; more code and a plugin loading story to design."
        }
      ]
    },
    {
      "header": "State / dedup persistence",
      "question": "Where should watcher cursor/dedup state live?",
      "options": [
        {
          "label": "File under `.orchestra/watchers/<id>.state.json` (Recommended)",
          "description": "Matches existing `.orchestra.pids.json` style; zero new dependencies; easy to inspect and reset."
        },
        {
          "label": "Pluggable store (file by default, can swap for SQLite / Redis)",
          "description": "Future-proofs for high-volume or multi-host setups; more abstraction up front."
        },
        {
          "label": "No persistence — keep cursor only in-memory",
          "description": "Simplest; re-fires for all 'currently new' items on every host restart, which is probably wrong."
        }
      ]
    },
    {
      "header": "Replace icm-auto-acknowledge?",
      "question": "Should this work include migrating one existing scheduler-style example (e.g. icm-auto-acknowledge) to the new watcher model as the canonical example, or leave existing examples untouched?",
      "options": [
        {
          "label": "Migrate one example as the reference (Recommended)",
          "description": "Forces the design through a real workload, gives docs a concrete before/after, validates ergonomics."
        },
        {
          "label": "Leave existing examples; add a new minimal example",
          "description": "Lower-risk; doesn't change anything users are using today."
        },
        {
          "label": "Migrate all scheduler-style examples",
          "description": "Strongest signal of the new pattern; biggest blast radius if the design needs to change."
        }
      ]
    }
  ]
}
```

**Output:**
```
User has answered your questions: "Should this live in a new `orchestra.watchers.json` (separate file, separate concept from OS-process services), or should it extend `orchestra.services.json` with a new `type: "watcher"` discriminator alongside `process` and `command`?"="New `orchestra.watchers.json` (Recommended)", "How should watchers fire orchestrations? This is the 'hook vs queue' question."="Direct in-process call to IChildOrchestrationLauncher (Recommended)", "How does a watcher know which orchestration(s) to invoke?"="Both. implement both. Not in the future.", "Which tiers should be in scope for the first cut?"="Tier 0 (poll-command) + Tier 1 (poll-script) (Recommended)", "Where should watcher cursor/dedup state live?"="File under `.orchestra/watchers/<id>.state.json` (Recommended)", "Should this work include migrating one existing scheduler-style example (e.g. icm-auto-acknowledge) to the new watcher model as the canonical example, or leave existing examples untouched?"="Leave existing examples; add a new minimal example". You can now continue with the user's answers in mind.
```

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 118.9s)

_Thinking:_

 The user has confirmed the approach: a separate `orchestra.watchers.json` config file, direct in-process calls to the launcher, both routing models shipped together, Tier 0 and Tier 1 support, file-based state storage under `.orchestra/watchers/`, and a new minimal example alongside the existing one. Now I'm thinking through how the event bus integrates with the web layer.

I'm settling on a new `Orchestra.Watchers` project to keep this isolated. The config will be discovered like `orchestra.services.json`, with a skip option for tests. I'll define a `WatcherEntry` base class with name, schedule, dispatch, dedup, and failure policy, then create subclasses for command and script polling. Schedules support both interval and cron expressions. For fetching, Tier 0 uses direct command execution with environment variables for state, while Tier 1 runs shell scripts with the same state-passing mechanism.

Now I'm refining the contract: watchers receive previous state as JSON on stdin and output a single JSON object containing both items and updated state on stdout—much cleaner than streaming JSON lines. Items are extracted via a jsonPath template, and dedup keys are computed from each item to track what's already been seen. For dispatch, I'm considering two approaches: direct orchestration launching for each new item, or publishing to an event bus where orchestrations can subscribe via a new event trigger type.

The event bus approach is elegant—every watcher publishes by default, and `dispatch` becomes syntactic sugar for a built-in subscriber. I'll add a small history to the event bus for diagnostics without complicating replay logic. The new `event` trigger type registers alongside scheduler and webhook triggers, and when a matching event fires, it invokes the orchestration through the same launcher. For the watcher lifecycle, I'm building a `WatcherManager` hosted service that loads config, maintains state files, and runs a loop per watcher: it ticks on schedule, reads prior state, invokes the fetch command, parses output into items and state, deduplicates against the window, dispatches new items, persists state atomically, and handles failures with backoff.

Now I'm defining the state file structure to track the watcher name, version, last success timestamp, an opaque cursor for pagination, and a ring buffer of seen keys with timestamps. I'll wire this into the DI container by loading the watchers config, registering the manager as a hosted service, and adding the event trigger type to the trigger registry. For testing, I need unit tests covering state persistence, deduplication, template rendering, and scheduling, plus integration tests for the full fetch-dispatch flow, restart durability, and event-to-orchestration paths. I'll also create a minimal example with a simple folder-watch scenario and start on the documentation.

A few key design decisions to nail down: whether webhook and event triggers should share infrastructure or stay separate in config, how to handle backpressure if a watcher emits thousands of items (serial dispatch with configurable concurrency), reusing the scheduler's cron parser, injecting a time provider for testability, keeping each watcher independent with single-threaded loops, and making the dedup window size configurable with sensible defaults like 10k entries or 7 days.

Locked. Here's the full plan with your answers baked in.

# Implementation Plan: In-Process Watchers

## Architecture summary

A new in-process subsystem in `Orchestra.Host` (with its own implementation project, mirroring how `Orchestra.ProcessHost` separates concerns) that runs polling watchers in-process and fires orchestrations only on real new events. Two output paths run in parallel:

1. **Direct dispatch** — watcher config names one or more target orchestrations; watcher invokes `IChildOrchestrationLauncher.LaunchAsync` per new item.
2. **Event bus** — every watcher also publishes events to a named channel; a new `trigger: { type: event, source: "..." }` lets any orchestration subscribe declaratively. Same underlying invocation path; just a different way to wire it up.

State (cursor + dedup window) persists to `.orchestra/watchers/<watcher-id>.state.json`. No queue, no HTTP loopback, no per-poll orchestration record.

## File layout

```
src/
  Orchestra.Watchers/                ← new project, mirrors Orchestra.ProcessHost
    Models/
      WatcherEntry.cs                ← abstract base, JsonPolymorphic on "type"
      PollCommandWatcher.cs          ← type: "poll-command"
      PollScriptWatcher.cs           ← type: "poll-script"
      WatcherSchedule.cs             ← intervalSeconds | cron
      WatcherDispatch.cs             ← direct dispatch config
      WatcherPublish.cs              ← event-bus publish config
      WatcherDedup.cs                ← key template, window size/ttl
      WatcherState.cs                ← state file shape
      WatcherFailurePolicy.cs        ← Warn | Fail | Ignore
    Events/
      IWatcherEventBus.cs            ← Publish, Subscribe
      InMemoryWatcherEventBus.cs
      WatcherEvent.cs                ← source, watcherName, item, timestamp, dedupKey
    State/
      IWatcherStateStore.cs
      FileWatcherStateStore.cs       ← atomic write, JSON, .orchestra/watchers/
    Runtime/
      WatcherManager.cs              ← IHostedService — owns lifecycles
      WatcherLoop.cs                 ← per-watcher loop (tick, fetch, dedup, dispatch)
      IWatcherFetcher.cs             ← internal abstraction
      CommandWatcherFetcher.cs       ← runs a process, parses output
      ScriptWatcherFetcher.cs        ← runs a shell/script, parses output
      WatcherTemplateRenderer.cs     ← {{item.x}} substitution for dedup key + parameters
    Config/
      WatcherConfigLoader.cs         ← parallels OrchestraConfigLoader
    Exceptions/
      WatcherInitializationException.cs

src/Orchestra.Engine/Triggers/
  EventTriggerConfig.cs              ← new trigger type
  (TriggerManager.cs)                ← wire FireEventTriggerAsync

src/Orchestra.Host/
  Extensions/ServiceCollectionExtensions.cs   ← register WatcherManager, event bus, event trigger handler

schemas/
  orchestra.watchers.schema.json

examples/
  orchestra.watchers.json            ← minimal example
  watcher-folder-poll/                ← self-contained example dir
    watchers.json
    handle-new-file.yaml
    fetch.ps1

tests/
  Orchestra.Watchers.Tests/          ← unit tests
  Orchestra.E2E/...                  ← end-to-end coverage
```

## Contracts

### `orchestra.watchers.json` shape

```jsonc
{
  "$schema": "../schemas/orchestra.watchers.schema.json",
  "watchers": [
    {
      "name": "github-pr-comments",
      "type": "poll-script",                          // or "poll-command"
      "schedule": { "intervalSeconds": 30 },          // or { "cron": "*/30 * * * * *" }
      "fetch": {
        "shell": "pwsh",                              // poll-script
        "scriptFile": "./watchers/fetch-pr-comments.ps1",
        "arguments": [],
        "workingDirectory": null,
        "env": {},
        "timeoutSeconds": 60,
        "includeStdErr": false
      },
      "items": { "jsonPath": "$.items[*]" },          // optional; if omitted, fetcher output IS the item list
      "dedup": {
        "key": "{{item.commentId}}",
        "window": { "maxEntries": 10000, "ttlDays": 14 }
      },
      "dispatch": [                                   // direct path (optional)
        {
          "orchestration": "pr-comment-triage",
          "mode": "async",                            // or "sync"
          "parameters": { "comment": "{{item}}" },
          "timeoutSeconds": null,
          "maxConcurrent": 4
        }
      ],
      "publish": { "source": "github.pr-comments" },  // event-bus path (optional)
      "failurePolicy": "warn",                        // warn | fail | ignore
      "enabled": true
    }
  ]
}
```

Either `dispatch` or `publish` (or both) must be present. `dispatch` is an array so a watcher can fan out to multiple targets without going through the event bus.

### Fetcher I/O contract (both tiers)

- **stdin (JSON):** previous state document — `{ "cursor": <opaque>, "seenKeys": [...] }`.
- **stdout (JSON, one document):**
  ```json
  { "items": [ {...}, {...} ], "cursor": {...} }
  ```
  - `items`: array of opaque JSON objects.
  - `cursor`: optional. If omitted, cursor is unchanged.
- **exit code:** non-zero is failure (respecting `failurePolicy`).
- **timeout:** kill on timeout, treat as failure.

Tier 0 (`poll-command`) uses exactly the same contract; the difference is purely how the process is launched (no shell wrapper).

### `WatcherEntry` (abstract base)

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PollCommandWatcher), "poll-command")]
[JsonDerivedType(typeof(PollScriptWatcher),  "poll-script")]
public abstract class WatcherEntry
{
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public required WatcherSchedule Schedule { get; init; }
    public WatcherItemsConfig? Items { get; init; }
    public WatcherDedup? Dedup { get; init; }
    public List<WatcherDispatch> Dispatch { get; init; } = [];
    public WatcherPublish? Publish { get; init; }
    public WatcherFailurePolicy FailurePolicy { get; init; } = WatcherFailurePolicy.Warn;
}
```

### `IWatcherEventBus`

```csharp
public interface IWatcherEventBus
{
    void Publish(WatcherEvent evt);
    IDisposable Subscribe(string source, Func<WatcherEvent, CancellationToken, Task> handler);
}
```

In-memory implementation. Subscribers are invoked sequentially per event (configurable per subscription if needed later). The new `EventTriggerConfig` registration becomes a subscriber that delegates to `IChildOrchestrationLauncher`.

### `EventTriggerConfig`

```csharp
public class EventTriggerConfig : TriggerConfig
{
    public required string Source { get; init; }   // matches WatcherPublish.Source
    public int MaxConcurrent { get; init; } = 1;
    public string? ParameterTemplate { get; init; } // optional Handlebars-style remap, default = pass item through
}
```

Orchestration usage:

```yaml
trigger:
  type: event
  enabled: true
  source: github.pr-comments
  maxConcurrent: 4
```

`TriggerManager` gets a new `RegisterEventTrigger` path that subscribes to the bus on registration, unsubscribes on tear-down, and calls `IChildOrchestrationLauncher.LaunchAsync` on each event (mirroring the webhook path).

### State file (`.orchestra/watchers/<watcher-id>.state.json`)

```json
{
  "watcher": "github-pr-comments",
  "version": 1,
  "lastTickAt": "...",
  "lastSuccessAt": "...",
  "lastFailureAt": null,
  "consecutiveFailures": 0,
  "cursor": { "...": "opaque, fetcher-defined" },
  "seenKeys": [
    { "key": "comment-12345", "seenAt": "2026-05-14T08:00:00Z" }
  ]
}
```

Atomic writes via temp + rename. `seenKeys` trimmed by `maxEntries` (FIFO) and `ttlDays`.

## Lifecycle and loop

`WatcherManager : IHostedService`:

- `StartAsync`:
  1. Load and validate `orchestra.watchers.json` (skip if Testing env or `skip-watchers` config flag, mirroring `skip-services`).
  2. Resolve target orchestration ids for direct dispatch entries; warn on unresolved (do not fail host startup; surface via metrics/logs).
  3. Wire event-bus subscriptions for all orchestrations whose registration includes an `EventTriggerConfig`.
  4. Spawn one `WatcherLoop` per enabled watcher.
- `StopAsync`: cancel all loops, await graceful drain, flush state.

`WatcherLoop` (per watcher):

```
loop:
  wait for next tick (interval or cron)
  state = stateStore.Read(name)
  result = fetcher.RunAsync(state)              // command/script with timeout
  if failure: applyFailurePolicy(); continue
  items = result.items (or jsonPath-extracted)
  newItems = items.Where(i => !state.seenKeys.Contains(dedupKey(i)))
  foreach newItem in newItems (bounded parallelism per dispatch.maxConcurrent):
    foreach dispatch entry: launcher.LaunchAsync(...)
    if publish: bus.Publish(WatcherEvent(...))
    state.seenKeys.Add(...)
  state.cursor = result.cursor ?? state.cursor
  state.lastSuccessAt = now
  trim seenKeys (maxEntries, ttl)
  stateStore.Write(state)                       // atomic
```

Per-watcher loop is single-threaded internally; dispatching across multiple new items is gated by `dispatch.maxConcurrent` to keep cumulative load bounded.

## Wiring into `Orchestra.Host`

In `ServiceCollectionExtensions`:

- Add `services.AddSingleton<IWatcherEventBus, InMemoryWatcherEventBus>()`.
- Add `services.AddSingleton<IWatcherStateStore, FileWatcherStateStore>()`.
- Add `services.AddSingleton<WatcherManager>()` + `services.AddHostedService(sp => sp.GetRequiredService<WatcherManager>())`.
- During `InitializeOrchestraHostAsync`, after services initialize, load watcher config and hand it to `WatcherManager`.
- In trigger registration code path (`ServiceCollectionExtensions.cs:474-501` area), add handling for `EventTriggerConfig`: register with `TriggerManager`, which subscribes to the bus.
- Add a `skip-watchers` config knob parallel to `skip-services`.

## Logging and metrics

Use `ILogger` with code-generated structured logging (per repo rules):

```csharp
[LoggerMessage(EventId=5101, Level=LogLevel.Debug,
  Message="Watcher {WatcherName} tick: {ItemCount} items, {NewItemCount} new")]
static partial void LogWatcherTick(ILogger logger, string watcherName, int itemCount, int newItemCount);
```

Metric counters per watcher (exposed via existing telemetry plumbing): `ticks`, `fetch_failures`, `items_seen`, `items_new`, `dispatches`, `dedup_hits`, `consecutive_failures`.

## Failure policy

- `warn`: log error, increment counter, continue loop.
- `ignore`: log debug, continue loop.
- `fail`: stop only this watcher loop, mark unhealthy; host stays up.

A watcher with `consecutiveFailures` over a configurable threshold transitions to a back-off schedule (exponential, cap at 5 minutes), regardless of policy, to avoid hammering a broken source.

## Tests (mandatory per repo rules)

Unit tests (`tests/Orchestra.Watchers.Tests`):

- `FileWatcherStateStoreTests` — atomic write, schema upgrade path, trimming by maxEntries and ttl.
- `WatcherTemplateRendererTests` — `{{item.x}}`, `{{item}}` whole-object pass-through, missing-path behavior.
- `CommandWatcherFetcherTests` — exit codes, timeouts, stdin/stdout JSON roundtrip.
- `ScriptWatcherFetcherTests` — pwsh script roundtrip with state pass-through.
- `WatcherLoopTests` — dedup logic, cursor preservation, failure policy paths, fake `TimeProvider` for tick control.
- `InMemoryWatcherEventBusTests` — multi-subscriber dispatch ordering, unsubscribe.
- `EventTriggerConfigTests` — registration path subscribes/unsubscribes correctly.

Integration tests:

- Watcher with a small in-test command fetcher → asserts `IChildOrchestrationLauncher.LaunchAsync` called once per new item, not on second tick.
- Restart preserves state: stop manager, restart, verify no re-dispatch.
- Event-bus path: orchestration with `trigger: { type: event, source: ... }` is invoked when watcher publishes.
- Direct + publish at the same time: both paths fire exactly once each.
- `failurePolicy: fail` halts only the offending watcher; others keep running.

E2E:

- Add to `tests/Orchestra.E2E`: a full host instance with `orchestra.watchers.json` pointing at a script that emits a single new item, asserts the target orchestration completes and produces expected output. Mirror the style of existing webhook E2E tests.

## Example to add

A self-contained, no-external-dependency example so the docs/playground users can run it as-is:

```
examples/watcher-folder-poll/
  orchestra.watchers.json    ← single watcher, poll-script
  fetch.ps1                  ← lists files in a folder; emits new ones; updates cursor
  handle-new-file.yaml       ← orchestration with type:event trigger, prints/processes file
```

This demonstrates both routing models in one example: `dispatch` direct to one orchestration, and `publish` to an event source consumed by a second orchestration — to make the dual model concrete.

## Things I am explicitly NOT building in this pass

- Queue / durable backpressure (revisit if a real source needs it).
- Tier 2 .NET `IWatcherSource` plugin contract (internal `IWatcherFetcher` is shaped so this can be added without breaking changes).
- Cross-host / distributed dedup (file store is local-only by design).
- Webhook trigger ↔ event trigger unification (kept as separate trigger types; they share the launcher path internally but differ on the wire).

## Risks and open questions to flag during implementation

- **Cron parsing**: must reuse whatever `SchedulerTriggerConfig` uses today so behavior matches. Will check that file in implementation phase.
- **Templating engine**: prefer reusing whatever template renderer the engine uses for step parameter templates (e.g., `{{fetch.output}}`); falling back to a small internal one only if the engine renderer can't be cleanly used outside an orchestration context.
- **Item identity when `items.jsonPath` omitted**: define explicitly. Recommendation: if `items` is omitted, treat the entire output `items` array as items; if `dedup.key` is omitted, dedup on `JSON.stringify(item)` hash.
- **Trigger registration for `event` triggers** on orchestrations loaded after the watcher manager starts: ensure registration is idempotent and dynamic, since orchestrations may be re-registered on file changes (if hot-reload exists in this codebase — needs a quick check during implementation).
- **`mode: sync` direct dispatch**: should the watcher loop block on completion? Recommendation: even for `sync` dispatch from a watcher, do not block the watcher tick on it — issue async and don't tick again until handle resolves OR next interval, whichever comes first. Will lock this down in implementation.

## Order of execution when implementing

1. New project skeleton + models + JSON deserialization + schema.
2. State store + tests.
3. Fetchers (command, script) + tests.
4. WatcherLoop + WatcherManager + tests.
5. EventBus + EventTriggerConfig + TriggerManager wiring + tests.
6. `ServiceCollectionExtensions` wiring + `skip-watchers` config flag.
7. Example (`examples/watcher-folder-poll`).
8. E2E test.
9. Run full build + test suite; ensure no `nul` files, no CRLF artifacts per repo rules.

Ready to execute on your go-ahead. If anything in the plan should change — particularly the fetcher I/O contract or the dual dispatch+publish shape — flag it now, since those are the load-bearing surfaces.

---

## User

I'm thinking that this creates a separation between the "recipe" orchestration and the trigger/scheduler. There is no connected between the two. If we remove all orchestrations that need a specific watcher, that watcher still runs but no one listens. Also vice versa.

I'm wondering how can we connect the two? maybe we can have another type of trigger or not trigger buy "watcher" (or other name) metadata in orchestration files leading to a watcher. Only if there is one orchestratrion active that watcher will be active. 
What do you think? Technically the watcher can be anything that returns a json in a speicifc formta/schema, right? It can be a PowerShell script, python script, exe or even .NET single-file app (.NET 10 featuer, no csproj required), or even a .NET tool.

Seems similar to MCPs, how each orchestration also configure an MCP that is invoked with the orchestration.

What do you think?

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 1307.1s)

_Thinking:_

 The user is raising a solid architectural concern about the coupling between watchers and orchestrations—if one side of the pair disappears, the other becomes orphaned and wastes resources. The real issue is that lifecycle management needs to be aware of dependencies: a watcher should only run if something actually consumes its events, and orchestrations shouldn't subscribe to events that won't arrive. I'm also noting that the watcher fetcher itself is just a generic JSON contract, so it could be implemented in various ways like PowerShell.

This connects to a bigger insight: MCPs are already configured per-orchestration, so why not apply the same pattern to watchers? That's worth exploring.

I'm weighing three main approaches. Option A would have each orchestration declare its watchers inline, letting the host compute the active set by deduplicating identical watcher signatures—this keeps everything self-contained and tightly coupled to lifecycle, but risks configuration drift if two orchestrations try to share a fetch. Option B mirrors the existing `globalMcps` pattern: a shared `orchestra.watchers.json` file defines reusable watchers by name, and orchestrations reference them—this gives you DRY configuration and tight coupling, but you're back to managing two files and keeping names in sync. Option C is a hybrid approach that I'm leaning toward.

The real question is whether the watcher itself should be the trigger. If an orchestration says `trigger: { type: watcher, ... }`, then the watcher becomes the trigger mechanism directly—no separate event bus needed. Multiple orchestrations triggered by the same watcher would either deduplicate inline definitions (awkward) or reference a shared library definition (clean). I think the cleanest model is to treat the trigger as a watcher reference, where the watcher can be defined inline or pulled from a library, and every watcher-to-orchestration relationship becomes a symmetric subscription.

For executable types, the fetcher contract is language-agnostic—it just needs stdin/stdout JSON. I can support both `poll-command` for direct executable launches (covering exe, .NET tools, single-file apps, Python scripts) and `poll-script` for shell-based execution, which covers everything the user needs.

On activation semantics, if multiple orchestrations reference the same watcher, I run one instance and dispatch each item to all subscribers, with dedup happening at the watcher level. Inline watchers should run independently rather than trying to deduplicate across orchestrations—simpler and more predictable. The design consolidates into a single orchestration file with optional library references, where `orchestra.watchers.json` holds reusable watcher definitions.

Schedulers stay separate from watchers since they're about cadence, not data novelty, and webhooks remain distinct as externally-driven triggers. When the registry changes, the watcher activation set re-evaluates automatically using the existing TriggerManager hooks, giving us hot-reload support. The main tradeoff is that this unified model loses some explicit separation between concerns, but gains simplicity and consistency. Defining the trigger schema...

Now I'm settling on the core design: watchers are reusable sources that produce items on a schedule, and orchestrations subscribe to them by reference or inline definition. The key question is where parameter mapping lives—I think it belongs in the trigger config since each orchestration might transform the same watcher's output differently. I'm also weighing whether to start watchers eagerly at host init or lazily when first subscribed, and leaning toward eager for simplicity. One more thing to clarify with the user: should inline watchers support dedup, or should that be a library-only feature to keep things simple?

You're identifying a real flaw, and the MCP analogy is exactly the right reframe. Let me work through it.

# The decoupling problem you're naming

In the v1 design, watchers and orchestrations are bound by string matching (`publish.source` ↔ `trigger.event.source`) but their lifecycles are independent:

- Watcher with no subscriber → runs forever, burning CPU, polluting state files, hammering external services for nothing.
- Orchestration subscribed to a non-existent source → silent failure, no events ever arrive, the orchestration looks broken but never logs anything wrong.
- No invariant that links the two; misalignment is silent.

That's a worse failure mode than the current scheduler-orchestration pattern, where at least everything is self-contained in one file. We shouldn't ship that.

# The MCP analogy is precise

You're right that this is the same shape as MCPs (`src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs:257` area shows `mcps` + `globalMcps`). MCPs solve the exact same problem:

- An MCP server is an external process with its own lifecycle.
- Orchestrations declare which MCPs they need (`mcps: [...]`).
- The host starts only the MCPs that at least one active orchestration references.
- Definitions can be local-inline or referenced from a global library.

Watchers map onto this 1:1. The fix is to stop treating watchers as independent top-level entities and treat them as **resources that orchestrations subscribe to**, with the host materializing exactly the set that's actually needed.

# Revised design: watcher is the trigger

Collapse the previous "watcher publishes / event trigger subscribes" two-step into a single `trigger: { type: watcher }` concept. The orchestration's trigger IS the subscription. The host derives the active watcher set from the union of all enabled orchestrations' triggers.

## Two ways to declare a watcher (mirroring MCPs)

**Library** — `orchestra.watchers.json` defines reusable named watchers:

```jsonc
{
  "watchers": {
    "github-pr-comments": {
      "type": "poll-script",
      "schedule": { "intervalSeconds": 30 },
      "fetch": { "shell": "pwsh", "scriptFile": "./watchers/fetch-pr-comments.ps1" },
      "dedup": { "key": "{{item.commentId}}" }
    },
    "icm-active-incidents": {
      "type": "poll-command",
      "schedule": { "intervalSeconds": 15 },
      "fetch": { "command": "dnx", "arguments": ["Icm.Cli", "--", "incidents", "--json"] },
      "dedup": { "key": "{{item.id}}" }
    }
  }
}
```

Orchestrations reference by name:

```yaml
trigger:
  type: watcher
  watcher: github-pr-comments
  enabled: true
  maxConcurrent: 4
  parameters:                      # how an item maps to orchestration parameters
    comment: "{{item}}"
  inputHandlerPrompt: "..."        # optional LLM shaping, same as webhook
```

**Inline** — watcher lives in the orchestration file, for one-off cases:

```yaml
trigger:
  type: watcher
  enabled: true
  maxConcurrent: 1
  parameters: { comment: "{{item}}" }
  definition:
    type: poll-script
    schedule: { intervalSeconds: 30 }
    fetch: { shell: pwsh, scriptFile: ./fetch.ps1 }
    dedup: { key: "{{item.id}}" }
```

## Lifecycle invariants

- **Activation:** a watcher runs iff at least one enabled orchestration's trigger references it. Reference count drops to zero → watcher stops, state file is preserved but loop is torn down.
- **Reference to a missing library watcher:** orchestration registration fails with a clear, named error — same behavior as referencing a missing MCP. Treats misalignment as a startup-time invariant violation, not a silent runtime hole.
- **Inline watchers** are private to their orchestration. They don't dedup across orchestrations; if you want sharing, promote to the library. Simple, no surprising "your two inline definitions got merged because they happened to hash-match" semantics.
- **Library watchers with multiple subscribers:** one watcher loop, one state file, one dedup window; each item is dispatched to all subscribed orchestrations (subject to each orchestration's `maxConcurrent`).

## The event bus survives, but as an implementation detail

Internally `WatcherManager` still uses a pub/sub bus to fan out items to multiple subscribers efficiently. But it's no longer a user-facing API. Users only see "trigger type: watcher." This is the same way the trigger manager today hides the webhook signal plumbing behind `trigger: { type: webhook }`.

## On "what can a watcher be"

You're correct — the fetcher contract is just `stdin: prevState JSON, stdout: { items, cursor } JSON, exit code`. Anything that can satisfy that contract works. The two existing tiers cover everything you listed without modification:

| Runtime | Use which | Example |
|---|---|---|
| PowerShell script | `poll-script` | `shell: pwsh, scriptFile: ./fetch.ps1` |
| Python script | `poll-script` | `shell: python, scriptFile: ./fetch.py` (or `poll-command` with `command: python`) |
| `.exe` | `poll-command` | `command: ./watcher.exe` |
| .NET tool | `poll-command` | `command: my-watcher-tool` (resolved from PATH) |
| .NET 10 single-file (no csproj) | `poll-command` | `command: dotnet, arguments: [run, fetch.cs]` |
| Node script | `poll-script` or `poll-command` | `command: node, arguments: [fetch.js]` |

No new tier needed. The contract is the protocol; the runtime is incidental — exactly like MCP servers.

## What this changes vs the v1 plan

| Aspect | v1 plan | Revised |
|---|---|---|
| `orchestra.watchers.json` | Flat list, each self-activates | Named map, library only — nothing self-activates |
| `event` trigger type | New, separate from watchers | Dropped — `watcher` IS the trigger type |
| `dispatch` array on watcher | Yes | Dropped — dispatch is derived from subscribers |
| `publish.source` on watcher | Yes | Dropped — no naming required, lookup is by watcher identity |
| Coupling | Loose (string match) | Tight (declared reference, validated at load) |
| Dead-watcher problem | Silent CPU waste | Cannot happen |
| Missing-source problem | Silent no-events | Loud registration failure |
| Inline option | n/a | Yes, mirrors MCP `mcps: [{...}]` inline form |
| Sharing | Implicit by source name | Explicit by library reference |

This is a strict improvement; I don't see a trade-off being made. The only thing v1 had that this loses is the ability to have a watcher run "standalone" with no orchestration consumer — which you correctly identified as the wrong default.

## Implementation impact

Smaller than v1, not larger:

- `Orchestra.Watchers` project: same models, minus `WatcherDispatch` and `WatcherPublish`.
- `WatcherTriggerConfig : TriggerConfig` (single new trigger type) replaces `EventTriggerConfig`.
- `WatcherManager` becomes a reference-counted resource owner driven by trigger registrations, analogous to how MCPs are managed today.
- `orchestra.watchers.json` schema becomes a named map, with optional schema validation.
- No internal event-bus public API surface to design or document; it stays internal.

## Open decisions before locking this in

A few things I want explicit calls on:

**Tool: question**

**Input:**
```json
{
  "questions": [
    {
      "header": "Naming",
      "options": [
        {
          "description": "Clear, matches the function. The word is broadly understood; trigger type reads naturally as `type: watcher`.",
          "label": "`watcher` (Recommended)"
        },
        {
          "description": "Emphasizes that it's an upstream producer of items; reads naturally as `type: source`. Slightly more abstract.",
          "label": "`source`"
        },
        {
          "description": "Emphasizes the lifecycle aspect (it 'feeds' the orchestration). Less common term in this domain.",
          "label": "`feed`"
        }
      ],
      "question": "What should the trigger type / file be named?"
    },
    {
      "header": "Inline watchers",
      "options": [
        {
          "description": "Matches MCP precedent (`mcps: [{ \"command\": ... }]` works inline too). Users get full flexibility.",
          "label": "Support both library and inline (Recommended)"
        },
        {
          "description": "Forces all watchers through the library file; simplest mental model; one place to look for every watcher in a repo.",
          "label": "Library only — no inline"
        },
        {
          "description": "Optimizes for self-contained orchestrations; loses library-level sharing.",
          "label": "Inline only — no library"
        }
      ],
      "question": "Should orchestrations be able to declare inline watcher definitions, or only reference library entries?"
    },
    {
      "header": "Inline + library name conflicts",
      "options": [
        {
          "description": "Clearest failure mode; encourages explicit naming choices.",
          "label": "Error on registration if an inline name collides with a library name (Recommended)"
        },
        {
          "description": "Most flexible but harder to reason about — same name could mean different things in different files.",
          "label": "Inline shadows library for that orchestration"
        },
        {
          "description": "Force inline watchers to be anonymous; library entries are the only named ones.",
          "label": "Inline watchers cannot have a name"
        }
      ],
      "question": "If an inline watcher and a library watcher share a name, what should happen?"
    },
    {
      "header": "Lifecycle hot-reload",
      "options": [
        {
          "description": "Mirrors how MCPs/triggers work today; consistent behavior across resource types.",
          "label": "Hot-reload if the registry already supports it for triggers; otherwise startup-only (Recommended)"
        },
        {
          "description": "Simplest, predictable; restart Orchestra to pick up a new orchestration that adds a watcher.",
          "label": "Startup-only, regardless of what other resources do"
        },
        {
          "description": "Most reactive; adds complexity (must reconcile reference counts on each file-system change).",
          "label": "Always hot-reload, even if other resources don't"
        }
      ],
      "question": "When orchestrations are added/removed/changed at runtime, should watcher activations reconcile live, or only at host startup?"
    },
    {
      "header": "Cross-orchestration dedup",
      "options": [
        {
          "description": "Most common expectation when sharing a watcher — \"PR comment 42\" is processed once total, not once per subscriber.",
          "label": "Single dedup window per watcher (Recommended)"
        },
        {
          "description": "Each orchestration gets its own \"have I seen this?\" view — useful if subscribers do different things and shouldn't influence each other.",
          "label": "Per-(watcher, orchestration) dedup window"
        },
        {
          "description": "Most predictable but verbose — every orchestration declares its own dedup key.",
          "label": "Configurable per subscription, default to shared"
        }
      ],
      "question": "When a library watcher has multiple subscribers, should dedup be shared (one item = dispatched once to each subscriber) or per-subscriber (each subscriber has its own \"seen\" set)?"
    },
    {
      "header": "First-cut migration",
      "options": [
        {
          "description": "Restates the v1 answer in light of the revised design; lower risk; lets the new system mature before touching real workflows.",
          "label": "Leave existing scheduler-style examples; add one new minimal watcher example (Recommended)"
        },
        {
          "description": "Validates the design against a real workload; gives docs a concrete before/after.",
          "label": "Migrate icm-auto-acknowledge to the new model as canonical reference"
        },
        {
          "description": "Strongest signal of the new pattern; biggest blast radius if the design needs to change.",
          "label": "Migrate all scheduler-style examples"
        }
      ],
      "question": "Same question as before, restated for the revised design: which examples to migrate?"
    }
  ]
}
```

**Output:**
```
User has answered your questions: "What should the trigger type / file be named?"="`watcher` (Recommended)", "Should orchestrations be able to declare inline watcher definitions, or only reference library entries?"="Support both library and inline (Recommended)", "If an inline watcher and a library watcher share a name, what should happen?"="Error on registration if an inline name collides with a library name (Recommended)", "When orchestrations are added/removed/changed at runtime, should watcher activations reconcile live, or only at host startup?"="Hot-reload if the registry already supports it for triggers; otherwise startup-only (Recommended)", "When a library watcher has multiple subscribers, should dedup be shared (one item = dispatched once to each subscriber) or per-subscriber (each subscriber has its own "seen" set)?"="Per-(watcher, orchestration) dedup window", "Same question as before, restated for the revised design: which examples to migrate?"="Leave existing scheduler-style examples; add one new minimal watcher example (Recommended)". You can now continue with the user's answers in mind.
```

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 104.4s)

_Thinking:_

 The user confirmed most of the implementation choices, but there's one point that diverges from my original recommendation: using a per-(watcher, orchestration) dedup window instead of a global one. I need to think through what this actually means—it sounds like even with a single watcher loop pulling items, each subscription would maintain its own deduplication window based on the watcher and orchestration pair.

When a new subscriber is added, its dedup set starts empty, so it would fire on the first cycle for every visible item. That could be the desired behavior if you want new orchestrations to process the backlog, or it could be wrong if you only want them handling items that arrive after subscription. The alternative would be tracking a subscription start cursor, but that adds complexity.

Per-subscriber dedup is actually more flexible than a shared approach—each subscriber could have its own state file or they could share a composite one. This lets different orchestrations have different dedup strategies: one might dedup on commentId, another might count everything with a high TTL, and a replay orchestration could reset its state to reprocess items. I'm starting to see why this approach makes sense.

For implementation, state keys would be `(watcherName, orchestrationId)` with files at `.orchestra/watchers/<watcherName>/<orchestrationId>.state.json`. The tricky part is the cursor—it comes from the source and represents the last seen point, but if subscribers want different windows they'd need different cursors. However, the fetch happens once per cycle for the whole watcher, so the cursor has to be shared. The resolution is keeping the cursor per-watcher while making dedup per-subscriber, so the fetcher gets one cursor and returns items that each subscriber filters independently.

There's a trade-off though: a shared cursor means late-added subscribers could miss items if the cursor has already moved past them, while per-subscriber cursors would require multiple fetches per cycle and defeat the whole "one loop" benefit. A hybrid approach might work—using the earliest cursor across all subscribers so you fetch everything anyone might need, then let dedup do the filtering. But that could be wasteful for sources where the cursor is critical.

Actually, many APIs don't use temporal cursors at all—they just return "everything currently in state X" like active incidents or open comments. For those, there's no cursor and items are just the current relevant set. Per-subscriber dedup handles that fine. For temporal sources where the cursor matters, I can add a config knob called `catchup` per subscription that defaults to "from now"—when a subscriber is newly added, its seen-set gets pre-populated with everything currently in the watcher's seen-set so it only fires on actually new items going forward. Power users can opt to replay everything by clearing their state or setting `catchup: all`.

The state structure would be a shared watcher state file containing cursor and metadata, with per-subscriber state files tracking their individual seen-keys.

I'm thinking through the hot-reload behavior: when an orchestration's trigger changes, we keep its dedup state keyed by orchestration ID. If the orchestration ID itself changes, state is lost and the watcher effectively resets for that one. When there are zero subscribers the watcher stops, but when a subscriber is added later it resumes from its saved cursor and the subscriber's seen-set is either empty (if new) or restored (if it existed before). If a previously-subscribed orchestration is removed then re-added, its seen-set survives, which is good. And if a watcher is removed entirely, we keep state files unless explicitly cleared—that's restart-friendly.

Locked in. Per-subscriber dedup changes a few details; the rest holds. Here's the final consolidated plan.

# Final Plan: Watcher-as-Trigger

## Conceptual model

- **Watcher** is a resource. It's a named (or inline) definition of "how to fetch items from somewhere."
- **`trigger: { type: watcher }`** is how an orchestration subscribes to one. The trigger IS the subscription.
- **Activation** is reference-counted from triggers: a watcher runs iff ≥1 enabled orchestration's `watcher` trigger references it. Zero subscribers → watcher stops. New subscriber → watcher starts.
- **Library** (`orchestra.watchers.json`) defines reusable named watchers. **Inline** definitions live in the orchestration file. Name collision between inline and library is a registration error.
- **Dedup is per-(watcher, orchestration)**. The watcher fetches once per tick (one source call), but each subscriber maintains its own seen-set.
- **Cursor is shared** (it belongs to the source). New subscribers default to "from now" — the watcher's current seen-keys are copied into the new subscriber's seen-set on first registration so it doesn't fire for the existing backlog.

## File layout

Same as before, with these differences:

```
src/Orchestra.Watchers/
  Models/
    WatcherEntry.cs                ← abstract base
    PollCommandWatcher.cs
    PollScriptWatcher.cs
    WatcherSchedule.cs
    WatcherDedup.cs
    WatcherFailurePolicy.cs
    WatcherState.cs                ← shared (cursor) and subscriber (seenKeys) forms
    WatcherLibrary.cs              ← Dictionary<string, WatcherEntry>
    -- removed: WatcherDispatch, WatcherPublish
  Events/
    -- removed (no longer public)
    InternalWatcherFanout.cs       ← internal-only fan-out within WatcherManager
  State/
    IWatcherStateStore.cs
    FileWatcherStateStore.cs       ← writes <name>.state.json + <name>--<orchId>.state.json
  Runtime/
    WatcherManager.cs              ← IHostedService, ref-counted resource owner
    WatcherLoop.cs
    IWatcherFetcher.cs
    CommandWatcherFetcher.cs
    ScriptWatcherFetcher.cs
    WatcherSubscriptionRegistry.cs ← maps watcherName → set of subscribers
    WatcherTemplateRenderer.cs
  Config/
    WatcherConfigLoader.cs

src/Orchestra.Engine/Triggers/
  WatcherTriggerConfig.cs          ← new (replaces EventTriggerConfig from v1)
```

## Config shapes

### `orchestra.watchers.json` (library, optional)

```jsonc
{
  "$schema": "../schemas/orchestra.watchers.schema.json",
  "watchers": {
    "github-pr-comments": {
      "type": "poll-script",
      "schedule": { "intervalSeconds": 30 },
      "fetch": {
        "shell": "pwsh",
        "scriptFile": "./watchers/fetch-pr-comments.ps1",
        "timeoutSeconds": 60
      },
      "items": { "jsonPath": "$.items[*]" },
      "dedup": { "key": "{{item.commentId}}",
                 "window": { "maxEntries": 10000, "ttlDays": 14 } },
      "failurePolicy": "warn"
    }
  }
}
```

### Watcher trigger in an orchestration

Library reference:

```yaml
trigger:
  type: watcher
  watcher: github-pr-comments
  enabled: true
  maxConcurrent: 4
  parameters:
    comment: "{{item}}"
  inputHandlerPrompt: "..."          # optional LLM shaping
  catchup: from-now                  # from-now (default) | all
```

Inline:

```yaml
trigger:
  type: watcher
  enabled: true
  maxConcurrent: 1
  parameters: { comment: "{{item}}" }
  definition:
    name: pr-comments-local          # required for inline; must NOT collide with library
    type: poll-script
    schedule: { intervalSeconds: 30 }
    fetch: { shell: pwsh, scriptFile: ./fetch.ps1 }
    dedup: { key: "{{item.id}}" }
```

Validation at registration time:
- `watcher` (reference) + `definition` (inline) are mutually exclusive; exactly one is required.
- Inline `definition.name` must not collide with any library watcher name. Collision → registration fails with a clear, actionable error pointing to both definitions.
- Reference to a non-existent library watcher → registration fails the same way.

## Fetcher contract (unchanged)

- **stdin (JSON):** `{ "cursor": <opaque>, "watcher": "<name>", "subscribers": [<orchId>...] }`. Subscribers are informational; cursor is what most fetchers care about.
- **stdout (one JSON document):** `{ "items": [...], "cursor": {...} }`. Cursor optional; absence means "unchanged."
- **exit code:** non-zero → failure per `failurePolicy`.
- **timeout:** kill + failure.

Runtime-agnostic. Works with PowerShell, Python, exes, .NET tools, .NET 10 single-file apps — anything that respects the protocol.

## Subscription, activation, and dedup mechanics

`WatcherManager` (an `IHostedService`) owns:

- A `WatcherSubscriptionRegistry`: `Dictionary<watcherName, HashSet<subscription>>` where `subscription = (orchestrationId, parameters, inputHandlerPrompt, catchup, maxConcurrent)`.
- Per-watcher running loops keyed by name. Loop starts when the first subscriber registers; stops when the last unregisters.

Registration flow (triggered by `TriggerManager` registering/unregistering a `WatcherTriggerConfig`):

1. `RegisterSubscription(watcherName, subscription)`:
   - If watcher not yet known, materialize from library or inline definition.
   - If subscription is new (orchestrationId not seen before for this watcher): load its subscriber state file. If missing AND `catchup == from-now`: copy the current shared seen-keys into the subscriber's seen-set, so it won't fire for the backlog. If `catchup == all`: start with empty seen-set.
   - If no loop is running yet, start one.
2. `UnregisterSubscription(...)`:
   - Remove from the set.
   - Subscriber state file is preserved (so re-subscribing later resumes).
   - If set is empty, stop the loop (shared cursor state file is preserved).

Loop tick:

```
state.shared = sharedStore.Read(name)
result = fetcher.Run(state.shared)              // one fetch per watcher per tick
items = extract(result.items, watcher.items.jsonPath)

for each subscription in subscriptions(name):
  state.sub = subStore.Read(name, subscription.orchestrationId)
  newItems = items.Where(i => dedupKey(i) not in state.sub.seenKeys)
  for each newItem (bounded by subscription.maxConcurrent):
    launcher.LaunchAsync(buildRequest(newItem, subscription))
    state.sub.seenKeys.Add(dedupKey(newItem))
  trim state.sub.seenKeys; subStore.Write(state.sub)   // atomic

state.shared.cursor = result.cursor ?? state.shared.cursor
state.shared.seenKeys = union(state.shared.seenKeys, items.dedupKeys)
trim; sharedStore.Write(state.shared)
```

Shared `seenKeys` exists only to support `catchup: from-now` for late subscribers — it's the "what does the watcher currently know about" set, not used for dispatch filtering.

## State file layout

```
.orchestra/watchers/
  <watcher-name>.state.json                  ← cursor + shared seenKeys + stats
  <watcher-name>--<orchestration-id>.state.json  ← per-subscriber seenKeys
```

Filename uses `--` as separator (filesystem-safe). Orchestration ids already use the sanitized form, so collisions are impossible.

## Hot-reload

Trigger registration today already happens at orchestration registration time (`ServiceCollectionExtensions.cs:474-501` area). The `WatcherManager` plugs into the same hook, so:

- Add an orchestration with a watcher trigger → subscription registered → loop started if first subscriber.
- Remove an orchestration → subscription unregistered → loop stopped if last subscriber.
- Modify an orchestration → unregister old, register new (driven by whatever the existing registry change pipeline does).

If the registry doesn't currently support runtime changes, we get startup-only behavior automatically and pick up dynamic behavior for free when/if it lands. No special-casing required from us.

## DI / host wiring

In `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs`:

- Register `IWatcherStateStore`, `WatcherSubscriptionRegistry`, `WatcherManager` (as both singleton and hosted service).
- In `InitializeOrchestraHostAsync`, after services initialize, load `orchestra.watchers.json` via `WatcherConfigLoader.LoadWatcherLibrary(...)`; hand it to `WatcherManager`.
- In `TriggerManager` registration paths, add `WatcherTriggerConfig` handling: validate (library vs inline, collisions, missing refs), call `WatcherManager.RegisterSubscription`.
- Add `skip-watchers` config flag mirroring `skip-services`.

## `WatcherTriggerConfig`

```csharp
public class WatcherTriggerConfig : TriggerConfig
{
    public string? Watcher { get; init; }                // library reference (mutually exclusive with Definition)
    public WatcherEntry? Definition { get; init; }       // inline (mutually exclusive with Watcher)
    public int MaxConcurrent { get; init; } = 1;
    public Dictionary<string, string>? Parameters { get; init; }
    public WatcherCatchupMode Catchup { get; init; } = WatcherCatchupMode.FromNow;
    // Inherited from base: Enabled, InputHandlerPrompt, InputHandlerModel
}

public enum WatcherCatchupMode { FromNow, All }
```

## Logging and metrics

Code-generated `ILogger` (per repo rules), structured. Per watcher: `ticks`, `fetch_failures`, `items_seen`, `subscribers`, `consecutive_failures`. Per subscription: `items_new`, `dispatches`, `dispatch_failures`.

## Failure policy

Same as v1: `warn` / `ignore` / `fail`. Exponential back-off on consecutive failures regardless of policy, cap at 5 minutes.

## Tests (mandatory per repo rules)

Unit (`tests/Orchestra.Watchers.Tests`):

- `WatcherSubscriptionRegistryTests` — register/unregister, ref-count behavior, parameter snapshot per subscription.
- `FileWatcherStateStoreTests` — shared and per-subscriber state file roundtrip, atomic writes, trimming.
- `WatcherLoopTests` — per-subscriber dedup independence, shared cursor advancement, catchup `from-now` vs `all`, failure policy paths, fake `TimeProvider`.
- `CommandWatcherFetcherTests`, `ScriptWatcherFetcherTests` — stdin/stdout JSON roundtrip, timeouts, exit codes.
- `WatcherTriggerConfigValidationTests` — reference vs inline mutual exclusion, name collision detection, missing library entry detection.
- `WatcherTemplateRendererTests` — `{{item.x}}`, `{{item}}` whole-object.

Integration:

- Two orchestrations subscribe to the same library watcher; one fetch per tick; each gets its own dispatch per new item; removing one keeps the other running.
- Removing all subscribers stops the loop; re-adding restarts it with state preserved.
- `catchup: from-now` skips backlog; `catchup: all` processes it.
- Inline watcher with a name colliding with a library entry fails registration with a clear error.
- Per-subscriber dedup: subscriber A acks an item, subscriber B still sees it as new.

E2E (`tests/Orchestra.E2E`):

- Full host with `orchestra.watchers.json` + an orchestration with a `watcher` trigger; fetch script returns one new item; orchestration completes; second tick with no changes does not re-fire.

## Example to add

`examples/watcher-folder-poll/`:

- `orchestra.watchers.json` defining `local-folder` watcher (poll-script, lists files in a dir).
- `fetch.ps1` reading state from stdin (last-seen file list), emitting new files.
- `handle-new-file.yaml` orchestration with `trigger: { type: watcher, watcher: local-folder, parameters: { path: "{{item.path}}" } }`.
- A second `summarize-new-files.yaml` orchestration also subscribing to the same watcher, demonstrating fan-out + per-subscriber dedup.

Self-contained, no external dependencies, exercises both single and multi-subscriber paths in one runnable example.

## Build/quality checklist (per repo rules)

- No `nul` files left behind.
- No CRLF artifacts in shipped files.
- Server is not left running after work completes.
- Full build + test suite green before declaring done.

## What's explicitly out of scope

- Queue-based decoupling between fetch and dispatch.
- Cross-host / distributed dedup.
- A formal `IWatcherSource` plugin contract (in-process `IWatcherFetcher` is internal-only; .NET tools / single-file apps already cover the "code-based fetcher" use case via the existing fetcher protocol).
- Migrating existing scheduler-style examples.

## Risks I'll watch during implementation

- **Templating engine reuse:** prefer the engine's existing template renderer over a bespoke one. If it's not cleanly callable outside an orchestration context, scope a small renderer and document the divergence.
- **Cron parsing:** reuse whatever `SchedulerTriggerConfig` does today.
- **Hot-reload behavior:** confirm the registry's trigger lifecycle hooks fire on orchestration removal, not just add. If they don't, watcher cleanup-on-removal won't work and we'll need a small addition there — will flag at that point.
- **State file portability:** orchestration IDs are deterministic from name + path. If a watcher subscriber's orchestration moves on disk, its state file's id-suffix changes and the subscriber effectively resets. Document this.

Ready to execute on your approval. Anything still to change?

---

## User

A question I have. When we invoke the watcher/script, it could be a legitimate situations that the watcher want to note "nothing new here, don't call an orchestration".
How is that done using the current implementation?

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 61.5s)

_Thinking:_

 I'm considering two approaches for handling the "nothing new" case in the fetcher response: either return empty items and let the dedup filters handle it, or return all currently-relevant items and rely on per-subscriber dedup to filter out duplicates. Both work within the protocol, so I need to think through the tradeoffs.

The fetcher's job is really to report what's currently at the source, and dedup determines what's actually new to each subscriber. For truly empty sources, return empty items; for sources with items the watcher already knows about, the fetcher can either pre-filter or let dedup handle it. There's also the cursor itself as a signal—if the timestamp hasn't advanced, that implicitly means nothing new.

But there's a separate concern from "nothing new": explicitly skipping a tick due to rate limits, temporary unavailability, or heuristics. I could signal this with a special field like `"skipped": true` rather than conflating it with an empty items response. And I should clarify whether omitting items entirely versus returning an empty array should be treated the same way, with the cursor still updating in both cases.

There's also the multi-subscriber scenario: if a fetcher returns 100 items and subscriber A has seen them all but subscriber B is new, subscriber A gets zero dispatches while subscriber B gets 100—because dedup is per-subscriber, not global. The fetcher doesn't know about individual subscriber state; it just returns what's at the source. The most efficient pattern is cursor-based fetching to limit what comes back, with dedup as a secondary filter. A stateless fetcher that returns everything works too, just less efficiently.

So for "nothing new": I don't need a special signal within the current protocol—just return empty items and the loop fires zero dispatches. The fetcher can also return all current items and let per-subscriber dedup filter. Or it can advance the cursor without items to signal "I checked up to here, nothing happened." For other cases like backoff or retry delays, that's a future extension; the current protocol doesn't need it.

The key insight is that the fetcher reports facts and the watcher loop applies dedup—the fetcher never directly triggers orchestrations. An explicit `"hasChanges": false` flag would be redundant with empty items and doesn't account for per-subscriber dedup anyway. Even if all returned items are already in every subscriber's seen-set, the cursor still updates and shared state gets maintained, same as the empty-items case. The design is correct as-is.

Good question — and the design already handles it cleanly without a special signal. Three ways the case arises, all covered by the existing protocol:

## 1. Truly nothing happened at the source

Fetcher returns:

```json
{ "items": [], "cursor": { "...": "..." } }
```

Loop behavior:
- `items` is empty → no per-subscriber filtering needed.
- Zero dispatches. No orchestration invoked.
- Shared cursor advances (the watcher remembers it checked up to here).
- `lastSuccessAt` updated; success counters incremented.

Exit code stays 0. Failure policy doesn't kick in. No alarms.

This is the canonical "nothing new" path.

## 2. The source has items, but the fetcher already knows they're old

Two valid styles:

**Style A — fetcher is smart, returns nothing:**

```json
{ "items": [], "cursor": { "lastEventId": 4823 } }
```

The fetcher uses its prior cursor to ask the source "only give me items after 4823" and the source returns nothing. Same path as case 1.

**Style B — fetcher is stateless, returns everything currently relevant:**

```json
{ "items": [ {...}, {...}, {...} ], "cursor": null }
```

…and the per-subscriber dedup filters every item out because all their keys are in `seenKeys`. Net result: zero dispatches.

Both work. Style A is more efficient (smaller payload, less work in the loop); Style B is simpler to write (no cursor logic in the fetcher). The protocol supports either.

## 3. Fetcher wants to say "I checked, made progress, but emit nothing"

Same as case 1, but with an updated cursor:

```json
{ "items": [], "cursor": { "lastPolledAt": "2026-05-14T10:00:00Z" } }
```

The cursor advance is the watcher's way of recording "I'm caught up to here," even though no item was worth dispatching.

## Why no explicit `hasChanges: false` flag

Considered and rejected for two reasons:

- **Redundant.** Empty `items` already means "nothing to dispatch." Adding a flag creates two ways to express the same state, and inconsistencies between them (`hasChanges: false` with non-empty items?) would be a footgun.
- **It conflates source-truth with dispatch-truth.** The fetcher's job is to report what's at the source. Whether that becomes a dispatch depends on per-subscriber dedup, which the fetcher doesn't (and shouldn't) know about. Example: "all 50 incidents are already-known" is true for an existing subscriber but false for a newly-registered subscriber with `catchup: all`. A `hasChanges: false` from the fetcher would be wrong for the second subscriber.

## What the fetcher gets to control

Summarizing the fetcher's expressive range in the protocol:

| Fetcher returns | Effect |
|---|---|
| `{ "items": [], "cursor": null }` | No-op tick. No dispatches. Cursor unchanged. |
| `{ "items": [], "cursor": {...} }` | No-op dispatch. Cursor advances. |
| `{ "items": [{...}], "cursor": {...} }` | Per-subscriber dedup decides. |
| Omit `items` key entirely | Same as `items: []`. |
| Omit `cursor` key entirely | Same as `cursor: null` (unchanged). |
| Exit code != 0 | Failure path; `failurePolicy` applies. Used for "I broke," not "I'm idle." |

## A related case worth flagging: "skip this tick, back off"

This is *different* from "nothing new." It's "I hit a rate limit / source is unreachable / please pause briefly." Today the only ways to signal that are:

- Exit 0 with `items: []`, but the next tick fires on schedule anyway (no back-off, may hit the rate limit again).
- Exit non-zero, which triggers back-off — but logs as a failure, which is noisy if it's an expected condition.

The protocol could be extended with an optional field (e.g., `"retryAfterSeconds": 60`) to delay the next tick without marking the cycle as a failure. **I'm leaving this out of v1** — the use case is real but speculative, and we can add the field without breaking the contract later. If you want it in v1, say so and I'll fold it in.

## Bottom line

For the question you asked — "nothing new here, don't call an orchestration" — the answer is: return empty `items` (with or without cursor update), exit 0, and the loop does the right thing. No special signal needed. This is by design: the fetcher reports source state, the loop and per-subscriber dedup decide dispatches.

---

## User

So in the case of the "not smart" watcher, Orchestra saved all the returned key. Where in a file? won't this possibly can be very big if there are new keys each time?

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 888.9s)

_Thinking:_

 The user is raising a valid concern about unbounded growth in the state file—specifically, the `seenKeys` set in both the per-subscriber and shared state files will keep accumulating keys indefinitely if the fetcher always returns all current items. I'm realizing the window-based cleanup mechanism I mentioned is incomplete, and I need to think through how to actually bound this growth without losing the dedup capability that makes the pattern work. I'm considering a few optimizations: storing hashes instead of full keys to shrink the footprint, or using a bloom filter for constant-size storage—though that risks false positives and dropped items. A more practical approach is pruning keys that haven't appeared in recent fetches, keeping the dedup set bounded by the source's active items, but that introduces a subtle bug where transient gaps could cause re-dispatch. A safer version adds a grace period—only prune keys missing for N consecutive ticks or M minutes. Really, the best path forward is to expose these as configurable strategies so users can pick what fits their source's behavior.

For the user's concern about unbounded growth: the current design already bounds it with `maxEntries` and `ttlDays`, most real sources have naturally bounded key spaces, and for genuinely high-cardinality cases they should use a smarter fetcher with cursor support. I can offer presence-based pruning as an option.

Now I need to nail down where state files live—I'm thinking `.orchestra/watchers/<watcher-name>--<orchestration-id>.state.json` relative to the workspace root, though I should verify this assumption since it wasn't explicit earlier. On the compaction side, a 100KB JSON file with 10000 entries is fine for performance, though I should consider the cost of encoding and decoding that data.

I'm going to structure my response around where the file lives, the existing bounded-growth mechanism, honest concerns about high-cardinality sources, mitigation strategies like cursor advancement and hash storage, what I'd recommend for v1, and ask the user to weigh in on whether to include presence-based pruning from the start.

You've found the real cost of the stateless-fetcher pattern. Let me lay out where state lives, what bounds it today, and the honest failure modes.

## Where the file lives

Per the current plan:

```
<orchestra-working-dir>/.orchestra/watchers/
  <watcher-name>.state.json                       ← shared: cursor, shared seenKeys, stats
  <watcher-name>--<orchestration-id>.state.json   ← per-subscriber seenKeys
```

The exact root (workspace root vs host CWD) should match wherever the existing `.orchestra.pids.json` ends up — I want to verify and reuse the same `IFileSystem` / path-resolution helper Orchestra already uses for service state, rather than inventing a parallel convention. That's a 5-minute check during implementation; I'll flag if there's any divergence.

## What bounds growth today

The plan already includes:

```jsonc
"dedup": {
  "key": "{{item.id}}",
  "window": { "maxEntries": 10000, "ttlDays": 14 }
}
```

Both limits apply: trim by entry count (FIFO) and by age. So even a pathological fetcher returning 100% novel keys forever can't blow up the file — it stabilizes at ~`maxEntries` entries.

Rough sizing for `maxEntries: 10000` with a typical key like `"comment-abc123def456"`:

- ~30 bytes/key on disk × 10000 ≈ ~300 KB per per-subscriber file.
- Per watcher: 1 shared file + N subscriber files. Ten subscribers ≈ 3 MB total per watcher.
- I/O cost: re-encoding 10000 strings every tick on a 15s loop = trivial CPU, but real disk churn.

That's bounded. It's not zero.

## But you're right to scrutinize — there are failure modes

**1. Trim shorter than the source's "still-visible" window → duplicate dispatch.**

If a stateless fetcher always returns 20000 currently-active items, and `maxEntries: 10000`, then every tick:
- 20000 items come in.
- 10000 of their keys are already in `seenKeys`, 10000 are not (because they got trimmed last tick).
- 10000 dispatches fire. Many of them are for items already dispatched hours ago.

The bound prevents file explosion but produces *wrong dispatches* — silently. This is the bear trap.

**2. TTL shorter than source visibility → same problem.** Same mechanism, time-based.

**3. Encoding 10000+ keys per tick.** Not catastrophic, but if you're polling every second, it shows up.

**4. Per-subscriber multiplication.** N subscribers × M trimmed keys means N times the chance of the trim-window bug above.

## Mitigation strategies, in order of preference

**A. Use a smart fetcher (advance the cursor).**

The cleanest answer. If the source supports any form of "since X" query — timestamp, sequence number, ETag, `If-Modified-Since` — the fetcher returns only items after `cursor` and the seen-set never grows. Dedup becomes defense-in-depth, not the primary mechanism.

For PR comments, IcM incidents, GitHub events, etc., this is almost always possible.

**B. Presence-based pruning (config-opt-in).**

For sources that return "currently in state X" (active incidents, open PRs, unread emails), unique keys naturally come and go as items leave the active set. Add a dedup mode:

```jsonc
"dedup": {
  "key": "{{item.id}}",
  "mode": "presence",
  "graceTicks": 3      // forget a key only if absent for 3 consecutive ticks
}
```

State stays bounded by `|currently-active items| + |items in grace period|`, which is the source's working set — not unbounded history. The `graceTicks` window protects against transient source flakes that would otherwise cause re-dispatch.

**C. Hash-only storage.**

Store 8-byte truncated SHA-256 of the dedup key instead of the raw string: ~80 KB instead of ~300 KB at `maxEntries: 10000`. Adds a near-zero false-positive risk (collisions in 64 bits over 10000 entries: negligible). Pure footprint optimization; doesn't help with the trim-window correctness bug.

**D. Surface the problem loudly.**

If during a tick we observe items whose dedup key fell out of the window AND was previously dispatched (i.e., we're about to re-dispatch something we've seen before), log a warning. Even better: track "items currently being trimmed because they're at the FIFO tail" and alarm if the window churn rate is high. Tells the operator "your `maxEntries` is too small for your source."

## What I'd ship in v1

A combination:

1. **Default `mode: "fifo"`** — the bounded FIFO + TTL design. Keep it simple.
2. **`mode: "presence"`** as a first-class option — small additional code, big correctness win for the common "list active X" pattern.
3. **No cursor-only / no-dedup mode** — too easy to misuse; if you trust your cursor that much, just leave the seen-set empty and rely on the cursor in practice.
4. **No hash-only storage** — premature optimization at 300 KB/file. Add later if real workloads hit limits.
5. **Loud diagnostics** — warn when trimming evicts keys that have ever been dispatched in this watcher's lifetime; expose `dedup_evictions` and `dedup_re_dispatches` metrics so operators see the problem.
6. **Documentation explicitly says**: "if you don't / can't write a smart fetcher, prefer `mode: presence` over `mode: fifo` whenever the source returns 'currently relevant' items rather than an unbounded event stream."

## One thing this changes in the plan

The earlier draft schema had `dedup.window` as the only knob. I'd revise it to:

```jsonc
"dedup": {
  "key": "{{item.id}}",
  "mode": "fifo",                                      // "fifo" (default) | "presence"
  "fifo":     { "maxEntries": 10000, "ttlDays": 14 }, // used when mode=fifo
  "presence": { "graceTicks": 3 }                      // used when mode=presence
}
```

Small surface, covers the two real-world patterns explicitly.

## Decisions I need from you

**Tool: question**

**Input:**
```json
{
  "questions": [
    {
      "header": "Dedup modes in v1",
      "options": [
        {
          "description": "Ship FIFO as the default and presence as opt-in. Covers both stateless-fetcher patterns (event-stream sources and active-set sources) without forcing users to write smart fetchers.",
          "label": "Both `fifo` and `presence` (Recommended)"
        },
        {
          "description": "Simpler v1; presence-based added later if operators ask. Risk: the trim-window correctness bug bites stateless fetchers on active-set sources.",
          "label": "Only `fifo` (existing plan)"
        },
        {
          "description": "Forces every user to either advance the cursor or accept the FIFO trim risk; presence avoids both.",
          "label": "Only `presence`"
        }
      ],
      "question": "Which dedup mode(s) should v1 support?"
    },
    {
      "header": "Re-dispatch protection",
      "options": [
        {
          "description": "Cheap to implement, catches the silent-corruption case where a trim window is too small. Operators get a clear signal that their config is wrong.",
          "label": "Track lifetime-dispatched count per watcher; warn + metric when an evicted key is re-encountered (Recommended)"
        },
        {
          "description": "Doubles state file size in the worst case (lifetime set never trims), but guarantees no duplicate dispatch ever.",
          "label": "Maintain a separate never-trimmed \"ever dispatched\" set in addition to the FIFO window"
        },
        {
          "description": "Smallest footprint. Operators discover misconfiguration through duplicate orchestration runs.",
          "label": "No protection — trust the configured window"
        }
      ],
      "question": "Should v1 include any protection against duplicate dispatch when the trim window is too small?"
    },
    {
      "header": "Storage format",
      "options": [
        {
          "description": "Predictable, debuggable, atomic write is trivial. Fits well under 1 MB per file for any realistic config.",
          "label": "JSON, full keys (Recommended)"
        },
        {
          "description": "Quarter the size; adds a tiny collision risk. Worth it only if operators report file-size pain in practice.",
          "label": "JSON, 8-byte truncated SHA-256 hashes of keys"
        },
        {
          "description": "Faster I/O for huge state, but no longer human-debuggable, and introduces a new storage primitive to maintain.",
          "label": "Binary append-log format"
        }
      ],
      "question": "How should seen-keys be stored on disk?"
    },
    {
      "header": "Default `maxEntries` / `ttlDays`",
      "options": [
        {
          "description": "Generous enough for most polling sources at typical cadence; bounded file size around 300 KB.",
          "label": "`maxEntries: 10000, ttlDays: 14` (Recommended)"
        },
        {
          "description": "Smaller default protects users from large files; trim-window bugs become more likely without explicit tuning.",
          "label": "`maxEntries: 1000, ttlDays: 7`"
        },
        {
          "description": "Forces every watcher to declare its window; no silent defaults that might be wrong.",
          "label": "Require explicit values; no defaults"
        }
      ],
      "question": "What FIFO defaults should ship?"
    }
  ]
}
```

**Output:**
```
User has answered your questions: "Which dedup mode(s) should v1 support?"="Both `fifo` and `presence` (Recommended)", "Should v1 include any protection against duplicate dispatch when the trim window is too small?"="Track lifetime-dispatched count per watcher; warn + metric when an evicted key is re-encountered (Recommended)", "How should seen-keys be stored on disk?"="JSON, full keys (Recommended)", "What FIFO defaults should ship?"="`maxEntries: 10000, ttlDays: 14` (Recommended)". You can now continue with the user's answers in mind.
```

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 41.5s)

Locked. Here's the consolidated state-management section that supersedes earlier drafts.

## Updated: dedup, state, and storage

### Schema

```jsonc
"dedup": {
  "key": "{{item.id}}",
  "mode": "fifo",                                       // "fifo" (default) | "presence"
  "fifo":     { "maxEntries": 10000, "ttlDays": 14 },   // used when mode=fifo
  "presence": { "graceTicks": 3 }                       // used when mode=presence
}
```

- `mode: "fifo"` — bounded ring of recently-seen keys with both count and age limits. Right for event-stream sources.
- `mode: "presence"` — keys present in the source's current snapshot are kept; keys absent for `graceTicks` consecutive ticks are pruned. Right for active-set sources ("list currently open Xs"). Tolerates transient source flakes.
- Defaults: `maxEntries: 10000`, `ttlDays: 14`, `graceTicks: 3`. Documented; can be overridden per watcher.

### Files

```
<orchestra-working-dir>/.orchestra/watchers/
  <watcher-name>.state.json
  <watcher-name>--<orchestration-id>.state.json
```

- JSON, full string keys (no hashing). Atomic write via temp + rename.
- Path resolution: reuse whatever helper the existing `.orchestra.pids.json` writer uses. Will verify during implementation; flag if there's no shared helper and propose one.

### State file shape (per-subscriber)

```json
{
  "version": 1,
  "watcher": "github-pr-comments",
  "orchestrationId": "pr-comment-triage-a1b2",
  "mode": "fifo",
  "seenKeys": [
    { "key": "comment-12345", "seenAt": "2026-05-14T08:00:00Z" }
  ],
  "stats": {
    "lifetimeDispatched": 4823,
    "evictions": 12,
    "reDispatchesAfterEviction": 0,
    "lastDispatchAt": "2026-05-14T09:42:11Z"
  }
}
```

`stats.lifetimeDispatched` and `stats.reDispatchesAfterEviction` power the re-dispatch protection below. They're cheap counters; we don't keep the full lifetime key set.

### Shared state file shape

```json
{
  "version": 1,
  "watcher": "github-pr-comments",
  "cursor": { "...": "opaque, fetcher-defined" },
  "sharedSeenKeys": [ { "key": "...", "seenAt": "..." } ],
  "stats": {
    "ticks": 12480,
    "fetchFailures": 3,
    "consecutiveFailures": 0,
    "lastTickAt": "...",
    "lastSuccessAt": "..."
  }
}
```

`sharedSeenKeys` exists for `catchup: from-now` (so new subscribers can be pre-populated with what the watcher currently knows). It's subject to the same trimming rules as per-subscriber seen-keys.

### Re-dispatch protection (loud-trim warning)

When a key is evicted by trim, we don't keep the full key, but we increment `evictions`. When we then encounter what looks like a new key for that subscriber, we have no way to know with certainty whether it was ever dispatched — so the protection works at the population level, not per-key:

- **Metric** `dedup_evictions{watcher, orchestration}`: incremented on every FIFO eviction.
- **Metric** `dedup_lifetime_dispatched{watcher, orchestration}`: monotonic counter.
- **Heuristic warning**: if eviction rate × tick rate exceeds a threshold (e.g., > 10% of `maxEntries` evicted per hour), emit a warning log like:
  > Watcher `github-pr-comments` (subscriber `pr-comment-triage-a1b2`): FIFO trim rate is high (1,200 evictions/hour at maxEntries=10,000). The configured window may be smaller than the source's visibility window; consider increasing `maxEntries`, raising `ttlDays`, switching to `mode: presence`, or making the fetcher cursor-aware.

This is the cheapest signal that gets the operator looking at the right knob. We're explicitly *not* maintaining a never-trimmed "ever dispatched" set (you didn't pick that option), so we won't claim to *prevent* duplicate dispatch — only to surface it.

### Logging

Code-generated `ILogger` per repo rules, e.g.:

```csharp
[LoggerMessage(EventId = 5121, Level = LogLevel.Warning,
    Message = "Watcher {WatcherName} subscriber {OrchestrationId}: FIFO trim rate {EvictionsPerHour}/hr at maxEntries={MaxEntries}. Possible duplicate dispatches; consider mode=presence or larger window.")]
static partial void LogHighTrimRate(ILogger logger, string watcherName, string orchestrationId, double evictionsPerHour, int maxEntries);
```

### Tests added on top of the previously listed set

- **FIFO mode**: keys trim by `maxEntries` (FIFO order); keys trim by `ttlDays` (age); both limits enforced independently.
- **Presence mode**: keys absent for `graceTicks` consecutive ticks are pruned; transient absence within grace does not prune; key reappears within grace → no re-dispatch; key reappears after prune → counts as new (correct behavior).
- **High-trim warning**: synthetic loop that forces eviction rate above threshold → warning logger called; below threshold → not called.
- **Stats counters**: `lifetimeDispatched`, `evictions` advance monotonically; survive restart.
- **Schema migration safety**: a state file with `version: 1` is readable; an unknown future version is rejected cleanly.

### Documentation note (for whenever docs are written)

When the docs land, the "writing a watcher" guide will explicitly say:

> Prefer to make your fetcher cursor-aware whenever the source supports a "since X" query. If you can't (or won't), use `mode: presence` for sources that return "currently in state Y" snapshots, and `mode: fifo` only for genuine event-stream sources where items have a natural lifetime smaller than your window. `mode: fifo` with no cursor on a source whose items remain visible longer than your window will cause duplicate dispatches; the trim-rate warning will tell you when this is happening.

That note is the operator's protection against the silent-correctness footgun.

## Status

This is the final state of the plan, incorporating everything from this conversation:

- `watcher` as a trigger type, replacing the v1 `event` trigger.
- Library + inline definitions; collision is a hard error.
- Reference-counted activation: watchers run iff referenced by ≥ 1 enabled orchestration.
- Hot-reload via the existing trigger lifecycle hooks; falls back to startup-only if those don't fire on removal.
- Per-`(watcher, orchestration)` dedup with `fifo` and `presence` modes.
- File-backed state under `.orchestra/watchers/` with atomic writes.
- Trim-rate warning + metrics; no never-trimmed lifetime set.
- New minimal example only; existing scheduler-style orchestrations untouched.
- Direct in-process invocation via `IChildOrchestrationLauncher`; no queue, no HTTP loopback.
- Fetcher protocol is runtime-agnostic (works with pwsh, python, exes, .NET tools, .NET 10 single-file apps).

Nothing else is open. Ready to implement on your go-ahead.

---

## User

How will watcher implementors know the DTO/contract of what they should receive and what they should return?


---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 328.4s)

_Thinking:_

 I'm considering how watcher implementors will learn the protocol contract—what data format they receive and what they need to return. The main approaches are documentation, JSON schemas for validation, or language-specific SDKs that handle the serialization details for them.

Looking at how other systems solve this—MCP, LSP, GitHub Actions, Kubernetes—they all follow the same pattern: a formal specification plus schemas, concrete examples, and optional typed libraries. For Orchestra, I think the best approach is publishing canonical JSON schemas for both input and output as the source of truth, then having Orchestra validate fetcher responses against those schemas and provide clear, actionable error messages when something doesn't match.

The CLI should support a dry-run mode to test a fetcher once without running the full host, plus scaffolding to generate starter files in different languages. I'd include examples for PowerShell, Python, .NET, and documentation showing the spec and a worked example. Optional typed SDKs for PowerShell and .NET would be nice but probably not essential for v1—the schemas and scaffolding alone get most of the way there. The fetcher input should also be self-describing so the same script can handle multiple watchers if needed, and I should keep the scope tight by deferring the SDKs and focusing on what gives the most value upfront.

I'm realizing that JSON Schema descriptions will automatically surface in most IDEs as inline docs and autocomplete, which is a huge win for discoverability without extra work. Before I finalize the approach, I need to ask the user a few key questions: whether typed SDKs should ship in v1 and for which languages, whether the CLI subcommands for testing and scaffolding are priorities, and where the documentation should live relative to the schema itself.

Real ergonomics question — fetcher authors need to discover the protocol without reading source. The fetcher contract has been described in prose so far; for v1 it needs a concrete, machine-checkable surface. Here's how I'd cover it.

## Layered discovery

Five complementary surfaces, each useful in different moments:

### 1. Canonical JSON Schemas (source of truth)

Two schema files alongside the existing `orchestra.watchers.schema.json`:

```
schemas/
  orchestra.watchers.schema.json          ← config (already in plan)
  watcher-fetcher-input.schema.json       ← what the fetcher receives on stdin
  watcher-fetcher-output.schema.json      ← what the fetcher writes to stdout
```

Each with rich `description` annotations on every field. JSON Schema descriptions show up as hover-docs and autocomplete in VS Code, JetBrains IDEs, etc., so writing a fetcher in any text editor with schema mapping configured gives inline guidance for free.

Concrete shapes (recap, now formalized):

`watcher-fetcher-input.schema.json`:

```json
{
  "type": "object",
  "required": ["watcher", "cursor", "subscribers"],
  "properties": {
    "watcher":   { "type": "string", "description": "Name of the watcher invoking this fetcher." },
    "cursor":    { "description": "Opaque cursor returned by the previous fetch, or null on first run." },
    "subscribers": {
      "type": "array",
      "description": "Informational list of orchestration ids currently subscribed.",
      "items": { "type": "string" }
    },
    "config":    { "type": "object", "description": "Static config passed through from the watcher's `fetch.config` block, if any." }
  }
}
```

`watcher-fetcher-output.schema.json`:

```json
{
  "type": "object",
  "properties": {
    "items":  { "type": "array", "description": "Items produced this tick. Empty or omitted means nothing new." },
    "cursor": { "description": "Opaque cursor to remember for next call. Omit to leave unchanged." },
    "diagnostics": {
      "type": "object",
      "description": "Optional structured info for operators (logged, not used for dispatch).",
      "properties": {
        "message": { "type": "string" },
        "details": { "type": "object" }
      }
    }
  }
}
```

The schemas are versioned (`"$id": ".../v1/..."`), so we can evolve without breaking deployed fetchers.

### 2. Runtime validation with actionable errors

`WatcherManager` validates every fetcher's stdout against `watcher-fetcher-output.schema.json` before processing. On validation failure, the log message names the watcher, the schema path that failed, the JSON pointer to the offending value, and the literal value seen:

```
Watcher 'github-pr-comments' returned invalid output:
  /items/3/id: expected string, got number (47)
  Fetcher command: pwsh ./watchers/fetch-pr-comments.ps1
  See schema: schemas/watcher-fetcher-output.schema.json
```

This is the difference between "my script is broken somehow" and "line 3 of my items has the wrong type." It's the highest-leverage piece of the contract experience.

### 3. CLI `orchestra watcher test`

A diagnostic command that runs a configured fetcher once, end-to-end, without starting the host or invoking any orchestration. Output shows:

- Stdin payload sent (so the fetcher author can see exactly what Orchestra hands them).
- Stdout received.
- Schema validation result.
- Items extracted via `items.jsonPath`.
- Per-subscriber dedup decisions (which would dispatch, which would skip and why).
- Cursor advance preview.

Flags:
- `--watcher <name>` — pick a library entry.
- `--from-state <path>` — load a real or synthetic prior state.
- `--empty-state` — start with no cursor / no seen-keys.
- `--save-state <path>` — write the resulting state to a file for the next dry-run.
- `--ticks N` — run N consecutive ticks.

This is the iteration loop fetcher authors will actually use day to day.

### 4. CLI `orchestra watcher new`

Scaffolding for a starter fetcher in any supported runtime:

```
orchestra watcher new my-watcher --shell pwsh
orchestra watcher new my-watcher --shell python
orchestra watcher new my-watcher --runtime dotnet-script   # .NET 10 single-file
orchestra watcher new my-watcher --runtime command          # bare exe / dotnet tool
```

Generated file:
- Reads stdin JSON, parses it.
- Writes a TODO comment showing where the fetch logic goes.
- Emits a valid empty output (so the file is runnable end-to-end on day zero).
- Adds an entry to `orchestra.watchers.json` (or prints the snippet to paste).

### 5. Worked examples under `examples/watchers/`

The `examples/watcher-folder-poll/` example already in the plan; expanded to include sibling examples in different runtimes, each implementing the same trivial source ("list files in a directory") so authors can compare:

```
examples/watchers/folder-poll-pwsh/
examples/watchers/folder-poll-python/
examples/watchers/folder-poll-dotnet-script/
```

Each one is a complete, runnable mini-watcher. Picking a runtime then becomes "copy the example, change the body."

## What I'd ship in v1 vs defer

| Item | v1 | Defer |
|---|---|---|
| JSON Schemas with descriptions | ✓ | |
| Runtime validation + actionable errors | ✓ | |
| `orchestra watcher test` | ✓ | |
| `orchestra watcher new` scaffolding | ✓ | |
| One worked example (existing plan) | ✓ | |
| Multiple-runtime examples | optional | |
| Typed SDK (PowerShell module) | | ✓ |
| Typed SDK (NuGet helper for .NET) | | ✓ |
| Typed SDK (pip package for Python) | | ✓ |
| Hosted spec docs page | optional | |

Strong case for the four checked items: schemas without runtime validation are advisory only; runtime validation without `watcher test` forces a full host run to iterate; `watcher new` cuts the time-to-first-working-fetcher from minutes to seconds. They reinforce each other and are cheap relative to the rest of the project.

SDKs (typed helper libraries) are genuine convenience but they multiply the surface area we have to maintain. The validation + scaffolding combo gives you 80% of the SDK benefit without the multi-language commitment.

## Decisions before locking this in

**Tool: question**

**Input:**
```json
{
  "questions": [
    {
      "header": "JSON Schema delivery",
      "options": [
        {
          "description": "Authoritative, machine-checkable, drives both runtime validation and editor tooling. Discoverable via `$schema` references.",
          "label": "Both input and output schemas, with rich descriptions (Recommended)"
        },
        {
          "description": "Only validate what Orchestra reads (output); fetcher authors learn the input shape from docs and `watcher test --print-input`.",
          "label": "Output schema only"
        },
        {
          "description": "Smallest surface; relies entirely on docs and runtime errors.",
          "label": "No schemas; documentation-only"
        }
      ],
      "question": "How should the fetcher I/O contract be formalized?"
    },
    {
      "header": "Runtime validation strictness",
      "options": [
        {
          "description": "Hard fail — actionable, prevents downstream corruption, matches how malformed orchestration files are treated.",
          "label": "Strict: invalid output is a fetch failure (applies `failurePolicy`) (Recommended)"
        },
        {
          "description": "Operators may rely on the watcher despite minor schema drift; warns but keeps trying. Risk: silently dispatches malformed items downstream.",
          "label": "Lenient: warn but continue, ignoring unknown/invalid fields"
        },
        {
          "description": "Validation only on demand via `watcher test`.",
          "label": "No runtime validation"
        }
      ],
      "question": "How strict should Orchestra be when a fetcher returns output that doesn't match the schema?"
    },
    {
      "header": "CLI tooling in v1",
      "options": [
        {
          "description": "Both pay off immediately and reinforce each other; modest implementation cost.",
          "label": "Ship both `orchestra watcher test` and `orchestra watcher new` (Recommended)"
        },
        {
          "description": "Iterate-and-validate loop is the bigger win; scaffolding can come later or live as example copy-paste.",
          "label": "Only `orchestra watcher test`"
        },
        {
          "description": "Fastest first-time experience; debugging without `watcher test` means running the whole host.",
          "label": "Only `orchestra watcher new`"
        },
        {
          "description": "Neither in v1; rely on examples and runtime errors.",
          "label": "Neither in v1"
        }
      ],
      "question": "Which diagnostic / scaffolding CLI commands should v1 ship?"
    },
    {
      "header": "Example coverage",
      "options": [
        {
          "description": "Demonstrates the contract is truly language-agnostic; small extra work; future-proofs the docs against \"can I use X?\" questions.",
          "label": "PowerShell + Python + .NET single-file example, all implementing the same source"
        },
        {
          "description": "Keep scope tight; one example proves the contract; users in other runtimes adapt from the schema + `watcher test`.",
          "label": "Single example, PowerShell only (existing plan) (Recommended)"
        },
        {
          "description": "Cover every supported runtime including bare exe and .NET tool patterns.",
          "label": "PowerShell + Python + .NET single-file + bare exe + .NET tool"
        }
      ],
      "question": "How many runtime variants of the example should v1 ship?"
    },
    {
      "header": "Typed SDKs",
      "options": [
        {
          "description": "Lets the project ship; SDKs become a follow-up driven by real demand. Schemas + runtime validation cover the iteration loop in the meantime.",
          "label": "None in v1 (Recommended)"
        },
        {
          "description": "PowerShell is the lowest-cost SDK to ship (a module with a `Read-WatcherInput` / `Write-WatcherOutput` pair) and is the runtime the IcM example would use today.",
          "label": "PowerShell helper module only"
        },
        {
          "description": "Maximum convenience for .NET users writing single-file fetchers, at the cost of a published NuGet package to maintain.",
          "label": ".NET helper NuGet only"
        },
        {
          "description": "Most polish, biggest commitment.",
          "label": "Both PowerShell module and .NET NuGet"
        }
      ],
      "question": "Typed SDKs / helper libraries for fetcher authors?"
    },
    {
      "header": "Schema versioning",
      "options": [
        {
          "description": "Future-proofs the protocol; fetchers can adapt; old fetchers keep working.",
          "label": "Schemas are versioned (`/v1/`) and stdin payload includes a `protocolVersion` field (Recommended)"
        },
        {
          "description": "Simpler today, painful when we want to evolve.",
          "label": "Unversioned; breaking changes require coordinated upgrade"
        },
        {
          "description": "Avoids any in-payload version negotiation; relies on Orchestra refusing newer fetchers cleanly.",
          "label": "Schemas versioned but no version field in stdin"
        }
      ],
      "question": "Should the protocol be explicitly versioned from day one?"
    }
  ]
}
```

**Output:**
```
User has answered your questions: "How should the fetcher I/O contract be formalized?"="Both input and output schemas, with rich descriptions (Recommended)", "How strict should Orchestra be when a fetcher returns output that doesn't match the schema?"="Strict: invalid output is a fetch failure (applies `failurePolicy`) (Recommended)", "Which diagnostic / scaffolding CLI commands should v1 ship?"="Only `orchestra watcher test`", "How many runtime variants of the example should v1 ship?"="Two examples, PowerShell and .NET single-file", "Typed SDKs / helper libraries for fetcher authors?"="None in v1 (Recommended)", "Should the protocol be explicitly versioned from day one?"="Schemas are versioned (`/v1/`) and stdin payload includes a `protocolVersion` field (Recommended)". You can now continue with the user's answers in mind.
```

---

## Assistant (Plan · Claude Opus 4.7 (1M context)(Internal only) · 42.7s)

_Thinking:_

 The user went with a custom option combining PowerShell and .NET single-file examples rather than one of my suggested paths, which works well. Now I'm refining the discoverability and contract section to be more concise.

Locked. Consolidating the contract-discovery section.

## Updated: fetcher contract surface

### Schemas

```
schemas/
  orchestra.watchers.schema.json
  watchers/v1/fetcher-input.schema.json
  watchers/v1/fetcher-output.schema.json
```

Both versioned under a `/v1/` path. Each property carries a `description` field; descriptions are written to be useful both in editor hover-docs and in error messages.

### stdin payload

```json
{
  "protocolVersion": "1",
  "watcher":   "github-pr-comments",
  "cursor":    { ... } | null,
  "subscribers": [ "pr-comment-triage-a1b2", "pr-comment-summary-c3d4" ],
  "config":    { ... } | null
}
```

`protocolVersion` is the on-the-wire version negotiation knob. Orchestra advertises the highest `1.x` it speaks; fetchers may inspect this and refuse, downgrade, or upgrade.

### stdout payload

```json
{
  "protocolVersion": "1",
  "items":       [ ... ]    | null,
  "cursor":      { ... }    | null,
  "diagnostics": { "message": "...", "details": { ... } } | null
}
```

If `protocolVersion` is omitted in the response, Orchestra assumes `"1"` (the version it sent).

### Validation policy

- Strict validation of stdout against `watcher-fetcher-output.schema.json/v1`.
- A validation failure is treated as a fetch failure, applying the watcher's `failurePolicy` (`warn` / `ignore` / `fail`) plus the back-off rules.
- Error message includes: watcher name, fetcher command, JSON pointer to the failure, expected vs actual, and a hint at the schema URL.

Example log line:

```
[ERR ] Watcher 'github-pr-comments' fetcher returned invalid output.
       command: pwsh ./watchers/fetch-pr-comments.ps1
       schema:  schemas/watchers/v1/fetcher-output.schema.json
       error:   /items/3/id — expected string, got integer (47)
       failurePolicy: warn (continuing)
```

### Version evolution rules

- New optional fields are additive within `v1`.
- Breaking changes get a new `/v2/` schema directory. Orchestra reads the response's `protocolVersion` and chooses validation accordingly.
- The schema URL (`$id`) is `https://orchestra.invalid/schemas/watchers/v1/fetcher-output.json` (or whatever the chosen canonical base is); fetchers can pin this in `$schema` for editor mapping.

### `orchestra watcher test` (v1)

A read-only diagnostic command. Shape:

```
orchestra watcher test --watcher <name>
                       [--from-state <path> | --empty-state]
                       [--save-state <path>]
                       [--ticks <N>]
                       [--print-input]
```

Output (single-tick, formatted):

```
== orchestra watcher test: github-pr-comments ==

Resolved from: orchestra.watchers.json
Command:       pwsh ./watchers/fetch-pr-comments.ps1
Timeout:       60s

stdin sent:
  { "protocolVersion": "1", "watcher": "github-pr-comments", "cursor": null,
    "subscribers": [], "config": null }

stdout received (215 bytes, exit 0):
  { "protocolVersion": "1", "items": [ ... 7 ... ], "cursor": {...} }

schema validation: OK

items extracted (jsonPath=$.items[*]): 7

simulated subscribers:
  (none — pass --subscribers to simulate dedup)

cursor advance preview:
  before: null
  after:  { "lastEventId": 4823 }
```

With `--subscribers <orchId>[,<orchId>...]` it loads each subscriber's real state file (or starts empty) and reports which items would dispatch.

Importantly: `watcher test` never invokes any orchestration. It's purely an introspection command.

### `orchestra watcher new` — deferred

Not shipped in v1. Scaffolding is one good-quality example away from being a copy-paste exercise, which is acceptable given we'll ship two examples.

### Examples shipped in v1

Two parallel implementations of the same source ("list files in a directory") so authors can compare runtime conventions side-by-side:

```
examples/watchers/folder-poll-pwsh/
  orchestra.watchers.json     ← single watcher entry, type: poll-script
  fetch.ps1                   ← reads stdin, emits valid v1 output
  handle-new-file.yaml        ← orchestration with type: watcher trigger
  README.md                   ← short walkthrough

examples/watchers/folder-poll-dotnet/
  orchestra.watchers.json
  fetch.cs                    ← .NET 10 single-file (no csproj)
  handle-new-file.yaml
  README.md
```

Both:
- Implement the same source (list `*.txt` in a dir).
- Use the same cursor strategy.
- Reference the schema (`$schema` on `orchestra.watchers.json`) so editor IDE picks up autocomplete.
- The .NET version's `fetch.cs` demonstrates the `dotnet run fetch.cs` invocation pattern, making it explicit that no `.csproj` is required.

### Tests added

On top of the previously listed set:

- Schema-driven validation tests: hand-crafted valid and invalid stdout samples assert pass/fail.
- Schema-driven test for stdin payload: round-trip serialize/deserialize, ensures Orchestra's emitted stdin always conforms to the input schema.
- `orchestra watcher test` integration tests: against real fetcher scripts from `examples/watchers/`, asserting the command output structure and exit codes (zero on validation OK, non-zero on validation fail).
- Version negotiation: fetcher returns `protocolVersion: "2"` → Orchestra treats as invalid (no v2 schema yet) and reports a clear "unsupported protocolVersion" error.

### Documentation note

The eventual "writing a watcher" doc will reference:
- The two schema files as the spec.
- The two example folders for runnable reference.
- `orchestra watcher test` for the iteration loop.
- The version-evolution rules so authors know how to write forward-compatible fetchers.

(Doc page itself is out of scope for v1 unless you say otherwise — per repo rules I won't proactively create markdown documentation.)

## Final plan status

Everything has been resolved. To summarize the full scope of v1, in order of execution:

1. New project `Orchestra.Watchers` with models, fetchers, state store, runtime, config loader, event fan-out (internal only).
2. `WatcherTriggerConfig` in `Orchestra.Engine.Triggers` replacing earlier `EventTriggerConfig` idea.
3. `TriggerManager` wiring for watcher triggers (register, hot-reload-aware unregister).
4. DI wiring in `Orchestra.Host`, `skip-watchers` config flag, config loader for `orchestra.watchers.json`.
5. Three schemas: `orchestra.watchers.schema.json`, `watchers/v1/fetcher-input.schema.json`, `watchers/v1/fetcher-output.schema.json`.
6. Runtime validation against output schema, strict, applying `failurePolicy`.
7. Both `fifo` and `presence` dedup modes; loud-trim warning + metrics.
8. State files under `.orchestra/watchers/` with shared and per-subscriber variants, atomic writes.
9. Reference-counted activation with `catchup: from-now | all`.
10. `orchestra watcher test` CLI command for iteration.
11. Two example folders: PowerShell and .NET single-file, both implementing the same folder-poll source.
12. Unit, integration, and E2E tests covering everything above.
13. Code-generated `ILogger` structured logging throughout, per repo rules.
14. Cleanup checks: no `nul` files, no CRLF artifacts, server not left running.

No remaining open questions. Ready to implement on your approval.

---

