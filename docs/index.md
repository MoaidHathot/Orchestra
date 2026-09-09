---
layout: default
title: Home
nav_order: 1
---
{% raw %}

# Orchestra

**Deterministic AI agent orchestrations.** Describe a workflow as a declarative DAG of steps in
a single JSON/YAML file, and Orchestra runs it — resolving dependencies, streaming progress, and
driving each `Prompt` step on a real coding agent (GitHub Copilot or OpenCode).

It ships as **one self-contained command-line tool** with a built-in web portal, a one-shot
runner, MCP integration, triggers, checkpointing, and human-in-the-loop pauses.

## Quick start

```bash
dotnet tool install --global Orchestra

orchestra init      # scaffold a workspace with a runnable example
orchestra doctor    # check this machine is ready
orchestra run hello # run it
```

Or without installing anything, using the `dnx` launcher:

```bash
dnx Orchestra --yes -- init
```

See [Getting Started](getting-started) for the full walkthrough.

## What an orchestration looks like

```yaml
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
    userPrompt: Research "{{param.topic}}" and list the key findings as bullet points.

  - name: brief
    type: Prompt
    dependsOn: [research]
    systemPrompt: You are a technical writer who values clarity over length.
    userPrompt: |
      Using the research below, write a 120-word executive brief on {{param.topic}}.

      {{research.output}}

  - name: document          # deterministic: no agent call, no cost
    type: Transform
    dependsOn: [brief]
    template: "# {{param.topic}}\n\n{{brief.output}}"
```

`dependsOn` builds the DAG; steps with no unmet dependencies run in parallel automatically.

## Features

- **One file, one DAG.** `Prompt`, `Command`, `Script`, `Http`, `Transform`, `Approval`, and
  nested `Orchestration` steps wired by `dependsOn`, with template expressions.
- **Pluggable agents.** Run any Prompt step on `copilot` or `opencode`, chosen per step or per
  orchestration. The engine fails a step fast if it uses a feature the provider lacks, rather
  than silently dropping it.
- **Deterministic where it matters.** Command, Script, Transform, and Http steps cost nothing
  and cannot hallucinate. A Script step can set its own status or halt the whole run.
- **Human-in-the-loop.** `Approval` steps and the `request_user_input` engine tool persist a
  pending record to disk, so a paused run survives a host restart.
- **MCP-native.** Attach Model Context Protocol servers to steps, and expose your
  orchestrations *as* an MCP server for other agents to discover and invoke.
- **Agent Skills.** Ship `SKILL.md` directories to a step — including the bundled
  `orchestration-authoring` skill that teaches an agent to write Orchestra files.
- **Operate it.** A web portal, triggers (cron / webhook / file / manual), durable
  checkpoint-and-resume, profiles and tags, run history and export.

## Tooling

| Command | Purpose |
|---|---|
| `orchestra init` | Scaffold a workspace: starter orchestration, schemas, `orchestra.json`. |
| `orchestra doctor` | Verify config, data path, agent CLI, credentials, and server before a run. |
| `orchestra validate` | Parse an orchestration and check its expressions — no server, no agent. |
| `orchestra run` / `exec` | Run one orchestration to completion, streaming progress. |
| `orchestra portal` | Long-running host: dashboard, REST API, and MCP endpoints. |

## Documentation

| Doc | Contents |
|---|---|
| [Getting Started](getting-started) | Install, first run, workspace layout, team rollout |
| [CLI](cli) | Every command, flag, and exit code |
| [Engine](engine) | Step types, triggers, hooks, checkpointing, template expressions |
| [Host](host) | Configuration reference, REST API, MCP server, retention |
| [Agent providers](copilot) | Copilot vs OpenCode, per-step controls, capability matrix |
| [API reference](api-reference) | Complete REST API |
| [Run storage](run-storage) | On-disk run records and parent/child links |

## Requirements

- .NET 10 runtime
- An agent provider: a **GitHub Copilot** subscription, or **OpenCode** on `PATH`

> Orchestra is distributed as the single **`Orchestra`** package on NuGet. There are no
> `Orchestra.Engine` / `Orchestra.Host` / `Orchestra.Copilot` packages — to use the libraries
> directly, reference the projects from a clone of the repository.

## License

MIT. Repository: <https://github.com/MoaidHathot/Orchestra>.
{% endraw %}
