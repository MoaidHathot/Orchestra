using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

var size = args.Length > 0 ? int.Parse(args[0]) : 1000;
var samples = args.Length > 1 ? int.Parse(args[1]) : 6;
if (size <= 0 || samples <= 0) throw new ArgumentOutOfRangeException(nameof(args), "Step and sample counts must be positive.");

foreach (var shape in new[] { "wide", "chain" })
{
    for (var sample = 0; sample < samples; sample++)
    {
        var steps = Enumerable.Range(0, size).Select(i => (OrchestrationStep)new TransformOrchestrationStep
        {
            Name = $"s{i}", Type = OrchestrationStepType.Transform, Template = "done",
            DependsOn = shape == "chain" && i > 0 ? [$"s{i - 1}"] : [],
        }).ToArray();
        var work = new Work();
        var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new UnusedBuilder(),
            NullOrchestrationReporter.Instance, NullLoggerFactory.Instance,
            stepExecutorRegistry: new StepExecutorRegistry().Register(work));
        var definition = new Orchestration { Name = "probe", Description = "Provider-independent scheduling probe", TimeoutSeconds = null, Steps = steps };
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var result = await executor.ExecuteAsync(definition);
        timer.Stop();
        if (result.Status != ExecutionStatus.Succeeded || result.StepResults.Count != size) throw new Exception("Invalid execution");
        Console.WriteLine(JsonSerializer.Serialize(new { shape, sample, size, elapsedMs = timer.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, executionThreads = work.Threads.Count,
            dedicatedThreads = work.Threads.Count(p => !p.Value) }));
    }
}

sealed class UnusedBuilder : AgentBuilder
{
    public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("unused");
    public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

sealed class Work : IStepExecutor
{
    public OrchestrationStepType StepType => OrchestrationStepType.Transform;
    public ConcurrentDictionary<int, bool> Threads { get; } = new();
    public Task<ExecutionResult> ExecuteAsync(OrchestrationStep step, OrchestrationExecutionContext context, CancellationToken cancellationToken = default)
    {
        Threads.TryAdd(Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread);
        return Task.FromResult(ExecutionResult.Succeeded("done"));
    }
}
