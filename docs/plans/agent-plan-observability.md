# Automatic agent plan observability

## Goal

Capture explicit provider task lists and plan documents as engine-owned execution data.
Expose automatic read-only live updates, durable observed revision history, and a Portal
plan panel. Plan progress is observational and independent of the orchestration DAG outcome.

## Milestones and commits

1. **Research and contracts:** verify pinned Copilot/OpenCode payloads; add provider-neutral
   snapshots/revisions, step execution identity, capture configuration, and engine reduction.
2. **Durable storage and live API:** append plan revisions during execution, survive partial
   writes/restart, include latest snapshots in saved traces and SSE reconnect snapshots,
   expose paginated read-only history for active/completed runs.
3. **Provider adapters:** Copilot plan/todo RPC readers with serialized non-blocking refresh
   and bounded final capture; OpenCode native todo updates and durable reconciliation, scoped
   by provider session/actor. Capture supported Markdown plan documents without broad scans.
4. **Portal and documentation:** read-only tasks/documents/history panel, live and historical
   hydration, empty/error states; document authoring controls and provider limitations.
5. **Verification and final review:** deterministic unit/integration/browser tests, real
   provider plan updates, full suite, cleanup, and evidence summary. Commit each major layer.

## Contract decisions

- Capture defaults on; `capturePlans: false` disables it per Prompt step. An orchestration
  `defaultCapturePlans` supplies the default. This does not require native plan mode or
  autopilot, force plan creation, or enable persistent cross-session memory.
- A snapshot identifies provider, session, source, and optional actor. Task IDs/statuses and
  dependency edges are preserved with normalized display states. Missing ownership is not
  guessed. Documents retain Markdown and source path metadata.
- Each observed change gets immutable run/step/execution identity, revision, and timestamp.
  Deduplicate identical snapshots; keep distinct attempts, loop iterations, and sessions.
- A per-run append-only journal preserves changes while the run is active. Latest snapshots
  appear in step traces and authoritative SSE state. History reads are paginated. Older runs
  with no plans remain readable. A capture error preserves the last known plan and reports a
  diagnostic rather than changing the business step's outcome.
- Provider callbacks must not block their transport reader on RPC or disk I/O. Final capture
  is bounded and occurs before session teardown. Capture is automatic only; no plan-edit API.

## Evidence and verification

- Copilot SDK 1.0.28 exposes `SessionTodosChangedEvent`, `SessionPlanChangedEvent`,
  `Plan.ReadSqlTodosWithDependenciesAsync`, and `Plan.ReadAsync` (experimental RPC surface).
  Todo reads are documented as best effort, with optional row fields.
- OpenCode V2 documents `experimental.todo: true` and `todowrite`; its published HTTP API
  has no dedicated todo/plan resource. Verify actual tool/message payloads before mapping.
- Test: empty/deleted/revised plans, malformed/failed updates, deduplication, overlapping
  actors/steps, retries/resume, cancellation/final capture, durable partial records, reconnect
  after SSE eviction, read-only Portal rendering, and existing-run compatibility.
- Retain reproducible live evidence under ignored `artifacts/agent-plans/`. Test servers must
  be stopped after verification. No package publication is part of this task.

## Progress

- Shared snapshots/revisions, capture controls, engine reduction, and append-only storage
  implemented. Initial engine and storage tests pass, including restart, paging, partial-tail
  repair, parallel writes, capture opt-out, and preserving the last known plan on errors.
- Live OpenCode 2.0.26 probe: the documented experimental todo flag did not expose a native
  todo tool (including a Code Mode catalog search). Support native todo events when present
  and provide a clearly labelled shared-step `orchestra_update_plan` compatibility tool.
  Do not infer a structured plan from prose. Capture evidence is under
  `artifacts/agent-plans/opencode-probe/`.
- Provider capture, live API, Portal, and final verification remain in progress.
