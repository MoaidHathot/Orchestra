---
layout: default
title: Portal onboarding
nav_order: 3
---
{% raw %}

# Portal-first onboarding

Install the .NET 10 SDK or later and launch:

```bash
dnx Orchestra -- portal --open
```

The Portal frontend, schemas, six templates, authoring skill and two built-in assistant
definitions are included in the tool package. The initial download is handled by DNX.

## First success

An empty library opens **Get started** and adds the small `smoke-test` demo. Click **Try the
demo** to see a real execution with outputs and history before connecting an agent. This
uses only the installed .NET tooling. Existing libraries keep the normal dashboard; the
**Get started** and **Settings** buttons remain available in the sidebar.

## Connect a provider

In **Settings → Providers**:

1. Choose Copilot or OpenCode.
2. Install the runtime when needed. Copilot uses Orchestra's existing CLI bootstrap. OpenCode
   installs a pinned native V2 binary in Orchestra's cache, with npm integrity verification;
   Node/npm are not required. An existing OpenCode installation takes precedence.
3. Sign in. Copilot provides a browser link and device code. OpenCode lists its native
   integrations and supported OAuth, API-key and external-credential methods, including any
   provider-specific form fields. Credentials stay with the provider's native store.
4. Refresh the model list, choose a model and **Test selected model**. This makes a small real
   model request. **Use provider and model** saves defaults for new runs.

Setup jobs show progress, can be cancelled, and remain available when the panel is reopened.
They terminate on host shutdown and have time limits. Merely opening Settings does not
download runtimes or start model calls. Provider readiness is separate from host health.

For corporate npm mirrors, the OpenCode installer honors `ORCHESTRA_OPENCODE_NPM_REGISTRY`,
`npm_config_registry`, and registry settings in `~/.npmrc`. Copilot retains its existing
`ORCHESTRA_COPILOT_NPM_REGISTRY` behavior.

## Create a workflow

Describe the goal under **Get started → Create with an assistant** and choose **Create
workflow**. The built-in `orchestra-author` workflow uses normal engine steps:

- A Prompt uses the bundled authoring skill and opt-in read-only validation/inspection tools.
- Native questions appear through Portal's human-input flow. An execution waiting for input
  exposes **Respond to workflow** so you can answer and return to the execution.
- A deterministic HTTP step validates the complete artifact and saves it through the workspace
  service and normal registry. Invalid artifacts and name collisions are reported as failures.

The saved workflow appears in the library immediately. It is a normal JSON orchestration,
with normal source-directory resolution, history and triggers. The basic assistant has no
PowerShell dependency and needs no external MCP connection.

**Help configure Orchestra** uses a separate normal orchestration: inspect/propose settings,
preview, Approval, then apply. Settings revisions prevent a proposal from overwriting an edit
made while the user was reviewing it.

If the host stops while configuration approval is pending, the checkpoint and approval
survive. After restart the workflow reattaches to that approval and uses the current Portal
listener when applying the reviewed patch. Host shutdown never selects an approval's
timeout default response.

## Workspace and configuration compatibility

- Source files go under the configured `scan.directory/orchestrations`, or under
  `<dataPath>/workspace/orchestrations` when no scan workspace exists.
- The existing registry continues maintaining its managed JSON copies and original source
  pointers. Existing registry IDs, files, history, project/global configs and sidecars are retained.
- App-owned assistant definitions live under `<dataPath>/builtins`; their bundled skill and
  selected defaults are resolved when prepared. Registered assistants refresh after a restart
  or defaults change, keeping their IDs while picking up the current listener and package
  assets. User-created workflow files are separate.
- Template installation is additive/idempotent. Existing user-edited templates and skills
  are retained. New template copies use the selected provider/model defaults.
- Settings edits target the configuration file discovered at host startup. With no file,
  **Save** creates one at the existing user-global/XDG configuration location. Startup itself
  does not rewrite an existing configuration or install all examples.
- JSONC comments, unrelated settings, and environment references are preserved. Concurrent
  edits return a conflict rather than silently replacing the newer file.
- Provider/model/concurrency defaults apply to new runs; existing runs retain their snapshot.
  Storage paths, file watching, listening URLs, scheduler enablement and logging changes
  require a Portal restart. The UI identifies these and shows launch/environment overrides.

## Optional connections and diagnostics

**Settings → Connections** edits the existing `orchestra.mcp.json` sidecar. Add local stdio
or remote HTTP definitions, edit/remove existing connections and test them by listing tools. Header/environment
credentials are redacted on read, and redacted values are retained during edits. Prefer
environment references such as `${API_TOKEN}`. Restart Portal to activate saved global MCP
changes; testing a connection uses a temporary client that is disposed afterwards.

**History** settings control normal retention. Favorited runs remain exempt. The diagnostics
button opens the existing health, logs and maintenance panel.

The **Visual Builder** is explicitly an incomplete preview supporting Prompt, HTTP and
Transform steps. The assistant and JSON/YAML import support the full engine schema. Folder
selection uses the host's filesystem browser on Windows, macOS and Linux; on a remote Portal,
these are folders on the server. JSON and YAML previews use the same backend parser.

{% endraw %}
