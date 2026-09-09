# Orchestra

**Deterministic AI agent orchestrations.** Describe a workflow as a declarative DAG of
steps in a single JSON/YAML file, and Orchestra runs it — resolving dependencies, streaming
progress, and driving each `Prompt` step on a real coding agent (GitHub Copilot or OpenCode).
It ships as one self-contained command-line tool with a built-in web portal, a one-shot runner,
MCP integration, triggers, checkpointing, and human-in-the-loop pauses.

```text
inputs ─▶ ┌─ research ─┐                       orchestra init
          │            ├─▶ brief ─▶ document   orchestra doctor
          └─ (Copilot) ─┘         (no model)   orchestra run hello
```

## Highlights

- **Zero to running in two commands.** `orchestra init` scaffolds a working workspace; `orchestra doctor` verifies your machine before the first run instead of failing mid-run.
- **One file, one DAG.** Steps (`Prompt`, `Command`, `Script`, `Http`, `Transform`, `Approval`, nested `Orchestration`) wired by `dependsOn`, with template expressions (`{{param.x}}`, `{{step.output}}`).
- **Pluggable agents.** Run any Prompt step on `copilot` or `opencode`, selectable per step or per orchestration.
- **MCP-native.** Attach Model Context Protocol servers to steps, and expose your orchestrations *as* an MCP server.
- **Agent Skills.** Drop `SKILL.md` directories onto a step so the agent gains specialized, on-demand workflows.
- **Operate it.** A web portal, triggers (cron/webhook/file/manual), durable checkpoint/resume, profiles & tags, and a full CLI — all under the single `orchestra` command.

## Install

Orchestra is distributed as the **`Orchestra`** command-line tool (command: `orchestra`) on NuGet.
Your orchestrations are just JSON/YAML — no C# required.

```bash
# Run without installing (recommended for trying it out) — requires the `dnx` launcher:
dnx Orchestra --yes -- <command> [options]

# …or install it as a global tool:
dotnet tool install --global Orchestra
orchestra <command> [options]
```

Running `orchestra` with no command prints the help. `orchestra <command> --help` documents any subcommand.

## Quick start

```bash
orchestra init             # scaffold a workspace with a runnable example
orchestra doctor           # verify prerequisites before the first run
orchestra run hello        # run it
orchestra new my-workflow  # then add your own, from a template
```

No agent set up yet? `orchestra init --template smoke-test --yes && orchestra run smoke-test`
gives you a green run in about a second with nothing configured - it proves the install before
credentials enter the picture.

`init` writes a starter orchestration under `orchestrations/`, the JSON schemas under
`.orchestra/schemas/` for editor autocomplete, and an `orchestra.json` whose `scan` block is
what lets you run orchestrations **by name**. It prompts for the template, provider, and model —
pass any of those as flags to skip the prompt, or `--yes` to accept every default:

```bash
orchestra init ./my-workflows --template research --provider copilot --yes
```

Five templates ship with the tool, and every one runs with **no arguments**:

| Template | Demonstrates |
|---|---|
| `hello` *(default)* | Three-step DAG: two Prompt steps, then a `Transform` that costs nothing. |
| `smoke-test` | Two deterministic steps. No agent, no credentials, no download. |
| `research` | Parallel fan-out — two analyses run concurrently, a third synthesizes them. |
| `code-review` | A deterministic `Command` step (`git diff`) feeding an agent. |
| `approval` | A human-in-the-loop gate that survives a host restart. |
| `generate` | Writes new orchestrations from a description, then machine-checks them. |

`doctor` checks the things that otherwise only fail *during* a run: which `orchestra.json` is in
effect and whether it parses, whether the data path is writable, whether the agent CLI is present
and authenticated, and whether a configured server is reachable. The first Copilot run downloads
the Copilot CLI (~100 MB, once per machine) — `orchestra doctor --fix` does it up front instead of
mid-run. If credentials are missing, `orchestra login` runs the provider's own sign-in flow.

Orchestra discovers `orchestra.json` by walking up from your working directory, so a scaffolded
folder is self-contained; a user-global config at `%APPDATA%\Orchestra\` (or `~/.config/Orchestra/`)
applies everywhere else.

`validate` parses an orchestration and checks its expressions without a server, an agent, or any
cost — it exits `0`/`1`/`2` so CI and Script steps can branch on it:

```bash
orchestra validate ./orchestrations/hello.yaml
```

## Your first orchestration

`orchestra init` writes this for you as `orchestrations/hello.yaml`; here it is in full. It's a
DAG: `research` produces findings, `brief` summarizes them, and `document` shapes the result
without calling a model at all.

```yaml
# yaml-language-server: $schema=../.orchestra/schemas/orchestration.schema.json
name: hello
description: Research a topic, brief it, then assemble a document.
defaultModel: claude-opus-4.8

inputs:
  topic:
    type: string
    required: false
    default: deterministic AI agent orchestration

steps:
  - name: research
    type: Prompt
    systemPrompt: You are a meticulous research assistant.
    userPrompt: Research "{{param.topic}}" and list the key findings as concise bullet points.

  - name: brief
    type: Prompt
    dependsOn: [research]
    systemPrompt: You are a technical writer.
    userPrompt: |
      Using the research below, write a 120-word executive brief on {{param.topic}}.

      {{research.output}}

  - name: document
    type: Transform
    dependsOn: [brief]
    template: |
      # {{param.topic}}

      {{brief.output}}
```

Because `topic` has a default, `orchestra run hello` works with no arguments — and
`--param topic="vector databases"` points it somewhere else.

## Run it

`orchestra run` runs one orchestration to completion. With `dnx` (no install):

```bash
dnx Orchestra --yes -- run --run-file ./orchestrations/hello.yaml --param topic="vector databases" --report markdown
```

`--mode` decides *where* it runs:

- **`auto`** (default) — attach to a running Orchestra instance when one is configured (via `--server`, the `ORCHESTRA_URL` env var, or the `hostBaseUrl`/`urls` in your discovered `orchestra.json`) **and** healthy; otherwise spawn a throwaway in-process host just for this run.
- **`existing`** — require a healthy configured instance (error if none).
- **`isolated`** — always run self-contained. `orchestra exec` is shorthand for `run --mode isolated`.

```bash
orchestra run hello --param topic="vector databases"   # registered, on your server (auto)
orchestra exec --run-file ./orchestrations/hello.yaml  # self-contained one-shot
orchestra portal                                       # long-running host + web UI
```

The portal (`orchestra portal`) serves the dashboard, REST API, and MCP endpoints; the client
verbs (`orchestra list`, `get`, `register`, `attach`, `runs`, `triggers`, `profiles`, `tags`,
`pending`, `respond`, …) talk to it over HTTP/SSE.

## Step types

| Type | Purpose |
|------|---------|
| `Prompt` | Run a coding agent (Copilot/OpenCode) with a system/user prompt, optional MCPs, subagents, skills, attachments. |
| `Command` | Execute a shell/CLI command and capture its output. |
| `Script` | Run an inline PowerShell/bash/python/node script. Can deterministically set its status or halt the run (`orchestra_complete`/`set_status` equivalent) via the control channel. |
| `Http` | Make an HTTP request (templated URL/headers/body). |
| `Transform` | Render a template — pure string shaping, no agent call. |
| `Approval` | Pause for human approval (HITL). |
| `Orchestration` | Invoke another orchestration as a child (composition). |

## Agent providers

Every `Prompt` step runs on an agent provider, chosen by precedence **step `provider` →
orchestration `defaultProvider` → host default**:

```yaml
defaultProvider: opencode
steps:
  - name: draft   # runs on OpenCode (the orchestration default)
    type: Prompt
    userPrompt: "..."
  - name: review
    type: Prompt
    provider: copilot   # this step overrides to Copilot
    userPrompt: "..."
```

- **`copilot`** — GitHub Copilot CLI via the Copilot SDK (supports the full feature surface).
- **`opencode`** — spawns an `opencode serve` HTTP server; supports MCPs, subagents, reasoning, working dir, skills, engine tools, attachments, permissions, excluded tools, infinite-session toggle, and worker swap with session resume.

The engine **fails a step fast** if it uses a feature the chosen provider doesn't support
(rather than silently dropping it). See [`docs/copilot.md`](docs/copilot.md) and the provider
notes for the full capability matrix.

## Agent Skills

Agent Skills are `SKILL.md` directories that give a Prompt step specialized, discover-on-demand
knowledge and workflows. Reference them per step with `skillDirectories` (relative paths resolve
from the orchestration file):

```yaml
steps:
  - name: code-review
    type: Prompt
    skillDirectories:
      - ./skills/code-review
      - ./skills/security-analysis
    userPrompt: "Review these changes: {{param.changes}}"
```

A `SKILL.md` is Markdown with a small YAML frontmatter (`name`, `description`) plus the
instructions and any reference files. Orchestra ships an **`orchestration-authoring`** skill
(under [`skills/`](skills/)) that teaches an agent to write valid Orchestra files. It ships
inside the tool too, so `orchestra init --with-skill` drops a copy into `.orchestra/skills/`
without cloning this repo — that's what the `generate` template uses to write orchestrations
from a plain-English description:

```bash
orchestra init --template generate
orchestra run generate --param description="review my open PRs every morning and post a digest"
```

The generated file is machine-checked with `orchestra validate` before it's written, so a draft
that wouldn't parse fails the run instead of landing on disk.

## MCP integration

Attach [Model Context Protocol](https://modelcontextprotocol.io/) servers (local stdio or remote
HTTP) to a step so the agent gains tools:

```yaml
mcps:
  - name: zakira-recall
    type: local
    command: dnx
    arguments: ["Zakira.Recall", "--yes", "--", "mcp"]
steps:
  - name: research
    type: Prompt
    mcps: [zakira-recall]
    userPrompt: "Use the web tools to research {{param.topic}} and summarize."
```

Orchestra also **exposes your orchestrations as an MCP server** (`/mcp/data`), so other agents
can discover and invoke them. See [`docs/host.md`](docs/host.md).

## Documentation

| Doc | Contents |
|-----|----------|
| [`docs/getting-started.md`](docs/getting-started.md) | Install, first run, workspace layout, rolling out to a team |
| [`docs/cli.md`](docs/cli.md) | Full `orchestra` command reference + exit codes |
| [`docs/engine.md`](docs/engine.md) | Step types, triggers, hooks, checkpointing, template expressions |
| [`docs/host.md`](docs/host.md) | Configuration reference, REST API, MCP server, retention |
| [`docs/copilot.md`](docs/copilot.md) | Agent providers, per-step controls, capability matrix |
| [`skills/orchestration-authoring/`](skills/orchestration-authoring/) | The authoring skill (full schema reference + examples) |
| [`templates/`](templates/) | Starter orchestrations scaffolded by `orchestra init` |
| [`examples/`](examples/) | Runnable example orchestrations |
| [`schemas/`](schemas/) | JSON schemas for orchestrations and `orchestra.json` |

## License

MIT — see [LICENSE](LICENSE). Repository: <https://github.com/MoaidHathot/Orchestra>.
