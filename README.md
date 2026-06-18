# Orchestra

**Deterministic AI orchestrations for .NET.** Describe a workflow as a declarative DAG of
steps in a single JSON/YAML file, and Orchestra runs it — resolving dependencies, streaming
progress, and driving each `Prompt` step on a real coding agent (GitHub Copilot or OpenCode).
It ships as one self-contained .NET tool with a built-in web portal, a one-shot runner, MCP
integration, triggers, checkpointing, and human-in-the-loop pauses.

```text
inputs ─▶ ┌─ research ─┐                      orchestra run research-assistant
          │            ├─▶ brief ─▶ output     --param topic="vector databases"
          └─ (Copilot) ─┘  (OpenCode)
```

## Highlights

- **One file, one DAG.** Steps (`Prompt`, `Command`, `Script`, `Http`, `Transform`, `Approval`, nested `Orchestration`) wired by `dependsOn`, with template expressions (`{{param.x}}`, `{{step.output}}`).
- **Pluggable agents.** Run any Prompt step on `copilot` or `opencode`, selectable per step or per orchestration.
- **MCP-native.** Attach Model Context Protocol servers to steps, and expose your orchestrations *as* an MCP server.
- **Agent Skills.** Drop `SKILL.md` directories onto a step so the agent gains specialized, on-demand workflows.
- **Operate it.** A web portal, triggers (cron/webhook/file/manual), durable checkpoint/resume, profiles & tags, and a full CLI — all under the single `orchestra` command.

## Install

Orchestra is published to NuGet as the **`Orchestra`** .NET tool (command: `orchestra`).

```bash
# Run without installing (recommended for trying it out) — needs the .NET 10 SDK:
dnx Orchestra --yes -- <command> [options]

# …or install globally:
dotnet tool install --global Orchestra
orchestra <command> [options]
```

Running `orchestra` with no command prints the help. `orchestra <command> --help` documents any subcommand.

## Your first orchestration

Save this as `research-assistant.yaml`. It's a two-step DAG: `research` produces findings, then
`brief` summarizes them. (`orchestra schemas` drops the JSON Schema locally for editor autocomplete.)

```yaml
# yaml-language-server: $schema=./.orchestra/schemas/orchestration.schema.json
name: research-assistant
description: Research a topic, then write a short executive brief about it.
defaultModel: claude-opus-4.8

inputs:
  topic:
    type: string
    required: true

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
      Using the research below, write a 150-word executive brief on {{param.topic}}.

      {{research.output}}
```

## Run it

`orchestra run` runs one orchestration to completion. With `dnx` (no install):

```bash
dnx Orchestra --yes -- run --run-file ./research-assistant.yaml --param topic="vector databases" --report markdown
```

`--mode` decides *where* it runs:

- **`auto`** (default) — attach to a running Orchestra instance when one is configured (via `--server`, the `ORCHESTRA_URL` env var, or the `hostBaseUrl`/`urls` in your discovered `orchestra.json`) **and** healthy; otherwise spawn a throwaway in-process host just for this run.
- **`existing`** — require a healthy configured instance (error if none).
- **`isolated`** — always run self-contained. `orchestra exec` is shorthand for `run --mode isolated`.

```bash
orchestra run research-assistant --param topic="vector databases"   # registered, on your server (auto)
orchestra exec --run-file ./research-assistant.yaml                  # self-contained one-shot
orchestra portal                                                     # long-running host + web UI
```

The portal (`orchestra portal`) serves the dashboard, REST API, and MCP endpoints; the client
verbs (`orchestra list`, `get`, `register`, `attach`, `runs`, `triggers`, `profiles`, `tags`,
`pending`, `respond`, …) talk to it over HTTP/SSE.

## Step types

| Type | Purpose |
|------|---------|
| `Prompt` | Run a coding agent (Copilot/OpenCode) with a system/user prompt, optional MCPs, subagents, skills, attachments. |
| `Command` | Execute a shell/CLI command and capture its output. |
| `Script` | Run an inline PowerShell/bash script. |
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
instructions and any reference files. Orchestra also ships an **`orchestration-authoring`** skill
(under [`skills/`](skills/)) you can hand to an agent so it writes valid Orchestra files for you.

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
| [`docs/getting-started.md`](docs/getting-started.md) | Install, first orchestration, embedding Orchestra in your own host |
| [`docs/cli.md`](docs/cli.md) | Full `orchestra` command reference + exit codes |
| [`docs/engine.md`](docs/engine.md) | Step types, triggers, hooks, checkpointing, template expressions |
| [`docs/host.md`](docs/host.md) | REST API, MCP server, profiles, tags, retention |
| [`docs/copilot.md`](docs/copilot.md) | Agent providers, per-step controls, capability matrix |
| [`skills/orchestration-authoring/`](skills/orchestration-authoring/) | The authoring skill (full schema reference + examples) |
| [`examples/`](examples/) | Runnable example orchestrations |

## License

MIT — see [LICENSE](LICENSE). Repository: <https://github.com/MoaidHathot/Orchestra>.
