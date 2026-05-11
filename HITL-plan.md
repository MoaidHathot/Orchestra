# Human-in-the-Loop (HITL) Plan

## Goal

Add a Human-in-the-Loop capability to Orchestra so that an orchestration can pause for user input — either (a) **only when needed**, decided by an LLM during a `Prompt` step, or (b) **as an explicit gate** placed in the DAG by the author. Each path supports an optional timeout, sends notifications via the existing hooks system, and exposes a REST/CLI surface for answering.

---

## Design Decisions (all locked)

| # | Decision | Rationale |
|---|---|---|
| 1 | **Hybrid invocation**: a new declarative `Approval` step type **and** a new engine tool `orchestra_request_user_input`. | Two genuine use-cases: explicit gates (deploy approval) vs. agent-decided "I need clarification". |
| 2 | **Engine tool blocks inside the tool call** (TCS await), and the user's reply is returned as the tool result so the LLM continues its loop naturally with the answer in hand. The agent session is **not** torn down. | Pausing the step would destroy the agent's conversation; the entire point of asking is for the agent to continue with the answer. |
| 3 | **Approval step uses full step suspension**: persist a checkpoint, set status `AwaitingInput`, and on response call `OrchestrationExecutor.ResumeAsync` with the answer seeded as the step's output. | Survives host restarts. Required for long human-approval gates (hours, days). |
| 4 | **Persistent + in-memory** wait: every pause writes a `PendingInputRecord` JSON file (sibling to the checkpoint) **and** registers a `TaskCompletionSource` in the host. Live host → microsecond resume. Restarted host → see #11 (engine-tool path) and #12 (Approval path). | Best of both: fast common case, durable uncommon case. |
| 5 | **No default HITL timeout**, mirroring Orchestra's existing per-step timeout default of `null`. When the author **does** set a timeout, the per-step `onTimeout` decides what happens (see #6). | Consistency with the rest of Orchestra. |
| 6 | **Timeout behavior**: `onTimeout` ∈ `fail` (default-when-set) / `defaultResponse` / `cancel`. `fail` produces `CancellationCause.AwaitingInputTimeout`. | Explicit, predictable. |
| 7 | **Notifications via existing hooks**: a new lifecycle event `step.awaitingInput` is added. Authors wire any hook (script, etc.) the same way they wire `step.failure`. The hook payload carries `respondUrl`, the step's `prompt`, and `choices`. | Zero new mechanism. Slack/Teams/PagerDuty/email/SMS all reachable from the existing `script` hook action via curl. |
| 8 | **Hook layering is free** because the existing hooks system already supports orchestra-wide hooks (in `orchestra.json`) and per-orchestration hooks (in the file). No new config block. | Mirrors `defaultStepTimeoutSeconds`, `defaultRetryPolicy`, `mcp.json`, etc. |
| 9 | **Response shape** = `{ choice?: string; reply?: string }`. `Approval` may declare `choices: [...]` to constrain. Engine tool accepts free text. `{{stepName.output}}` resolves to `reply ?? choice`. | Covers approve/reject and free-form Q&A in one schema. |
| 10 | **Orchestration timeout clock pauses during waits**, configurable per-orchestration via `pauseTimeoutDuringWait: bool` (default `true`). | Wait time isn't compute time. Author can disable for hard SLAs. |
| 11 | **Engine-tool host crash**: on startup, scan pending records; for any `kind == engine-tool`, mark the run `Failed` with cause `HostShutdownDuringWait`. The previous step's checkpoint stays intact, so author can retry from there. | Agent sessions are in-memory; no LLM SDK lets us re-attach. Best we can do is fail-fast and let the existing retry-from-checkpoint mechanism handle recovery. |
| 12 | **Approval host crash**: on startup, restore the in-memory waiter from the persisted pending record. The run stays in `AwaitingInput` indefinitely until answered or the orchestration's overall timeout fires. | Approval has no agent session to lose. |
| 13 | **Engine tool is opt-in** per Prompt step via `enableTools: [request_user_input]`; orchestration default via `defaultEnableTools`. | Existing pipelines see zero behavior change. LLM cannot start asking questions on a previously-deterministic pipeline. |
| 14 | **CLI v1 ships full**: `orchestra pending`, `orchestra respond`, plus an SSE consumer for `orchestra run` with interactive prompting on `awaiting-input`, and an `orchestra attach <orchestration> <runId>` command for re-attaching to a live run. Auto-falls back to print-and-exit-2 when stdin is non-interactive. | Streaming `run` is overdue anyway; HITL needs it. |

---

## Architecture

### Pause flow — engine tool (LLM-decided)

```
LLM in Prompt step calls orchestra_request_user_input(prompt, choices?, reply_required?)
        │
        ▼
RequestUserInputTool.Execute(args, EngineToolContext ctx)
   1. PendingInputStore.SaveAsync(runId, stepName, prompt, choices, kind=EngineTool)
   2. Reporter.ReportAwaitingInput(...)         ──► SSE event "awaiting-input"
   3. HookRuntime.RunHooksAsync(step.awaitingInput, payload incl. respondUrl)
   4. Waiter.BeginWait(runId)                    ──► clock-pause start
   5. var resp = await Waiter.WaitAsync(runId, stepName, ct)
                                          ↑ ct = step.CancellationToken
                                            (linked to caller / orchestrationTimeout / orchestrationComplete / stepTimeout)
   6. Waiter.EndWait(runId)                      ──► clock-pause stop
   7. PendingInputStore.DeleteAsync(...)
   8. Reporter.ReportInputReceived(...)
   9. return resp.Reply ?? resp.Choice          ──► tool result string
        │
        ▼
LLM resumes its turn with the answer as a tool result and continues.
```

The step's existing `timeoutSeconds` (or orchestration's `defaultStepTimeoutSeconds` / `timeoutSeconds`) still bounds the wait through the linked CTS chain — no new timeout primitive is added.

### Pause flow — Approval step (declarative)

```
ApprovalStepExecutor.ExecuteAsync
   1. PendingInputStore.SaveAsync(runId, stepName, prompt, choices, kind=Approval)
   2. Reporter.ReportAwaitingInput(...)
   3. HookRuntime.RunHooksAsync(step.awaitingInput, payload incl. respondUrl)
   4. Waiter.BeginWait(runId)
   5. var resp = await Waiter.WaitAsync(runId, stepName, ct)
   6. Waiter.EndWait(runId)
   7. PendingInputStore.DeleteAsync(...)
   8. Reporter.ReportInputReceived(...)
   9. return ExecutionResult.Succeeded(content: resp.Reply ?? resp.Choice)
```

Difference from the engine tool: when the host restarts mid-wait, the OrchestrationExecutor is no longer running the step in-memory. The `Approval` executor's response handling is therefore split:

- If the host is still running when the response arrives, the in-memory TCS completes and the executor returns normally.
- If the host restarted, the persistent record is detected on startup and the run is left `AwaitingInput`. When the response arrives, the host calls `OrchestrationExecutor.ResumeAsync` with the response seeded as the step's `ExecutionResult.Content`.

### Resume flow when host restarted (Approval path)

```
POST /api/orchestrations/{name}/runs/{runId}/respond  { stepName, choice?, reply? }
        │
        ▼
HumanInputApi.Respond
   1. PendingInputStore.GetAsync(runId, stepName) → must exist
   2. Try Waiter.Complete(runId, stepName, response)
        ├── alive → TCS completes; executor finishes the step normally
        └── not alive (Approval, host restarted):
              a. Load CheckpointData
              b. Build synthetic step result for stepName with content = response
              c. Replace/insert into checkpoint and call ResumeAsync
   3. Delete PendingInputRecord
   4. Reporter.ReportInputReceived
```

### Clock-pause plumbing

The `IHumanInputWaiter` exposes `BeginWait(runId)` / `EndWait(runId)` lifecycle calls. The `OrchestrationExecutor` registers a callback that:

1. On `BeginWait`: records `_waitStart[runId] = utcNow`.
2. On `EndWait`: computes `elapsed = utcNow - _waitStart[runId]`; accumulates `_totalWaitElapsed[runId] += elapsed`; if `pauseTimeoutDuringWait` is true, re-arms the orchestration timeout CTS via `CancelAfter(originalDeadline + totalWaitElapsed - alreadyElapsed)`.

For Approval-with-restart: the `_totalWaitElapsed` is persisted into the checkpoint as `TotalWaitElapsedTicks` so the resumed run starts with the correct accumulated offset.

---

## State Machine Additions

| Existing | New |
|---|---|
| `ExecutionStatus`: `Pending, Running, Succeeded, Failed, Skipped, Cancelled, NoAction` | `+ AwaitingInput` |
| `CancellationCause`: `OrchestrationTimeout, OrchestrationComplete, External, HostShutdown` | `+ AwaitingInputTimeout, + HostShutdownDuringWait` |
| Hook events: `step.success, step.failure, step.after, orchestration.success, orchestration.failure, orchestration.after` | `+ step.awaitingInput` |
| SSE event types | `+ awaiting-input, + input-received, + input-timeout` |

---

## File-Level Change Set

### `src/Orchestra.Engine/`

| File | Change |
|---|---|
| `Orchestration/Steps/OrchestrationStepType.cs` | + `Approval` |
| `Orchestration/Steps/ApprovalOrchestrationStep.cs` | new — `Prompt`, `Choices`, `OnTimeout`, `DefaultResponse` |
| `Orchestration/Steps/PromptOrchestrationStep.cs` | + `EnableTools: string[]` |
| `Orchestration/Orchestration.cs` | + `PauseTimeoutDuringWait: bool = true`, `DefaultEnableTools: string[]` |
| `Orchestration/Executor/ApprovalStepExecutor.cs` | new |
| `Orchestration/Executor/ExecutionStatus.cs` | + `AwaitingInput` |
| `Orchestration/Executor/IHumanInputWaiter.cs` | new |
| `Orchestration/Executor/OrchestrationExecutor.cs` | branch for Approval; clock-pause callbacks; resume-seed for Approval |
| `EngineTools/RequestUserInputTool.cs` | new |
| `EngineTools/EngineToolContext.cs` | + `RunId`, `OrchestrationName`, `Waiter`, `PendingInputStore`, `Reporter`, `HookRuntime`, `RespondUrlBuilder` accessors |
| `EngineTools/EngineToolRegistry.cs` | conditional registration based on `EnableTools` |
| `Storage/IPendingInputStore.cs` + `PendingInputRecord.cs` + `PendingInputKind.cs` + `UserInputResponse.cs` | new |
| `Storage/CheckpointData.cs` | + `PendingInput` (Approval), + `TotalWaitElapsedTicks` |
| `Reporting/IOrchestrationReporter.cs` | + `ReportAwaitingInput`, `ReportInputReceived`, `ReportInputTimeout` (default no-ops) |
| `Orchestration/CancellationDetails.cs` (or wherever `CancellationCause` is) | + `AwaitingInputTimeout`, `HostShutdownDuringWait` |
| `Orchestration/Hooks/HookEvent.cs` | + `step.awaitingInput` |
| `Orchestration/Hooks/HookRuntime.cs` | payload includes `respondUrl`, `awaitingInputKind`, `prompt`, `choices` |
| `Serialization/OrchestrationParser.cs` (and parser sibling) | parse `Approval` step + new fields |

### `src/Orchestra.Host/`

| File | Change |
|---|---|
| `Persistence/FileSystemPendingInputStore.cs` | new |
| `Persistence/InMemoryHumanInputWaiter.cs` | new |
| `Api/HumanInputApi.cs` | new — `POST /api/orchestrations/{name}/runs/{runId}/respond`, `GET /api/runs/pending`, `GET /api/orchestrations/{name}/runs/{runId}/pending/{stepName}` |
| `Api/SseReporter.cs` | emit `awaiting-input`, `input-received`, `input-timeout` |
| `Extensions/EndpointRouteBuilderExtensions.cs` | wire endpoints |
| `Extensions/ServiceCollectionExtensions.cs` | register store + waiter; startup orphan handling for engine-tool waits and Approval re-arm |

### `src/Orchestra.Cli/`

| File | Change |
|---|---|
| `OrchestraClient.cs` | + `GetPendingAsync`, `RespondAsync`, `OpenRunStreamAsync`, `OpenAttachStreamAsync`; convert `RunOrchestrationAsync` to consume SSE (now done via `RunSession`) |
| `Program.cs` | + `pending`, `respond`, `attach` commands; streaming `run` with `--no-interactive`, `--quiet`, `--verbose`, `--by` |
| `Sse/SseStreamReader.cs` | + spec-compliant SSE frame parser for the CLI |
| `Run/RunSession.cs` | + dispatch loop with HITL prompting (Spectre `SelectionPrompt` / `TextPrompt`) |

### Schemas / docs / examples

- `schemas/orchestration.schema.json` — `Approval` step shape, `enableTools`, `pauseTimeoutDuringWait`, hook event
- `orchestration-composing.md` — new section on HITL
- `skills/orchestration-authoring/` — schema reference update
- `examples/hitl-approval-deploy.yaml` — declarative example
- `examples/hitl-engine-tool-clarify.yaml` — engine-tool example

### Tests (per `AGENTS.md`: full coverage required before "done")

| Suite | Cases |
|---|---|
| Engine unit | `Approval` step parses with/without choices and timeout |
| Engine unit | `RequestUserInputTool` blocks on waiter, returns answer string |
| Engine unit | `ResumeAsync` seeds Approval response into the right step result |
| Engine unit | Orchestration timeout clock pauses while in `AwaitingInput` (config on/off) |
| Host integration | E2E declarative: register → run → SSE awaiting-input → POST respond → completes |
| Host integration | E2E engine-tool: prompt with `enableTools` → tool fires SSE → respond → tool returns answer → step completes |
| Host integration | E2E timeout `fail` (default) — step Failed with `AwaitingInputTimeout` |
| Host integration | E2E timeout `defaultResponse` — step Succeeded with default value |
| Host integration | E2E timeout `cancel` — orchestration cancelled |
| Host integration | E2E hook: `step.awaitingInput` hook fires with `respondUrl` and `prompt` payload |
| Host integration | Restart resilience (Approval): pause → simulate restart → respond → resume |
| Host integration | Restart resilience (engine-tool): pause → simulate restart → run marked `Failed (HostShutdownDuringWait)` |
| Host integration | Concurrent: two runs awaiting input on same orchestration |
| CLI integration | `orchestra pending` lists waiting runs |
| CLI integration | `orchestra respond` with `--choice` and with `--reply` |
| CLI integration | `orchestra run` SSE consumer prints events; `--interactive` answers prompts |

All logging uses `ILogger<T>` with source-generated `[LoggerMessage]` attributes per `AGENTS.md`.

---

## Author-Facing Examples

### Declarative gate

```yaml
- name: review-deploy
  type: Approval
  prompt: "Approve deploy of {{param.service}} to {{param.env}}?"
  choices: [approve, reject]
```

### Engine tool — agent decides

```yaml
- name: writer
  type: Prompt
  systemPrompt: |
    You write articles. If anything is ambiguous, call orchestra_request_user_input
    to ask the user — they'll respond and you'll get the answer in your tool result.
  userPrompt: "Write an article about {{param.topic}}"
  model: claude-opus-4.6
  enableTools: [request_user_input]
```

### Hook — Slack notification

```yaml
hooks:
  - name: slack-on-pause
    on: step.awaitingInput
    payload: { detail: compact, includeRefs: true }
    action:
      type: script
      shell: pwsh
      script: |
        $payload = $input | ConvertFrom-Json
        $body = @{ text = "[$($payload.orchestration.name)] needs input on '$($payload.step.name)': $($payload.respondUrl)" } | ConvertTo-Json
        Invoke-RestMethod -Uri $env:SLACK_WEBHOOK -Method Post -Body $body -ContentType 'application/json'
```

---

## Implementation Order

1. **Engine** — status, cancellation cause, hook event, pending types, waiter interface, EngineToolContext extensions, ApprovalStep + executor, RequestUserInputTool, parser, Orchestration fields, OrchestrationExecutor branches and clock-pause hooks.
2. **Host** — pending store, in-memory waiter, HumanInputApi, SseReporter events, endpoint registration, startup orphan handling.
3. **Hooks** — payload extension with `respondUrl` and `awaitingInputKind`.
4. **Tests** — engine units → host integration → restart resilience → clock-pause → hooks.
5. **CLI** — SSE consumer + `pending` + `respond` + tests.
6. **Schemas + docs + examples**.
7. **Build + run all tests**. Server is not left running.

---

## Portal UI

Surfaces pending HITL waits in the Portal so users can triage and respond without dropping to the CLI.

### Live updates — push via dashboard SSE

- `DashboardEventBroadcaster` gains `BroadcastAwaitingInput`, `BroadcastInputReceived`, `BroadcastInputTimeout` (mirrors the per-execution event payloads).
- `SseReporter` calls these alongside its existing `Write(...)` so the same data lands on both the per-run stream (CLI / ExecutionModal) and the dashboard stream (Portal).
- `SseReporterFactory` resolves the broadcaster from DI; manual construction (unit tests) keeps it null and the fan-out is silently skipped.

### React state

- `usePendingInputs()` hook owns the canonical list. Loads from `GET /api/runs/pending` on mount; mutated by App.tsx forwarding `awaiting-input` / `input-received` / `input-timeout` events from its existing single `useDashboardEvents` subscription. Records are deduped by composite key `orchestrationName|runId|stepName`.
- `identity.ts` persists a Portal-level `respondedBy` display name in `localStorage` (key `orchestra.portal.respondedBy`). Purely advisory — no auth.
- `useDashboardEvents` extended with `onAwaitingInput` / `onInputReceived` / `onInputTimeout` handlers.

### UI surfaces

- **Sidebar button + count badge** ("Waiting for Input") next to MCP Tools / Visual Builder. Badge shows live count.
- **WaitingInputsModal** (modeled on `HistoryModal`): left list of pending records, right pane response form. Choices render as a radio group; reply field is always available; submit posts to `POST /api/orchestrations/{name}/runs/{runId}/respond?step={step}`. 404 is treated as a known race ("already resolved") and removes the record from the list.
- **`ActiveOrchestrationCard` "Waiting" chip** appears on a running card whose `executionId` is in the pending set (recall `runId == executionId` for active runs).

### Files changed

| Path | Change |
| --- | --- |
| `src/Orchestra.Host/Api/DashboardEventBroadcaster.cs` | + `BroadcastAwaitingInput/InputReceived/InputTimeout` |
| `src/Orchestra.Host/Api/SseReporter.cs` | optional `DashboardEventBroadcaster` ctor + fan-out from HITL methods |
| `src/Orchestra.Host/Api/SseReporterFactory.cs` | DI ctor wiring broadcaster through |
| `tests/Orchestra.Server.Tests/DashboardEventsHitlTests.cs` | 7 tests for fan-out + reporter wiring |
| `playground/.../portal/src/types.ts` | `PendingInputRecord`, `HumanInputResponse`, `HumanInputKind` |
| `playground/.../portal/src/identity.ts` (+ `.test.ts`) | `respondedBy` localStorage helpers |
| `playground/.../portal/src/hooks/useDashboardEvents.ts` | HITL handlers |
| `playground/.../portal/src/hooks/usePendingInputs.ts` (+ `.test.ts`) | canonical waiting-list state |
| `playground/.../portal/src/components/modals/WaitingInputsModal.tsx` (+ `.test.tsx`) | modal + response form |
| `playground/.../portal/src/components/ActiveOrchestrationCard.tsx` | new optional `awaitingInput` prop + "Waiting" chip |
| `playground/.../portal/src/App.tsx` | hook usage, sidebar button, modal render, plumb dashboard SSE → hook |
| `playground/.../portal/src/icons.tsx` | new `Hand` icon |
| `playground/.../portal/src/App.css` | `.waiting-inputs-*` styles |
| `tests/Orchestra.Portal.E2E/WaitingInputsUiTests.cs` | Playwright happy path: register Approval → wait for badge → submit → run resolves |
