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

## Run annotations

Everything under `executions/` is an immutable record of what happened. User-curated
metadata — **favorite**, **title**, **tags**, **note** — is mutable, so it lives in a
parallel root alongside the other per-run state directories (`checkpoints/`, `pending/`,
`temp/`):

```
<dataPath>/annotations/
└── <orchestration-name>/
    └── <runId>.json
```

Why out-of-band rather than a field on `run.json`: `OrchestrationRunRecord` is entirely
`init`-only, `IRunStore` has no update operation, and re-saving a record to mutate it would
append a **duplicate** entry to the in-memory history index.

Why one file per run rather than a single `run-annotations.json` (the shape used by
`orchestration-tags.json`): orchestrations number in the dozens, runs in the thousands per
year. Annotations are **sparse** — a file exists only for a run you acted on — so file count
tracks annotations, not executions. Per-run files also keep each mutation a ~300-byte atomic
write instead of rewriting one growing blob, and contain corruption to a single record.
Annotations are the only irreplaceable data in the run store, so blast radius matters.

Annotations are merged into history projections at read time, are searchable
(`GET /api/history/search` matches title, tags and note), and are filterable
(`?favorites=true`, `?tags=a,b` with OR semantics).

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

## The run index

Queries against run history (the history panel, search, lineage lookups, retention) are served
from a SQLite index at:

```
<dataPath>/executions/.index.db
```

**It is derived, never authoritative.** Every column is a projection of a `run.json` that remains
plain on disk exactly as before. Delete the file and it is rebuilt; corrupt it and the host
discards it and rebuilds rather than failing to start. Nothing is stored there that cannot be
recomputed.

### Why it exists

The index used to be an in-memory dictionary rebuilt on every process start by deserializing every
`run.json`. On a real store of **5,421 runs / 5,748 MB** that produced roughly twenty scalar fields
per run at a cost of **~7 s of wall clock and the entire 5.7 GB read**, and every CLI command that
spawns a throwaway host paid it too.

Measured on that store, through the CLI:

| | index cost |
|---|---|
| before | **~7 s**, on every host start and every CLI invocation |
| first start after upgrade | ~5.3 s, once |
| every start after that | **~0.15 s** |

The database is ~6 MB. The steady-state win is larger than the ~35x wall-clock ratio suggests: the
I/O drops from 5,748 MB to 6 MB, so the gap widens further whenever the file cache is cold.

### Why a folder path is the key

Run folders are **write-once** — `SaveRunAsync` creates one and never modifies it, and mutable
per-run state (annotations, checkpoints, temp files) lives in separate roots. An index row keyed on
folder path therefore cannot go stale. Only additions and deletions need reconciling, and a
directory walk finds both in ~250 ms without opening a single `run.json`.

Startup does exactly that: walk `executions/`, drop rows whose folder is gone, and project only
folders that are not yet indexed. After the first pass that set is empty.

### Streaming projection

Runs that *do* need projecting are read with a `Utf8JsonReader` that skips the subtrees dominating
the file — per-step `trace`, `conversationHistory` and `content`. Only `allStepRecords` is descended
into, and only for the four fields needed to reproduce the failure summary. This avoids
materializing the whole object graph (recall p99 is 9 MB, with a 52 MB outlier).

### Schema changes

`.index.db` carries a schema version. A mismatch drops the table and rebuilds rather than
migrating — correct by construction for derived data, and cheaper to reason about than a migration
path. The rebuild on the 5,421-run store above takes ~9 s, once.

### Filtering and paging happen in SQL

`QueryRunsAsync(query, offset, limit)` returns one page plus the total number of matches, with
every predicate, the ordering, the count and the page evaluated by SQLite. The history endpoints
used to pull the whole index into memory and filter with LINQ, so a request for 15 rows still
materialized every run in the store and built a run-id dictionary the same size.

Two details the SQL has to get right:

- **The order must be total.** Runs launched in one batch share a start timestamp, and SQLite may
  return tied rows in a different order per query. `LIMIT`/`OFFSET` over an unstable order
  silently repeats some rows and drops others, so the primary key is appended to every
  `ORDER BY`.
- **`origin` is a stored column,** written by `RunOriginClassifier` at index time rather than
  recomputed from `triggeredBy` in SQL, so the C# classifier stays the single definition of what
  an origin is.

### Full-text search over run output

Run names are frequently machine-generated (`ephemeral-efca835904b6-attempt-3`), and annotations
only help for runs someone already went back and labelled. Searching what a run actually
*produced* is the only way to find a run you did not know you would need again — so `.index.db`
also carries an FTS5 index over run content.

What is indexed, measured across a real 5,421-run / 5,748 MB store:

| | size | share of store |
|---|---|---|
| `finalContent` + per-step `content` + error messages | **299 MB** | **5.2%** |
| `trace` subtrees | 4,804 MB | 83.6% |
| `promptSent` | 218 MB | 3.8% |
| `rawContent` | 18 MB | 0.3% |

Traces are skipped — they are the bulk of the store and nobody searches for them. So is
`promptSent`: a prompt is input the user wrote, not a result they are trying to find again.
Content is **stored** in the index rather than merely referenced, which is what allows `snippet()`
to return the matching excerpt with the search terms wrapped in `<mark>`. A list of run ids with
no excerpt is not a usable search result. The cost of that choice is index size: 142 MB rather
than the ~7 MB the metadata alone needs.

Tokenizer is `unicode61` without stemming, because run output is full of identifiers, paths and
log lines where stemming produces matches the user cannot predict. Query words are matched as
prefixes, so `recon` finds `reconciliation`.

**User input never reaches `MATCH` directly.** FTS5 has its own query grammar, so a bare `AND`, an
unbalanced quote or a stray `*` would be a syntax error thrown back at whoever typed it.
`RunIndexQuery.ToFtsQuery` quotes each word into a literal phrase and ANDs them; words containing
no letter or digit are dropped, since they tokenize to nothing and FTS5 rejects that outright.

### When content gets indexed

Two paths, because they have very different costs:

- **As runs complete** — the record is already in memory, so its text is indexed synchronously.
  A run is searchable the moment it finishes.
- **For runs already on disk** (after an upgrade, a restore, or a deleted index) — the text has
  to be read back out of every `run.json`. That is a whole-store read, and it runs **in the
  background** after startup rather than in front of it. Measured on the 5,421-run store: metadata
  4.8 s (blocking), content 9.6 s more (background) with a warm file cache; from a cold cache the
  content pass has been observed at over two minutes. A host that did that work before answering
  its first request would look hung after an upgrade.

Progress is recorded per run in `runs.fts_indexed`, so a host killed part-way resumes where it
stopped instead of starting over, and search simply covers progressively more of the history
while it catches up. Runs whose file is missing or unreadable are marked done rather than retried
on every start.

### Annotations are not indexed

Favorites, tags, titles and notes stay in `annotations/` and are deliberately **not** mirrored
into `.index.db`. The index is derived and deletable; user data is neither, and a copy would need
keeping honest.

Annotation-backed filters are instead resolved against the in-memory annotation map — small,
because only runs a human acted on appear in it — and passed into the query as a set of run ids
that SQL joins against. `?favorites=true` and `?tags=` become an allow-list;
`?favorites=false` asks for the *complement* of a set, which includes every unannotated run, so
it becomes a deny-list instead.

## Reading and writing

- **Writer:** `FileSystemRunStore.SaveRunAsync(record, orchestration?, ct)` writes all the
  files atomically per orchestration directory (using a per-orchestration `SemaphoreSlim`
  to avoid Windows file locking conflicts under concurrent saves).
- **Reader:** `FileSystemRunStore.GetRunAsync(orchestrationName, runId)` deserializes
  `run.json` and returns the full `OrchestrationRunRecord`. Other methods (
  `GetRunSummariesAsync`, `FindRunByIdAsync`, `FindChildRunsAsync`, `QueryRunsAsync`,
  `GetOrchestrationRunStatsAsync`) are answered by the SQLite index described above and never
  open a `run.json`.
- **Lifetime:** `FileSystemRunStore` owns the index handle and implements `IDisposable`. The DI
  container disposes the singleton at shutdown; embedded hosts and tests that construct one
  directly must dispose it, or `.index.db` stays locked.

## Retention and favorites

`RunRetentionService` applies `RetentionPolicy` hourly. Two independent rules, OR-ed: a run
is deleted when it exceeds `maxRunAgeDays`, **or** when it falls outside the newest
`maxRunsPerOrchestration` for its orchestration. Default is keep-forever.

**Favorited runs are exempt.** They are also removed from the ranking the max-count rule
uses, not merely skipped at deletion time — the rule deletes by *position*
(`i >= maxRunsPerOrchestration`), so favorites left in the ranking would occupy keep-slots
and block pruning of ordinary runs entirely.

Deleting a run — by retention or explicitly — also removes its annotation, so curation never
outlives its subject. Explicit `DELETE` of a favorited run requires `?force=true`.

## Export

Because a run's artifacts are split between `executions/` and `temp/`, copying the execution
folder is **not** a complete export — it captures the inline summary a step returned and
loses the document that step actually saved.

`RunExporter` gathers both, resolving saved-file paths first from the run record and then by
sweeping `{dataPath}/temp/{orch}/{runId}/`, so artifacts are found even when the data
directory has moved since the run. GUID filenames are renamed to the producing step, step
payloads have markdown code fences stripped and are JSON-validated, and anything missing or
unparseable is reported in the bundle's README rather than dropped silently.

See [`orchestra runs export`](cli.md#run-export) and
[`GET /api/history/{name}/{runId}/export`](api-reference.md#run-export).

## Related references

- [Engine reference: storage layer](engine.md)
- [Host reference: data plane MCP tools](host.md#data-plane-tools)
- [REST API: GET /api/history/{name}/{runId}](api-reference.md)
- [Orchestration step deep-dive](orchestration-step-deep-dive.md)
- Source: `src/Orchestra.Host/Persistence/FileSystemRunStore.cs`
- Source: `src/Orchestra.Host/Persistence/RunAnnotationStore.cs`
- Source: `src/Orchestra.Host/Export/RunExporter.cs`
- Source: `src/Orchestra.Host/Persistence/SqliteRunIndex.cs`
- Source: `src/Orchestra.Host/Persistence/RunIndexProjector.cs`
- Source: `src/Orchestra.Engine/Storage/OrchestrationRunRecord.cs`
- Source: `src/Orchestra.Engine/Storage/StepRunRecord.cs`
