# Automatic agent plan observability: verification

Verified during 2026-10-30–31. Scope: automatic, read-only live plans and durable observed
history, shared by the engine, provider adapters, HTTP/SSE consumers, and Portal.

## Runtime and API evidence

- Copilot SDK **1.0.28**, CLI **1.0.105**. Pinned SDK source exposes
  `SessionTodosChangedEvent`, `SessionPlanChangedEvent`, `ReadSqlTodosWithDependenciesAsync`,
  and `Plan.ReadAsync`. Live tests observed SQL task-status transitions and plan-document
  updates/deletion. Persistent recall remains disabled.
- OpenCode **2.0.26**. Live probes confirmed that the documented `experimental.todo: true`
  did not expose `todowrite`, including through a Code Mode catalog search. This runtime is
  covered by the explicit `orchestra_update_plan` compatibility tool, with its own source
  label and shared-step scope. Live tests observed task and Markdown updates through it.
- Conditional native OpenCode capture is tested against V2 tool-event and durable-message
  fixtures. Its envelope follows the official V2 API; native todo behavior could not be
  live-verified on 2.0.26 because that tool was unavailable. Unrecognizable task payloads
  produce diagnostics rather than invented task state.
- Live model: `claude-sonnet-5`, or `github-copilot/claude-sonnet-5` for OpenCode. The authoring
  example retains `claude-opus-4.8` as required by this repository.
- Context7 was unavailable due to quota. Sources were the pinned SDK, official V2
  [configuration guide](https://opencode.ai/v2/docs/config),
  [API reference](https://opencode.ai/v2/docs/api), and
  [OpenAPI](https://opencode.ai/v2/openapi.json), plus live runtime captures.

## Review findings and regression coverage

1. **Cancellation:** early cancellation placeholders could discard collected traces. Final
   capture is now drained with a deadline, and plan-producing workers get a bounded settlement
   period. Tests cover success, failure, cancellation before the first snapshot, and cancellation
   after a live plan was persisted.
2. **Loops and retries:** cancellation inside a loop could attribute the target's partial trace
   to the checker and overwrite the previous iteration's plan. Interrupted iterations now retain
   their own records; earlier revisions and execution identities remain distinct.
3. **Reconnects:** plans in restored traces were missing from authoritative snapshots. Trace
   hydration now includes plans, and retired executions cannot overwrite a newer attempt during
   replay. Tests cover replay-buffer eviction and browser reconnection.
4. **Transport isolation:** child-plan ownership queries formerly waited in OpenCode's event
   reader. They now run on a separate serial queue. A regression test holds the lookup open and
   proves ordinary content events still arrive.
5. **Malformed/failed writes:** native reconciliation validates message/tool shapes, ignores
   non-assistant and failed writes, and prevents an older durable projection from reverting newer
   live state. Capture failures are diagnostic and preserve the last known plan.
6. **Bounded reads:** a non-cooperative Copilot read cannot start overlapping refresh RPCs after a
   timeout. Final refresh remains bounded. OpenCode captures durable messages before session
   deletion, including cancellation and known plan-producing child sessions.
7. **Durability:** tests cover parallel appends, restart before `run.json` exists, pagination,
   corrupt rows, incomplete-tail repair, and execution IDs sharing a folder suffix. Bundle exports
   include the complete journal as well as latest plans in `run.json`.
8. **Portal/API compatibility:** the Host normally omits null JSON fields, which could omit the
   terminal pagination cursor and leave a spurious “Load more” button. The page contract now emits
   an explicit null cursor, and Portal also tolerates its omission. Browser tests use the Host's
   real serializer settings. History reads use captured run/step identity for restored steps and
   loop aliases; empty revisions remain selectable.
9. **Internal MCP readiness and visibility:** registration and catalog readiness are distinct
   states, so prompt admission now waits for the internal engine-tool server to connect. Tests
   prove delayed readiness and fail-fast behavior. Saved live traces also showed the model
   confusing its partial Code Mode search catalog with directly exposed MCP tools, incorrectly
   reporting that `orchestra_update_plan` was unavailable. Provider guidance now supplies the
   exact MCP-prefixed planning tool name and its invocation interface. Live fixture cleanup also
   waits briefly for Windows to release a terminated shell's working-directory handle.
10. **Concurrent trace projection:** a repeated DAG run exposed an existing LINQ materialization
    race on the live result dictionary (`Count` followed by `CopyTo` while a sibling finishes).
    A focused concurrent-update test reproduced the exception. Trace projection now uses the
    dictionary's atomic snapshot before sorting, with both direct and repeated DAG coverage.

The plan's reported task statuses never determine the orchestration step's outcome. The
compatibility tool cannot set engine status, and Portal task controls are read-only.

## Verification results

Evidence is retained locally under ignored `artifacts/agent-plans/`.

| Check | Result | Evidence |
|---|---|---|
| Final Release backend suite | **5,090 passed**, 0 failed across 13 projects | `final/accepted_*.trx` |
| Portal unit/component suite | **481 passed** across 32 files | `final/portal.json` |
| Final Copilot live plans + existing execution controls | **8 passed** | `final/copilot-live.trx`, `final/copilot-live/` |
| Final OpenCode live plans + existing execution controls | **4 passed** | `final/opencode-live-complete.trx`, `final/opencode-live/` |
| Browser live/reconnect/history flow | **1 passed** using production assets and Host serializer settings | `final/browser-verified.trx`, `final/browser/plan-history.png` |
| TypeScript and production build | Passed | `npm run typecheck`, `npm run build` |
| Authoring example | Parser + schema valid, 2 steps | CLI validation of `examples/agent-plan-observability.yaml` |
| Live plan persistence | **4 passed**: completion and cancellation on both providers | `final/live-persistence-complete.trx`, `final/live-persistence/` |

The selected final results are summarized in `final/verification-summary.json`: 5,090 backend
tests, 481 Portal tests, and 17 live/provider/browser checks, all passing. Earlier failed probes
and their diagnostics remain alongside the final results for traceability.

An initial OpenCode cancellation probe timed out waiting for a plan event: the test waiter did
not also observe execution completion. Adding that observation and saved failure diagnostics
revealed the internal MCP readiness race described above. A provider may legitimately create no
plan; these live tests explicitly request one. A browser probe also selected the definition card
while the running card was still loading; its saved screenshot identified that fixture race, and
the selector now targets the execution ID. Repeated verification runs are not additional
independent task samples.

## Reproduction

```powershell
dotnet test OrchestrationEngine.slnx -c Release --filter 'Category!=E2E' -m:1 `
  -p:SKIP_PORTAL_BUILD=true -p:CopilotSkipCliDownload=true

# Use an existing authenticated Copilot/OpenCode setup.
$env:ORCHESTRA_COPILOT_TEST_MODEL = 'claude-sonnet-5'
$env:ORCHESTRA_OPENCODE_TEST_MODEL = 'github-copilot/claude-sonnet-5'
$env:ORCHESTRA_OPENCODE_E2E = '1'
$env:ORCHESTRA_PLAN_E2E = '1'
dotnet test tests/Orchestra.Copilot.Tests -c Release `
  --filter 'FullyQualifiedName~CopilotPlanE2ETests|FullyQualifiedName~CopilotStepControlsE2ETests'
dotnet test tests/Orchestra.OpenCode.Tests -c Release `
  --filter 'FullyQualifiedName~OpenCodePlanE2ETests|FullyQualifiedName~OpenCodeStepControlsE2ETests'
dotnet test tests/Orchestra.Server.Tests -c Release `
  --filter 'FullyQualifiedName~ProviderPlanPersistenceE2ETests'

# Build Portal assets first. Use installed Edge, or omit the channel to use Playwright Chromium.
$env:ORCHESTRA_PLAYWRIGHT_CHANNEL = 'msedge'
dotnet test tests/Orchestra.Portal.E2E -c Release --filter 'FullyQualifiedName~AgentPlanUiTests'
```

The browser test uses production Portal assets, the actual SSE reporter, plan-history endpoint,
and file-system store on an isolated loopback server. The live persistence tests run actual
provider agents through the orchestration executor and inspect both saved records and live
snapshots. Test server and provider lifetimes are disposed by their fixtures.
