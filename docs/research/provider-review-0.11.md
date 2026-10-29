# Provider review for Orchestra 0.11.0

Reviewed and verified on 2026-10-30. This supersedes the implementation notes in
[the initial V2 verification](provider-v2-verification.md) where behavior differs.

## Decisions retained

- Use the actual provider agent runtimes, with independent Prompt sessions.
- Default Prompt/Command/Script working directories to the definition directory, including
  .NET tool installations. Relative overrides use the same base.
- Share configuration, cancellation, output contracts, human-input waiting, and recovery;
  keep native lifecycle and API mapping inside each adapter.
- Preserve the distinction between Copilot native constrained JSON/native autopilot and
  OpenCode guided JSON/Orchestra-managed continuation. Standard mode remains single-pass.

## Corrections from the review

1. **Root output isolation:** late Copilot child messages/deltas cannot replace the root
   response or contaminate its fallback text. Root messages are not attributed to an active
   child merely because its frame exists.
2. **Durable completion:** OpenCode prompt admission IDs are now checked against the durable
   transcript. A previous successful idle cannot complete a newer prompt. Test-only legacy
   completion bypasses were removed from the transport contract.
3. **Cancellation and cleanup:** failures before the swap loop close the event reader;
   admission cancellation joins the stream; abort/delete operations are bounded; configuration
   is created after capacity is acquired; cleanup failures do not replace valid results.
4. **Output handlers:** transformations retain authentication, directory, discovery,
   permissions, and sandbox controls while remaining non-interactive and independent of the
   main session's autopilot/control tools.
5. **Bounded JSON correction:** OpenCode autopilot now uses its existing continuation budget
   to correct invalid structured output from completed work. Standard mode still fails invalid
   output without additional model calls. The coding/subagent/JSON live test was restored.
6. **Actual tool catalogs:** the OpenCode MCP bridge advertises only enabled step tools,
   rather than advertising disabled status/question tools and rejecting them at dispatch.
7. **Asynchronous input:** `IAsyncEngineTool` enables cancellable, non-blocking dispatch in
   both providers. Native and engine-tool questions serialize through the shared context.
8. **Human-readable forms:** OpenCode forms present field descriptions and choice labels,
   convert primitive/multiselect answers, and respect conditional/hidden fields.
9. **Honest capabilities:** unsupported OpenCode subagent MCP/skill/inference controls fail
   instead of disappearing. Copilot explicitly declares implemented capabilities. Excluded
   delegation tools cannot be re-enabled by later inline-agent permission rules.
10. **Lazy skills:** OpenCode no longer incorrectly inherits Copilot's historical
    skill-directory-to-instruction fallback. Explicit Markdown instruction files are sorted
    deterministically and directory links are not traversed.
11. **Required tools:** OpenCode waits for required MCP startup and fails before prompting if
    required servers are missing or their status check fails.
12. **Release evidence:** `pack.ps1` cleans the package output directory rather than deleting
    all artifacts, preserving the study and verification evidence.

## Authoring documentation

The updated skill ships inside the tool, including
`references/provider-controls.md`: provider matrix, runnable JSON/autopilot example,
working-directory migration, output-handler behavior, human-input semantics, and fields
authors must not invent. The main skill, full schema reference, JSON schema descriptions,
README, and provider documentation were aligned.

## Verification

- Release build and portal production bundle succeeded.
- **5,007 non-E2E tests** passed across 13 projects against the Release binaries.
- **6 live Copilot tests** and **8 live OpenCode tests** passed, including root JSON after
  delegation, explicit/automatic discovery, native questions, permissions, tool-error recovery,
  bounded autopilot, and downstream generated-script verification.
- Native MCP `tools/list` integration verifies the filtered engine-tool catalog.
- The packed tool was installed into a fresh private tool path and ran both the scaffolded
  smoke test and a definition-directory Command/Script probe from another launch directory.
- The installed copy of the authoring skill contains the new provider reference/features.
- Portal type checking passed; package assemblies match the tested Release assemblies.

Two test-fixture corrections are retained explicitly: the ordinary Copilot completion test
now removes control tools from its test registry instead of relying on the model to obey a
natural-language ban; OpenCode fixture cleanup does not mask the real result when only an
empty directory remains locked by a separate service after the private server exits.

Evidence: `artifacts/provider-review/`, especially `release-suite/*.trx`,
`copilot-release-live.trx`, `opencode-release-live-final.trx`, and `packed-tool-smoke.json`.

Package: `artifacts/packages/Orchestra.0.11.0.nupkg` (28,996,062 bytes).
SHA-256: `3a89b776363f5da39f9abc871b1015f04d6be2a2e76740cb55059af318eee429`.
The package includes Copilot SDK 1.0.28 and lazily acquires CLI 1.0.105; the native runtime
is not bundled. OpenCode live verification used 2.0.26. Live model: `claude-sonnet-5`;
authored examples retain `claude-opus-4.8`.

## Publication

The verified package was accepted by NuGet.org and added to
`C:\nuget\local-feed\orchestra\0.11.0\orchestra.0.11.0.nupkg`. The local copy's SHA-256 matches
the verified artifact. NuGet.org's V3 endpoint encountered a TLS handshake failure in this
environment; its documented V2 upload endpoint returned HTTP 201 Created.

Public version URL: <https://www.nuget.org/packages/Orchestra/0.11.0>.
Indexing status and publication details are recorded in
`artifacts/provider-review/publication.json`; aggregate verification is in
`artifacts/provider-review/verification-summary.json`.

At package publication, repository changes were uncommitted; the package was published before
the subsequent source commit and push.

## Follow-up coverage before the source commit

Added 29 deterministic cases covering multi-field forms, hidden/default/conditional fields,
typed and multi-select replies, invalid-answer retries, descendant-session ownership,
permission/form HTTP contracts, startup deadlines, and queued/active input cancellation.
The tests exposed acceptance of null entries in multi-select JSON arrays; source validation
now rejects these and prompts for a corrected answer. This follow-up fix postdates the
published 0.11.0 package described above.

The offline lifecycle reproducer was also updated to honor newly added optional handler
constructor parameters. Follow-up verification is retained under
`artifacts/source-commit-tests/`.

Follow-up results: **5,036 non-E2E tests passed** across the 13-project suite, all **8 offline
parity tests passed**, and the synthetic lifecycle probe exited successfully with no model
calls and `defectReproduced: false`.
