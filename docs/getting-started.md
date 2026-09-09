---
layout: default
title: Getting Started
nav_order: 2
---
{% raw %}

# Getting Started

Orchestra ships as a single command-line tool. This guide takes you from nothing to a running
orchestration, explains what the scaffolded workspace contains, and covers rolling it out to a
team.

## Prerequisites

| Requirement | Why |
|---|---|
| .NET 10 runtime | Orchestra is a .NET tool. The SDK is only needed if you build from source. |
| An agent provider | **GitHub Copilot** (subscription required) or **OpenCode** (installed and on `PATH`). |

Nothing else. Orchestra downloads the Copilot CLI itself on first use — see
[the first run](#the-first-run-and-the-100-mb-download) below.

## Install

```bash
# Run without installing - requires the `dnx` launcher
dnx Orchestra --yes -- <command>

# ...or install as a global tool
dotnet tool install --global Orchestra
orchestra <command>
```

> Only the **`Orchestra`** package exists. There are no `Orchestra.Engine` /
> `Orchestra.Host` / `Orchestra.Copilot` packages on NuGet — if you have seen those in older
> docs, they were never published. To use the libraries directly, reference the projects from
> a clone of the repository.

## Three commands to a first run

```bash
orchestra init      # scaffold a workspace
orchestra doctor    # check this machine is ready
orchestra run hello # run it
```

### `orchestra init`

Creates a workspace in the target directory (default: the current one).

```text
orchestrations/
  hello.yaml                the starter orchestration
.orchestra/
  schemas/*.json            JSON schemas, for editor completion
  skills/                   the authoring skill (only when requested)
  data/                     run history, registry, checkpoints (gitignored)
  .gitignore                excludes data/
orchestra.json              project config; its `scan` block enables running by name
```

It prompts for the template, provider, and model. **Every prompt has a flag**, so supplying
them all makes the command non-interactive, and `--yes` accepts the defaults for whatever is
left:

```bash
orchestra init                                              # fully interactive
orchestra init ./workflows --template research              # prompts only for provider/model
orchestra init --template hello --provider copilot --yes    # unattended
```

Re-running is safe: existing files are reported as skipped, never overwritten, unless you pass
`--force`.

#### Templates

| Template | Demonstrates |
|---|---|
| `hello` *(default)* | A three-step DAG: two Prompt steps, then a `Transform` that costs nothing. |
| `research` | Parallel fan-out — two analyses run concurrently, a third synthesizes them. |
| `code-review` | A deterministic `Command` step (`git diff`) feeding an agent. |
| `approval` | A human-in-the-loop gate that survives a host restart. |
| `generate` | Writes new orchestrations from a description, then machine-checks them. |

All of them run with **no arguments** — every input has a default — so you can try any of them
immediately and edit afterwards.

### `orchestra doctor`

Checks the things that otherwise only fail *during* a run:

| Check | Answers |
|---|---|
| `installation` | Are the tool's bundled schemas and templates present? |
| `configuration` | Which `orchestra.json` is in effect, and does it parse? |
| `data path` | Is the run-history directory writable? |
| `copilot cli` | Is the Copilot CLI cached, or will the first run download it? |
| `copilot auth` | Are credentials valid and is a subscription active? |
| `opencode cli` | Is OpenCode resolvable on `PATH`? |
| `server` | Is a configured server reachable? |

```bash
orchestra doctor                            # human-readable, the default
orchestra doctor --fix                      # download the Copilot CLI now
orchestra doctor --format json              # for scripts and CI
orchestra doctor --offline                  # skip network checks
```

Exit code is `1` if any check fails, so CI can gate on it.

A **malformed `orchestra.json` is reported as a failure** even though the host tolerates it.
That is deliberate: the host logs a warning and silently falls back to built-in defaults, so
none of your settings apply and nothing tells you. `doctor` is the only place that surfaces it.

### `orchestra run`

```bash
orchestra run hello                              # by name, thanks to the scan block
orchestra run hello --param topic="vector databases"
orchestra run hello --report markdown            # print a report afterwards
```

`run` attaches to a running Orchestra instance when one is configured and healthy, and
otherwise spawns a throwaway host just for that run. See
[`--mode`](cli#host-selection---mode).

## The first run, and the ~100 MB download

The Copilot provider needs the GitHub Copilot CLI. Orchestra downloads it automatically the
first time a run needs it: roughly **100 MB**, once per machine per CLI version, cached under
your local application data. During that download the run looks like it has stalled.

Run `orchestra doctor --fix` first to get it out of the way, and to find out *before* paying
that cost whether your credentials actually work.

To use a Copilot CLI you already have, set `ORCHESTRA_COPILOT_CLI_PATH`. For an internal npm
mirror, set `ORCHESTRA_COPILOT_NPM_REGISTRY`.

## What `init` wrote, and why

### `orchestra.json`

The scaffolded config is JSONC — comments and trailing commas are allowed — and binds
`orchestra.schema.json`, so an editor gives you completion and hover documentation for every
key.

```jsonc
{
  "$schema": "./.orchestra/schemas/orchestra.schema.json",

  // NOTE: this is the workspace ROOT, not the orchestrations folder. The host
  // looks for `orchestrations/` and `profiles/` subdirectories inside it.
  "scan": { "directory": ".", "recursive": true, "watch": true },

  "dataPath": "./.orchestra/data",

  "defaultProvider": "copilot",
  "defaultModel": "claude-opus-4.8"
}
```

The `scan` block is what makes `orchestra run hello` work instead of
`orchestra run --run-file ./orchestrations/hello.yaml`. Point `scan.directory` at the
**workspace root**; aiming it at `./orchestrations` makes the host look for
`./orchestrations/orchestrations` and silently register nothing.

### How `orchestra.json` is found

First match wins:

1. `ORCHESTRA_CONFIG_PATH` — an explicit file path.
2. **Project-local** — walking up from the working directory, checking `./orchestra.json` then
   `./.orchestra/orchestra.json` at each level, stopping once it has inspected a repository
   root (a directory containing `.git`).
3. `$XDG_CONFIG_HOME/Orchestra/orchestra.json`.
4. `%APPDATA%\Orchestra\orchestra.json` or `~/.config/Orchestra/orchestra.json`.

Project-local beats the user-global locations, the same way `.editorconfig`, `global.json`, and
`Directory.Build.props` behave. So a scaffolded folder is self-contained, and a personal config
still applies everywhere else. `orchestra.mcp.json` and `orchestra.services.json` are looked up
next to whichever `orchestra.json` won.

`orchestra doctor` prints which file is actually in effect.

### The starter orchestration

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
    userPrompt: Research "{{param.topic}}" and list the key findings as bullet points.

  - name: brief
    type: Prompt
    dependsOn: [research]
    systemPrompt: You are a technical writer who values clarity over length.
    userPrompt: |
      Using the research below, write a 120-word executive brief on {{param.topic}}.

      {{research.output}}

  - name: document          # no agent call, no cost
    type: Transform
    dependsOn: [brief]
    template: |
      # {{param.topic}}

      {{brief.output}}
```

Three things worth noticing:

- **`dependsOn` builds the DAG.** Steps with no unmet dependencies run in parallel.
- **Steps communicate through `{{stepName.output}}`.** Only namespace-prefixed expressions
  resolve — `{{param.topic}}`, `{{vars.x}}`, `{{env.X}}`, `{{stepName.output}}`. A bare
  `{{topic}}` is *not* an expression and reaches the model as literal text.
- **Not every step needs an agent.** `Transform`, `Command`, `Script`, and `Http` are
  deterministic, free, and cannot hallucinate. Prefer them wherever the work is not genuinely
  fuzzy.

## Checking your work: `orchestra validate`

Runs the same two gates the executor applies before a real run — the parser and the
template-expression validator — with no server, no agent, and no cost.

```bash
orchestra validate ./orchestrations/hello.yaml
orchestra validate ./draft.yaml --format json
```

| Exit code | Meaning |
|---|---|
| `0` | Valid. |
| `1` | Parses but is invalid, or does not parse at all. |
| `2` | File missing or unreadable. |

The most common failure it catches is the bare `{{name}}` above. To show template syntax to a
model deliberately — in a prompt that documents Orchestra itself, say — escape it as
`\{{...}}`.

## Letting an agent write orchestrations for you

Orchestra ships an **`orchestration-authoring`** Agent Skill: the schema, every step type, the
template expressions, and the common mistakes, written for an agent to consult.

```bash
orchestra init --with-skill        # drops it into .orchestra/skills/
```

Any Prompt step can then load it:

```yaml
    skillDirectories:
      - ../.orchestra/skills/orchestration-authoring
```

> `skillDirectories` paths resolve against the **orchestration file's** directory, not your
> working directory, and a missing directory is skipped **silently** — no error, just worse
> output. Count the `../` hops from the file, and check the resolved path exists.

The `generate` template wires this up for you:

```bash
orchestra init --template generate      # implies --with-skill
orchestra run generate --param description="review my open PRs and post a digest"
```

It drafts an orchestration, runs a reviewer loop over it, then hands the result to a
deterministic `Script` step that writes the file and verifies it with `orchestra validate`. A
draft that would not parse fails the run instead of landing on disk.

## Rolling Orchestra out to a team

### What to commit

| Path | Commit? |
|---|---|
| `orchestrations/` | **Yes** — the workflows are the point. |
| `orchestra.json` | **Yes** — everyone gets the same scan, model, and provider defaults. |
| `.orchestra/schemas/` | Yes. Small, and gives editor validation without network access. |
| `.orchestra/skills/` | Optional. ~135 KB; commit it if agents author orchestrations in this repo. |
| `.orchestra/data/` | **No** — machine-local run history. The scaffolded `.gitignore` excludes it. |

A teammate then needs only:

```bash
dotnet tool install --global Orchestra
orchestra doctor     # tells them exactly what their machine is missing
orchestra list       # sees every orchestration in the repo
```

Because `orchestra.json` is discovered by walking up from the working directory, this works
anywhere inside the repository with no per-machine setup.

### Keep secrets out of the config

`orchestra.json` expands `${VAR}` and `"env:VAR"` against the environment before parsing, and a
**missing variable is a hard error** rather than an empty string:

```jsonc
{ "copilot": { "gitHubToken": "${GITHUB_TOKEN}" } }
```

### In CI

```bash
orchestra doctor --format json --offline          # fail fast on a broken workspace
orchestra validate ./orchestrations/deploy.yaml   # gate PRs on parseable orchestrations
orchestra run deploy --no-interactive --quiet     # exits 2 if anything needs a human
```

`--no-interactive` matters: `run` auto-degrades when stdin is redirected, so a human-in-the-loop
pause exits `2` with instructions rather than hanging a build.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `orchestra list` returns nothing | `scan.directory` is not the workspace root, or the orchestration file is not under `orchestrations/`. |
| Settings in `orchestra.json` seem ignored | It failed to parse, and the host silently used defaults. Run `orchestra doctor`. |
| A run hangs on the first Prompt step | The one-time Copilot CLI download. `orchestra doctor --fix`. |
| The model receives literal `{{name}}` text | Bare expression. Use `{{param.name}}`; `orchestra validate` catches it. |
| A step ignores its skill | `skillDirectories` resolved to a directory that does not exist; a missing one is skipped silently. |
| A checker loop never iterates | `exitPattern` is a substring match, so `VALID` also matches `INVALID`. Use disjoint markers. |

## Next steps

- [CLI reference](cli) — every command, flag, and exit code
- [Engine](engine) — step types, triggers, hooks, checkpointing
- [Host](host) — REST API, MCP server, configuration reference
- [Agent providers](copilot) — Copilot vs OpenCode, per-step controls
- [API reference](api-reference) — complete REST documentation
{% endraw %}
