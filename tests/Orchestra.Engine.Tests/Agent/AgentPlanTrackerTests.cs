using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Threading.Channels;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Agent;

public class AgentPlanTrackerTests
{
	[Fact]
	public void Parser_PreservesPlanCaptureDefaultsAndOverrides()
	{
		var orchestration = OrchestrationParser.ParseOrchestration("""{"name":"plans","description":"controls","defaultCapturePlans":false,"steps":[{"name":"step","type":"Prompt","systemPrompt":"","userPrompt":"go","capturePlans":true}]}""", []);
		orchestration.DefaultCapturePlans.Should().BeFalse();
		((PromptOrchestrationStep)orchestration.Steps.Single()).CapturePlans.Should().BeTrue();
	}
	[Theory]
	[InlineData(null, true, 1)]
	[InlineData(false, true, 0)]
	[InlineData(true, false, 1)]
	public async Task PromptPipeline_ForwardsCaptureControlAndSavesPlanInFinalTrace(bool? stepCapture, bool defaultCapture, int expected)
	{
		var store = new Store();
		var builder = new MockAgentBuilder().WithHandler((_, _, _) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.TryWrite(new() { Type = AgentEventType.PlanUpdated, Plan = Plan() });
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromResult(new AgentResult { Content = "ok" }));
		});
		var executor = new PromptExecutor(builder, Substitute.For<IOrchestrationReporter>(), DefaultPromptFormatter.Instance,
			NullLogger<PromptExecutor>.Instance, planHistory: store);
		var step = new PromptOrchestrationStep { Name = "step", Type = OrchestrationStepType.Prompt, Model = "model", SystemPrompt = "", UserPrompt = "go", CapturePlans = stepCapture };
		var context = new OrchestrationExecutionContext { OrchestrationInfo = new("workflow", "1", "run", DateTimeOffset.UtcNow), DefaultCapturePlans = defaultCapture };
		var result = await executor.ExecuteAsync(step, context);
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		builder.CapturedConfig!.CapturePlans.Should().Be(stepCapture ?? defaultCapture);
		result.Trace!.Plans.Should().HaveCount(expected);
		result.Trace.WithContext(context, step, []).Plans.Should().HaveCount(expected);
		store.Entries.Should().HaveCount(expected);
	}
	private static readonly PlanRunIdentity Run = new("run", "workflow", "1", "manual", DateTimeOffset.UtcNow);
	private static AgentPlanSnapshot Plan(string session = "session", string status = "pending") => new()
	{
		Provider = "provider",
		SessionId = session,
		Source = "todos",
		Tasks = [new() { Id = "task", Title = "Do work", Status = status, RawStatus = status }],
	};

	[Fact]
	public async Task Revisions_AreOwnedDeduplicatedAndScopedToExecutionAndSession()
	{
		var store = new Store();
		var reporter = Substitute.For<IOrchestrationReporter>();
		var tracker = new AgentPlanTracker(Run, "step", 2, reporter, store, NullLogger.Instance);
		var tasks = new[] { new AgentPlanTask { Id = "task", Title = "Do work", Status = "pending" } };
		await tracker.ObserveAsync(Plan() with { Tasks = tasks });
		tasks[0] = new() { Title = "caller mutation" };
		store.Entries[0].Plan.Tasks[0].Title.Should().Be("Do work");
		await tracker.ObserveAsync(store.Entries[0].Plan);
		await tracker.ObserveAsync(Plan(status: "completed"));
		await tracker.ObserveAsync(Plan(session: "restarted"));
		store.Entries.Select(r => r.Revision).Should().Equal(1, 2, 3);
		store.Entries.Should().OnlyContain(r => r.LoopIteration == 2 && r.Run.RunId == "run");
		store.Entries[0].StreamId.Should().Be(store.Entries[1].StreamId).And.NotBe(store.Entries[2].StreamId);
		var another = new AgentPlanTracker(Run, "step", 3, reporter, store, NullLogger.Instance);
		await another.ObserveAsync(Plan());
		store.Entries.Last().StepExecutionId.Should().NotBe(store.Entries[0].StepExecutionId);
		reporter.Received(4).ReportPlanUpdated(Arg.Any<AgentPlanRevision>());
	}

	[Fact]
	public async Task ConcurrentObservations_KeepSequentialUniqueRevisions()
	{
		var store = new Store();
		var tracker = new AgentPlanTracker(Run, "step", null, Substitute.For<IOrchestrationReporter>(), store, NullLogger.Instance);
		await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => tracker.ObserveAsync(Plan(session: "session-" + i)))));
		store.Entries.Select(r => r.Revision).Should().Equal(Enumerable.Range(1, 30).Select(i => (long)i));
		tracker.Latest.Should().HaveCount(30);
	}

	[Fact]
	public async Task CaptureFailure_PreservesLastKnownContent_AndRecoveryClearsError()
	{
		var tracker = new AgentPlanTracker(Run, "step", null, Substitute.For<IOrchestrationReporter>(), null, NullLogger.Instance);
		await tracker.ObserveAsync(Plan());
		await tracker.ObserveAsync(Plan() with { Tasks = [], CaptureError = "read failed" });
		tracker.Latest.Single().Plan.Tasks.Should().ContainSingle();
		tracker.Latest.Single().Plan.CaptureError.Should().Be("read failed");
		await tracker.ObserveAsync(Plan(status: "completed"));
		tracker.Latest.Single().Plan.CaptureError.Should().BeNull();
		tracker.Latest.Single().Plan.Tasks[0].Status.Should().Be("completed");
	}

	[Fact]
	public async Task StorageFailure_StillPublishesObservedPlanAndDiagnostic()
	{
		var reporter = Substitute.For<IOrchestrationReporter>();
		var tracker = new AgentPlanTracker(Run, "step", null, reporter, new Store { Fail = true }, NullLogger.Instance);
		await tracker.ObserveAsync(Plan());
		tracker.Latest.Should().ContainSingle();
		reporter.Received().ReportSessionWarning("plan_history_write_failed", Arg.Any<string>());
		reporter.Received().ReportPlanUpdated(Arg.Any<AgentPlanRevision>());
	}

	private sealed class Store : IPlanHistoryStore
	{
		public bool Fail { get; init; }
		public List<AgentPlanRevision> Entries { get; } = [];
		public Task AppendPlanAsync(AgentPlanRevision revision, CancellationToken token = default)
		{ if (Fail) throw new IOException("disk unavailable"); Entries.Add(revision); return Task.CompletedTask; }
		public Task<PlanHistoryPage> ReadPlansAsync(string id, string? step = null, long after = 0, int limit = 100, CancellationToken token = default) => Task.FromResult(new PlanHistoryPage(Entries, null));
	}
}
