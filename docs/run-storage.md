# Run storage layout

Every orchestration run is persisted to disk so it can be inspected, retried, replayed, or
audited later. This document explains the on-disk layout, what each file contains, and the
design decisions behind the format — in particular, how parent → child orchestration links
are persisted.

## Where runs live

Run storage is rooted at `<dataPath>/executions/`, where `dataPath` is the host-side data
directory configured via `OrchestrationHostOptions.DataPath` (or `--data-path` on the CLI).

Each run gets its own folder:

```
<dataPath>/executions/
└── <orchestration-name>/
    └── <orchestration-name>_<version>_<trigger>_<timestamp>_<execution-id>/
        ├── orchestration.json
        ├── run.json
        ├── <step-name>-inputs.json
        ├── <step-name>-outputs.json
        ├── <step-name>-result.json
        ├── <step-name>-iteration-N-inputs.json     (loops only)
        ├── <step-name>-iteration-N-outputs.json
        ├── <step-name>-iteration-N-result.json
        └── result.md
```

Folder names are sanitized: any filesystem-invalid characters are replaced with `_`. The
canonical id of a run is its `runId` (also referred to as `executionId`) — the suffix at
the end of the folder name.

## What each file contains

| File | Purpose | Typical size |
|---|---|---|
| `orchestration.json` | Snapshot of the orchestration definition at the moment the run started. Useful for audits and for replaying a run against the exact definition it ran against. | 1–20 KB |
| `run.json` | **The canonical record.** A `OrchestrationRunRecord` containing identification, status, all step records (final and per-iteration), final content, lineage, hook executions, and aggregate token usage. | 5 KB – 400+ KB |
| `<step>-inputs.json` | The step's `Parameters`, `RawDependencyOutputs`, and `PromptSent` (for Prompt steps). Human-readable projection of inputs. | 1–50 KB |
| `<step>-outputs.json` | The step's `Content`, `RawContent`, model metadata, `Usage`, and `SavedFiles`. Human-readable projection of outputs. | 1 KB – 100+ KB |
| `<step>-result.json` | Status, timing, and `ErrorMessage`. Quick top-of-the-stack view. | <1 KB |
| `result.md` | The run's final markdown content (`FinalContent`) when non-empty. Rendered for human readers. | depends |

The sidecar `-inputs.json` / `-outputs.json` / `-result.json` files are **redundant projections**
of subsets of `run.json`. They exist for human inspection and external tooling that prefers
to read per-step files directly. The canonical reader path (`FileSystemRunStore.GetRunAsync`)
always reads from `run.json`.

## `run.json` shape

`run.json` is a serialized `OrchestrationRunRecord` (see `src/Orchestra.Engine/Storage/OrchestrationRunRecord.cs`).
The top-level fields are:

### Identification & outcome
| Field | Type | Purpose |
|---|---|---|
| `runId` | string | Unique execution identifier |
| `orchestrationName` | string | The orchestration's `name` field |
| `orchestrationVersion` | string | Version at run start (snapshot) |
| `triggeredBy` | string | `manual`, `scheduler`, `webhook`, `loop`, or `orchestration:<parent>` |
| `triggerId` | string? | Trigger that initiated this run (null for manual) |
| `startedAt` / `completedAt` | DateTimeOffset | Wall-clock bounds |
| `status` | enum | Terminal status: `Succeeded`, `Failed`, `Cancelled`, `Skipped`, `NoAction` |

### Inputs & outputs
| Field | Type | Purpose |
|---|---|---|
| `parameters` | dictionary | Inputs at start time |
| `finalContent` | string | Terminal content (the `result.md` content) |
| `savedFiles` | string[] | Full paths saved via `orchestra_save_file` |
| `totalUsage` | TokenUsage? | Aggregated tokens across all steps |

### Step records (the bulk of the file)

| Field | Type | Purpose |
|---|---|---|
| `stepRecords` | dict<name, StepRunRecord> | Final record per step name (latest iteration for loops) |
| `allStepRecords` | dict<name-or-`name:iteration-N`, StepRunRecord> | Every iteration kept separately |

### Completion & cancellation
| Field | Type | Purpose |
|---|---|---|
| `completionReason` | string? | Set when `orchestra_complete` ended the run early |
| `completedByStep` | string? | Step that triggered early completion |
| `isIncomplete` | bool | True when all terminal steps were NoAction/Skipped, or run was completed early |
| `cancellation` | CancellationDetails? | Structured cause when `status == Cancelled` |

### Hooks & retries
| Field | Type | Purpose |
|---|---|---|
| `hookExecutions` | HookExecutionRecord[] | Lifecycle hook records (event, status, duration, content) |
| `retriedFromRunId` | string? | Source run id when this run is a retry |
| `retryMode` | string? | `"failed"`, `"all"`, or `"from-step:<name>"` |

### Parent / nesting lineage
| Field | Type | Purpose |
|---|---|---|
| `parentExecutionId` | string? | Immediate parent's runId (null for top-level) |
| `parentStepName` | string? | Parent step that invoked this run |
| `rootExecutionId` | string? | Top-of-chain runId (equals `runId` for top-level) |
| `nestingDepth` | int | 0 = top-level, 1 = direct child, … |

### Context
| Field | Type | Purpose |
|---|---|---|
| `context` | RunContext? | Resolved variables, accessed env vars, data directory |

## `StepRunRecord` shape

Each entry in `stepRecords` / `allStepRecords` is a serialized `StepRunRecord`
(see `src/Orchestra.Engine/Storage/StepRunRecord.cs`). The fields:

### Identification & timing
- `stepName`, `status`, `startedAt`, `completedAt`, `duration` (computed)
- `loopIteration` (null for non-loop steps; 0+ for iterations)

### Content
- `content` — final output (after output handler). **Often the largest field per step.**
- `rawContent` — pre-output-handler content (null when no handler ran)
- `errorMessage`, `errorCategory` — failure detail (null on success)

### Inputs at execution time
- `parameters` — actual values that were injected
- `rawDependencyOutputs` — what `{{dep.rawOutput}}` would have produced
- `promptSent` — final prompt after substitutions (Prompt steps only)

### Model & usage (Prompt steps)
- `actualModel`, `selectedModel` — runtime model attribution
- `requestedModelInfo`, `selectedModelInfo`, `actualModelInfo` — SDK metadata trios
- `usage` — token counts

### Diagnostics
- `trace` — full conversation history, tool calls, audit log, MCP statuses, etc.
  **Frequently the largest single field** when populated.
- `savedFiles` — files this step saved via `orchestra_save_file`
- `retryHistory` — per-attempt error records when retries happened

### Child orchestration link (Orchestration steps only)
- `childExecutionId` — execution id of the child run this step invoked
- `childOrchestrationName` — the child orchestration's name
- `childStatus` — the child's terminal status (`succeeded`/`failed`/`cancelled`/…)

For non-Orchestration steps these three fields are null and elided from the JSON.

## Typical sizes

Measured from `artifacts/portal-publish/data/executions/` across 502 sample runs:

| Statistic | `run.json` size |
|---|---|
| Min | ~600 bytes (no-op / NoAction runs) |
| Mean | ~105 KB |
| Max | ~400 KB |

The bulk of the size comes from:

1. **Per-step `trace.conversationHistory`** — multi-turn Prompt conversations can carry
   hundreds of messages plus tool-call audit entries.
2. **Per-step `content`** — agent outputs are unbounded; Command/Script step stdout can
   be megabytes.
3. **`hookExecutions[*].content`** — hook scripts that emit large summaries.

These are the fields the data-plane MCP's `detail` parameter is designed to gate.

## Child step content persistence — the design decision

When a parent orchestration uses a `type: Orchestration` step to invoke a child, the
**parent's `run.json` stores ONLY a pointer to the child** — three small fields on
`StepRunRecord`:

```json
{
  "stepName": "invoke-child",
  "status": "Succeeded",
  "content": "child final content",
  "childExecutionId": "abc-123",
  "childOrchestrationName": "build-and-test",
  "childStatus": "Succeeded"
}
```

The child's full per-step data lives in the **child's own folder** at
`<dataPath>/executions/<child-orch-name>/.../run.json`.

### Why a pointer instead of an inline copy?

1. **Storage economy.** Inlining doubles the disk usage for every nested run. With deep
   nesting (e.g., a self-healing controller that spawns 5 attempts, each of which spawns
   3 sub-children), inline copies would 6–10× the storage cost. Pointer references stay
   constant regardless of depth.
2. **Single source of truth.** When a consumer queries the data-plane MCP
   (`get_orchestration_status` / `get_orchestration_step`), the lookup is always against
   the child's own `run.json`. A copy embedded in the parent would risk staleness or
   divergence after retries / replays.
3. **Schema simplicity.** Each `run.json` describes one run. Consumers don't need to
   recursively walk a tree of inlined children to reconstruct what happened.
4. **Replay support.** Future replay tooling can rehydrate the parent's
   `ChildOrchestrationInfo` on demand by reading the child's `run.json` once. That's a
   single disk read per orchestration step — well within budget.

### What about the parent's templates?

While the parent is **running**, its in-memory `ExecutionResult.ChildOrchestrationInfo`
DOES carry the child's per-step `Content`/`RawContent`/`ErrorMessage`/`SavedFiles`. That's
what makes `{{step.steps.X.output}}` work without truncation, without an MCP call. This
data is held in memory for the duration of the parent run and discarded when the parent
finishes — at which point the child's persisted `run.json` becomes the canonical source.

### Trade-off acknowledged

If a child run's folder is deleted while a parent is being analyzed **retrospectively**
(e.g., loading a parent's `run.json` after a host restart and trying to drill into a
child via the data-plane MCP), the lookup will fail with a "no run found" error from
`FindRunByIdAsync`. Mitigations:

- Don't delete child runs while their parents are still actively referenced.
- Treat orchestration trees as a single retention unit — if you delete a parent, delete
  its descendants too (a future retention policy could enforce this automatically).

The same trade-off applies to `retriedFromRunId`: if you delete the source run, the
retry's lineage is broken.

## Reading and writing

- **Writer:** `FileSystemRunStore.SaveRunAsync(record, orchestration?, ct)` writes all the
  files atomically per orchestration directory (using a per-orchestration `SemaphoreSlim`
  to avoid Windows file locking conflicts under concurrent saves).
- **Reader:** `FileSystemRunStore.GetRunAsync(orchestrationName, runId)` deserializes
  `run.json` and returns the full `OrchestrationRunRecord`. Other methods (
  `GetRunSummariesAsync`, `FindRunByIdAsync`, `FindChildRunsAsync`) read from the
  in-memory index that's populated by scanning all `run.json` files at startup.

## Related references

- [Engine reference: storage layer](engine.md)
- [Host reference: data plane MCP tools](host.md#data-plane-tools)
- [REST API: GET /api/history/{name}/{runId}](api-reference.md)
- [Orchestration step deep-dive](orchestration-step-deep-dive.md)
- Source: `src/Orchestra.Host/Persistence/FileSystemRunStore.cs`
- Source: `src/Orchestra.Engine/Storage/OrchestrationRunRecord.cs`
- Source: `src/Orchestra.Engine/Storage/StepRunRecord.cs`
