# Copilot harness parity experiments

Reproduce the investigation in
[`docs/research/copilot-harness-parity-2026-10-29.md`](../../docs/research/copilot-harness-parity-2026-10-29.md).

The three principal arms are:

1. **CLI:** the real `copilot -p` command, ordinary interaction mode, automatic permissions.
2. **SDK:** the repository's .NET SDK package, driving that **same executable** over stdio.
3. **Orchestra:** the real JSON parser, scheduler, `OrchestrationExecutor`, `PromptExecutor`,
   Copilot provider, engine tools, and result processing. There are no mock agents.

`tui.py` additionally runs the actual interactive CLI in Windows ConPTY.
`mixed_cli.py` exercises the actual `orchestra exec` command with a mixed deterministic/AI DAG.

## Run

Requires .NET 10, Python 3.12+, Git, a Copilot CLI executable, and an authenticated
`gh` CLI or `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN` in the environment.

```powershell
dotnet build scripts/copilot-parity/ParityRunner.csproj -p:CopilotSkipCliDownload=true
python -m unittest discover -s scripts/copilot-parity -p test_parity.py -v

# Use the exact same executable for every arm, matching the checked-out SDK/runtime pair.
$cli = "$env:LOCALAPPDATA/Orchestra/copilot-cli/1.0.105/win-x64/copilot.exe"

python scripts/copilot-parity/run.py --cli $cli --model claude-sonnet-5 `
  --output artifacts/copilot-parity/my-run --repeats 2

python scripts/copilot-parity/analyze.py artifacts/copilot-parity/my-run
```

The original October 29 baseline used SDK 1.0.14 and CLI 1.0.85, as recorded in the report.
Current-source runs use SDK 1.0.28 and CLI 1.0.105; use the report's source revision and
version pair when reproducing the historical baseline rather than comparing different pins.
The example command records the model actually used in the October 29 investigation.
The driver's default is `claude-opus-4.8`; preflight deliberately fails if the requested
model is absent from the live catalog. Select an available model explicitly rather than
silently substituting one. All arms request `high` effort and the default context tier.

Use a **new output directory** per invocation. `--cases`, `--arms`, `--repeats`, and
`--timeout` narrow or extend an experiment. `--probe-only` checks runtime/auth/model
availability without inference. Trials are sequential, with rotated arm ordering.
When launching a complete suite through an outer job runner, allow it to run longer
than its individual 480-second per-trial timeout.

## Workloads and grading

| Case | What it exercises | Independent acceptance |
|---|---|---|
| `instructions` | Root instructions, ambient project skill, supporting files | Exact policy code and five classifications; instruction canaries separately |
| `repair` | Read/edit/test loop, regression tests | Holdout normalization cases, non-mutation, supplied and added tests |
| `graph` | Multi-file feature, planning, CLI, error handling | Dependency closure/order, full-graph validation, CLI exit/stdout/stderr, plan and tests |
| `analysis` | Non-coding synthesis of conflicting dated sources | Exact decision, mandatory cost, rejection reasons, sensitivity, source references |
| `automation` | Script creation and execution | Binary/Unicode inputs, dry-run, deduplication, repeat idempotency, source preservation |
| `mcp-recovery` | Real stdio MCP invocation and error recovery | Correct invoice plus server-side evidence of failure followed by successful retry |

The supplemental `nested-instructions` case checks on-demand discovery of a nested
`AGENTS.md` after a file view. Select it with `--cases nested-instructions`; the SDK-only
`--on-demand-instructions` flag explicitly enables that runtime capability. It is separate
from `--enable-discovery`, which controls ambient configuration such as project skills.

`grade.py` runs outside the agent workspace. Holdout inputs are supplied only after the
agent finishes. Inputs and supplied tests are hashed. A successful process exit or an
agent saying "done" does not establish a successful task. The non-coding report checks
test required source references/acknowledgment text; they are not a general semantic judge.

Each trial gets a fresh Git repository and `COPILOT_HOME`. The same account token is
passed only through child-process environments. No account token is written to the
configuration/evidence files. Auto-update is disabled. Experiments preserve their
workspaces under the approved OpenCode temporary directory for investigation.

## Evidence

Each trial records:

- Exact prompt, invocation, timeout, exit status, initial file hashes, and final workspace.
- SDK events or CLI JSONL, native session events, and Orchestra's real execution trace.
- OpenTelemetry model requests: served/requested model, effort, effective system prompt,
  tool definitions, model-call counts, and token metrics.
- Deterministic acceptance results, independent of the runtime's completion status.

`analyze.py` produces `analysis.json`, `comparisons.json`, request snapshots, and normalized
system-prompt diffs. Normalization removes only trial paths, UUIDs, and current-time tags.
It also compares common tool definitions, and distinguishes returned Orchestra content
from the final assistant message in the native transcript. A separate post-run Git audit
detects commits against the fixture's instruction. Original acceptance results remain intact.

Raw evidence belongs under the repository's ignored `artifacts/` directory. The report's
condensed evidence file preserves results and hashes for review without committing all
raw telemetry or scratch workspaces.

## Targeted follow-ups

Restore project skill discovery in the direct SDK arm:

```powershell
python scripts/copilot-parity/run.py --cli $cli --model claude-sonnet-5 `
  --cases instructions --arms sdk --enable-discovery --repeats 2 `
  --output artifacts/copilot-parity/discovery-check
```

Test the existing explicit-directory configuration in both SDK and Orchestra:

```powershell
python scripts/copilot-parity/run.py --cli $cli --model claude-sonnet-5 `
  --cases instructions --arms sdk orchestra --explicit-skill `
  --output artifacts/copilot-parity/explicit-skill-check
```

Probe native plan/autopilot mode RPCs, plan-file storage, and explicit memory/session-store
configuration. `--catalog-only` omits the one inference call used to capture the configured
tool catalog; mode/skill/plan RPC probes themselves do not invoke a model.

```powershell
python scripts/copilot-parity/profiles.py --cli $cli --model claude-sonnet-5 `
  --runner scripts/copilot-parity/bin/Debug/net10.0/ParityRunner.dll `
  --output artifacts/copilot-parity/profile-check `
  --work-root "$env:LOCALAPPDATA/Temp/opencode/orchestra-profile-check"
```

`profiles.py --surface-only` additionally checks native frontend-context options, including
`runningInInteractiveMode=false`, against captured prompts and tool catalogs.

Synthetic completion-contract probe (no authentication, CLI process, or model call):

```powershell
python scripts/copilot-parity/lifecycle.py --output artifacts/copilot-parity/lifecycle-check
```

This sends SDK event objects to Orchestra's real internal event handler. The audited
baseline exits **1**, reproducing a late child-idle event incorrectly completing the parent;
the corrected provider exits **0**. The original failing evidence remains in the study.
This is a protocol/adapter probe, not a live subagent workload.

Actual interactive CLI, Windows only:

```powershell
$site = "$env:LOCALAPPDATA/Temp/opencode/orchestra-parity-python"
python -m pip install --target $site pywinpty==3.1.0
python scripts/copilot-parity/tui.py --cli $cli --model claude-sonnet-5 `
  --python-site $site --output artifacts/copilot-parity/tui-check `
  --work-root "$env:LOCALAPPDATA/Temp/opencode/orchestra-tui-check"
```

The TUI driver submits the same prompt using `-i`, observes the completed turn, and sends
`/exit`. It uses automatic permissions and `--no-ask-user` to match the non-human benchmark.
It clears inherited terminal identity and dismisses optional terminal-setup dialogs.

Full Orchestra CLI integration:

```powershell
dotnet build src/Orchestra.Cli/Orchestra.Cli.csproj `
  -p:SKIP_PORTAL_BUILD=true -p:CopilotSkipCliDownload=true
python scripts/copilot-parity/mixed_cli.py --cli $cli --model claude-sonnet-5 `
  --orchestra src/Orchestra.Cli/bin/Debug/net10.0/Orchestra.Cli.dll `
  --output artifacts/copilot-parity/mixed-check `
  --work-root "$env:LOCALAPPDATA/Temp/opencode/orchestra-mixed-check"
```

This runs `Command -> Prompt -> Command -> Transform` through `orchestra exec`, verifies
the produced artifact in a deterministic downstream step, writes a report, and exits.
