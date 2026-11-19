---
layout: default
title: Decision steps
---
{% raw %}

# Decision steps

Use `type: Decision` when the answer is a label, a yes/no judgment, or a score on a rubric you can describe in advance. A single Copilot SDK call answers all the questions about the same evidence. The decision has no chat session, tool calls, or generated explanation. A downstream `when` decides which steps run.

```yaml
steps:
  - name: triage
    type: Decision
    context: "{{param.ticket}}"
    questions:
      - name: refund
        kind: boolean
        instructions: Does the customer explicitly request a refund?
        threshold: 0.85
  - name: queue
    type: Transform
    dependsOn: [triage]
    when: "{{triage.output.answers.refund.value}}"
    template: "Queue for refund review. Probability: {{triage.output.answers.refund.probability}}."
```

The [complete triage example](../examples/copilot-decision-triage.yaml) declares the input, asks all three kinds of question, and routes the ticket. From a checkout with Copilot signed in:

```sh
orchestra validate examples/copilot-decision-triage.yaml --schema
orchestra exec --run-file examples/copilot-decision-triage.yaml
orchestra exec --run-file examples/copilot-decision-triage.yaml --param ticket="How do I change the display language?"
```

## Authoring and reading answers

`context` and `model` accept template expressions. Context is text by default. To pass JSON produced by an earlier step, set `contextFormat: json` and `context: "{{source.output}}"`; the resolved text must be a JSON object or array of objects. Question names, instructions, and rubrics are static. Give each question a unique name using ASCII letters, digits, `_` or `-`.

| `kind` | What you supply | What `output.answers.<name>` contains |
|---|---|---|
| `boolean` | `instructions`; optional `whenTrue` / `whenFalse` descriptions and `threshold` (default `0.5`) | `probability` of yes, `threshold`, and Boolean `value` (`probability >= threshold`). |
| `choice` | `options`: 2–10 `{value, description}` entries | Selected `value`, a `probabilities` map keyed by option, `tied`, and `confidence`. For a tie, the SDK picks the first tied option in request order. |
| `score` | `levels`: 2–10 `{label, description}` entries, lowest first; optional `range: {min, max}` | Most probable `level`, a `probabilities` map keyed by label, `confidence`, raw `score`, and numeric `value`. `score` is the probability-weighted **zero-based** level index. `value` equals `score` unless rescaled linearly to `range`. |

Every answer includes its `kind`. A model that declines a question returns `kind: refused` for that name, with no fabricated `value`; other answers are still available. You can route on `{{triage.output.answers.refund.kind}} == 'refused'`. Choice/score `confidence` measures how concentrated the distribution is, **not** the probability that the choice is correct. The SDK's `vendorConfidence` is preserved when present. Choose thresholds using labeled examples from your own workflow.

The JSON output also contains `model` (catalog ID), `upstreamModel` when reported, runtime `latencyMs`, and token `usage` when reported. Run history records the selected ID, actual model, and usage. Normal step dependencies, conditions, timeouts, retry policies, and checkpoint/resume apply. A request failure fails the step; it never becomes a negative answer. The SDK call has a 30-second ceiling; a shorter Orchestra deadline cancels it earlier.

**Routing whole workflows:** a webhook-triggered orchestration can use a `choice` question whose option values are registered child workflow names. On a dependent `type: Orchestration` step, set `orchestration: "{{triage.output.answers.workflow.value}}"`. That gives you a router using existing steps and lets you replay past webhook payloads to evaluate the choices.

## Which model is called?

Decision steps always use the `copilot` provider and default to the catalog ID **`microsoft-decision-1`**, even in workflows whose Prompt steps use a different model or provider. Set step `model` to override it with another available *decision* catalog ID. Orchestra enables `DECISIONS_API` in the Copilot worker's environment and uses the existing Copilot account configuration. No separate Foundry deployment or API key is needed.

**Live verification, 2026-11-18:** using `GitHub.Copilot.SDK` 1.0.34 and its matched CLI 1.0.112, `(await client.Rpc.Models.ListAsync()).DecisionModels` listed `microsoft-decision-1` and `gpt-6-luna-decisions`; the latter was the account default. The same three-question ticket request produced:

| Requested ID | Observed SDK result |
|---|---|
| `microsoft-decision-1` | `model: microsoft-decision-1`, `upstreamModel: microsoft-decision-1.1` |
| `Microsoft-Decision-1` | “No usable decision model.” Use the lowercase catalog ID. |
| `microsoft-decision-1.1` | “No usable decision model.” |
| `Microsoft-Decision-1.1` | “No usable decision model.” |

For “I was charged twice for my order. Please refund the duplicate charge,” the SDK returned refund probability **0.9993**, team **billing**, and severity **Medium**, score **0.9804** out of 2. That one call reported **303 ms**, **107 input tokens**, and **3 output tokens**. This measures the request after runtime startup; it is not an Orchestra latency benchmark. The SDK did not return a monetary cost. Microsoft's published pricing is for Foundry, not a Copilot bill. A catalog alias can move, so rely on the returned upstream ID for a particular run rather than treating `1.1` as a permanent pin.

The observed catalog allowed 32 questions, 2–10 options or levels per question, 256 KiB serialized requests and JSON depth 32. Microsoft also reported a 32,768-token budget for context plus any **one** question and `supportsRefusals: false`. The runtime rejects oversized requests rather than silently truncating them.

In a separate isolated Orchestra run, “How do I change the display language?” selected **support** (probability **0.9886**), set `refund.value` to **false**, and assigned severity **Low** (**4.63/100**). The runtime reported **1,366 ms** and the general-review branch ran. Re-run the SDK-backed workflow tests with `dotnet test tests/Orchestra.Copilot.Tests --filter FullyQualifiedName~DecisionStepE2ETests`.

Sources: [GitHub SDK release introducing Decisions](https://github.com/github/copilot-sdk/releases/tag/v1.0.31) · [pinned .NET RPC contract](https://github.com/github/copilot-sdk/blob/v1.0.34/dotnet/src/Generated/Rpc.cs) · [original SDK sample](https://github.com/MoaidHathot/Github-Copilot-Decision-Model-Invoker/blob/main/sample/Decision.cs) · [Microsoft's 1.1 announcement and pricing](https://commandline.microsoft.com/microsoft-decision-model-ga) · [Microsoft's decision model guide](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision).

{% endraw %}
