# Orchestration step deep-dive

The `Orchestration` step type invokes another registered orchestration from inside a parent
orchestration. It's the building block for composition, fan-out, and self-healing patterns.

This document covers:

- Semantics (sync vs. async, cancellation, timeouts)
- Template binding reference for parent-side drill-in
- Self-healing patterns (in-process via templates vs. MCP-driven)
- Header stamping for the data-plane MCP
- Decision flowchart: "I need X about a child run — where do I look?"

## Step definition

```yaml
- name: invoke-child
  type: Orchestration
  dependsOn: [previous-step]
  orchestration: child-orch-name      # required; supports template expressions
  mode: sync                          # sync (default) or async
  timeoutSeconds: 600                 # sync-mode hard cap; ignored in async
  parameters:                         # values support template expressions
    projectPath: "{{param.projectPath}}"
    previousOutput: "{{previous-step.output}}"
  inputHandlerPrompt: |               # optional LLM-based parameter shaping
    Transform the inputs into a JSON object mapping parameter names to string values.
  inputHandlerModel: claude-opus-4.6  # optional model override
```

## Execution modes

### Sync (default)

The parent step blocks until the child reaches a terminal state (Succeeded, Failed, Cancelled).
The parent step's result reflects the child's outcome:

| Child status | Parent step status | Parent step content |
|---|---|---|
| `Succeeded` | `Succeeded` | child's `FinalContent` (terminal-step summary) |
| `Failed` | `Failed` | child's top-level `ErrorMessage` |
| `Cancelled` | `Failed` (treated as a failure for orchestration purposes) | child's `ErrorMessage` or default "Cancelled" |

On every terminal branch the parent step's `ExecutionResult.ChildOrchestrationInfo` is
populated with the child's full per-step results, executionId, status, completionReason,
and cancellation cause. This is what enables the template bindings below.

**Cancellation propagation:** if the parent is cancelled, the child is cancelled too. The
child's `CancellationDetails.Detail` will include `"propagated from parent <id> (step: <name>)"`.

**Timeout:** when sync-mode `timeoutSeconds` fires, the parent observes `TimedOut: true` on
the result. The child is cancelled with `CancellationDetails.Kind = SyncInvokeTimeout`.

### Async

The parent step returns immediately with a small JSON dispatch summary. The child runs to
completion in the background and persists its own run record.

```json
{
  "executionId": "...",
  "orchestrationId": "...",
  "orchestrationName": "...",
  "status": "dispatched",
  "startedAt": "..."
}
```

`{{step.output}}` is this JSON blob. Use the template accessors below (especially
`{{step.executionId}}`) to capture the child's id for follow-up polling via MCP.

`ChildOrchestrationInfo` is populated with `Status = Pending` (surfacing as `"pending"` in
templates) and an empty `StepResults` map. The async dispatch can't drill into per-step
data — only the MCP tools can, once the child progresses.

## Template binding reference

For any step `S` of type `Orchestration`, every step that depends on `S` (directly or
transitively via `DependsOn`) can use these expressions.

### Top-level accessors

| Expression | Resolves to | Notes |
|---|---|---|
| `{{S.output}}` | Child's `FinalContent` on success, or `ErrorMessage` on failure | Backward-compatible; existing orchestrations keep working |
| `{{S.rawOutput}}` | Same as `output` for orchestration steps | No separate raw-vs-processed distinction at this level |
| `{{S.executionId}}` | Child run's execution id (string) | Always present; usable with `get_orchestration_status` / `get_orchestration_step` |
| `{{S.status}}` | Lowercase child status (`succeeded`/`failed`/`cancelled`/`pending`) | `pending` is the async-dispatch case |
| `{{S.errorMessage}}` | Top-level error string from the child | Empty string when child succeeded |
| `{{S.completionReason}}` | `orchestra_complete` reason when child completed early | Empty string otherwise |
| `{{S.childResult}}` | JSON blob of executionId, status, errorMessage, finalContent, completionReason, cancellation, stepResults | Use in Transform templates to embed the full child for downstream tooling |
| `{{S.steps}}` | JSON map of all child-step results | Each entry has status, output, rawOutput, error, files |

### Per-child-step accessors

For any child step name `X` in the child orchestration:

| Expression | Resolves to | Notes |
|---|---|---|
| `{{S.steps.X.output}}` | Child step X's `Content` (post-output-handler) | Untruncated. Available even when the overall child run failed |
| `{{S.steps.X.rawOutput}}` | Child step X's `RawContent ?? Content` | Pre-output-handler content |
| `{{S.steps.X.error}}` | Child step X's `ErrorMessage` | Empty when the child step succeeded |
| `{{S.steps.X.status}}` | Lowercase status of child step X | `succeeded`/`failed`/`cancelled`/`skipped`/`noaction` |
| `{{S.steps.X.files}}` | JSON array of files saved by child step X | Empty array `[]` when nothing was saved |
| `{{S.steps.X.files[N]}}` | Nth saved file path (0-indexed) | Empty string when index is out of range |
| `{{S.steps.X}}` (no leaf) | JSON of one child step (status, output, rawOutput, error, files) | Same shape as a single entry from `{{S.steps}}` |

### Things to know

- **Available on every terminal branch.** Failed and cancelled child runs populate the same
  bindings, so a downstream Prompt step can inspect per-step errors and partial successes
  without conditional logic.
- **In-process, no truncation.** All accessors read from the in-memory
  `ChildOrchestrationInfo` populated during the parent's run. Step content of any size is
  available verbatim — there is no truncation, no chunking.
- **Lifetime is the parent run.** The bindings work only while the parent is executing.
  After the parent run persists and `ActiveExecutionInfo` is removed, the data lives in
  the child's own `run.json` and is fetchable via the data-plane MCP tools.
- **Validation at registration time.** The C# `TemplateExpressionValidator` rejects:
  - Child accessors used on non-orchestration steps (`{{prompt-step.executionId}}` → error).
  - Unknown leaves (`{{S.steps.X.bogus}}` → error).
  - References to non-existent step names (same rule as `{{stepName.output}}`).
  - Unreachable references via `DependsOn` (same rule as `{{stepName.output}}`).

### Example

```yaml
- name: attempt-1
  type: Orchestration
  orchestration: build-and-test
  mode: sync

- name: repair
  type: Prompt
  dependsOn: [attempt-1]
  systemPrompt: |
    Previous attempt {{attempt-1.executionId}} terminated with status {{attempt-1.status}}.
  userPrompt: |
    ## Build step output
    {{attempt-1.steps.build.output}}

    ## Test step error (if any)
    {{attempt-1.steps.test.error}}

    ## All child step records (JSON)
    {{attempt-1.steps}}
```

See [`examples/self-healing-with-child-bindings.yaml`](../examples/self-healing-with-child-bindings.yaml)
for a complete working example.

## Self-healing patterns

### Pattern 1 — In-process via template bindings

Use when the parent orchestration itself can express the repair logic in a finite DAG.

```yaml
steps:
  - name: attempt-1
    type: Orchestration
    orchestration: target-pipeline
    mode: sync
  - name: repair-prompt
    type: Prompt
    dependsOn: [attempt-1]
    systemPrompt: "If the previous attempt failed, produce a repair instruction set."
    userPrompt: |
      Status: {{attempt-1.status}}
      Error:  {{attempt-1.errorMessage}}
      Build:  {{attempt-1.steps.build.output}}
      Test:   {{attempt-1.steps.test.error}}
```

**Pros:** zero MCP round-trips; untruncated data; works for failed/cancelled children; static
DAG is straightforward to reason about.

**Cons:** the number of attempts is bounded by the number of steps in the parent. For
unbounded retry loops you need pattern 2.

### Pattern 2 — MCP-driven controller (unbounded loop)

Use when the number of attempts is dynamic (e.g., "keep trying until success criteria match,
up to N attempts"). The controller is a single Prompt step that owns the entire loop and
uses the data-plane MCP to spawn / observe / cancel children.

```yaml
mcps:
  - name: orchestra
    type: remote
    endpoint: "{{server.url}}/mcp/data"

steps:
  - name: controller
    type: Prompt
    model: claude-opus-4.6
    mcps: [orchestra]
    infiniteSessions:
      enabled: true
    systemPrompt: |
      Spawn attempts via invoke_orchestration. Inspect their per-step outputs via
      get_orchestration_status (detail="compact") and get_orchestration_step. Use
      list_child_runs (no args — auto-scoped to your subtree) to track in-flight attempts.
      Stop when an attempt satisfies success criteria, or after N attempts.
```

**Pros:** unbounded loop logic; LLM decides when to stop.

**Cons:** content travels over MCP (subject to detail-level truncation unless you fetch via
`get_orchestration_step` with `length: -1`); each tool call is a round-trip.

See [`examples/list-child-runs-fan-out-controller.yaml`](../examples/list-child-runs-fan-out-controller.yaml)
and the system's `run-self-healing.yaml` orchestration for full examples.

## Header stamping (for the curious)

When a parent orchestration uses an `mcps:` entry that targets this Orchestra host's
`/mcp/data` endpoint, the engine automatically stamps four HTTP headers on the outbound
request so the server-side MCP tools can identify the caller:

| Header | Value |
|---|---|
| `X-Orchestra-Parent-Execution-Id` | The parent run's `runId` |
| `X-Orchestra-Parent-Orchestration-Name` | The parent run's orchestration name |
| `X-Orchestra-Parent-Step-Name` | The step whose agent owns the connection |
| `X-Orchestra-Root-Execution-Id` | The root of the nesting chain (equals parent id for top-level parents) |

Consequences:

- `invoke_orchestration` auto-populates `parentExecutionId` from these headers, so spawned
  children inherit the parent/root lineage automatically.
- `list_child_runs` with no arguments scopes to the caller's whole subtree (header-resolved
  `rootExecutionId`).
- External MCP clients (no headers) must pass `parentExecutionId` or `rootExecutionId`
  explicitly to `list_child_runs` — the tool refuses to enumerate without a scope, so
  external agents can't leak unrelated runs.

## Decision flowchart

**"I have a parent orchestration; I need data about its child."**

```
Are you writing the PARENT orchestration definition?
└── Use template bindings: {{S.executionId}}, {{S.steps.X.output}}, etc.
    (In-process, untruncated, works for failed/cancelled children.)

Are you an external MCP client / Portal / CLI?
├── Need overall run status?
│   └── get_orchestration_status(executionId)
│       Includes nesting block, completedStepNames (active runs).
├── Need one step's full content (run completed or step done in an active run)?
│   └── get_orchestration_step(executionId, stepName, length=-1)
│       Response: source ("active"|"persisted") + runStatus (overall run status).
├── Need to enumerate runs in your execution chain?
│   └── list_child_runs()   (no args; header-scoped to your subtree)
│       Filter by status to limit to active / failed / etc.
├── Need a paginated read of one large step?
│   └── get_orchestration_step(executionId, stepName, offset, length)
│       Stitch consecutive calls until truncated == false.
└── Step is currently running, can't wait?
    └── get_orchestration_step returns error="step-in-flight" + completedStepNames
        list so you know which siblings you CAN drill into right now.
```

## Related references

- [Engine reference: template expressions](engine.md#template-expressions)
- [Host reference: data-plane MCP tools](host.md#data-plane-tools)
- [REST API: GET /api/history/{name}/{runId}](api-reference.md)
- [Run storage layout](run-storage.md)
