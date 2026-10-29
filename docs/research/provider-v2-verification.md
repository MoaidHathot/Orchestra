# Provider controls and OpenCode V2 verification

Verified on 2026-10-29. This follows the earlier Copilot parity study; it is an implementation
verification, not a claim that providers produce identical answers.

## Versions and contracts

- Copilot SDK: **1.0.28**, source tag commit
  `fa108e71ee3025663e0dac20f2a071055d3a6b14`.
- Copilot runtime: **1.0.105**, matching the SDK package's `GitHub.Copilot.SDK.props`.
- OpenCode runtime: **2.0.26**, checked through the installed CLI and private server's
  `/api/info`. Request/event shapes were checked against the V2 OpenAPI and live captures.
- Live model: `claude-sonnet-5` (OpenCode: `github-copilot/claude-sonnet-5`). Examples retain
  the repository's default `claude-opus-4.8`.
- Context7 was unavailable due to quota. Official V2 docs, pinned SDK source, and local
  runtime probes supplied the API evidence.

## Verified behavior

1. File-based Prompt, Command, and Script steps use the definition directory by default;
   relative overrides resolve from that same base. In-memory definitions retain CWD fallback.
2. Copilot native autopilot uses explicit continuation options and request-correlated
   `session.autopilot_stopped`. Idle turns, old requests, and child completion do not end it.
   A clean shutdown without correlated task completion is not success.
3. OpenCode uses V2 session creation with Location/model, durable prompt admission, native
   wait, and paginated message projection. Final root text is distinct from tool/child output.
4. OpenCode forms, human permission approval, inline subagents, explicit skills/instructions,
   MCP bridging, tool-error recovery, and generated-script verification work end to end.
5. Shared output validation applies to both providers. Native constrained generation is
   advertised only for Copilot. OpenCode bounded continuation reuses the same session;
   deterministic tests verify budget exhaustion and transport swap/resume.

## Important live findings

- V2 private servers generate an authentication password if none is supplied. Orchestra now
  supplies its own per-process password, with the fixed V2 username `opencode`.
- `/api/info` can succeed before model/agent catalogs are loaded. The adapter explicitly
  waits for the requested model/agent and checks requested variants.
- `form.created` nests the form under `data.form`; treating it as a flat request caused an
  operator-question timeout. The envelope handling was corrected and the live test passed.
- A repeat guided-JSON OpenCode coding run returned a verification sentence before its JSON.
  Orchestra correctly failed schema validation and blocked its dependent step. This is
  retained as evidence that guidance is not native constrained output. The coding/recovery
  test now uses ordinary text and verifies the generated program independently; dedicated
  structured-autopilot and question/JSON tests exercise the structured contract. Invalid
  output rejection is also covered deterministically.
- Windows can briefly retain a child process's working-directory handle during teardown;
  live fixture cleanup waits briefly for that handle to close.

## Results and reproducibility

Local evidence is under `artifacts/provider-v2-verification/`:

| Check | Result/evidence |
|---|---|
| Complete serial non-E2E suite | 4,987 passed across 13 projects; `serial-suite/*.trx` |
| Final Copilot unit regressions | 377 passed; `copilot-final-unit.trx` |
| Final OpenCode unit/transport regressions | 79 passed; `opencode-final-unit.trx` |
| Final engine contract checks | 36 passed; `engine-final-contracts.trx` |
| API metadata checks | 7 passed; `host-api-final.trx` |
| Copilot live controls | 5 passed; `copilot-upgrade-live.trx`; final autopilot recheck in `copilot-autopilot-final.trx` |
| OpenCode live scenarios | 8 passed in `opencode-v2-live-verified.trx`; subsequent variant/readiness recheck in `opencode-final-live.trx` includes the guided-JSON rejection above; final ordinary coding/recovery check passed in `opencode-coding-final.trx` |
| Portal types | `npm run typecheck` passed |
| New example | CLI `validate examples/provider-autopilot.yaml --schema` passed |

The full-suite count is the checkpoint before the final targeted regressions were added.
Repeated tests are not additional independent task samples.

```powershell
dotnet test OrchestrationEngine.Tests.slnx --filter 'Category!=E2E' -m:1 `
  -p:SKIP_PORTAL_BUILD=true -p:CopilotSkipCliDownload=true --logger trx `
  --results-directory artifacts/provider-v2-verification/serial-suite

$env:ORCHESTRA_COPILOT_TEST_MODEL = 'claude-sonnet-5'
# Supply existing authentication through COPILOT_GITHUB_TOKEN or the CLI's login.
dotnet test tests/Orchestra.Copilot.Tests --filter 'FullyQualifiedName~CopilotStepControlsE2ETests'

$env:ORCHESTRA_OPENCODE_E2E = '1'
$env:ORCHESTRA_OPENCODE_TEST_MODEL = 'github-copilot/claude-sonnet-5'
# Requires OpenCode's existing authenticated provider integration.
dotnet test tests/Orchestra.OpenCode.Tests --filter 'Category=E2E'
```

Live tests optionally preserve results/traces with `ORCHESTRA_COPILOT_TEST_EVIDENCE_DIR` and
`ORCHESTRA_OPENCODE_TEST_EVIDENCE_DIR`. Provider limitations and author-facing semantics are
documented in [provider contracts](../providers.md), [Copilot](../copilot.md),
[OpenCode](../opencode.md), and the orchestration-authoring skill.
