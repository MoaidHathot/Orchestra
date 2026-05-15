# In-Process Watchers — Implementation Plan

> This document is the authoritative specification for the **in-process watchers**
> subsystem. It must be implementable end-to-end from this document alone,
> without access to the conversation that produced it. All decisions called out
> here are final; if something is genuinely ambiguous, prefer the option that
> minimizes surface area and matches existing Orchestra patterns.

---

## 1. Motivation and context

Orchestra today has a pattern where polling work (e.g., "fetch new PR comments
every 30s and dispatch handlers") is implemented as a top-level orchestration
with a `scheduler` trigger. That orchestration fetches data, then uses a Prompt
step calling the `orchestra_complete` engine tool to short-circuit when nothing
new is observed. The canonical example of this pattern is
`examples/icm-auto-acknowledge.yaml`.

This pattern works but has three real costs:

1. **History/noise** — every poll is an execution record, even when 99% of
   ticks are no-ops.
2. **Per-poll cost** — each tick spins up a full execution, runs a fetch step,
   and invokes the LLM via a gate step to decide "is there new data?"
3. **State** — "have I seen this id?" is re-derived from the source on every
   tick, or hacked into the source itself. There is no first-class cursor.

The goal of this work is to introduce a new in-process subsystem that:

- Runs polling/listening logic without producing an orchestration execution per
  tick.
- Invokes target orchestrations only when there is genuinely new data.
- Owns its own state (cursor + dedup window).
- Is configured via JSON in the spirit of the existing scheduler/webhook
  triggers, so adding a new poll source does not require new C# code for the
  common case.

The subsystem is called **watchers**. A watcher is a named (or inline) resource
that produces items; an orchestration's `trigger: { type: watcher }` declaration
subscribes to one. Watcher activation is reference-counted from these
subscriptions: a watcher runs iff at least one enabled orchestration's trigger
references it. This mirrors how MCPs are managed today.

---

## 2. Existing codebase context (use these as ground truth)

The implementer should read these locations before starting; the new code must
integrate with them, not parallel them.

| Concept | File | Lines |
|---|---|---|
| Service entry base (OS-process service contract) | `src/Orchestra.ProcessHost/Models/ServiceEntry.cs` | 7-33 |
| Process service subtype | `src/Orchestra.ProcessHost/Models/ProcessService.cs` | 7-38 |
| Service config schema | `schemas/orchestra.services.schema.json` | entire |
| Service config example | `examples/orchestra.services.json` | 1-55 |
| Service config loader | `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs` | 387-417 (look for `InitializeOrchestraHostAsync`) |
| Service manager lifecycle | `src/Orchestra.ProcessHost/ServiceManager.cs` | 12-110, 422-471 |
| `OrchestraConfigLoader` (path resolution + JSON load) | search in `src/Orchestra.Host` and `src/Orchestra.Engine` |  |
| Trigger config base | `src/Orchestra.Engine/Triggers/TriggerConfig.cs` | 7-29 |
| Webhook trigger config | `src/Orchestra.Engine/Triggers/WebhookTriggerConfig.cs` | 7-52 |
| Scheduler trigger config | `src/Orchestra.Engine/Triggers/SchedulerTriggerConfig.cs` | 7-25 |
| Webhook receiver | `src/Orchestra.Host/Api/WebhooksApi.cs` | 22-120 |
| `TriggerManager` (register, fire, lifecycle) | `src/Orchestra.Engine/Triggers/TriggerManager.cs` | look for `RegisterTrigger`, `FireWebhookTriggerAsync` (~line 280) |
| Trigger registration in host startup | `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs` | 474-501 |
| Trigger-id (URL/identity) generation | `src/Orchestra.Host/Registry/OrchestrationRegistry.cs` | 427-441 |
| `IChildOrchestrationLauncher` (in-process invocation API) | `src/Orchestra.Host/Services/ChildOrchestrationLauncher.cs` | 35-314 |
| `type: Orchestration` step (existing in-process call site) | `src/Orchestra.Engine/Orchestration/Steps/OrchestrationInvocationStep.cs` | 22-56 |
| MCP tool `invoke_orchestration` (existing in-process caller) | `src/Orchestra.Host/McpServer/DataPlaneTools.cs` | 87-244 |
| Hook definition + runtime (different concept — do not confuse) | `src/Orchestra.Engine/Orchestration/Hooks/HookDefinition.cs`, `.../Hooks/HookRuntime.cs` |  |
| Existing scheduler-orchestration pattern (the thing being improved on) | `examples/icm-auto-acknowledge.yaml` | 40-60, 208-211 |
| ProcessTracker (existing local-state file convention) | search `ProcessTracker` in `Orchestra.ProcessHost`; relevant for `.orchestra.pids.json` |  |

The implementer must verify path-resolution semantics for `.orchestra/`
state directories by reading how `.orchestra.pids.json` is located by
`ProcessTracker` and reuse the same helper/convention.

### Terms that already exist in the codebase and must not be confused with watchers

- **Service** (`orchestra.services.json`) — long-running OS processes managed by
  the host. Has its own lifecycle. **Not** the same thing as a watcher and must
  not be conflated. Watchers live in a new project; they do not extend
  `ServiceEntry` or `ServiceManager`.
- **HookDefinition / HookRuntime** — per-orchestration event scripts invoked on
  step/orchestration events. Different concept; do not modify.
- **Webhook trigger** — externally-fired trigger over HTTP. Stays as-is. A
  watcher trigger is a sibling of webhook triggers, not a replacement.
- **Scheduler trigger** — fires an orchestration on a cadence regardless of
  data. Stays as-is. A watcher is "scheduler + fetch + dedup + dispatch."

---

## 3. Conceptual model

### Watcher

A named resource defined by:

- A **fetcher** (a command or a script) that reports items from a source.
- A **schedule** (interval or cron).
- A **dedup** strategy.
- A **failure policy**.

A watcher is **not** itself a trigger. It is a resource that triggers reference.

### Watcher trigger

A new trigger type, `type: watcher`, attached to an orchestration. It declares:

- Which watcher to subscribe to (library reference **or** inline definition).
- How to map an item to orchestration parameters.
- Concurrency and catchup behavior.

The trigger **is** the subscription. There is no separate `event` trigger and
no separate `dispatch` config block on the watcher itself.

### Activation

A watcher loop runs iff ≥ 1 enabled orchestration's `watcher` trigger
references it. When the last subscriber goes away, the loop stops. State files
are preserved across activation cycles.

### Dedup scope

Dedup is per-`(watcher, orchestration)`. The watcher fetches once per tick, but
each subscriber maintains its own seen-key set so subscribers are independent
of each other.

### Cursor scope

Cursor is shared across subscribers. It belongs to the source ("where am I in
the source's stream"), not to a subscriber. New subscribers default to
`catchup: from-now`: the watcher's currently-known seen-keys are copied into
the new subscriber's seen-set on first registration so it does not fire for
the existing backlog.

---

## 4. File and project layout

### New project

```
src/Orchestra.Watchers/
  Orchestra.Watchers.csproj
  Models/
    WatcherEntry.cs                  ← abstract base, JsonPolymorphic discriminated on "type"
    PollCommandWatcher.cs            ← "poll-command"
    PollScriptWatcher.cs             ← "poll-script"
    WatcherSchedule.cs               ← intervalSeconds | cron (mutually exclusive)
    WatcherFetchCommandConfig.cs     ← command, arguments, workingDirectory, env, timeoutSeconds, includeStdErr, config
    WatcherFetchScriptConfig.cs      ← shell, scriptFile | script, arguments, workingDirectory, env, timeoutSeconds, includeStdErr, config
    WatcherItemsConfig.cs            ← jsonPath
    WatcherDedup.cs                  ← key, mode (fifo|presence), fifo (maxEntries, ttlDays), presence (graceTicks)
    WatcherFailurePolicy.cs          ← enum: Warn | Ignore | Fail
    WatcherLibrary.cs                ← Dictionary<string, WatcherEntry>
    WatcherCatchupMode.cs            ← enum: FromNow | All
  Events/
    InternalWatcherFanout.cs         ← internal-only fan-out; not a public API
  State/
    IWatcherStateStore.cs
    FileWatcherStateStore.cs         ← shared + per-subscriber files; atomic writes
    SharedWatcherState.cs            ← model
    SubscriberWatcherState.cs        ← model
  Runtime/
    WatcherManager.cs                ← IHostedService; owns lifecycles; reconciles subscriptions
    WatcherLoop.cs                   ← per-watcher loop body
    WatcherSubscriptionRegistry.cs   ← name -> set of WatcherSubscription
    WatcherSubscription.cs           ← OrchestrationId, Parameters, MaxConcurrent, Catchup, InputHandlerPrompt, InputHandlerModel
    IWatcherFetcher.cs               ← internal abstraction over command/script execution
    CommandWatcherFetcher.cs
    ScriptWatcherFetcher.cs
    WatcherTemplateRenderer.cs       ← {{item.path}} and {{item}} substitution
    WatcherOutputValidator.cs        ← JSON-schema-driven validation against fetcher-output schema
  Config/
    WatcherConfigLoader.cs           ← parallels OrchestraConfigLoader for orchestra.watchers.json
  Diagnostics/
    WatcherMetrics.cs                ← counters, exposed via existing telemetry plumbing
    WatcherLogging.cs                ← code-generated LoggerMessage partials
  Exceptions/
    WatcherInitializationException.cs
    WatcherValidationException.cs
```

### Engine additions

```
src/Orchestra.Engine/Triggers/
  WatcherTriggerConfig.cs            ← new trigger type
```

### Host additions / modifications

```
src/Orchestra.Host/
  Extensions/ServiceCollectionExtensions.cs   ← modified: register watcher services, load config, wire trigger
  Cli/Commands/WatcherTestCommand.cs          ← new: `orchestra watcher test` subcommand
```

### Schemas

```
schemas/
  orchestra.watchers.schema.json
  watchers/v1/fetcher-input.schema.json
  watchers/v1/fetcher-output.schema.json
```

### Examples

```
examples/watchers/folder-poll-pwsh/
  orchestra.watchers.json
  fetch.ps1
  handle-new-file.yaml
  README.md
examples/watchers/folder-poll-dotnet/
  orchestra.watchers.json
  fetch.cs
  handle-new-file.yaml
  README.md
```

### Tests

```
tests/Orchestra.Watchers.Tests/        ← new project (unit + integration)
tests/Orchestra.E2E/...                ← extend with at least one watcher E2E test
```

---

## 5. Config shapes

### `orchestra.watchers.json` (library)

This file is optional. When present, it is loaded once at host startup and
hot-reloaded if the existing config-watching infrastructure supports it.

```jsonc
{
  "$schema": "../schemas/orchestra.watchers.schema.json",
  "watchers": {
    "<name>": <WatcherEntry>,
    ...
  }
}
```

`watchers` is a **map** keyed by watcher name, not an array. The key is the
canonical name used in `trigger.watcher` references.

### `WatcherEntry` shapes

Common fields on every entry:

```jsonc
{
  "type": "poll-command" | "poll-script",   // discriminator
  "enabled": true,                           // default true; disabled watchers are not started even if referenced
  "schedule": <WatcherSchedule>,
  "items": <WatcherItemsConfig?>,            // optional; default behavior extracts from output.items
  "dedup": <WatcherDedup?>,                  // optional; if omitted, every item dispatches every tick (rarely correct)
  "failurePolicy": "warn" | "ignore" | "fail" // default "warn"
}
```

`WatcherEntry` is `abstract`; the two concrete subtypes add a `fetch` block.

#### `poll-command`

```jsonc
{
  "type": "poll-command",
  "schedule": { "intervalSeconds": 15 },
  "fetch": {
    "command": "dnx",                        // executable name or absolute path
    "arguments": ["Icm.Cli", "--", "incidents", "--json"],
    "workingDirectory": null,                // optional; defaults to host working dir
    "env": {},                               // optional environment overrides (merged on top of host env)
    "timeoutSeconds": 60,                    // optional; default 60
    "includeStdErr": false,                  // optional; default false (stderr logged but not parsed)
    "config": { }                            // optional; passed verbatim to fetcher stdin as `config`
  },
  "items": { "jsonPath": "$.items[*]" },
  "dedup": { "key": "{{item.id}}", "mode": "fifo" }
}
```

#### `poll-script`

```jsonc
{
  "type": "poll-script",
  "schedule": { "cron": "*/30 * * * * *" },
  "fetch": {
    "shell": "pwsh",                         // shell command (pwsh, bash, python, node, etc.)
    "scriptFile": "./watchers/fetch.ps1",    // mutually exclusive with "script"
    "script": null,                          // inline script body; mutually exclusive with "scriptFile"
    "arguments": [],
    "workingDirectory": null,
    "env": {},
    "timeoutSeconds": 60,
    "includeStdErr": false,
    "config": { }
  },
  "items": { "jsonPath": "$.items[*]" },
  "dedup": {
    "key": "{{item.commentId}}",
    "mode": "presence",
    "presence": { "graceTicks": 3 }
  }
}
```

#### Schedule

```jsonc
// exactly one of intervalSeconds or cron must be set
{ "intervalSeconds": 15 }            // simple polling cadence
{ "cron": "*/30 * * * * *" }         // 6-field cron (matches SchedulerTriggerConfig)
```

Reuse whatever cron parser `SchedulerTriggerConfig` uses today. Do **not**
introduce a second cron implementation.

#### Items

```jsonc
{ "jsonPath": "$.items[*]" }         // default if omitted: treat fetcher output's "items" array as the items list
```

If the watcher's fetcher follows the standard protocol (returning
`{ "items": [...], "cursor": ... }`), `items` can be omitted. `items.jsonPath`
is only needed when the fetcher emits a non-standard shape — uncommon, but
supported.

#### Dedup

```jsonc
{
  "key": "{{item.id}}",              // template; if omitted, defaults to JSON-canonical-form hash of the whole item
  "mode": "fifo",                    // "fifo" (default) or "presence"
  "fifo":     { "maxEntries": 10000, "ttlDays": 14 },  // used when mode=fifo; defaults shown
  "presence": { "graceTicks": 3 }                       // used when mode=presence; default 3
}
```

Dedup modes are described in §10 (State management).

### `WatcherTriggerConfig` in an orchestration

Inherits from `TriggerConfig` (which already provides `Enabled`,
`InputHandlerPrompt`, `InputHandlerModel`).

Library reference form:

```yaml
trigger:
  type: watcher
  watcher: github-pr-comments        # required (mutually exclusive with `definition`)
  enabled: true
  maxConcurrent: 4                   # default 1
  catchup: from-now                  # default; or "all"
  parameters:                        # required; how an item maps to orchestration parameters
    comment: "{{item}}"
  inputHandlerPrompt: "..."          # optional LLM shaping (inherited from TriggerConfig)
```

Inline form:

```yaml
trigger:
  type: watcher
  enabled: true
  maxConcurrent: 1
  catchup: from-now
  parameters: { path: "{{item.path}}" }
  definition:                        # required (mutually exclusive with `watcher`)
    name: pr-comments-local          # required; must NOT collide with any library name
    type: poll-script
    schedule: { intervalSeconds: 30 }
    fetch: { shell: pwsh, scriptFile: ./fetch.ps1 }
    dedup: { key: "{{item.id}}" }
```

Validation:

- Exactly one of `watcher` (reference) or `definition` (inline) is required.
  Specifying both, or neither, is a registration error.
- Inline `definition.name` must not collide with any library watcher name.
  Collision is a registration error with a message naming both definitions.
- A library reference to an unknown name is a registration error.

---

## 6. Fetcher protocol (versioned)

The protocol is versioned. v1 is described in full below.

### stdin payload (Orchestra → fetcher)

Schema: `schemas/watchers/v1/fetcher-input.schema.json`.

```json
{
  "protocolVersion": "1",
  "watcher":   "github-pr-comments",
  "cursor":    null,
  "subscribers": ["pr-comment-triage-a1b2"],
  "config":    null
}
```

| Field | Type | Notes |
|---|---|---|
| `protocolVersion` | string | Always `"1"` for v1. Required. |
| `watcher` | string | Watcher name (library name, or inline `definition.name`). |
| `cursor` | object \| null | Opaque cursor returned by the previous successful fetch, or `null` on first run / after state reset. |
| `subscribers` | string[] | Orchestration ids currently subscribed (informational; fetcher may ignore). |
| `config` | object \| null | Verbatim copy of the watcher's `fetch.config` block, if any. |

### stdout payload (fetcher → Orchestra)

Schema: `schemas/watchers/v1/fetcher-output.schema.json`.

```json
{
  "protocolVersion": "1",
  "items": [
    { "id": "comment-12345", "...": "..." }
  ],
  "cursor": { "lastEventId": 4823 },
  "diagnostics": { "message": "...", "details": { } }
}
```

| Field | Type | Notes |
|---|---|---|
| `protocolVersion` | string | Optional; if omitted, Orchestra assumes the version it sent. |
| `items` | array \| null | Items produced this tick. Empty or omitted means nothing new. |
| `cursor` | object \| null | Cursor to remember for the next call. Omit/null = leave unchanged. |
| `diagnostics` | object \| null | Optional structured info; logged at Info level; not used for dispatch. |

### Exit code semantics

- `0` — fetcher executed successfully. Output is parsed and validated.
- non-zero — fetch failure. `failurePolicy` applies. Stderr is captured and
  logged.

### "Nothing new" patterns (informational; not a protocol field)

A fetcher signals "nothing new" by returning empty `items` (or omitting it).
There is **no** dedicated `hasChanges: false` flag — it would conflict with
per-subscriber dedup semantics.

### Version evolution

- Additive changes within `v1` are non-breaking.
- Breaking changes get `schemas/watchers/v2/`. Orchestra picks validator by the
  response's `protocolVersion` (and a request's `protocolVersion`).
- If a fetcher returns an unknown `protocolVersion`, Orchestra reports a clear
  "unsupported protocolVersion" error and treats the tick as a failure.

---

## 7. JSON Schemas

Three schemas ship in `schemas/`. All carry `description` fields on every
property so editors with JSON Schema mapping provide hover-docs and
autocomplete.

### `schemas/orchestra.watchers.schema.json`

Validates the library config file. Shape:

```jsonc
{
  "$id": "https://orchestra.invalid/schemas/orchestra.watchers.json",
  "type": "object",
  "required": ["watchers"],
  "properties": {
    "$schema": { "type": "string" },
    "watchers": {
      "type": "object",
      "additionalProperties": { "$ref": "#/$defs/WatcherEntry" }
    }
  },
  "$defs": {
    "WatcherEntry": { /* oneOf: PollCommand, PollScript */ },
    "PollCommandWatcher": { /* ... */ },
    "PollScriptWatcher":  { /* ... */ },
    "WatcherSchedule":    { /* ... */ },
    "WatcherItemsConfig": { /* ... */ },
    "WatcherDedup":       { /* ... */ }
  }
}
```

Each `$def` mirrors the structure described in §5.

### `schemas/watchers/v1/fetcher-input.schema.json`

```jsonc
{
  "$id": "https://orchestra.invalid/schemas/watchers/v1/fetcher-input.json",
  "type": "object",
  "required": ["protocolVersion", "watcher"],
  "properties": {
    "protocolVersion": { "const": "1" },
    "watcher": { "type": "string" },
    "cursor":  { "description": "Opaque previous-tick cursor; null on first run." },
    "subscribers": { "type": "array", "items": { "type": "string" } },
    "config":  { "type": ["object", "null"] }
  }
}
```

### `schemas/watchers/v1/fetcher-output.schema.json`

```jsonc
{
  "$id": "https://orchestra.invalid/schemas/watchers/v1/fetcher-output.json",
  "type": "object",
  "properties": {
    "protocolVersion": { "type": "string" },
    "items":  { "type": ["array", "null"] },
    "cursor": { "description": "Opaque cursor to remember; null/omitted = unchanged." },
    "diagnostics": {
      "type": ["object", "null"],
      "properties": {
        "message": { "type": "string" },
        "details": { "type": "object" }
      }
    }
  },
  "additionalProperties": false
}
```

Schemas must be checked into source control and referenced from runtime
validation (§13) via embedded copies (do not network-fetch at runtime).

---

## 8. C# contracts

### `WatcherEntry` (abstract base)

```csharp
namespace Orchestra.Watchers.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PollCommandWatcher), "poll-command")]
[JsonDerivedType(typeof(PollScriptWatcher),  "poll-script")]
public abstract class WatcherEntry
{
    /// <summary>
    /// Watcher name. For library entries, set by the config loader from the map key.
    /// For inline entries, set from <c>definition.name</c>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; init; } = true;
    public required WatcherSchedule Schedule { get; init; }
    public WatcherItemsConfig? Items { get; init; }
    public WatcherDedup? Dedup { get; init; }
    public WatcherFailurePolicy FailurePolicy { get; init; } = WatcherFailurePolicy.Warn;
}

public sealed class PollCommandWatcher : WatcherEntry
{
    public required WatcherFetchCommandConfig Fetch { get; init; }
}

public sealed class PollScriptWatcher : WatcherEntry
{
    public required WatcherFetchScriptConfig Fetch { get; init; }
}
```

### `WatcherFetchCommandConfig` / `WatcherFetchScriptConfig`

```csharp
public class WatcherFetchCommandConfig
{
    public required string Command { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public bool IncludeStdErr { get; init; }
    public JsonObject? Config { get; init; }
}

public class WatcherFetchScriptConfig
{
    public required string Shell { get; init; }            // pwsh, bash, python, node, ...
    public string? ScriptFile { get; init; }               // mutually exclusive with Script
    public string? Script { get; init; }                   // mutually exclusive with ScriptFile
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public bool IncludeStdErr { get; init; }
    public JsonObject? Config { get; init; }
}
```

### `WatcherSchedule`

```csharp
public class WatcherSchedule
{
    public int? IntervalSeconds { get; init; }
    public string? Cron { get; init; }
    // Exactly one must be non-null; validate at load time.
}
```

### `WatcherDedup`

```csharp
public class WatcherDedup
{
    public string? Key { get; init; }                      // template; if null, hash whole item
    public WatcherDedupMode Mode { get; init; } = WatcherDedupMode.Fifo;
    public WatcherDedupFifoOptions? Fifo { get; init; }
    public WatcherDedupPresenceOptions? Presence { get; init; }
}

public enum WatcherDedupMode { Fifo, Presence }

public class WatcherDedupFifoOptions
{
    public int MaxEntries { get; init; } = 10_000;
    public int TtlDays { get; init; } = 14;
}

public class WatcherDedupPresenceOptions
{
    public int GraceTicks { get; init; } = 3;
}
```

### `WatcherTriggerConfig`

```csharp
namespace Orchestra.Engine.Triggers;

public class WatcherTriggerConfig : TriggerConfig
{
    public string? Watcher { get; init; }                  // library reference
    public WatcherEntry? Definition { get; init; }         // inline definition
    public int MaxConcurrent { get; init; } = 1;
    public Dictionary<string, string>? Parameters { get; init; }
    public WatcherCatchupMode Catchup { get; init; } = WatcherCatchupMode.FromNow;
    // Inherited: Enabled, InputHandlerPrompt, InputHandlerModel
}

public enum WatcherCatchupMode { FromNow, All }
```

### `IChildOrchestrationLauncher` (already exists — do not change)

The watcher loop dispatches via the existing `IChildOrchestrationLauncher`
(`src/Orchestra.Host/Services/ChildOrchestrationLauncher.cs:35-314`).

```csharp
Task<ChildOrchestrationHandle> LaunchAsync(
    ChildLaunchRequest request,
    CancellationToken cancellationToken = default);
```

`ChildLaunchRequest` fields the watcher will set:

- `OrchestrationId` — target orchestration id (resolved from registry).
- `Parameters` — built from the trigger's `Parameters` map with template
  substitution against the item.
- `Mode` — always `Async` for watchers (the watcher loop must not block on
  orchestration completion).
- `TriggeredBy` — `"watcher:<watcher-name>"` for observability.
- `TriggerId` — the watcher trigger's registry id.
- `UserMetadata` — include `watcherName`, `itemDedupKey`, `cursorAtFetch`.

### `IWatcherFetcher` (internal)

```csharp
internal interface IWatcherFetcher
{
    Task<WatcherFetchResult> RunAsync(
        WatcherEntry entry,
        WatcherFetchRequest stdinPayload,
        CancellationToken cancellationToken);
}

internal sealed record WatcherFetchResult(
    int ExitCode,
    string RawStdout,
    string RawStdErr,
    WatcherFetcherOutput? ParsedOutput,
    Exception? Error);
```

Two implementations: `CommandWatcherFetcher`, `ScriptWatcherFetcher`. Both
share child-process plumbing with the existing script step executor (see
`src/Orchestra.Engine/...` for `ScriptStepExecutor`) where practical, but do
not depend on engine-internal types if it would force a circular reference.

### `IWatcherStateStore`

```csharp
public interface IWatcherStateStore
{
    Task<SharedWatcherState> ReadSharedAsync(string watcherName, CancellationToken ct);
    Task WriteSharedAsync(SharedWatcherState state, CancellationToken ct);
    Task<SubscriberWatcherState> ReadSubscriberAsync(string watcherName, string orchestrationId, CancellationToken ct);
    Task WriteSubscriberAsync(SubscriberWatcherState state, CancellationToken ct);
}
```

`FileWatcherStateStore` writes JSON files under
`<orchestra-working-dir>/.orchestra/watchers/`. Atomic write via temp +
`File.Move(replace: true)`. Use the same path-resolution helper as
`ProcessTracker`.

State file shapes:

```csharp
public class SharedWatcherState
{
    public int Version { get; set; } = 1;
    public string Watcher { get; set; } = "";
    public JsonNode? Cursor { get; set; }
    public List<SeenKeyEntry> SharedSeenKeys { get; set; } = [];
    public SharedWatcherStats Stats { get; set; } = new();
}

public class SubscriberWatcherState
{
    public int Version { get; set; } = 1;
    public string Watcher { get; set; } = "";
    public string OrchestrationId { get; set; } = "";
    public WatcherDedupMode Mode { get; set; }
    public List<SeenKeyEntry> SeenKeys { get; set; } = [];
    public Dictionary<string, int> AbsenceTickCounts { get; set; } = []; // for presence mode
    public SubscriberWatcherStats Stats { get; set; } = new();
}

public record SeenKeyEntry(string Key, DateTimeOffset SeenAt);

public class SharedWatcherStats
{
    public long Ticks { get; set; }
    public long FetchFailures { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastTickAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
}

public class SubscriberWatcherStats
{
    public long LifetimeDispatched { get; set; }
    public long Evictions { get; set; }
    public DateTimeOffset? LastDispatchAt { get; set; }
}
```

---

## 9. Lifecycle and runtime

### `WatcherManager`

`IHostedService` registered as a singleton. Responsibilities:

1. On `StartAsync`:
   - Load `orchestra.watchers.json` via `WatcherConfigLoader`. Skip if
     env is `Testing` or config flag `skip-watchers` is true.
   - Validate library entries (schedule consistency, dedup mode/options
     consistency, fetcher mutual exclusion).
   - Do **not** start any loops yet. Loops are started lazily by subscription
     registration.
2. While running:
   - Expose `RegisterSubscription(WatcherSubscription)` and
     `UnregisterSubscription(string watcherName, string orchestrationId)`,
     called by `TriggerManager` as watcher triggers are registered/unregistered
     for orchestrations.
   - When first subscription for a watcher arrives, start a `WatcherLoop`.
   - When last subscription leaves, signal the loop to stop and await its
     completion.
3. On `StopAsync`:
   - Cancel all running loops, await drain, flush state.

### `WatcherSubscriptionRegistry`

Internal `Dictionary<string, HashSet<WatcherSubscription>>` keyed by
watcher name. Thread-safe (lock-around-writes is sufficient; reads via snapshot).

```csharp
public sealed record WatcherSubscription(
    string WatcherName,
    string OrchestrationId,
    int MaxConcurrent,
    Dictionary<string, string>? Parameters,
    WatcherCatchupMode Catchup,
    string? InputHandlerPrompt,
    string? InputHandlerModel,
    string TriggerId);
```

### `WatcherLoop`

One per active watcher. Cancellation-aware. Pseudocode:

```
state.shared = stateStore.ReadShared(watcher.name)
nextTick = computeFirstTick(watcher.schedule, now)

while not cancelled:
    await delay until nextTick (respecting back-off if any)
    state.shared.stats.ticks++
    state.shared.stats.lastTickAt = now

    request = build stdin payload from state.shared.cursor, subscribers snapshot, config
    result = await fetcher.RunAsync(watcher, request, ct)

    if result.ExitCode != 0 or result.Error != null:
        handleFailure(watcher, result)
        nextTick = computeNextTick(watcher.schedule, now, withBackoff: true)
        continue

    if not WatcherOutputValidator.Validate(result.ParsedOutput, out errors):
        treatAsFailure("validation failed", errors)
        continue

    items = extractItems(result.ParsedOutput, watcher.items?.jsonPath)
    state.shared.stats.consecutiveFailures = 0
    state.shared.stats.lastSuccessAt = now

    foreach sub in subscribersSnapshot(watcher.name):
        state.sub = stateStore.ReadSubscriber(watcher.name, sub.OrchestrationId)
        if state.sub is new:
            applyCatchup(state.sub, sub.Catchup, currentSharedSeenKeys)

        newItems = filterDedup(items, state.sub, watcher.dedup)

        if watcher.dedup.mode == Presence:
            updateAbsenceCounts(state.sub, items, watcher.dedup.presence.GraceTicks)

        dispatchSemaphore = new(sub.MaxConcurrent)
        foreach newItem in newItems:
            await dispatchSemaphore.WaitAsync(ct)
            _ = Task.Run(async () => {
                try { await launcher.LaunchAsync(buildRequest(sub, newItem, watcher.name, state.shared.cursor), ct); }
                finally { dispatchSemaphore.Release(); }
            })
            recordDispatch(state.sub, newItem, watcher.dedup)
        await dispatchSemaphore.WaitAllAsync()  // wait for in-flight launches (not for orchestration completion)

        trim(state.sub, watcher.dedup)
        stateStore.WriteSubscriber(state.sub)

    if result.ParsedOutput.Cursor != null:
        state.shared.cursor = result.ParsedOutput.Cursor
    updateSharedSeenKeys(state.shared, items, watcher.dedup)
    trimShared(state.shared, watcher.dedup)
    stateStore.WriteShared(state.shared)

    emitHighTrimRateWarningIfNeeded(...)

    nextTick = computeNextTick(watcher.schedule, now, withBackoff: false)
```

Important details:

- **`launcher.LaunchAsync` is always `Async` mode.** The loop must not block on
  orchestration completion, even if `MaxConcurrent` is 1 — the semaphore is for
  bounding *parallel launches*, not for awaiting target orchestrations.
- **Per-subscriber dispatch is sequential across subscribers within a tick** —
  iterate over `subscribersSnapshot(...)` serially, but within a subscriber,
  launches are bounded by `MaxConcurrent`. This keeps fan-out predictable and
  state writes atomic per subscriber.
- **State writes are atomic.** Always temp + replace. Crashes between tick
  boundaries lose at most the in-flight tick.
- **Cancellation.** When cancelled, abandon the current tick gracefully, do not
  write partial state.

### Failure handling

`handleFailure(watcher, result)`:

- Increment `state.shared.stats.fetchFailures` and `consecutiveFailures`.
- Apply `failurePolicy`:
  - `warn`: log warning, continue (subject to back-off).
  - `ignore`: log debug, continue (subject to back-off).
  - `fail`: log error, stop **only this watcher loop**; host stays up; mark
    the watcher unhealthy in metrics.
- Back-off: exponential when `consecutiveFailures > 0`. `delay = min(base * 2^(failures-1), 300s)` where `base = 2s`. The base interval still serves as a floor; back-off only delays *additional* wait.

### Hot-reload

`TriggerManager` registration calls `WatcherManager.RegisterSubscription`. When
an orchestration is removed/updated, `TriggerManager` must call
`UnregisterSubscription` (then `RegisterSubscription` for the new shape if
updated). The implementer must verify the existing registry tear-down path
fires on orchestration removal and add it if missing — Orchestra's behavior on
this point should match whatever existing trigger types do today (webhook,
scheduler). If they don't tear down, watchers don't either; both should be
fixed in a follow-up, not this work.

---

## 10. State management

### Locations

```
<orchestra-working-dir>/.orchestra/watchers/
  <watcher-name>.state.json
  <watcher-name>--<orchestration-id>.state.json
```

`<watcher-name>` may contain characters illegal in file names; sanitize via
the same routine as orchestration ids (see
`OrchestrationRegistry.SanitizeId(...)` at
`src/Orchestra.Host/Registry/OrchestrationRegistry.cs`). Separator between
watcher name and orchestration id is `--`. Both name segments are already
sanitized.

### Dedup modes

#### `fifo` (default)

- Maintains a list of `(key, seenAt)` ordered by `seenAt`.
- On each tick, after dispatching:
  - Add new keys to the head (most recent).
  - Trim by `maxEntries` (drop oldest if list exceeds limit).
  - Trim by `ttlDays` (drop entries older than now − ttlDays).
- An item is "new" iff its key is **not** in the list.

Trim eviction increments `subscriber.stats.evictions`. If the eviction rate
(evictions per hour, computed over a sliding window) exceeds 10% of
`maxEntries` per hour, emit a single warning per (watcher, subscriber, 1-hour
period):

```
Watcher 'X' subscriber 'Y': FIFO trim rate {EvictionsPerHour}/hr at maxEntries={MaxEntries}.
Possible duplicate dispatches; consider mode=presence, larger window, or a cursor-aware fetcher.
```

#### `presence`

- Maintains a set of keys currently visible at the source plus an
  `AbsenceTickCounts` map for keys missing in recent ticks.
- On each tick:
  - For every key present in the current items: clear its `AbsenceTickCounts`
    entry; if the key was not in the seen-set, dispatch and add to seen-set.
  - For every key in the seen-set not present this tick: increment its
    `AbsenceTickCounts` entry. If it reaches `graceTicks`, remove from seen-set
    and `AbsenceTickCounts`.
- An item is "new" iff its key is **not** in the seen-set at tick start.

Presence mode does **not** trigger trim-rate warnings — pruning is by-design
and bounded by source working-set size.

#### Templating for dedup keys

The `key` template uses the same renderer as orchestration parameters. For
this work:

- Reuse the engine's existing template renderer if cleanly callable from
  outside an orchestration context.
- Otherwise, write a small renderer (`WatcherTemplateRenderer`) that supports
  `{{item}}` (whole-object → JSON) and `{{item.path.to.field}}` (dotted path).
  Missing paths render as the literal string `null`; explicitly do **not**
  throw. Document this behavior in code XML comments.

If `dedup.key` is omitted entirely, the dedup key is `SHA-256` of the item's
canonical JSON form, hex-encoded, prefix-16-char.

### Catchup application

When a new subscriber state file is created (orchestrationId not seen for this
watcher before):

- `Catchup == FromNow`: populate `state.sub.SeenKeys` with a copy of
  `state.shared.SharedSeenKeys` at the moment of registration. The first tick
  will see these keys as already-known and dispatch nothing for them.
- `Catchup == All`: leave `state.sub.SeenKeys` empty. The first tick will
  dispatch for every item currently visible.

`Shared.SharedSeenKeys` is maintained on every tick using the same trim rules
as a FIFO subscriber. (Even when subscribers use presence mode, shared-seen is
FIFO — it only exists to seed late subscribers, not to filter dispatch.)

### Concurrency

- `FileWatcherStateStore` serializes writes per file via a per-file
  `SemaphoreSlim`. Read-modify-write callers must do their own serialization
  around the in-memory state; the store does not.
- The `WatcherLoop` is single-threaded internally; per-subscriber state is
  only mutated by the loop, so no cross-thread access on state objects.

---

## 11. DI wiring and host integration

In `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs`:

1. **Register services** (add alongside existing service registrations):

   ```csharp
   services.AddSingleton<IWatcherStateStore, FileWatcherStateStore>();
   services.AddSingleton<WatcherSubscriptionRegistry>();
   services.AddSingleton<IWatcherFetcherFactory, WatcherFetcherFactory>();
   services.AddSingleton<WatcherOutputValidator>();
   services.AddSingleton<WatcherManager>();
   services.AddHostedService(sp => sp.GetRequiredService<WatcherManager>());
   ```

2. **Load config** in `InitializeOrchestraHostAsync`, parallel to the
   `orchestra.services.json` load. Honor a new config flag `skip-watchers`
   that mirrors `skip-services`. Skip when env is `Testing`.

   ```csharp
   if (!skipWatchers && env != "Testing") {
       var path = WatcherConfigLoader.ResolveLibraryPath(...);
       if (File.Exists(path)) {
           var library = await WatcherConfigLoader.LoadLibraryAsync(path);
           await watcherManager.SetLibraryAsync(library);
       }
   }
   ```

3. **Wire `WatcherTriggerConfig` into `TriggerManager`** at the existing
   trigger-registration site (`ServiceCollectionExtensions.cs:474-501` area):

   - When registering an orchestration's trigger, if its
     `TriggerConfig` is a `WatcherTriggerConfig`, resolve the watcher
     (library reference or inline), validate, and call
     `watcherManager.RegisterSubscription(...)`.
   - When unregistering, call `watcherManager.UnregisterSubscription(...)`.
   - On collision (inline name conflicts with library name) or unknown
     reference, throw `WatcherInitializationException` with a clear message
     and fail registration (does not crash the host; the orchestration is
     marked as unregistered/errored, consistent with webhook misconfig
     behavior).

4. **No new HTTP endpoints.** Watchers are not webhook-reachable.

---

## 12. CLI: `orchestra watcher test`

A new CLI subcommand under `src/Orchestra.Cli`. Read-only diagnostic. Does
**not** start the host or invoke any orchestration.

### Syntax

```
orchestra watcher test --watcher <name>
                       [--from-state <path> | --empty-state]
                       [--save-state <path>]
                       [--ticks <N>]
                       [--subscribers <id>[,<id>...]]
                       [--print-input]
                       [--config-path <orchestra.watchers.json>]
```

Defaults:

- `--ticks 1`
- `--empty-state` (if neither `--from-state` nor `--empty-state` given)

### Behavior

1. Load the watcher library (from `--config-path` or default).
2. Resolve the named watcher.
3. Build stdin payload:
   - `cursor`: from `--from-state` if provided; null if `--empty-state`.
   - `subscribers`: from `--subscribers` arg or empty.
   - `config`: from the watcher's `fetch.config` block.
4. For each of `--ticks` iterations:
   - Spawn the fetcher with timeout from config.
   - Capture stdout, stderr, exit code.
   - Validate stdout against `fetcher-output.schema.json/v1`.
   - Extract items per `items.jsonPath`.
   - For each simulated subscriber, compute dedup decisions (`new` vs `seen`).
   - Print a structured report.
5. If `--save-state`, write the resulting state JSON to the given path so
   subsequent runs can pick it up via `--from-state`.

### Output format

Plain text, single tick:

```
== orchestra watcher test: github-pr-comments ==

Resolved from: orchestra.watchers.json
Type:          poll-script
Command:       pwsh ./watchers/fetch-pr-comments.ps1
Timeout:       60s

stdin sent:
  { "protocolVersion": "1", "watcher": "github-pr-comments", "cursor": null,
    "subscribers": [], "config": null }

stdout received (215 bytes, exit 0):
  { "protocolVersion": "1", "items": [...], "cursor": {...} }

schema validation: OK

items extracted (jsonPath=$.items[*]): 7

simulated subscribers: (none — pass --subscribers to simulate dedup)

cursor advance preview:
  before: null
  after:  { "lastEventId": 4823 }

exit: 0
```

Exit codes:

- `0` — all ticks validated OK.
- `1` — validation failure or fetch failure on any tick.
- `2` — config error (watcher not found, etc.).

---

## 13. Logging and metrics

### Logging — code-generated `LoggerMessage`

Per repo rules. Examples (event ids 5100–5199 reserved for watchers):

```csharp
public static partial class WatcherLogging
{
    [LoggerMessage(EventId = 5101, Level = LogLevel.Debug,
        Message = "Watcher {WatcherName} tick: fetched {ItemCount} items in {ElapsedMs}ms")]
    public static partial void LogTickFetched(this ILogger logger, string watcherName, int itemCount, long elapsedMs);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information,
        Message = "Watcher {WatcherName} subscriber {OrchestrationId}: dispatched {DispatchCount} new items")]
    public static partial void LogDispatched(this ILogger logger, string watcherName, string orchestrationId, int dispatchCount);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning,
        Message = "Watcher {WatcherName} fetch failed (exit {ExitCode}): {Reason}")]
    public static partial void LogFetchFailed(this ILogger logger, string watcherName, int exitCode, string reason);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Warning,
        Message = "Watcher {WatcherName} subscriber {OrchestrationId}: FIFO trim rate {EvictionsPerHour}/hr at maxEntries={MaxEntries}. Possible duplicate dispatches; consider mode=presence or a larger window.")]
    public static partial void LogHighTrimRate(this ILogger logger, string watcherName, string orchestrationId, double evictionsPerHour, int maxEntries);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Error,
        Message = "Watcher {WatcherName} fetcher output failed schema validation: {Pointer} — {Message}")]
    public static partial void LogValidationFailed(this ILogger logger, string watcherName, string pointer, string message);

    [LoggerMessage(EventId = 5106, Level = LogLevel.Information,
        Message = "Watcher {WatcherName} subscription added: {OrchestrationId} (catchup={Catchup})")]
    public static partial void LogSubscriptionAdded(this ILogger logger, string watcherName, string orchestrationId, WatcherCatchupMode catchup);

    [LoggerMessage(EventId = 5107, Level = LogLevel.Information,
        Message = "Watcher {WatcherName} subscription removed: {OrchestrationId}")]
    public static partial void LogSubscriptionRemoved(this ILogger logger, string watcherName, string orchestrationId);

    [LoggerMessage(EventId = 5108, Level = LogLevel.Information,
        Message = "Watcher {WatcherName} started (first subscriber)")]
    public static partial void LogWatcherStarted(this ILogger logger, string watcherName);

    [LoggerMessage(EventId = 5109, Level = LogLevel.Information,
        Message = "Watcher {WatcherName} stopped (no subscribers)")]
    public static partial void LogWatcherStopped(this ILogger logger, string watcherName);

    [LoggerMessage(EventId = 5110, Level = LogLevel.Error,
        Message = "Watcher {WatcherName} configuration error: {Reason}")]
    public static partial void LogConfigError(this ILogger logger, string watcherName, string reason);
}
```

No `Console.WriteLine`, no `Write-Host`-style messages anywhere in production
code. Use `ILogger` throughout.

### Metrics

Use whatever telemetry plumbing already exists in Orchestra (look for
existing `Meter` / `Counter` usage; reuse the same `Meter` name conventions).

Counters per watcher:

- `orchestra.watcher.ticks`
- `orchestra.watcher.fetch_failures`
- `orchestra.watcher.consecutive_failures` (gauge)
- `orchestra.watcher.items_seen`

Counters per (watcher, subscription):

- `orchestra.watcher.dispatches`
- `orchestra.watcher.dispatch_failures`
- `orchestra.watcher.dedup_hits` (items filtered by dedup)
- `orchestra.watcher.dedup_evictions`
- `orchestra.watcher.subscribers` (gauge per watcher; sum across subs)

All tagged with `watcher` and optionally `orchestrationId` for per-sub
metrics.

---

## 14. Output validation (runtime)

`WatcherOutputValidator` loads
`schemas/watchers/v1/fetcher-output.schema.json` at construction
(embedded resource, no network fetch) and exposes:

```csharp
public bool TryValidate(JsonNode output, out IReadOnlyList<WatcherValidationError> errors);

public record WatcherValidationError(string JsonPointer, string Expected, string Actual, string Message);
```

Use the same JSON Schema library Orchestra already depends on for its other
schemas (verify which one — likely `JsonSchema.Net` or similar; pick whatever
is already in `Directory.Packages.props`). Do **not** add a new dependency.

On validation failure inside `WatcherLoop`:

- Log via `LogValidationFailed` for each error.
- Treat the tick as a fetch failure (apply `failurePolicy`).
- Increment `fetch_failures` metric.

### Version negotiation

Before running the schema, check `output.protocolVersion`:

- Missing → assume `"1"`, validate against v1.
- `"1"` → validate against v1.
- Anything else → emit a specific error (`unsupported protocolVersion: <X>`),
  treat as failure. Do not attempt to validate.

---

## 15. Failure policy details

| Policy | Behavior on fetch failure |
|---|---|
| `warn`  | Log warning; back-off applies; continue loop. |
| `ignore`| Log debug; back-off applies; continue loop. |
| `fail`  | Log error; stop this watcher loop only; subscribers stay registered (loop restarts on host restart or on subscription change that re-evaluates health). |

Back-off:

- `consecutiveFailures = 0` → no extra delay; next tick at normal schedule.
- `consecutiveFailures > 0` → wait `min(2s * 2^(failures-1), 300s)` *in
  addition to* the next scheduled tick. Reset on first success.

---

## 16. Examples to ship

Two parallel examples implementing the same trivial source ("list files in a
directory") so authors can compare runtime conventions side-by-side. Both must
be fully runnable without any external services.

### `examples/watchers/folder-poll-pwsh/`

**`orchestra.watchers.json`:**

```jsonc
{
  "$schema": "../../../schemas/orchestra.watchers.schema.json",
  "watchers": {
    "local-folder-pwsh": {
      "type": "poll-script",
      "schedule": { "intervalSeconds": 5 },
      "fetch": {
        "shell": "pwsh",
        "scriptFile": "./fetch.ps1",
        "timeoutSeconds": 10,
        "config": { "folder": "./inbox" }
      },
      "dedup": { "key": "{{item.path}}", "mode": "presence" }
    }
  }
}
```

**`fetch.ps1`:**

```powershell
$ErrorActionPreference = 'Stop'
$stdin = [Console]::In.ReadToEnd()
$req = $stdin | ConvertFrom-Json

$folder = $req.config.folder
if (-not (Test-Path $folder)) {
    [Console]::Out.Write((@{ protocolVersion = '1'; items = @(); cursor = $req.cursor } | ConvertTo-Json -Depth 10 -Compress))
    exit 0
}

$items = @()
Get-ChildItem -Path $folder -File -Filter '*.txt' | ForEach-Object {
    $items += @{ path = $_.FullName; name = $_.Name; size = $_.Length }
}

$response = @{ protocolVersion = '1'; items = $items; cursor = $req.cursor }
[Console]::Out.Write(($response | ConvertTo-Json -Depth 10 -Compress))
exit 0
```

**`handle-new-file.yaml`:**

```yaml
name: handle-new-file-pwsh
description: Reacts to new files seen by the folder-poll-pwsh watcher.
trigger:
  type: watcher
  watcher: local-folder-pwsh
  enabled: true
  maxConcurrent: 2
  parameters:
    path: "{{item.path}}"
    name: "{{item.name}}"
steps:
  - name: greet
    type: Prompt
    model: claude-opus-4.6
    userPrompt: |
      A new file appeared: {{name}} at {{path}}. Write a one-sentence note.
```

**`README.md`:** Short walkthrough — create `inbox/`, drop a `.txt` file, watch
the orchestration fire once and not re-fire on the next tick.

### `examples/watchers/folder-poll-dotnet/`

Same shape, but `fetch.cs` is a .NET 10 single-file program runnable via
`dotnet run fetch.cs`.

**`orchestra.watchers.json`:**

```jsonc
{
  "$schema": "../../../schemas/orchestra.watchers.schema.json",
  "watchers": {
    "local-folder-dotnet": {
      "type": "poll-command",
      "schedule": { "intervalSeconds": 5 },
      "fetch": {
        "command": "dotnet",
        "arguments": ["run", "fetch.cs"],
        "timeoutSeconds": 30,
        "config": { "folder": "./inbox" }
      },
      "dedup": { "key": "{{item.path}}", "mode": "presence" }
    }
  }
}
```

**`fetch.cs`** (sketch):

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

var stdin = await Console.In.ReadToEndAsync();
var req = JsonNode.Parse(stdin)!;
var folder = req["config"]?["folder"]?.GetValue<string>() ?? "./inbox";

var items = new JsonArray();
if (Directory.Exists(folder))
{
    foreach (var f in Directory.EnumerateFiles(folder, "*.txt"))
    {
        var info = new FileInfo(f);
        items.Add(new JsonObject {
            ["path"] = info.FullName,
            ["name"] = info.Name,
            ["size"] = info.Length
        });
    }
}

var response = new JsonObject {
    ["protocolVersion"] = "1",
    ["items"] = items,
    ["cursor"] = req["cursor"]?.DeepClone()
};

Console.Out.Write(response.ToJsonString());
```

**`handle-new-file.yaml`:** Same as the pwsh example but referencing
`local-folder-dotnet`. Default model `claude-opus-4.6` per AGENTS.md.

Both example READMEs reference `orchestra watcher test` for fast iteration.

---

## 17. Testing requirements

Tests are mandatory per AGENTS.md. No "done" without green tests at every
listed layer.

### New unit test project

`tests/Orchestra.Watchers.Tests/Orchestra.Watchers.Tests.csproj`

Required unit tests:

- **`WatcherConfigLoaderTests`**
  - Valid library loads and exposes entries by name.
  - Schedule with both `intervalSeconds` and `cron` set → load fails.
  - Schedule with neither set → load fails.
  - `poll-script` with both `script` and `scriptFile` set → load fails.
  - `poll-script` with neither set → load fails.
  - Dedup mode `fifo` with `presence` block (and vice versa) → load fails.
  - Missing required fields in `WatcherEntry` → load fails with clear path.

- **`WatcherSubscriptionRegistryTests`**
  - Register increments count; first subscription for a watcher reports
    "first."
  - Unregister of last subscription reports "last."
  - Multiple subscribers for same watcher are distinct by orchestration id.
  - Snapshot is stable under concurrent register/unregister.

- **`FileWatcherStateStoreTests`**
  - Shared and per-subscriber read/write round-trip.
  - Atomic write: forcing a write failure mid-operation leaves the old file
    intact.
  - Read of a missing file returns a fresh default state, not a throw.
  - Path resolution uses the same root as `ProcessTracker`.
  - Version mismatch in a stored file (`version: 99`) → clear exception
    (`WatcherValidationException` or similar).

- **`WatcherOutputValidatorTests`**
  - Valid v1 output passes.
  - Missing `items` and missing `cursor` is OK.
  - Wrong type on `items` element fails with a JSON pointer.
  - Unknown `protocolVersion` returns specific error.

- **`WatcherTemplateRendererTests`**
  - `{{item.x}}` substitutes nested fields.
  - `{{item}}` substitutes whole-item JSON.
  - Missing path renders the literal `null`.
  - Templates inside parameter map work the same way.

- **`CommandWatcherFetcherTests`**
  - Successful exit + valid JSON → result populated.
  - Timeout → result reports timeout failure.
  - Non-zero exit → result reports failure with stderr.
  - Invalid JSON on stdout → result reports parse failure.

- **`ScriptWatcherFetcherTests`**
  - Same as above, plus: both `script` (inline) and `scriptFile` paths work.

- **`WatcherLoopTests`** (using fake `TimeProvider` and fake fetcher)
  - `fifo` mode: items in seen-set are not re-dispatched; items not in
    seen-set are dispatched once; trimming evicts oldest first.
  - `presence` mode: keys present this tick reset absence counters; keys
    absent for `graceTicks` are pruned and re-dispatch if reappearing.
  - `Catchup: FromNow` populates new subscriber with current shared-seen.
  - `Catchup: All` leaves new subscriber empty.
  - Cursor advances on success; preserved across ticks; preserved on failure.
  - `consecutiveFailures` increments on failure, resets on success.
  - Back-off: with 1 failure, next tick waits ≥ 2s extra; with 2 failures, ≥
    4s; capped at 300s.
  - `failurePolicy: fail` stops only this loop.
  - High trim-rate warning fires when threshold exceeded; not before.
  - Cancellation during a tick aborts cleanly without partial state writes.

- **`WatcherTriggerConfigValidationTests`**
  - Both `watcher` and `definition` set → registration fails.
  - Neither set → registration fails.
  - Inline `definition.name` collides with library name → registration fails
    with both definitions named in the error.
  - Reference to unknown library watcher → registration fails.

### Integration tests (in the same project)

Use a real `WatcherManager` + `FileWatcherStateStore` against a temp
directory, but mock `IChildOrchestrationLauncher`. Use a fake fetcher that
runs in-process to keep tests fast.

- Two orchestrations subscribe to the same library watcher; one fetch per
  tick; each subscriber receives an independent dispatch per new item.
- Removing one subscriber leaves the watcher running for the remaining one.
- Removing the last subscriber stops the loop; the next added subscriber
  restarts it with state preserved.
- `Catchup: FromNow` does not dispatch backlog; `Catchup: All` does.
- Inline-vs-library name collision is detected at registration.
- Per-subscriber dedup: A acks an item via dispatch, B still sees it as new.

### E2E test

In `tests/Orchestra.E2E`, add at least one test that:

1. Spins up a full host (matching style of existing webhook E2E tests).
2. Drops a `watchers.json` and an orchestration with `trigger: { type: watcher }`.
3. Provides a real `.ps1` or `.cs` fetcher script that returns one new item.
4. Waits for the target orchestration to complete and asserts its output.
5. Performs a second tick (forced via configuration short interval) and
   asserts the orchestration is **not** re-invoked for the same item.

### CLI test

In `tests/Orchestra.Watchers.Tests` (or wherever CLI tests live today):

- `orchestra watcher test --watcher <name> --empty-state` against an example
  fetcher prints the expected report shape and exits 0.
- Same against a fetcher that returns invalid output → exits 1; report shows
  validation errors.
- Unknown watcher → exits 2.

### What success looks like

```
dotnet build Orchestra.sln              # all green
dotnet test  Orchestra.sln              # all green
```

Verify by hand at least once:

- Run an example watcher locally; confirm it fires on a new file once and not
  on subsequent ticks.
- Confirm shutdown is clean (no orphan processes, no `nul` files left
  behind).

---

## 18. Build and quality rules (mandatory)

These come from `AGENTS.md` and the global cleanup rules. Every file
authored under this plan must comply.

- **No `nul` files.** If git or any shell command produces a file literally
  named `nul` in the working tree, delete it before declaring the task done.
- **No CRLF artifacts.** Authored text files must not contain `^M`
  characters. Use LF line endings unless the file is Windows-specific
  (e.g., `.ps1`, `.cmd`). Verify with `Get-Content -Raw` + regex if needed.
- **`ILogger` with code-generated `LoggerMessage` partials** for all logging
  (no string-interpolated `Log(...)` calls).
- **Tests must accompany the change.** Anything from §17 missing is blocking.
- **Do not leave the dev server running** after work is done.
- **Default model in any new orchestration example is `claude-opus-4.6`**.

---

## 19. Order of execution

Implement in this order so each step builds on the last and can be tested
incrementally.

1. **Schemas first.**
   - Author the three JSON schemas (§7). Check in as plain `.json` files.
   - Embed the output schema as an embedded resource in
     `Orchestra.Watchers` for the validator.

2. **New project skeleton.**
   - Create `src/Orchestra.Watchers/Orchestra.Watchers.csproj`.
   - Add to `Orchestra.sln`.
   - Set up `Directory.Packages.props` references (mirror similar projects'
     dependency style; do not add new third-party deps).

3. **Models + config loader.**
   - Implement all `Models/` classes (§5, §8).
   - Implement `WatcherConfigLoader` parallel to `OrchestraConfigLoader`.
   - Unit tests for config loading and model validation.

4. **State store.**
   - Implement `FileWatcherStateStore` with atomic writes, reusing the same
     root path helper as `ProcessTracker`. Verify by reading
     `ProcessTracker` first.
   - Unit tests for round-trip, atomicity, version handling.

5. **Output validator.**
   - Implement `WatcherOutputValidator` against the embedded v1 output
     schema. Reuse Orchestra's existing JSON Schema library.
   - Unit tests for valid/invalid payloads and version negotiation.

6. **Fetchers.**
   - Implement `CommandWatcherFetcher` and `ScriptWatcherFetcher`.
   - Share child-process plumbing with the existing script step executor
     where practical (read `Orchestra.Engine` for `ScriptStepExecutor`); do
     not introduce a circular reference.
   - Unit tests with synthetic scripts (a couple of tiny `.ps1` or `.cs`
     test fixtures under `tests/Orchestra.Watchers.Tests/fixtures/`).

7. **Template renderer.**
   - Reuse engine renderer if cleanly accessible; otherwise write the small
     local renderer described in §10. Unit tests.

8. **Subscription registry + loop + manager.**
   - Implement `WatcherSubscriptionRegistry`, `WatcherLoop`,
     `WatcherManager`.
   - Wire in `IChildOrchestrationLauncher` (already exists in DI).
   - Use a fake `TimeProvider` in tests; in production, `TimeProvider.System`.
   - Cover all behaviors in `WatcherLoopTests` (§17).

9. **Engine trigger type.**
   - Add `WatcherTriggerConfig` to `src/Orchestra.Engine/Triggers/`.
   - Hook into `TriggerManager` registration paths so it routes to
     `WatcherManager.RegisterSubscription` / `UnregisterSubscription`.
   - Validate (mutual exclusion, name collision, unknown library reference)
     at registration time.

10. **Host wiring.**
    - Update `src/Orchestra.Host/Extensions/ServiceCollectionExtensions.cs`
      per §11 — load config, register DI, wire trigger registration.
    - Add `skip-watchers` config flag.

11. **CLI command.**
    - Add `orchestra watcher test` per §12.
    - Tests at `Orchestra.Watchers.Tests` (CLI integration).

12. **Examples.**
    - `examples/watchers/folder-poll-pwsh/` (full).
    - `examples/watchers/folder-poll-dotnet/` (full).

13. **Integration + E2E tests.**
    - All tests listed in §17.

14. **Logging + metrics.**
    - Ensure every code path emits the appropriate `LoggerMessage` partial
      from §13.
    - Wire counters into Orchestra's existing telemetry meter.

15. **Quality gate.**
    - Run full `dotnet build` and `dotnet test`. All green.
    - Run the example by hand; confirm behavior.
    - Verify no `nul` files exist anywhere in the tree.
    - Verify no CRLF artifacts in shipped files.
    - Do not leave any background processes running.

---

## 20. Out of scope (explicitly)

- Durable / cross-process queue between fetch and dispatch.
- Cross-host / distributed dedup.
- A formal `IWatcherSource` .NET plugin contract (the internal
  `IWatcherFetcher` is internal-only; .NET tools, .NET 10 single-file apps,
  and external exes are already supported via `poll-command`).
- An `orchestra watcher new` scaffolding command (deferred; two worked
  examples cover the gap).
- Typed SDKs / helper libraries (PowerShell module, NuGet helper, pip
  package). All deferred to a follow-up; v1 ships schemas + validation +
  `watcher test` + examples.
- Migrating existing scheduler-style examples (e.g., `icm-auto-acknowledge`)
  to the watcher model. Add one new minimal example only; leave the rest
  alone.
- A new `event` trigger type as a separate concept. Watchers ARE the trigger;
  there is no separate event bus surface.
- HTTP loopback or webhook-style dispatch from watchers.
- Hot-reload of the `orchestra.watchers.json` file itself if Orchestra
  doesn't already do this for other config files; mirror the existing
  behavior.

---

## 21. Risks and edge cases the implementer must handle

These are the trapdoors flagged during design. Watch for them.

1. **Path resolution for `.orchestra/watchers/`.** Must use the same root as
   `ProcessTracker`'s `.orchestra.pids.json`. Verify by reading
   `ProcessTracker` *before* picking a path. If there is no shared helper,
   create one and use it for both.

2. **Template renderer reuse.** Prefer reusing the engine's existing template
   renderer (the one used for step parameters like `{{step.output}}`). If it
   is bound to the engine execution context and not cleanly callable outside,
   write the small `WatcherTemplateRenderer` and document the divergence in
   code XML comments. Do not silently fork the template syntax.

3. **Cron parsing.** Reuse what `SchedulerTriggerConfig` uses today. Do not
   introduce a second cron parser. If the existing parser is not exposed at
   the right layer, surface it via a shared helper.

4. **Per-watcher trim-rate warning bookkeeping.** The rate is "evictions per
   hour over a sliding 1-hour window." Implement as a small in-memory ring
   buffer of `(timestamp, count)` tuples per (watcher, subscriber). Persist
   to state on shutdown if cheap, but recomputing from cold start is also
   acceptable.

5. **Atomic state writes on Windows.** `File.Move(source, dest,
   overwrite: true)` is atomic on the same volume. Ensure temp files live in
   the same directory as the target. Do not write to `Path.GetTempPath()`
   then move across volumes.

6. **Trigger lifecycle on orchestration removal.** Verify that the existing
   `TriggerManager` calls back into a tear-down path when an orchestration is
   removed from the registry. If it does not, watcher subscriptions will
   leak. If it does not, mirror webhook behavior (which has the same issue
   or doesn't); do not invent new lifecycle semantics specific to watchers.

7. **`Async` dispatch from a `MaxConcurrent=1` watcher.** The `MaxConcurrent`
   value bounds *parallel launches into `LaunchAsync`*, not target
   orchestration concurrency (target orchestrations have their own
   concurrency control). The watcher loop must never `await
   handle.Completion`. Even with `MaxConcurrent=1`, the loop must move on to
   the next tick on schedule.

8. **Inline definition without `definition.name`.** Make the name field
   required in the schema for inline definitions. The error message on
   missing name must say "inline watcher definitions require a `name` field
   to be referenced for state and logging."

9. **Watcher name sanitization.** Names that are user-supplied may contain
   characters illegal in filenames on Windows (e.g., `:`, `/`). Sanitize
   identically to `OrchestrationRegistry.SanitizeId`. The state file uses
   the *sanitized* name; the in-memory name retains the original.

10. **Schema embedding.** Schemas live in `schemas/` for editor tooling but
    must also be embedded in the `Orchestra.Watchers` assembly as
    `EmbeddedResource` for runtime validation. Source-control the canonical
    copy in `schemas/`; the build step copies it into the assembly. The two
    must never drift; add a unit test that asserts equality between
    on-disk and embedded resource content.

11. **Stderr handling.** Always capture stderr; log it on fetch failure
    (level Warning) regardless of `includeStdErr`. The `includeStdErr` flag
    only controls whether stderr is *merged into the stdout JSON for
    parsing* (rarely useful; off by default).

12. **Big stdout payloads.** Set a sane cap on stdout buffer size (e.g.,
    16 MB). Beyond cap, treat as fetch failure with a specific message.
    Watcher fetchers are not meant to ship large blobs.

13. **Orchestration id resolution for dispatch.** The watcher loop must
    dispatch to a specific orchestration id at runtime. Get this from the
    `WatcherSubscription` populated at registration; do not look up by name
    at dispatch time. If the orchestration is removed, its subscription is
    already gone.

14. **State file size growth (stateless fetcher anti-pattern).** The default
    FIFO `maxEntries: 10000` bounds size at ~300 KB per per-subscriber file.
    The trim-rate warning is the operator's signal that their config is
    misaligned. Documentation must call this out (in the example README at
    minimum).

15. **JSON polymorphism.** `WatcherEntry`'s `JsonDerivedType` discriminator
    is `"type"`. Ensure deserialization works both for library
    (`watchers: { "name": { ... } }`) and inline (`definition: { ... }`)
    contexts.

---

## 22. Acceptance criteria

The work is "done" iff all of the following are true:

- All new source files exist under the paths in §4.
- All schemas in §7 exist and are referenced by the validator (embedded) and
  by the example configs (via `$schema`).
- `orchestra.watchers.json` is loaded on host startup when present, skipped
  when `skip-watchers` or env is `Testing`.
- Orchestrations can declare `trigger: { type: watcher, watcher: "<name>" }`
  or `trigger: { type: watcher, definition: { ... } }`, and both work
  end-to-end.
- A library watcher with no subscribers does **not** run. Adding the first
  subscriber starts the loop; removing the last subscriber stops the loop.
- Inline / library name collisions, missing references, and other invalid
  configurations fail registration loudly with actionable messages.
- Both dedup modes (`fifo`, `presence`) behave as specified in §10.
- The trim-rate warning fires when configured (§13 `LogHighTrimRate`).
- State files are written atomically under `.orchestra/watchers/` and survive
  host restarts (subscribers do not see backlog after restart unless
  `catchup: all`).
- `orchestra watcher test` runs a fetcher once without invoking
  orchestrations and produces the report described in §12.
- Both example folders (`folder-poll-pwsh`, `folder-poll-dotnet`) run
  end-to-end: drop a file, orchestration fires once, next tick does not
  re-fire.
- All tests in §17 exist and pass.
- `dotnet build` and `dotnet test` are green on the full solution.
- No `nul` files anywhere in the tree.
- No CRLF artifacts in shipped files.
- All logging uses code-generated `LoggerMessage` partials.
- Examples default to model `claude-opus-4.6`.
- No background processes left running after testing.

When all the above hold, the work is complete.
