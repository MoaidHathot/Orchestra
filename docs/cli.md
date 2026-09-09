---
layout: default
title: CLI
nav_order: 6
---
{% raw %}

# Orchestra CLI

The Orchestra CLI (`orchestra`) is the single entry point to Orchestra: it scaffolds and
diagnoses a workspace, runs orchestrations, and acts as an HTTP/SSE client for the Orchestra
REST API. Built on **Spectre.Console.Cli**, it provides typed arguments, per-command `--help`,
typo correction, and live event streaming for runs that include human-in-the-loop pauses.

> Most commands do **not** require a running server. `run`/`exec` and the management verbs
> (`list`, `get`, `register`, `runs`, `triggers`, `profiles`, `tags`) attach to a running
> instance when one is configured and healthy, and otherwise spawn a throwaway in-process host
> for the duration of the command — see [`--mode`](#host-selection---mode). Only the
> live-runtime verbs (`active`, `cancel`, `server-status`, `pending`, `respond`,
> `triggers fire`) need a server that is already up.

---

## Quick start

```bash
orchestra init      # scaffold a workspace: starter orchestration, schemas, orchestra.json
orchestra doctor    # verify prerequisites before the first run
orchestra run hello # run the scaffolded orchestration by name
```

### `init` — scaffold a workspace

Creates a ready-to-run project in the target directory (default: the current one):

```text
orchestrations/hello.yaml     the starter orchestration
.orchestra/schemas/*.json     JSON schemas for editor $schema validation
.orchestra/.gitignore         excludes the local run history
orchestra.json                project config; its `scan` block registers orchestrations by name
```

Interactive by default. Every prompted value has a flag, so supplying them all makes the
command non-interactive; `--yes` accepts defaults for whatever is left.

| Option | Purpose |
|--------|---------|
| `[DIRECTORY]` | Target directory (default: current). |
| `-t, --template <ID>` | `hello` (default), `research`, `code-review`, `approval`, `generate`. |
| `--with-skill` | Also copy the `orchestration-authoring` Agent Skill into `.orchestra/skills/`. Implied by `--template generate`. |
| `-p, --provider <PROVIDER>` | `copilot` (default) or `opencode`. |
| `-m, --model <ID>` | Default model (default: `claude-opus-4.8`). |
| `--no-config` | Skip `orchestra.json`. |
| `--no-schemas` | Skip local schemas; reference the public GitHub schema URL instead. |
| `-f, --force` | Overwrite existing files (otherwise they are skipped). |
| `-y, --yes` | Never prompt. |

```bash
orchestra init
orchestra init ./my-workflows --template research
orchestra init --template hello --provider copilot --yes
```

`init` is idempotent — re-running it leaves existing files untouched unless you pass `--force`.

### `doctor` — verify prerequisites

Checks everything that otherwise only fails *during* a run:

| Check | What it answers |
|-------|-----------------|
| `installation` | Are the tool's bundled schemas and templates present? |
| `configuration` | Which `orchestra.json` is in effect, and does it parse? |
| `data path` | Is the run-history directory writable? |
| `copilot cli` | Is the Copilot CLI cached, or will the first run download ~100 MB? |
| `copilot auth` | Are credentials valid and is a Copilot subscription active? |
| `opencode cli` | Is OpenCode resolvable on PATH? |
| `server` | Is a configured server reachable? |

| Option | Purpose |
|--------|---------|
| `--format <FORMAT>` | `text` (default) or `json`. |
| `-p, --provider <PROVIDER>` | Check only `copilot` or `opencode`. |
| `--fix` | Download the Copilot CLI now instead of during the first run. |
| `--offline` | Skip checks that touch the network. |

Exit code is `1` when any check fails, so CI can gate on it.

```bash
orchestra doctor
orchestra doctor --fix
orchestra doctor --provider opencode --format json
```

A malformed `orchestra.json` is reported as a **failure** here even though the host tolerates
it — the host logs a warning and silently runs on built-in defaults, so none of your settings
apply. `doctor` is the only place that surfaces this.

### `validate` — check an orchestration without running it

Runs the same two gates the executor applies before a real run: the orchestration parser and
the template-expression validator. No server, no agent, no cost.

```bash
orchestra validate ./orchestrations/hello.yaml
orchestra validate ./draft.yaml --format json
```

| Exit code | Meaning |
|---|---|
| `0` | Valid. |
| `1` | Parses but is invalid, or does not parse at all. |
| `2` | File missing or unreadable. |

The distinct exit codes let a `Script` step branch on the result, which is how the `generate`
template machine-checks its own output before writing a file.

The most common failure it catches is a **bare `{{name}}` expression**. Only the
namespace-prefixed forms resolve (`{{param.name}}`, `{{vars.name}}`, `{{env.NAME}}`,
`{{stepName.output}}`); a bare `{{name}}` is passed through to the model as literal text. To
show template syntax to a model deliberately, escape it as `\{{...}}`.

---

## Installation & invocation

Orchestra is published to NuGet as the **`Orchestra`** package with the command name
`orchestra`.

```bash
# Run without installing (requires the `dnx` launcher):
dnx Orchestra --yes -- <command> [options]

# …or install it as a global tool:
dotnet tool install --global Orchestra
orchestra <command> [options]
```

From a checkout:

```bash
dotnet run --project src/Orchestra.Cli -- list
```

Running `orchestra` with no arguments prints the help — or, on a machine with no
configuration and no run history, a short first-run banner pointing at `init` and `doctor`.

---

## Server URL resolution

Every command resolves the server URL in this order:

1. `--server <URL>` / `-s <URL>` flag on the command line.
2. `ORCHESTRA_URL` environment variable.
3. `hostBaseUrl` (or the first `urls` entry) in the discovered `orchestra.json`.
4. Fallback: `http://localhost:5000`.

```bash
orchestra -s https://orchestra.internal:8443 list
ORCHESTRA_URL=https://orchestra.internal:8443 orchestra list
```

There is no authentication layer in the CLI itself — the server is responsible for
gating access (e.g., reverse proxy, ASP.NET Core middleware).

---

## Configuration discovery

`orchestra.json` is resolved in this order, and the first match wins:

1. `ORCHESTRA_CONFIG_PATH` — an explicit file path.
2. **Project-local** — walking up from the working directory, checking `./orchestra.json`
   then `./.orchestra/orchestra.json` at each level. The walk stops after inspecting a
   repository root (a directory containing `.git`), so a config belonging to an unrelated
   parent checkout is never picked up. This is what `orchestra init` scaffolds.
3. `$XDG_CONFIG_HOME/Orchestra/orchestra.json`.
4. `%APPDATA%\Orchestra\orchestra.json` (Windows) or `~/.config/Orchestra/orchestra.json`.

Project-local beats the user-global locations, matching how `.editorconfig`, `global.json`,
and `Directory.Build.props` behave. `orchestra.mcp.json` and `orchestra.services.json` are
looked up next to whichever `orchestra.json` won, falling back to the user-global directory.

Run `orchestra doctor` to see which file is actually in effect.

### Environment variables

| Variable | Purpose |
|----------|---------|
| `ORCHESTRA_CONFIG_PATH` | Explicit `orchestra.json` path; highest precedence. |
| `ORCHESTRA_URL` | Server URL for client commands. |
| `ORCHESTRA_DATA_PATH` | Data path for run history / registry. |
| `ORCHESTRA_ORCHESTRATIONS_PATH` | Workspace directory scanned for orchestrations. |
| `ORCHESTRA_COPILOT_CLI_PATH` | Use a pre-installed Copilot CLI instead of the managed download. |
| `ORCHESTRA_COPILOT_NPM_REGISTRY` | npm registry mirror for the Copilot CLI download. |
| `ORCHESTRA_OPENCODE_PATH` | Path to the `opencode` binary when it is not on PATH. |

---

## Output format

JSON-producing commands accept `--format`:

| Value             | Behaviour                                                      |
|-------------------|----------------------------------------------------------------|
| `json` (default)  | Pretty-printed JSON to stdout. Machine-friendly; pipe to `jq`. |
| `table`           | Spectre.Console rendered table for humans.                     |

`doctor` is the exception: it defaults to `text` and accepts `text` or `json`, because it is
read by a human deciding whether to proceed.

Streaming commands (`run`, `attach`) emit live event lines, not buffered JSON, and
therefore do not accept `--format`.

```bash
orchestra list --format table
orchestra runs list --limit 50 --format table
```

---

## Exit codes

`run` and `attach` follow a deliberate exit-code convention so shell pipelines can
react to outcomes without parsing output:

| Code | Meaning                                                                            |
|------|------------------------------------------------------------------------------------|
| `0`  | Orchestration completed with `Succeeded`.                                          |
| `1`  | Errored, finished with a non-success status, was cancelled, or disconnected.       |
| `2`  | A HITL pause arrived but stdin is non-interactive (CI, pipes, `--no-interactive`). |
| `130`| POSIX SIGINT — you pressed Ctrl+C. The run continues on the server.                |

Non-streaming commands return `0` on success and `1` on any error.

---

## Identifying orchestrations: ID or declared name

Every CLI verb that takes an `<id>` argument accepts either the registry-generated ID
(e.g. `research-assistant-a1b2c3d4`) or the orchestration's declared `name` field
(e.g. `research-assistant`). The response always echoes back the canonical registry ID
so scripts can cache it. This applies to `get`, `run`, `remove`, `enable`, `disable`,
`tags add/get/remove`, plus every endpoint under `runs <name> <run-id>` (the latter uses
the declared name by convention because run records are stored by name).

If multiple orchestrations share a name, reference them explicitly by ID.

---

## Command reference

Every command supports `--help`. The summaries here are the same text Spectre prints.

### Getting started

| Command | Purpose |
|---|---|
| `orchestra init [DIRECTORY] [-t ID] [-p PROVIDER] [-m MODEL] [--no-config] [--no-schemas] [-f] [-y]` | Scaffold a workspace: starter orchestration, JSON schemas, and `orchestra.json`. |
| `orchestra doctor [--format text\|json] [-p PROVIDER] [--fix] [--offline]` | Check prerequisites: config, data path, agent CLI, credentials, server. |
| `orchestra schemas [-o DIR] [-f]` | Copy the bundled JSON schemas into a local directory (default `./.orchestra/schemas`). |
| `orchestra validate <PATH> [--format text\|json]` | Parse an orchestration and check its template expressions without running it. |

See [Quick start](#quick-start) for the full option tables.

### Orchestration management

| Command | Purpose |
|---|---|
| `orchestra list [--filter TEXT] [--tag TAG ...] [--enabled\|--disabled] [--format table]` | List orchestrations. Filters are client-side and conjunctive. |
| `orchestra get <id> [--format table]` | Get details for a single orchestration. |
| `orchestra register <path>` | Register a `.json` / `.yaml` orchestration file. |
| `orchestra remove <id>` | Remove an orchestration from the registry. |
| `orchestra scan <directory>` | Walk a directory and register every orchestration in it. |
| `orchestra enable <id>` | Enable an orchestration's trigger. |
| `orchestra disable <id>` | Disable an orchestration's trigger. |

#### Filter semantics for `list`

The Host returns the full registry on every call; the CLI narrows it client-side:

- `--filter <TEXT>` — case-insensitive substring match against the `name`,
  `description`, or `path` of each entry. Matches if any of those contains the text.
- `--tag <TAG>` — repeatable; an entry must carry **all** listed tags (AND semantics).
- `--enabled` / `--disabled` — keep only entries with the corresponding trigger state.
  Mutually exclusive; passing both fails validation.

```bash
orchestra list --filter deploy
orchestra list --tag prod --tag nightly
orchestra list --enabled --format table
orchestra list --filter research --tag prod --enabled
```

### Execution

| Command | Purpose |
|---|---|
| `orchestra run [NAME] [--run-file PATH] [--mode MODE] [--param k=v ...] [--report FORMAT] [--detailed] [--no-interactive] [-q\|--quiet] [-V\|--verbose] [--by NAME]` | Run one orchestration to completion, streaming live SSE and prompting inline on HITL pauses. |
| `orchestra exec [...same flags]` | Shorthand for `run --mode isolated`: always self-contained. |
| `orchestra attach <orchestration> <run-id> [--no-interactive] [-q] [-V] [--by NAME]` | Re-attach to a still-running run and stream the remaining events. |
| `orchestra portal [--urls URLS] [--data-path DIR] [--orchestrations-path DIR]` | Launch the long-running host + Portal web UI (blocks). |
| `orchestra active` | List currently active executions. |
| `orchestra cancel <execution-id> [--reason TEXT] [--source LABEL]` | Cancel a running execution. `--source` defaults to `cli`. |

Pass either a registered `NAME` or `--run-file <path>`, not both.

#### Host selection (`--mode`)

| Mode | Behaviour |
|------|-----------|
| `auto` (default for `run`) | Attach to a configured instance when one is reachable and healthy; otherwise spawn a throwaway in-process host for this run. |
| `existing` | Require a healthy configured instance; error with exit code 3 if there is none. |
| `isolated` (default for `exec`) | Always run self-contained, ignoring any running server. |

The management verbs (`list`, `get`, `register`, `remove`, `scan`, `enable`, `disable`, and
the `runs` / `triggers` / `profiles` / `tags` branches) accept the same `--mode`, plus
`--data-path` and `--no-config`. They spawn a deliberately inert host — no scheduler, no
auto-resume, no external services — and inject the `dataPath` and `scan.directory` resolved
from your `orchestra.json` so a project workspace's orchestrations are visible.

#### Reporting

`--report text|markdown|json` prints a post-run report; `--report-output <FILE>` writes it to
a file instead of stdout. `--detailed` adds live per-step detail (model, MCP/tool calls,
sub-agents, retries) while the run streams.

`run` and `attach` auto-degrade to non-interactive mode when stdin is redirected (CI,
shell pipes) so `orchestra run my-orch | jq '.'` does not hang on a HITL prompt — it
exits 2 with an instructional message instead.

```bash
# One-shot run of a registered orchestration
orchestra run research-assistant --param topic="quantum computing"

# Run a file directly, self-contained, with a Markdown report
orchestra exec --run-file ./pipeline.yaml --report markdown

# Quiet output, suitable for CI; exits 2 if anything needs a human
orchestra run nightly-deploy --no-interactive --quiet

# Verbose firehose of every SSE event
orchestra run my-orch -V

# Hand off a long-running run, then re-attach later
orchestra run my-long-orch          # Ctrl+C, exit 130
orchestra attach my-long-orch run-abc123
```

### Run history

| Command | Purpose |
|---|---|
| `orchestra runs list [--limit N] [--favorites] [--tag NAME]` | List recent runs (default 20). `--tag` is repeatable and matches **any** of the given tags. |
| `orchestra runs get <name> <run-id>` | Get a specific run's full record (every step's input/output). |
| `orchestra runs delete <name> <run-id> [--force]` | Delete a stored run record. `--force` is required for favorited runs. |

### Run annotations

Runs are named by the machine, not by you — an ephemeral run called
`ephemeral-efca835904b6-attempt-3` is impossible to find later. Annotations attach a
**title**, **tags**, a **note** and a **favorite** flag to a run so it stays findable, and
favorited runs are exempt from retention deletion.

| Command | Purpose |
|---|---|
| `orchestra runs favorite <name> <run-id>` | Mark a run as a favorite (alias `star`). Exempt from retention. |
| `orchestra runs unfavorite <name> <run-id>` | Remove the favorite mark (alias `unstar`). |
| `orchestra runs annotate <name> <run-id> [--title T] [--tag N]... [--note T] [--favorite] [--clear]` | Set curation fields. Omitted fields are left untouched; `--clear` removes the annotation. |
| `orchestra runs annotations [--orphans]` | List every annotated run and its tag counts. |
| `orchestra runs prune-annotations` | Drop annotations whose run no longer exists. |

```bash
# Make a machine-named run findable again
orchestra runs annotate ephemeral-efca835904b6-attempt-3 efca835904b6 \
  --title "Connect evidence pack" \
  --tag connect --favorite \
  --note "22/24 steps green despite the Cancelled status."

# Find it later by any of those words
orchestra runs list --tag connect
orchestra runs list --favorites
```

Search (`GET /api/history/search`) also matches the title, tags and note, so the words you
wrote are the words you can search for — and it matches the run's own output too, so a run
nobody labelled is still findable by something it said.

### Run export

A run's artifacts are split across two directories: the execution folder, and the temp store
where steps write files via `orchestra_save_file`. The second is usually where the real
deliverable is — a step producing a large document saves it and returns only a summary
inline. Export gathers both.

| Command | Purpose |
|---|---|
| `orchestra runs export <name> <run-id> --out <DIR>` | Export one run. |
| `orchestra runs export --tag NAME --out <DIR>` | Export every run carrying any of these tags. |
| `orchestra runs export --favorites --out <DIR>` | Export every favorited run. |

| Option | Purpose |
|---|---|
| `--out <DIR>` | Destination directory (default: current directory). |
| `--as <SHAPE>` | `bundle` (default), `report`, or `data`. Named `--as` because `--format` already selects the CLI's own output shape. |
| `--zip` | Write a `.zip` instead of a directory. |
| `--limit <N>` | Cap on how many runs a bulk selector exports (default 100). |

```bash
# One run, as a browsable directory
orchestra runs export connect-evidence efca835904b6 --out ./exports

# Just the document, nothing else
orchestra runs export connect-evidence efca835904b6 --out ./exports --as report

# Everything tagged 'connect', zipped
orchestra runs export --tag connect --out ./exports --zip
```

A `bundle` contains `README.md` (status, step table, parameters, the run's annotation, and
any export warnings), `run.json`, `orchestration.json`, `steps/` and `files/` — the last
holding the saved artifacts, renamed from their GUIDs to the step that produced them.

### Triggers

| Command | Purpose |
|---|---|
| `orchestra triggers list` | List all triggers and their state. |
| `orchestra triggers enable <id>` | Enable a trigger. |
| `orchestra triggers disable <id>` | Disable a trigger. |
| `orchestra triggers fire <id> [--param k=v ...]` | Fire a trigger manually. |

### Profiles

| Command | Purpose |
|---|---|
| `orchestra profiles list` | List all profiles. |
| `orchestra profiles get <id>` | Show a profile's contents. |
| `orchestra profiles activate <id>` | Activate (enables its orchestrations to run). |
| `orchestra profiles deactivate <id>` | Deactivate. |
| `orchestra profiles delete <id>` | Delete a profile. |

### Tags

| Command | Purpose |
|---|---|
| `orchestra tags list` | List all known tags with usage counts. |
| `orchestra tags get <orchestration-id>` | Show effective tags on an orchestration. |
| `orchestra tags add <orchestration-id> <tag1,tag2,...>` | Add tags. |
| `orchestra tags remove <orchestration-id> <tag>` | Remove a single tag. |

### Human-in-the-loop

| Command | Purpose |
|---|---|
| `orchestra pending [--orchestration NAME]` | List runs awaiting human input. |
| `orchestra respond <orchestration> <run-id> <step-name> [--choice X] [--reply "..."] [--by NAME]` | Submit a response. At least one of `--choice` / `--reply` is required. |

```bash
# Approve a deployment pause
orchestra respond deploy-pipeline run-abc123 review --choice approve --by alice

# Answer a free-form question step
orchestra respond draft-post run-xyz789 clarify --reply "AI angle, ~200 words"
```

### Script step control

Called from *inside* a Script step to signal orchestration control (the non-LLM equivalent of the `orchestra_complete` / `orchestra_set_status` engine tools). These are local-only: they write the JSON payload to the `ORCHESTRA_CONTROL_FILE` the engine sets for the step, and need no server connection.

| Command | Purpose |
|---|---|
| `orchestra step complete --status <success\|failed> [--reason "..."]` | Halt the whole orchestration, cancelling remaining steps. |
| `orchestra step set-status --status <success\|failed\|no_action> [--reason "..."]` | Set this step's status; `no_action` skips dependent steps. |

```bash
# Stop the tick early when there's nothing to process
orchestra step complete --status success --reason "Inbox is empty, nothing to dispatch."
```

pwsh scripts can use the injected `Orchestra-Complete` / `Orchestra-SetStatus` helpers instead. See [Script step control channel](engine.md) for the full contract.

### Server

| Command | Purpose |
|---|---|
| `orchestra server-status` | Return the server's reported status payload. |

---

## Typical workflows

### Run a single orchestration and inspect the result

```bash
orchestra register ./orchestrations/research-assistant.json
orchestra run research-assistant --param topic=AI
# … (live SSE)…
# When the run finishes, the full record is available:
orchestra runs list --limit 5 --format table
orchestra runs get research-assistant <run-id>
```

### Search the registry

```bash
# All orchestrations tagged "prod" that mention "deploy"
orchestra list --filter deploy --tag prod --format table

# Just the enabled ones
orchestra list --enabled --format table
```

### CI-friendly run with bounded failure modes

```bash
# Pipe into jq, never hang on HITL, fail loudly on anything but success.
set -euo pipefail
orchestra run nightly-checks --no-interactive --quiet | jq '.'
# Exit codes:
#   0  → success
#   1  → step error / cancel / non-success terminal
#   2  → HITL prompt arrived (would have hung interactively)
```

### Detach and re-attach to a long run

```bash
orchestra run lengthy-pipeline
# Ctrl+C → exit 130. The CLI prints the re-attach hint.

# Come back later from any machine that can reach the server:
orchestra -s https://orchestra.internal attach lengthy-pipeline run-abc123
```

---

## Notes & limitations

- **No editing in place**: there is no `orchestra edit` command — to modify an
  orchestration, re-`register` the updated file (the server treats it as a new
  version automatically).
- **Search is client-side**: the `list` filters narrow what the server already returned,
  so very large registries still transfer in full. This is fine for typical fleet sizes;
  if you have thousands of orchestrations and need server-side filtering, file an issue.
- **No saved credentials**: every invocation reads `--server` or `ORCHESTRA_URL` fresh.
  Use a shell profile or a wrapper script if you talk to multiple environments.
- **Branches don't have default subcommands**: `orchestra runs` shows help; the legacy
  `orchestra runs` shortcut for `orchestra runs list` was removed in the Spectre.Console.Cli
  migration to keep the help model consistent. Use `orchestra runs list` (or its alias)
  explicitly.

---

## Aliases

| Alias | Resolves to |
|-------|-------------|
| `orchestra ls` | `orchestra list` |
| `orchestra remove` short flags | none — use the full word |
| `orchestra rm` (under branches) | `delete` / `remove` where applicable (e.g. `orchestra runs rm`, `orchestra tags rm`) |
{% endraw %}
