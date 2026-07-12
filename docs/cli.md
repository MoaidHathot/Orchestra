---
layout: default
title: CLI
nav_order: 6
---

# Orchestra CLI

The Orchestra CLI (`orchestra`) is a thin HTTP/SSE client for the Orchestra REST API.
Built on **Spectre.Console.Cli**, it provides typed arguments, per-command `--help`,
typo correction, and live event streaming for runs that include human-in-the-loop pauses.

> The CLI **requires a running Orchestra server** (Portal/Host). It does not embed the
> engine, hold its own state, or talk to a database; everything goes over HTTP.

---

## Installation & invocation

The CLI ships as a .NET project in the Orchestra repo
(`src/Orchestra.Cli/Orchestra.Cli.csproj`, target `net10.0`).

```bash
# Run from a checkout
dotnet run --project src/Orchestra.Cli -- list

# Or build once and use the binary
dotnet build src/Orchestra.Cli -c Release
./src/Orchestra.Cli/bin/Release/net10.0/Orchestra.Cli list   # or .exe on Windows
```

You can wrap the binary as a `dotnet tool` or shell alias named `orchestra` for the
ergonomics in the examples below.

---

## Server URL resolution

Every command resolves the server URL in this order:

1. `--server <URL>` / `-s <URL>` flag on the command line.
2. `ORCHESTRA_URL` environment variable.
3. Fallback: `http://localhost:5000`.

```bash
orchestra -s https://orchestra.internal:8443 list
ORCHESTRA_URL=https://orchestra.internal:8443 orchestra list
```

There is no authentication layer in the CLI itself — the server is responsible for
gating access (e.g., reverse proxy, ASP.NET Core middleware).

---

## Output format

JSON-producing commands accept `--format`:

| Value             | Behaviour                                                      |
|-------------------|----------------------------------------------------------------|
| `json` (default)  | Pretty-printed JSON to stdout. Machine-friendly; pipe to `jq`. |
| `table`           | Spectre.Console rendered table for humans.                     |

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
| `orchestra run <id> [--param k=v ...] [--no-interactive] [-q\|--quiet] [-V\|--verbose] [--by NAME]` | Start a new run, stream live SSE, prompt inline on HITL pauses. |
| `orchestra attach <orchestration> <run-id> [...same flags]` | Re-attach to a still-running run and stream the remaining events. |
| `orchestra active` | List currently active executions. |
| `orchestra cancel <execution-id> [--reason TEXT] [--source LABEL]` | Cancel a running execution. `--source` defaults to `cli`. |

`run` and `attach` auto-degrade to non-interactive mode when stdin is redirected (CI,
shell pipes) so `orchestra run my-orch | jq '.'` does not hang on a HITL prompt — it
exits 2 with an instructional message instead.

```bash
# One-shot run
orchestra run research-assistant --param topic="quantum computing"

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
| `orchestra runs list [--limit N]` | List recent runs (default 20). |
| `orchestra runs get <name> <run-id>` | Get a specific run's full record (every step's input/output). |
| `orchestra runs delete <name> <run-id>` | Delete a stored run record. |

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
