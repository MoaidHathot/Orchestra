---
layout: default
title: Copilot harness parity investigation
nav_exclude: true
---
{% raw %}

# Copilot SDK, Copilot CLI, and Orchestra: harness parity

**Investigation date:** October 29, 2026.

**Implementation follow-up:** the provider now exposes discovery and explicit instruction
directories, uses `humanInput` to select non-interactive/interactive guidance, disables native
persistent recall, supports validated `responseSchema` output, and fixes the completion path
described below. See [current provider documentation](../copilot.md). This report and its
evidence index preserve the baseline at the recorded source commit; its descriptions of
missing mappings and failing lifecycle behavior refer to that baseline.

## Conclusion

**Orchestra's architectural premise is sound: the Copilot SDK runs the actual Copilot
agent runtime, including its tool-use loop. Orchestra does not need to implement that
loop itself. However, an Orchestra Prompt step is not automatically equivalent to
opening Copilot CLI with the same model and typing the same prompt.**

The differences are observable, not just theoretical:

- An ambient repository skill worked in both the programmatic CLI and actual interactive
  CLI, but failed in the default SDK and Orchestra configurations.
- Explicit SDK discovery restored it. Explicit `skillDirectories` restored it in Orchestra
  using configuration already supported today.
- A separate nested-`AGENTS.md` probe also failed with SDK/Orchestra defaults. Enabling
  **on-demand instruction discovery** fixed it; enabling general configuration discovery
  alone did not.
- Captured model requests had the same served model and reasoning effort, but different
  default system prompts and tool catalogs.
- All **22 shared initial tool definitions** in the default CLI/SDK comparison were
  identical after normalizing trial paths/IDs. Orchestra preserved those definitions and
  added four orchestration tools.
- Explicit SDK memory and session-store configuration restored three additional tools
  present in the interactive CLI. Those are configurable runtime capabilities, not a
  separate, smarter CLI-only reasoning engine.
- A separate synthetic SDK-event probe reproduced a premature-completion path in
  Orchestra's event adapter. This is an integration issue to address before claiming
  reliable subagent/advanced-mode parity, not a live-task failure attributed to the SDK.

"Same runtime", "same effective configuration", "same capabilities exposed", and
"same task outcome" are distinct claims. The evidence supports the first, identifies
gaps in the second and third, and measures the fourth on a bounded test suite. It cannot
establish identical internal reasoning or guarantee identical outputs.

## What was compared

| Item | Recorded value |
|---|---|
| Orchestra source | `4ac95a7742159b6db92dfe4000d245b6803ef423` |
| .NET SDK package | `GitHub.Copilot.SDK` **1.0.14** |
| SDK source tag/commit reviewed | `v1.0.14`, `e60d9037353249ef16b349eb4012e8c1d113fda5` |
| Runtime for matched tasks | **Copilot CLI 1.0.85**, same executable for every arm |
| Installed interactive CLI | **1.0.91**; separately checked with non-inference configuration probes |
| Model used | `claude-sonnet-5` |
| Reasoning/context | `high`, default context tier |
| Authentication | Same account token, passed through child environments |
| Permissions | CLI `--allow-all`; SDK/Orchestra approve-all |
| State | Fresh Git workspace and `COPILOT_HOME` per trial |
| Human interaction | Disabled for matched tasks; actual TUI probe also used `--no-ask-user` |

`claude-opus-4.8`, the repository's example default, was absent from the live model
catalog. Preflight rejected it; the experiment explicitly selected an available model.
Telemetry verified `gen_ai.response.model=claude-sonnet-5` and
`gen_ai.request.reasoning.level=high`, rather than relying on an agent's self-description.

The main CLI arm used `copilot -p`, which is the CLI's real programmatic interface.
Because that is not the same UI as opening the terminal interactively, a supplemental
test ran **the actual TUI through Windows ConPTY**, submitted the same task with `-i`,
and exited with `/exit`. That confirmation completed with exit code 0.

The main Orchestra arm used the real parser, scheduler, executor, provider, engine
tools, and result-processing code. A separate **actual `orchestra exec` integration**
ran `Command -> Prompt -> Command -> Transform`, with a downstream deterministic
assertion of the AI-produced file. This passed and the one-shot host exited.
The embedded arm did not configure optional host persistence, human-input services,
or engine artifact storage; native filesystem tools remained enabled. The separate
CLI integration used the actual host services.

## Architecture: what the SDK really runs

The pinned SDK's [agent-loop documentation][agent-loop] describes the SDK as the
transport layer and the CLI as the owner of the model/tool loop. Its [.NET source][client]
launches the supplied executable with:

```text
copilot --headless --no-auto-update --stdio
```

Orchestra's [`CopilotSdkClientFactory`](../../src/Orchestra.Copilot/ICopilotClient.cs)
resolves its CLI executable and passes it to `RuntimeConnection.ForStdio(cliPath)`.
[`CopilotAgent`](../../src/Orchestra.Copilot/CopilotAgent.cs) creates a session, sends a
message, receives events, and waits for completion. The runtime performs the intervening
model calls and native tool executions.

```text
Interactive CLI / copilot -p ───────────────────┐
                                             ├─ Copilot runtime ─ model + tools
Orchestra ─ .NET SDK ─ JSON-RPC/headless CLI ───┘
```

SDK `CopilotClientMode.CopilotCli` is the documented/default client-defaulting strategy
([source][types]). Orchestra does not select `Empty` mode. Nevertheless, the observed
headless defaults are not identical to the CLI frontend's defaults. The live discovery
failure below is a counterexample to treating that enum name as an end-to-end guarantee.

### A step is one user turn, not one model call

A single Prompt step can perform many model calls, searches, edits, commands, retries,
and verifications. It receives the runtime's accumulated tool results throughout that
session. This is the agent harness behavior the original design intended to reuse.

However, separate Orchestra Prompt steps normally create **separate sessions**.
Dependency output is formatted into a later prompt; the entire earlier conversation,
tool transcript, plan, and conversational state are not implicitly transferred. Sharing
a pooled CLI process does not turn those sessions into one continuous CLI conversation.

### Capability evidence levels

| Capability | Evidence in this investigation |
|---|---|
| Search/read/edit files, run commands/tests, iterate on results | Live task traces and independent artifact checks |
| Reasoning configuration | Served model and `high` effort captured on actual model requests; internal reasoning equivalence is not directly measurable |
| Planning | Live multi-file task with a plan; native SDK plan mode and plan-file RPCs verified separately |
| MCP and recovery from a tool error | Real local stdio server with its own call log and acceptance checks |
| Skills and repository instructions | Live positive/negative controls and configuration interventions |
| Subagents/fleet | Native `task` tool offered; fleet API source/documentation reviewed; no dedicated fleet-quality benchmark |
| Automatic compaction and session resume | SDK supports them and Orchestra maps related settings/recovery; these experiments did not force context exhaustion or process-failure recovery |
| Web/browser/desktop interaction | Web tools appeared in the catalog; external research, browser and desktop tasks were not exercised |

## Live task results

The principal suite covers six workloads. The instruction/skill discovery test ran once
per arm; each of the other five workloads ran twice per arm. Acceptance is based on
produced files, preserved inputs, independent executable holdouts, and MCP server-side
call records. Instruction canaries are reported separately from artifact correctness.

**Completed principal cohort: 33 runs.** Counts below are passes of the predeclared
automated acceptance checks, not just successful process exits.

| Workload | Copilot CLI | Direct SDK | Orchestra |
|---|---:|---:|---:|
| Bug repair + regression tests | 2/2 | 2/2 | 2/2 |
| Multi-file dependency planner + CLI + plan + tests | 2/2 | 2/2 | 2/2 |
| Non-coding analysis of conflicting dated evidence | 2/2 | 2/2 | 2/2 |
| File automation, including retained-test deliverable | 0/2* | 0/2* | 0/2* |
| MCP failure followed by recovery | 2/2 | 2/2 | 2/2 |
| Ambient repository skill | 1/1 | 0/1 | 0/1 |

**\* All six automation programs passed every functional holdout**: dry-run, binary and
Unicode files, deduplication, repeat idempotency, source preservation, and empty input.
They also supplied verification notes. The failed check was the absence of a retained
`test*.py` file: the agents ran edge checks inline instead. The prompt said "include
tests" without specifying persistence/naming, so this is a test-deliverable/specification
mismatch, not evidence that any surface lacked automation tools. The original failing
scores are retained rather than retroactively relabeled as passes.

Across the five core tasks, **30/30 runs passed their functional artifact checks** and
**24/30 passed the full automatic acceptance criteria**. Each used **4–25 observed model
calls**, demonstrating real tool-use/verification loops rather than a single text response.
Every captured model call in this cohort reported `claude-sonnet-5` with `high` effort.

The supplemental nested-instruction baseline (§1b) adds three runs: CLI passed, default
SDK and Orchestra failed. Together these make **36 baseline task runs**, followed by
the targeted configuration interventions and interface/protocol probes.

**Post-run Git audit:** the first CLI automation run created local commit
`9b67f45a7d5cd70978eba5f689116606b880ade2` in its isolated fixture repository, despite
the root instruction not to commit. The other **35 baseline runs created no commits**.
This audit is recorded separately from the original artifact grader. A successful
instruction canary proves loading, not complete instruction compliance.

The [condensed evidence index](copilot-harness-parity-2026-10-29.evidence.json) preserves
per-run checks, observed tools/models, Git audit, configuration probes, and raw-evidence
hashes. The successful main runs' returned SDK/Orchestra content matched their final
native assistant messages. The failed Orchestra skill run instead returned a failed step
with an error reason and empty content; its explanatory assistant message remains in the
native transcript.

These are small, controlled capability probes, including a modest multi-file feature;
they are not a benchmark of large production repositories or a statistical equivalence
test. Model-generated tests supplement, but do not replace, the independent holdouts.

**Manual review found a defect outside the automatic checks:** the first CLI vendor
report made the correct selection and cost calculation, but described the budget change
from 12,000 to 11,500 as "roughly 3%"; it is **4.17%**. That incidental prose arithmetic
was not part of the JSON/acceptance oracle. Passing these checks does not certify every
sentence of an answer, and this one observation does not establish a general quality
ranking. The source reports and their hashes are retained for review.

## Confirmed default/configuration differences

### 1. Repository skill discovery: a reproduced functional gap

The fixture put a real `SKILL.md` under `.github/skills/triage-policy`, with a policy
document containing information absent from the prompt. The required result was an exact
five-incident classification and policy identifier.

| Configuration | Observed result |
|---|---|
| CLI `-p`, ordinary defaults | Skill invoked; correct file produced |
| Actual interactive CLI | Skill invoked; correct file produced |
| Default direct SDK | `skill` invocation failed; required project skill absent |
| Default Orchestra | Same missing skill; no result file; step marked failed |
| SDK `EnableConfigDiscovery=true` | Passed **2/2** fresh runs; skill-list RPC included the project skill |
| SDK explicit `SkillDirectories` | Passed |
| Orchestra explicit `skillDirectories` | Passed |

The canary instructions in both root `AGENTS.md` and `.github/copilot-instructions.md`
appeared even in the failed runs. That proves those sources were loaded, not that every
instruction was followed. This is specifically a discovery difference, not a general inability to
read files. The default SDK's skill-list RPC returned
only the two built-in skills; enabling discovery returned the fixture skill too.

The same default-versus-explicit discovery distinction was reproduced by skill-list RPC
against **1.0.91**. Updating Orchestra's CLI pin alone therefore does not fix this gap.

Evidence directories: `instructions-baseline`, `discovery-enabled`, `explicit-skills`,
`interactive-tui-confirmation`, and `sdk-profiles-1.0.91` under `artifacts/copilot-parity/`.

### 1b. Nested instructions: a second, independent functional gap

A supplemental fixture placed an `AGENTS.md` beside `nested/facts.json`. It required
answers in that directory to include `localPolicy: NESTED-POLICY-E4381`. The user prompt
asked for the sum of three quantities and did not reveal that local policy.

| Configuration | Correct sum | Required local policy |
|---|---|---|
| CLI | Yes | Included |
| Default SDK | Yes | **Missing** |
| Default Orchestra | Yes; step status `Succeeded` | **Missing** |
| SDK `EnableConfigDiscovery=true` only | Yes | **Missing** |
| SDK `EnableOnDemandInstructionDiscovery=true` | Yes, **2/2** | Included, **2/2** |

This isolates instruction discovery from mathematical/coding ability. It also demonstrates
why an exit code of zero or an Orchestra status of `Succeeded` is insufficient evidence of
task correctness. These two SDK settings address different discovery paths:

```csharp
EnableConfigDiscovery = true,
EnableOnDemandInstructionDiscovery = true,
```

They are **SDK session options**. Current Orchestra YAML does not expose them; simply
adding those keys to a step does not implement the mapping. The tested explicit skill
workaround fixes the skill fixture, not the nested-instruction fixture.

Evidence: `nested-baseline`, `nested-discovery-only`, and `nested-enabled`.

### 2. System prompts: preserved core, different frontend defaults

OpenTelemetry captured the effective system instructions actually supplied with model
requests. The normalized default SDK and Orchestra system prompts were identical in the
instruction experiment. The CLI differed in specific sections:

- `copilot -p` introduced itself as the CLI in **non-interactive mode**, with instructions
  to make reasonable assumptions and finish autonomously.
- The SDK introduced itself as an interactive coding agent.
- The TUI used an interactive CLI identity, with additional self-documentation and
  memory guidance. Its captured prompt also included a content-exclusion advisory;
  prompt-text comparison alone does not establish a difference in enforcement.
- The CLI included the ambient project skill in its initial skill catalog; the default
  SDK did not.

The shared coding, tool-use, environment, and planning guidance was present in the SDK.
This is not evidence that Orchestra reduced Copilot to a bare chat completion.

A follow-up probe restored the CLI's non-interactive autonomy guidance through the
native SDK options RPC, without replacing the system prompt:

```csharp
await session.Rpc.Options.UpdateAsync(runningInInteractiveMode: false);
```

This changes frontend interaction guidance; it is distinct from selecting native plan
or autopilot mode. The SDK identity remains a coding-agent identity. Setting the separate
`agentContext: "cli"` option did not change that identity or add the CLI self-documentation
helper in the tested runtime. Evidence: `sdk-surface-profiles`.

Separately, Orchestra supports `systemPromptMode: Replace`. That intentionally replaces
the runtime prompt. For CLI-like behavior use **Append**, or leave the runtime prompt
intact. Replacing it with "You are a helpful assistant" changes the instructions that
make the harness effective, even when the underlying tools remain available.

### 3. Tool catalogs and memory

Initial offered tools in the instruction task, with user questions disabled:

| Surface/configuration | Initial tools | Relevant difference |
|---|---:|---|
| Programmatic CLI | 24 | Includes `session_store_sql` and `fetch_copilot_cli_documentation` |
| Default direct SDK | 22 | All 22 shared definitions match the CLI |
| Default Orchestra | 26 | Same 22 plus four `orchestra_*` tools |
| Actual interactive CLI | 26 | Also includes `store_memory` and `vote_memory` |
| SDK with discovery + session store + memory explicitly enabled | 25 | Restores session-store/memory tools; CLI self-documentation helper remains absent |

The three restored tools were observed in a real model request after setting
`EnableSessionStore=true` and `Memory = new MemoryConfiguration { Enabled = true }`.
The probe instructed the model not to call tools or store memories. **Tool exposure** was
verified here; long-term memory quality and cross-session retrieval were not benchmarked.
These cross-session features are distinct from the ordinary conversation history that
the runtime already maintains within one Prompt step.

The CLI self-documentation helper is a concrete frontend-specific convenience. It is not
evidence of an extra coding/reasoning engine. The SDK's [compatibility document][compatibility]
also distinguishes terminal/product workflows from runtime APIs; some entries lag the
generated API surface, so that table was cross-checked against source and live probes.

### 4. Native plan/autopilot modes exist in the SDK

Live RPC probes on **both 1.0.85 and 1.0.91** successfully changed session mode from
`interactive` to `plan` and to `autopilot`. A plan-mode probe wrote and read the native
session `plan.md` through SDK RPCs.

```csharp
await session.Rpc.Mode.SetAsync(SessionMode.Plan);
await session.Rpc.Plan.UpdateAsync(planText);
var plan = await session.Rpc.Plan.ReadAsync();
```

These bindings are experimental in SDK 1.0.14. Their availability was tested; a long-running
autopilot objective, automatic continuation budget, and a fleet workload were not.
Fleet is also documented/exposed through `session.Rpc.Fleet.StartAsync` ([source][fleet]).

Orchestra currently sends an ordinary session message and exposes no per-step selection
of these native modes. Its source explicitly notes that it does not use autopilot.
Asking for a `PLAN.md` in a prompt can exercise planning, but is not the same configuration
as selecting the CLI's native plan mode or `/autopilot` workflow.

## Orchestra-specific semantics to account for

These are source-audited integration differences; they should not all be interpreted as
failures in the live tasks:

| Area | Current behavior | Implication for parity |
|---|---|---|
| CLI selection | Bootstrap pins 1.0.85; `ORCHESTRA_COPILOT_CLI_PATH` overrides it | Match and record the actual binary, not just the SDK version |
| Working directory | Passed when explicitly configured; otherwise runtime process cwd | Set it to the same repository as the manual CLI |
| Extra context | Dependency outputs, input formatting, loop feedback, optional output-handler call | Compare the resolved prompt and raw agent output, not only the authored prompt |
| Engine tools | Always adds status, completion, file-save and file-read tools | The offered action space is larger than a plain CLI session |
| `orchestra_set_status` | Immediately terminates the step; success output can become the status reason | Returned step content can differ from the final conversational response |
| Session lifetime | Fresh sessions for normal prompt steps; resume primarily used for worker recovery | A multi-step DAG is not automatically a multi-turn chat |
| Recovery | Can resume or cold-restart and resend the original prompt | Failure-path execution is not identical to a manually resumed CLI conversation |
| Human interaction | Optional elicitation/plan callbacks and an Orchestra input tool | Full native `ask_user` parity needs explicit callback/variant wiring and E2E verification |
| Configuration surface | No step-level discovery, session-store/memory, plugin-directory, or native mode controls | SDK capability does not automatically imply a declarative Orchestra setting |

### Completion adapter: a deterministic protocol counterexample

[`CopilotSessionHandler`](../../src/Orchestra.Copilot/CopilotSessionHandler.cs) treats
`SessionTaskCompleteEvent` and a root-inferred `AssistantIdleEvent` as completion shortcuts.
The SDK distinguishes agent-idle/task-complete from whole-session idle. A synthetic probe
fed real SDK event objects to the **actual internal handler**, without spawning an agent:

```text
subagent.started
assistant.idle(agentId="child-agent")   -> parent completion remains false
subagent.completed
assistant.idle(agentId="child-agent")   -> parent completion becomes TRUE
```

No root/session completion was sent. The second child-idle incorrectly became an
Orchestra `SessionIdle` event after the active-subagent frame had been removed. The handler
uses the active-frame count instead of rejecting an explicit non-root `AgentId`.

This **reproduces a component-level premature-completion path**; it does not demonstrate
that the live CLI emitted this particular ordering during our task suite. No native
subagent calls occurred in those live baseline tasks. The probe exits **1** to report the
contract violation and records `defectReproduced: true` under `lifecycle-confirmation`.

Filter non-root terminal events by SDK agent identity and validate completion against the
runtime's whole-session/end-of-run signals before exposing advanced modes. Keep the
explicit Orchestra status-tool cancellation semantics distinct from runtime completion.

## What to do to get the intended behavior

### Available now

1. **Use one runtime version for both comparisons and production expectations.** The
   explicit CLI-path override already exists. Record `GetStatusAsync()`/runtime telemetry;
   the SDK session-start producer field alone is not reliable version evidence.
2. **Preserve the runtime system prompt** and match model, reasoning effort, context tier,
   cwd, permissions, repository instructions, MCP configuration, and starting session state.
3. **Register needed skills explicitly** in Orchestra. This is a tested workaround today:

```yaml
name: cli-like-task
description: Run a task with the native Copilot prompt and explicit repository skills.
defaultProvider: copilot
defaultModel: claude-opus-4.8
defaultSystemPromptMode: Append
inputs:
  repo:
    type: string
    required: true
  task:
    type: string
    required: true
steps:
  - name: task
    type: Prompt
    systemPrompt: ""
    userPrompt: "{{param.task}}"
    workingDirectory: "{{param.repo}}"
    skillDirectories:
      - "{{param.repo}}/.github/skills"
    reasoningLevel: High
```

Select a model available to the account; the example default is not an assertion that
`claude-opus-4.8` was available during this investigation. Skill paths must resolve to
the same repository as the working directory.

4. **Use deterministic verification after the Prompt step** when correctness is machine
   checkable. The mixed-CLI experiment demonstrates this exact composition.

### Recommended provider work, in order

1. Add an explicit **CLI-like configuration profile** rather than relying on nullable
   defaults. Wire `EnableConfigDiscovery` and `EnableOnDemandInstructionDiscovery` first:
   both have reproduced failing/passing cases in this study. Then map file hooks, plugins
   and separate instruction directories with their own focused fixtures. Cover create
   and resume; do not assume one discovery switch fixes every discovery surface.
2. Expose **memory/session-store choices** and a clear **fresh versus continued session**
   policy. Match interactive CLI and `-p` deliberately: they differ here too.
3. Expose **native interaction modes and callbacks**: plan, autopilot, plan approval,
   `ask_user` (including the elicitation variant when used), and eventually fleet. Also
   match interactive versus non-interactive guidance via `runningInInteractiveMode`. Let
   the Copilot runtime perform these workflows instead of recreating its agent loop.
4. Provide a **transparent Prompt-step option** for callers wanting the CLI's normal
   completion/output behavior, with engine control tools opt-in. Preserve existing
   orchestration semantics for callers that intentionally use those tools.
5. Fix the **reproduced child-idle completion path** and add live regression coverage for
   final response delivery, stop hooks, background tasks, continuation and subagents before
   enabling those advanced modes. Respect SDK `AgentId` rather than inferring root identity
   solely from the current subagent stack.
6. Make the experiment's **effective configuration and capability evidence** available
   in diagnostics: runtime version, actual model/effort, loaded skills/MCPs, selected
   mode, and output provenance.

This is primarily provider/configuration integration work. The evidence does not justify
building a replacement coding harness.

## Reproducibility and limits

The reusable driver, synthetic fixtures, deterministic graders, and offline grader tests
are in [`scripts/copilot-parity`](../../scripts/copilot-parity/README.md).
Raw evidence is retained under **`artifacts/copilot-parity/`**: prompts, process exits,
native session logs, telemetry, effective requests, final workspaces, and holdout results.

- This study tests one model/effort on Windows. It does not establish equivalence across
  every model, provider-routing change, operating system, or runtime release.
- Task quality, exposed reasoning summaries, and reasoning-token counts do not reveal or
  prove identical internal reasoning. Repeated CLI runs themselves are nondeterministic.
- No production GitHub writes, browser/computer-use tasks, long-context compaction,
  interactive clarification round trips, or large-scale fleet jobs were benchmarked.
- The smaller paired suite and configuration interventions support specific conclusions;
  pass counts are descriptive, not confidence bounds or a universal quality ranking.
- Supplementary probes overlapped parts of the main suite. Recorded durations are
  diagnostic data, not a controlled latency comparison.
- One exploratory suite was interrupted by the outer tool's two-minute timeout and was
  rerun with an unbounded outer job and bounded per-trial timeouts. Its partial results
  are retained under `baseline` and excluded from the completed-suite totals.
- The first exploratory TUI teardown encountered a terminal-setup dialog. Its added
  keybinding was restored byte-for-byte from the CLI-created backup (matching SHA-256),
  and the confirmation run cleared inherited terminal identity and exited normally.
- Early driver event serialization omitted the SDK's type discriminator. Native persisted
  events and OTel supplied the evidence for those trials; the corrected driver uses the
  SDK's `SessionEvent.ToJson()`. The completed main suite uses that correction.
- Context7 was unavailable due to quota. Official documentation, the pinned SDK source,
  local runtime help/source, and captured live behavior were used instead.

### Verification of the investigation tooling

- Existing Copilot provider suite: **354 passed**, excluding its older model-specific E2E cases.
- New offline grader/protocol-fixture/Git-audit tests: **8 passed**.
- C# experiment driver: builds with **0 warnings and 0 errors**.
- Recommended YAML example: passes the real `orchestra validate --schema` command.
- Actual interactive CLI confirmation and mixed `orchestra exec` integration: **passed**.
- Synthetic lifecycle contract probe: **fails as expected**, reproducing the existing adapter issue.
- Runtime task acceptance failures are reported above; they are not hidden by these tooling checks.

## Sources

- [SDK 1.0.14 README and architecture][sdk-readme]
- [Pinned .NET client source: runtime launch, session-create mapping and defaults][client]
- [Pinned .NET types: `CopilotClientMode`, session configuration][types]
- [Agent-loop ownership and completion semantics][agent-loop]
- [SDK/CLI compatibility document][compatibility]
- [Skills documentation][skills] and [SDK discovery E2E source][skills-test]
- [Native fleet RPC documentation][fleet]
- [Official CLI interfaces and modes][cli-docs]
- Local Orchestra source: [`CopilotAgent`](../../src/Orchestra.Copilot/CopilotAgent.cs),
  [`CopilotSessionHandler`](../../src/Orchestra.Copilot/CopilotSessionHandler.cs),
  [`PromptExecutor`](../../src/Orchestra.Engine/Orchestration/Executor/PromptExecutor.cs),
  [`SetStatusTool`](../../src/Orchestra.Engine/EngineTools/SetStatusTool.cs).

[sdk-readme]: https://github.com/github/copilot-sdk/blob/e60d9037353249ef16b349eb4012e8c1d113fda5/README.md
[client]: https://github.com/github/copilot-sdk/blob/e60d9037353249ef16b349eb4012e8c1d113fda5/dotnet/src/Client.cs
[types]: https://github.com/github/copilot-sdk/blob/e60d9037353249ef16b349eb4012e8c1d113fda5/dotnet/src/Types.cs
[agent-loop]: https://github.com/github/copilot-sdk/blob/e60d9037353249ef16b349eb4012e8c1d113fda5/docs/features/agent-loop.md
[compatibility]: https://github.com/github/copilot-sdk/blob/e60d9037353249ef16b349eb4012e8c1d113fda5/docs/troubleshooting/compatibility.md
[skills]: https://github.com/github/copilot-sdk/blob/e60d9037353249ef16b349eb4012e8c1d113fda5/docs/features/skills.md
[skills-test]: https://github.com/github/copilot-sdk/blob/e60d9037353249ef16b349eb4012e8c1d113fda5/dotnet/test/E2E/SkillsE2ETests.cs
[fleet]: https://github.com/github/copilot-sdk/blob/e60d9037353249ef16b349eb4012e8c1d113fda5/docs/features/fleet-mode.md
[cli-docs]: https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-copilot-cli

{% endraw %}
