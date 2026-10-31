# Portal-first onboarding

## Goal

`dnx Orchestra -- portal --open` should lead from a clean profile to a useful demo,
provider setup, and a saved, runnable orchestration. Existing configuration discovery,
source paths, registry IDs, run history, and CLI workflows remain compatible.

## Implementation sequence

- [x] **1. Repair the starting experience.** Fix the generator's approved-result contract,
  provide JSON/YAML preview through the real parser, add a cross-platform folder browser,
  and label Visual Builder clearly as an incomplete preview.
  Verified with 46 CLI/template tests (including execution of an APPROVED draft),
  43 Portal file/API tests, TypeScript checking, and directory-picker/builder UI tests.
- [x] **2. Workspace and configuration foundation.** Share template/scaffolding and validation
  mechanisms with the host. Add idempotent starter installation, durable workspace-aware
  saving, configuration provenance and validated, conflict-aware settings persistence.
  Preserve unknown settings and existing file/environment/argument precedence.
  Shared writers retain CLI compatibility facades. JSONC editing preserves comments and
  unknown fields; revisions detect stale saves. Verified with workspace/configuration API
  integrations, JSONC round trips, and 436 CLI scaffolding/validation regressions.
- [x] **3. Provider setup.** Add provider-neutral readiness, installation/login jobs, model
  discovery, and connection checks backed by the existing provider adapters. Bootstrap
  remains usable before authentication. Jobs are bounded, cancellable and host-owned.
  Uses Copilot preflight/native device login and OpenCode V2 integration APIs/private
  servers. Native OpenCode installation verifies npm integrity. Job/API tests cover
  cancellation, duplicate starts, credentials, and models; live probes against both
  installed providers passed. MCP sidecar edits and redaction round trips are covered.
- [x] **4. Built-in assistants.** Ship normal orchestrations for authoring and configuration,
  reusing human-input, reporting, validation, registry and execution mechanisms. Validate
  generated artifacts in process, register saved workflows immediately, and use the
  configured provider/model. Basic authoring must not require PowerShell or external MCPs.
  Bundled authoring uses Prompt plus a deterministic HTTP workspace save; configuration
  uses native preview/Approval/apply steps. Read-only tools are opt-in. Normal-engine
  integration tests cover successful creation/run, invalid-artifact rejection and settings
  flow/default selection. The authoring save goes through the existing registry.
- [x] **5. Portal onboarding and Settings.** Welcome/demo/starter/assistant entry points;
  provider setup and model selection; workspace, integration, execution and retention
  settings; actionable readiness and restart feedback. Preserve normal dashboard behavior
  for existing users and address related accessibility, stale-state and request-race issues.
  Welcome and Settings are lazy-loaded; existing dashboards retain their normal view.
  Forms send only edited fields, surface conflicts/restarts and reconnect to active setup
  jobs. All 488 Portal tests and TypeScript checking passed before this commit.
- [ ] **6. End-to-end verification and review.** Exercise clean-profile and existing-workspace
  paths, settings round trips/restarts, bundled assets, authoring and registration, provider
  jobs, cancellation and all affected backend/UI tests. Run full CI and isolated browser
  tests. Revisit every item and document evidence and any necessary corrections.

Each numbered step is tested and committed before proceeding. Add meaningful regression
coverage for observed defects and integration/browser coverage for the complete journey.

## Acceptance criteria

- A fresh Portal offers a credential-free demo and useful starter/assistant actions.
- Sign-in/readiness/configuration are accessible without an already-working agent.
- Provider selection and available models are visible and testable in Portal.
- A natural-language request produces a validated workflow saved using the existing
  registry/source-directory conventions, available to run from Portal immediately.
- Configuration editing retains unrelated values, shows effective sources, handles
  concurrent edits, and clearly distinguishes new-run changes from restart-required ones.
- Existing project/global configurations, sidecars, saved workflows and history require
  no migration or manual adjustment. Upgrades never overwrite user-edited starters.
- JSON and YAML previews work; directory selection works without a Windows native dialog.
- Visual Builder explicitly identifies its incomplete preview status and supported types.
- The shipped tool contains all assets needed for the basic journey; provider downloads
  and authentication have clear progress and retry paths.
- Test servers/processes are stopped after verification; changes use LF line endings.
