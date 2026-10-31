# Engine correctness and concurrency review — October 2026

This review covers DAG scheduling, checker loops, cancellation, provider-pool lifetime,
child invocation, checkpoint/resume, template validation, and run persistence. The starting
revision was `5b6347ba`.

## Findings and fixes

### Scheduling and terminal state

- **Joins could start before a dependency settled.** Readiness used the visible result map,
  which already contained a step's initial output while hooks, loops, or checkpoint writes
  were still running. Readiness now counts settled dependency edges independently of output
  publication. Wide joins require one update per edge rather than repeated dependency scans.
- **Every ready step created a dedicated thread.** An asynchronous `LongRunning` task only
  used that thread until its first incomplete await. Step workers now use the thread pool,
  preserving execution-context/AsyncLocal flow. Worker settlement is registered before launch.
- **Missing executors could hang the run.** Resolution happened outside the worker's error
  boundary. Missing plugins now produce a failed step, and dependent steps settle normally.
- **Thrown plugin errors bypassed retries and fail-fast.** They now enter the same failure
  path as returned failed results.
- **Cancellation could lose partial results without plan capture.** All step types receive
  the existing bounded settlement window before final history is built. Partial cancellation
  results retain their trace and cancellation cause. Returned/persisted result maps are snapshots.
- **Late completion signals overwrote the winning reason/status.** The first completion
  signal atomically claims ownership of the run's completion metadata. Fail-fast also has one owner.

### Checker loops

- Re-executions bypassed concurrency slots, per-attempt timeouts, and retry policies. Both
  target and checker iterations now use the ordinary step execution path. Target retries
  retain their feedback, and retry delays release the concurrency slot.
- A failed target left the earlier successful checker result visible to downstream work.
  The checker now blocks its dependents when refinement fails; original and iteration records
  remain separate.
- Fail-fast and `orchestra_complete` used the initial checker result instead of the final
  iteration result. They now act on the actual signaling target/checker, with correct attribution.
- Iterations now emit completion events and step hooks. Run token totals include every recorded
  iteration, rather than only the latest result for each step.
- Invalid loop targets, non-Prompt targets, missing dependency edges, out-of-range iteration
  limits, and empty exit markers are rejected before execution.

The established successful-exhaustion behavior remains explicit in the authoring reference:
successful calls that exhaust `maxIterations` return the final output.

### Checkpoint durability and resume

- Concurrent checkpoint saves shared `checkpoint.json.tmp` and could corrupt or fail one
  another's writes. Each save now owns a staging file; operations are coordinated per checkpoint
  path, including Windows readers that can otherwise interfere with replacement.
- Older overlapping snapshots could overwrite newer progress. A run-scoped checkpoint writer
  serializes publication, batches overlapping completions, and lets each caller proceed only
  after a snapshot containing its revision has been saved. Different runs have independent writers.
- Snapshots included active loops' intermediate successful results. Only settled results are
  submitted now, with a loop's target/checker changes published together. An interrupted checker
  therefore executes again on resume.
- Closing the checkpoint writer joins its current write and rejects late submissions, preventing
  a late worker from recreating an already deleted checkpoint.
- Null checkpoint stores formerly incurred repeated full snapshots. That work is eliminated.
  File-backed snapshots stream JSON instead of building an additional full JSON string.
- Failed replacement cleans staging files. Read/list operations propagate caller cancellation
  and tolerate directories disappearing during run cleanup.
- Resume discarded applied input defaults, and its human-wait timeout adjustment used the
  historical run start. Defaults now reach runtime validation/templates, and the timeout clock
  uses the current invocation's start.
- Restored step records now retain trace, usage, selected-model metadata, retry history, and
  child pointers through the same projection used by freshly executed steps.

### Provider lifetime and child invocation

- Partial multi-provider startup failures leaked already opened scopes and waited indefinitely
  on other pending startups. Failure cancels sibling startup operations and disposes successful
  scopes while preserving the original error. Scope creation stays in the caller's execution
  context so AsyncLocal provider pools flow into workers correctly.
- Disabled or completely restored Prompt steps no longer initialize unused providers. Pending
  checkers still initialize the provider needed to revise a restored target.
- Invalid concurrency limits are rejected before acquiring provider resources.
- Bounded `forEach` uses O(`maxConcurrency`) workers instead of one suspended task per item.
  Rollups retain input order; individual launch failures are collected without losing later items.
- Caller cancellation now propagates through single-child and fan-out waits. It cannot become
  an ordinary child failure or a successful `continueOnItemFailure` rollup.
- Child input handlers forward cancellation, drain bounded event channels, and propagate
  cancellation instead of falling back to untransformed inputs. The configured handler also
  runs separately for each `forEach` item inside its child's scope.

### Avoidable shared work

- Template validation eagerly built the whole transitive closure even when no expression needed
  it. Validation now resolves and memoizes individual reachability questions within the current
  definition, with a direct-dependency fast path and early exit for transitive searches.
- Runtime step-output lookup now matches the validator's case-insensitive step-reference rules.
- Run saves and plan journals shared a lock across all executions of a workflow. Gates are now
  keyed by actual run directory and retire when unused. File publication and index projection
  remain ordered within a run.

## Measured engine overhead

Windows, .NET 10, Release configuration, same machine. The probe executes provider-independent
no-op steps with null reporters/stores. Values are medians of warm samples (baseline: three;
after the performance changes: five), excluding sample zero.

| 1,000-step shape | Baseline elapsed | Updated elapsed | Baseline managed allocations | Updated allocations |
| --- | ---: | ---: | ---: | ---: |
| Independent parallel steps | 74.8 ms | 2.51 ms | 176.1 MB | 4.96 MB |
| Linear chain | 267.4 ms | 7.29 ms | 203.7 MB | 4.95 MB |

The updated probe observed zero dedicated execution threads. These measurements isolate engine
overhead; provider latency and real step work determine end-to-end workflow duration.

Reproduce using the checked-in probe:

```powershell
dotnet run --project benchmarks/Orchestra.Engine.Scheduling -c Release -- 1000 6
```

Raw local evidence lives under `artifacts/engine-review/`: `scheduling-before.log`,
`scheduling-benchmark.log`, and regression TRX files with before/fixed results.

## Regression coverage

The full local Release solution build and all **5,157 non-E2E backend tests** passed,
including **58 additional regression cases** from this review.

Controlled gates reproduce ordering and cleanup races without depending on relative provider
speeds. Coverage includes wide/deep DAGs, late completion, loop policy/feedback and failure
propagation, provider startup failures, concurrent AsyncLocal run isolation, bounded child
fan-out, input-handler backpressure/cancellation, and checkpoint batching/closure.

File-backed integration tests exercise concurrent save/read operations and a 48-step parallel
run interrupted for host shutdown, then resumed with every durable result preserved and no
completed step re-executed. Separate persistence tests hold one run's serialization open while
another run of the same workflow saves history or appends a plan.

Verification commands:

```powershell
dotnet build OrchestrationEngine.slnx --configuration Release -p:CopilotSkipCliDownload=true
dotnet test OrchestrationEngine.Tests.slnx --no-build --configuration Release -m:1 --filter "Category!=E2E"
```

The GitHub Actions matrix adds Linux/Windows execution, Portal type-check/unit/build checks,
and the isolated Playwright Portal E2E suite.
