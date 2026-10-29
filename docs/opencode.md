# OpenCode V2 provider

Orchestra connects its .NET adapter to a private `opencode serve` process using the **V2 HTTP
API**. Install and authenticate OpenCode V2 first; Orchestra does not download it. The adapter
is verified against **2.0.26** and rejects a V1 server during readiness checks.

```yaml
name: analyze
description: A bounded OpenCode agent step
defaultProvider: opencode
defaultModel: claude-opus-4.8
steps:
  - name: answer
    type: Prompt
    systemPrompt: Produce accurate, verified results.
    userPrompt: Calculate 17 + 25 using a tool, then return count.
    executionPolicy:
      mode: autopilot
      maxContinues: 2
    responseSchema:
      type: object
      properties:
        count: { type: integer }
      required: [count]
      additionalProperties: false
```

Bare model names use the configured `opencode.fallbackProvider` (normally `github-copilot`).
You can also use `provider/model#variant`. `reasoningLevel` selects the corresponding model
variant; it must be supported by that model. Copilot-specific reasoning-summary, context-tier,
token, sandbox, and system-section controls are rejected when unsupported.
`infiniteSessions.enabled` maps to automatic compaction; Copilot-specific utilization
thresholds are rejected rather than ignored.
Subagent-specific MCP servers/skills and `infer: false` are also rejected; they are not
silently discarded.

## Directories and discovery

- Prompt, Command, and Script steps default to the YAML/JSON file's directory, including when
  Orchestra is installed as a .NET tool. Relative `workingDirectory` overrides use that base.
  In-memory definitions without source metadata fall back to process CWD.
- `skillDirectories` uses native V2 skill paths. Supporting files remain beside `SKILL.md`;
  Orchestra does not copy skills into the project.
- `instructionDirectories` loads Markdown files recursively through V2's `instructions`
  configuration. Explicit `[]` and omission supply none; skills remain on-demand. Copilot's
  legacy skill-directory instruction fallback does not apply to OpenCode.
  Explicit skill/instruction paths resolve from the definition directory even when the agent
  works elsewhere.
- OpenCode's ambient configuration, global/project `AGENTS.md`, skills, and nested instruction
  discovery remain enabled. `enableConfigDiscovery: true` and
  `enableOnDemandInstructionDiscovery: true` are accepted. Explicit `false`, including an
  inherited false default, is rejected: V2 has no equivalent independently scoped switches in
  this adapter. Its project-disable flag also changes root instructions and leaves global
  configuration enabled, so it is not treated as a Copilot-equivalent control.

## Interaction and permissions

The default is non-interactive guidance, with the native `question` tool denied. With
`humanInput: true`, native questions/forms route through Orchestra's pending-input surface.
Form fields are presented as readable questions with choice labels. Numeric and boolean
answers are converted to native types; multiple selections accept comma-separated choices
or a JSON string array. External-browser form actions are unsupported. Permission approval
is separately controlled by `permissionPolicy`.

The adapter uses V2 `permissions` and hard-deny policies, V2 permission replies, and native
form replies. Inline agents use V2 `agents`, `system`, and permission rules. Native permission
actions include `shell`, `edit`, and `subagent`; legacy `bash`, `write`/`patch`, and `task`
exclusion names map to those actions. Denying `edit` covers writes and patches as well.

## Structured output and autopilot

`responseSchema` has the same **exported-output contract** as Copilot: successful output,
including an output handler, must be valid JSON matching the schema. Markdown fences,
invalid types, and extra prose fail validation. Use fields downstream with
`{{answer.output.count}}`.

The V2 prompt API has no native response-schema option. Orchestra supplies schema guidance
and validates the final response, rather than claiming native constrained generation.
Authors use the same schema field and do not manage completion tools.

`executionPolicy.mode: autopilot` enables **Orchestra-managed bounded continuation** in the
same OpenCode session. After an idle execution, Orchestra checks a completion signal in the
root agent's final response. If missing, it sends a continuation prompt until `maxContinues`
is exhausted; exhaustion fails the step. Invalid JSON uses this same budget for a formatting
correction based on completed work. Standard mode does not make correction calls.
The internal marker is stripped before schema
validation and exported output. Authors do not add the marker to their prompts or schemas.
Tool/child-session output cannot satisfy this check. This differs from Copilot's native
autopilot, but both expose the same opt-in policy and remain bounded by step cancellation.

## Lifecycle and isolation

Each Prompt step has an independent session. Dependencies transfer explicit data, not a
shared conversation. Completed, cancelled, and terminally failed sessions are deleted;
recoverable failures may retain that step's session for a bounded worker swap/resume.
OpenCode uses its existing authenticated database and configuration. This is conversation
isolation, not a replacement for its database or a native memory-disable option. Orchestra
does not inject previous runs or add a memory integration; configure explicit MCP tools for
cross-run memory when wanted.

V2 prompts are durable inbox admissions, not completion responses. The adapter subscribes
before prompting, waits through `/api/experimental/session/{sessionID}/wait`, and reads the
paginated durable message projection. Failed/interrupted outcomes cannot become success just
because text or an idle event arrived. Only the final root assistant response is exported;
tool calls, reasoning, child activity, and usage are reported separately. Cancellation uses
the V2 interrupt endpoint.

Per-step configuration runs on a dedicated server under the run's existing pool capacity
limit. Orchestra stops its own server after the step. V2 authentication uses the fixed
username `opencode`; Orchestra generates a private password when none is configured.

## API references

- [V2 migration](https://opencode.ai/v2/docs/migrate-v1)
- [V2 HTTP client](https://opencode.ai/v2/docs/build/client)
- [V2 API/OpenAPI](https://opencode.ai/v2/openapi.json)
- [V2 configuration](https://opencode.ai/v2/docs/config)
- [Shared provider contracts](providers.md)
