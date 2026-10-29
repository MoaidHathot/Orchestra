# Provider execution contracts

The engine builds an immutable `AgentBuildConfig` and asks the selected provider's
`AgentProviderCapabilities` whether the requested features are supported. Unsupported
controls fail before inference; adapters must not silently discard them.

| Contract | Copilot | OpenCode V2 |
|---|---|---|
| Agent runtime | Copilot CLI through the .NET SDK | Private V2 server through HTTP |
| Default working directory | Definition directory | Definition directory |
| Normal tool loop/internal planning | Native | Native |
| Independent Prompt sessions | Yes | Yes |
| Explicit skills/instructions | Native directory controls | Native skill paths/instruction files |
| Ambient/nested discovery | Independently configurable | Enabled; explicit false rejected |
| Human input | Native question/elicitation callbacks | Native forms and permission replies |
| JSON Schema output | Native generation + engine validation | Schema guidance + engine validation |
| Opt-in autopilot | Native bounded continuation | Orchestra-managed bounded continuation |
| Native persistent recall | Disabled on create/resume | No equivalent memory-disable control |

## Shared logic

- `ExecutionWorkingDirectory` resolves the definition-based directory rule for Prompt,
  Command, and Script steps.
- `AgentExecutionPolicy` defines standard/autopilot execution and validates continuation
  limits. `AgentContinuation` supplies a bounded fallback for providers without native
  continuation, and `AgentExecutionGuidance` supplies common model-facing guidance where needed.
- `StructuredResponseValidator` enforces the final output contract, including output handlers.
  `StructuredOutput` advertises support for that contract; `NativeStructuredOutput` separately
  indicates native constrained generation.
- `AgentSwapLoop`, cancellation, orchestration retry/loop handling, engine tools,
  human-input waiting, and `AgentEventProcessor` remain provider-neutral.
- `IAsyncEngineTool` supports cancellable asynchronous tool dispatch in both adapters.
  Native and engine-tool questions serialize through `EngineToolContext`, and OpenCode's
  MCP catalog advertises only the tools enabled for the current step.
- Adapters own transport DTOs, runtime configuration, native interaction mapping, and
  interpretation of completion/error events. An assistant turn, child completion, and
  whole-step completion are distinct lifecycle events.

To add a provider, implement `AgentBuilder`/`IAgent`, declare actual capabilities, translate
its native events to `AgentEvent`, and return only its final root response in `AgentResult`.
Use shared contracts where behavior matches; add adapter-specific handling when it does not.
Verify the transport contract, cancellation, child-session isolation, and live tool use before
advertising a capability.

## Authoring JSON steps

Set `responseSchema`, describe the desired fields, and consume them downstream. No special
tool calls, stop instructions, or internal completion markers are needed. Orchestra filters
immediate-stop tools for structured/autopilot steps so the runtime can finish normally.
Workflow branching belongs in downstream conditions/scripts; a response field is data, not
an implicit engine command.

See [Copilot](copilot.md), [OpenCode](opencode.md), and the
[cross-provider autopilot example](../examples/provider-autopilot.yaml).
