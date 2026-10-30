# Agent plans: live progress and durable history

Orchestra automatically captures explicit task lists and plan documents during Prompt steps.
The Portal's **Plan** panel, SSE/REST consumers, and saved execution records use the same
provider-neutral observations. Agent-reported progress is independent of DAG step outcomes.

## Configuration

Capture defaults on. Use orchestration `defaultCapturePlans: false` or Prompt-step
`capturePlans: false` to disable it. Step values override the orchestration default.

This does not select native plan mode or autopilot, require every simple task to create a
plan, or enable cross-session recall. “No plan reported” is a valid state. Output-handler
transformations do not create a second plan stream. Capturing state adds no model calls just
to summarize progress. See [the example](../examples/agent-plan-observability.yaml).

## Provider sources

### Copilot

SDK 1.0.28 exposes `session.todos_changed` and `session.plan_changed`. Orchestra reads native
SQL todos/dependencies and the session plan document on startup, on change signals, and once
more before session disposal. Reads are serialized, coalesced, and bounded; transport event
callbacks never wait synchronously for RPC results.

The SQL list is labelled **shared** because it is session coordination state. A notification's
agent ID does not establish ownership of every task. Optional row fields and unrecognized
statuses are preserved where possible; unknown status is displayed honestly.

These are session-local APIs and work with persistent recall disabled. The SDK marks the RPC
surface experimental. Its best-effort reads may return empty lists when tables/columns are
unavailable. History contains observed snapshots, not every underlying database transaction.

### OpenCode V2

Successful recognizable native `todowrite` calls are captured when the runtime provides that
tool. Failed calls do not publish task state. Durable message reconciliation repairs missed
root-session observations without replaying older states over the newest one. Child-session
ownership is checked before accepting native child plans; those checks run away from the
transport reader. Current durable messages are captured on resume and before teardown,
including cancellation, for the root session and known plan-producing child sessions.

Installed **2.0.26** did not expose `todowrite` in live probes, even with the current docs'
`experimental.todo` flag and a Code Mode catalog search. Orchestra therefore also provides
**`orchestra_update_plan`** when capture and the engine-tool bridge are enabled (the defaults).
It publishes a complete task list and optional Markdown, is labelled `orchestra-plan-tool`,
and has a shared step scope. It is not
presented as a native OpenCode plan API. Markdown survives same-session worker recovery and
resets on a new session. No structured plan is inferred from ordinary prose or broad file scans.
The provider waits for the internal MCP catalog and supplies its exact wire tool name in the
agent's instructions, so authors can use the simpler planning request shown in the example.

## Portal

Select a Prompt step to see its Plan panel:

- task titles, descriptions, reported statuses, and completed/total counts;
- task dependencies and Markdown documents when available;
- provider/source, session/actor, revision, and capture time;
- a read-only revision selector with paginated durable history.

Checkboxes are disabled and there is no plan-edit API. Reconnecting restores current plans
from the authoritative execution snapshot, even after SSE buffer eviction. Historical runs
read latest plans from saved traces. Older runs without plans remain readable.
Plans restored from a prior run use their recorded run identity when opening history. Empty
revisions remain selectable, so clearing a task list or deleting a document is visible.

## Saved data and APIs

Each observation has run identity, step name, unique `stepExecutionId`, optional loop
iteration, stream identity, revision, and timestamp. Retries, iterations, sessions, and
sources retain distinct identities.

- `run.json`: latest observations in each step record's `trace.plans`.
- `plan-events.jsonl`: append-only observed revisions in the same run directory, written
  during execution, including before `run.json` exists.
- SSE: `step-plan-updated` with `{ stepName, revision }`.
- Live state: each `execution-snapshot` / `/api/execution/{id}/state` step includes `plans`.
- History: `GET /api/execution/{id}/plans?stepName=analyze&after=0&limit=100` returns
  `{ items, nextCursor, isPartial }`. Pass `nextCursor` as `after` with the same filter.
  Limit is 1–1000; history is append ordered. Unfinished runs with a journal are readable too.

The file-system run store implements `IPlanHistoryStore`; alternative stores can implement
that optional interface. In-memory execution still streams and retains latest snapshots.
Storage/read errors emit diagnostics and preserve the last known plan where available.

A newline commits a journal record. Readers ignore an interrupted final record, and the
next append removes that partial tail. A fresh store instance can read committed history
after restart. Journal files live with their run so normal run deletion/retention owns them.
Bundle exports include the journal alongside `run.json` and the other run artifacts.
On cancellation, plan-producing steps get a bounded final-capture grace period. Uncooperative
providers or storage failures produce diagnostics; already-observed plans remain available.

## References

- [Copilot plan RPCs](https://github.com/github/copilot-sdk/blob/v1.0.28/dotnet/src/Generated/Rpc.cs)
- [OpenCode experimental todos](https://opencode.ai/v2/docs/config#todos)
- [Provider controls](providers.md)
