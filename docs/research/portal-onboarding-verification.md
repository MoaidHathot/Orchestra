# Portal onboarding: implementation and verification

Implementation was split into commits following
[`portal-first-onboarding.md`](../plans/portal-first-onboarding.md). The browser-facing
guide is [`portal-onboarding.md`](../portal-onboarding.md).

## Requirement review

| Requirement | Implementation and evidence |
| --- | --- |
| One-command browser entry point | `dnx Orchestra -- portal --open`; the existing listening-address-aware browser launcher remains the entry point. Package smoke verification installs the actual tool and starts its Portal. |
| Useful before an account is connected | Empty libraries offer/install only the credential-free demo. Settings, filesystem browsing and provider setup use deterministic services. |
| Existing workspaces keep working | The shared CLI scaffolder and normal registry/source paths are reused. Browser tests open an existing scanned workspace, preserve its source file, retain the normal dashboard, and import YAML. |
| Small built-in starter library | Six optional examples; agent/PowerShell requirements are labelled. Installation is additive and retains user edits. |
| Provider setup in Portal | Copilot device login and SDK model checks; OpenCode native runtime installation and V2 integration authentication. Jobs are bounded, cancellable, retained across panel navigation and stopped with the host. |
| Available models are usable | The model test makes a small real request. Provider connection/catalog status is distinct from general host health. |
| Natural-language authoring | A normal Prompt uses the bundled skill and opt-in read-only inspection/validation tools. A deterministic save step schema-validates, resolves source-relative references, writes atomically and registers the artifact. |
| Configuration assistant | A normal Prompt proposes a revision-bound patch, followed by preview, Approval and apply. The settings form and assistant use the same services. |
| Settings preserve configuration | JSONC member edits retain unrelated fields/comments/references; revisions and per-file gates reject stale/concurrent saves. Sources, effective values, launch overrides and restart requirements are visible. |
| Optional MCP configuration | The existing sidecar supports add/edit/remove, redacted credential round trips and live connection tests. JSONC remains readable by the host after a Portal edit. |
| Clear Visual Builder limits | A visible preview warning states that functionality is incomplete and supports Prompt, HTTP and Transform steps. Save failures appear in the UI. |
| First-run UI and performance | Workspace and builder views load on demand; folder browsing supports all host platforms, keyboard input and drive selection. Imports show partial failures and discard stale requests. |
| Human input stays accessible | Execution views expose “Respond to workflow”, then return to the execution after submission. Choice-based configuration approval preserves the choice when a comment accompanies it. |
| Durable restart behavior | Completed workflows/settings/history survive restart. Registered assistants refresh their listener, assets and defaults while retaining IDs. A pending configuration approval can resume and apply against the new listener. |

## Defects exposed by verification

- The legacy generator validated an approving review's `APPROVED` text instead of the
  final draft. A regression executes its validation/write step with that approving result.
- YAML file preview previously used `JSON.parse` in the browser. Preview now uses the
  backend parser and validates the captured content without reopening a changing file.
- Copilot rejected open-ended response schemas because native strict generation was
  unconditional. Compatible closed schemas retain strict mode; other schemas are sent
  unchanged in non-strict mode, with complete engine validation in both cases. Real
  authoring through both providers verified the fix.
- A first-use model check could reference a workspace that had not yet been created.
  The real selected-model test caught this and now verifies workspace bootstrap.
- The OpenCode installer assumed every npm mirror supported `/package/version`, and a
  cleanup operation could mask the download error before the cache directory existed.
  Installation now reads package metadata, follows its distribution URL and verifies
  integrity. Controlled mirror-fallback tests and a real native download/launch cover it.
- Configuration approval originally used a quoted template token in its condition; the
  full browser journey caught the skipped apply step. Its corrected condition and
  choice-with-comment behavior are exercised through the real engine/API/UI.
- The shared API/MCP launcher was not receiving the checkpoint store. Host shutdown also
  did not join its execution/persistence tasks, and Approval could treat run cancellation
  as a timeout. Pending-approval restart testing exposed these gaps. Checkpoints are now
  wired through the shared launcher, shutdown joins that work, and only a step deadline
  applies `onTimeout`. Recovery waits for host startup hooks and the listening address.
- Three-OS testing exposed path-string assumptions around macOS `/var` symlinks and a
  shell test that mistook the test host for an executor-owned wrapper after POSIX `exec`.
  Assertions now verify filesystem identity and the reported spawned process ID.
- A timing-dependent notification-coalescing test failed under concurrent suite load.
  It now controls the notification timer, while retaining the real registry mutations.

## Reproducing the checks

Backend checks (the solution includes the affected integration tests):

```sh
dotnet build OrchestrationEngine.slnx --configuration Release
dotnet test OrchestrationEngine.Tests.slnx --no-build --configuration Release -m:1 --filter "Category!=E2E"
```

Portal:

```sh
npm --prefix src/Orchestra.Portal/portal run typecheck
npm --prefix src/Orchestra.Portal/portal test
npm --prefix src/Orchestra.Portal/portal run build
dotnet build tests/Orchestra.Portal.E2E/Orchestra.Portal.E2E.csproj --configuration Debug
pwsh tests/Orchestra.Portal.E2E/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
dotnet test tests/Orchestra.Portal.E2E/Orchestra.Portal.E2E.csproj --no-build --configuration Debug --filter "Category=E2E"
```

The browser suite owns isolated hosts and includes new-user onboarding, existing-workspace
compatibility, and pending-approval recovery. Its external account/LLM boundary is replaced
with controlled adapters; orchestration execution, HTTP saves, approvals, persistence,
restarts and the browser itself are real. Screenshots are written to
`artifacts/onboarding/screenshots/`.

`ProviderSetupLiveTests` separately exercises installed Copilot/OpenCode model discovery,
selected-model generation and authoring with `gpt-6-astra`. It also checks native Copilot
device-code instructions and cancellation in an isolated credential directory.
`OpenCodeCliInstallerTests.NativePackage_CanBeDownloadedVerifiedAndLaunchedWithoutNpm`
downloads/verifies the pinned native V2 package and runs its version command. These live
tests are opt-in (`Category=E2E`) and require network/account access where applicable.

Package smoke checks are reproducible with:

```sh
dotnet pack src/Orchestra.Cli/Orchestra.Cli.csproj --configuration Release -p:PackageVersion=0.0.0-ci.verify -o artifacts/package-smoke
pwsh scripts/verify-portal-package.ps1 -Version 0.0.0-ci.verify -Source artifacts/package-smoke
```

The script compares bundled assemblies against the build, checks the frontend/schema/
skill/built-in assets, runs the demo, creates a workflow, edits settings, restarts, and
checks workflow/history/assistant persistence. It owns and stops both test processes.
CI runs backend, package and Playwright checks on Windows, Linux and macOS.

Local test records and package evidence are under `artifacts/onboarding/`. Final CI
results are recorded with the completed implementation plan.
