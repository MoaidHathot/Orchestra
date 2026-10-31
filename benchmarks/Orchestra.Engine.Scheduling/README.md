# Engine scheduling probe

Run from the repository root:

```powershell
dotnet run --project benchmarks/Orchestra.Engine.Scheduling -c Release -- 1000 6
```

Arguments are the number of steps and samples per shape. Each JSON output line reports
elapsed time, total managed allocations, and the distinct threads observed inside the
step executor. The workloads are a wide set of independent no-op steps and a linear
dependency chain, exercising validation, scheduling, result collection, and run teardown.
The reporter and persistence stores use their null implementations.

Use warm samples for comparisons: sample `0` includes startup/JIT work. Run builds in the
same configuration on the same machine and compare medians across several samples.
These measurements isolate engine overhead; provider calls, process execution, and disk
latency belong to their respective integration workloads. Correctness and concurrency
invariants are tested in `DagSchedulingTests`, `LoopExecutionPolicyTests`, and the
checkpoint/child invocation test suites.
