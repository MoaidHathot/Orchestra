using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Api;

namespace Orchestra.Host.Tests;

public class PlanHistoryStoreTests : IDisposable
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExecutionPipeline_PersistsBeforeCompletionAndKeepsLatestInSavedTrace(bool fail)
	{
		using var store = new FileSystemRunStore(_root);
		using var reporter = new SseReporter();
		var subscription = reporter.SubscribeWithSnapshot();
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var builder = new PlanningBuilder(release, fail);
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, reporter, NullLoggerFactory.Instance, runStore: store);
		var definition = OrchestrationParser.ParseOrchestration("""{"name":"workflow","description":"Plans","defaultModel":"test","steps":[{"name":"work","type":"Prompt","systemPrompt":"","userPrompt":"go"}]}""", []);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var running = executor.ExecuteAsync(definition, executionIdOverride: "pipeline-run", cancellationToken: timeout.Token);
		while ((await subscription.Future!.ReadAsync(timeout.Token)).Type != "step-plan-updated")
		{ }
		(await store.ReadPlansAsync("pipeline-run")).Items.Should().ContainSingle();
		running.IsCompleted.Should().BeFalse();
		release.TrySetResult();
		(await running).Status.Should().Be(fail ? ExecutionStatus.Failed : ExecutionStatus.Succeeded);
		var saved = await store.GetRunAsync("workflow", "pipeline-run");
		saved!.StepRecords["work"].Trace!.Plans.Single().Plan.Tasks.Single().Status.Should().Be(fail ? "blocked" : "completed");
		(await store.ReadPlansAsync("pipeline-run")).Items.Should().HaveCount(2);
	}

	[Fact]
	public async Task Cancellation_DrainsFinalPlanAndPersistsItInTheCancelledRun()
	{
		using var store = new FileSystemRunStore(_root);
		using var reporter = new SseReporter();
		var subscription = reporter.SubscribeWithSnapshot();
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new PlanningBuilder(new(), false), reporter, NullLoggerFactory.Instance, runStore: store);
		var definition = OrchestrationParser.ParseOrchestration("""{"name":"workflow","description":"Plans","defaultModel":"test","steps":[{"name":"work","type":"Prompt","systemPrompt":"","userPrompt":"go"}]}""", []);
		using var cancellation = new CancellationTokenSource();
		var running = executor.ExecuteAsync(definition, executionIdOverride: "cancel-run", cancellationToken: cancellation.Token);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while ((await subscription.Future!.ReadAsync(timeout.Token)).Type != "step-plan-updated")
		{ }
		await cancellation.CancelAsync();
		(await running.WaitAsync(timeout.Token)).Status.Should().Be(ExecutionStatus.Cancelled);
		(await store.ReadPlansAsync("cancel-run")).Items.Last().Plan.Tasks.Single().Status.Should().Be("cancelled");
		var saved = await store.GetRunAsync("workflow", "cancel-run");
		saved!.StepRecords["work"].Trace!.Plans.Single().Plan.Tasks.Single().Status.Should().Be("cancelled");
	}

	private sealed class PlanningBuilder(TaskCompletionSource release, bool fail) : AgentBuilder
	{
		public override Task<IAgent> BuildAgentAsync(CancellationToken token = default) => Task.FromResult<IAgent>(new PlanningAgent(release, fail));
		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken token = default) => BuildAgentAsync(token);
		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("test");
	}
	private sealed class PlanningAgent(TaskCompletionSource release, bool fail) : IAgent
	{
		public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			async Task<AgentResult> RunAsync()
			{
				void Publish(string status) => channel.Writer.TryWrite(new()
				{
					Type = AgentEventType.PlanUpdated,
					Plan = new() { Provider = "test", SessionId = "session", Source = "todos", Tasks = [new() { Title = "Work", Status = status }] }
				});
				try
				{
					Publish("in_progress");
					await release.Task.WaitAsync(cancellationToken);
					Publish(fail ? "blocked" : "completed");
					if (fail)
						throw new InvalidOperationException("Task failed after reporting its plan.");
					return new() { Content = "done" };
				}
				finally { if (cancellationToken.IsCancellationRequested) Publish("cancelled"); channel.Writer.Complete(); }
			}
			return new(channel.Reader, RunAsync());
		}
	}
	private readonly string _root = Directory.CreateTempSubdirectory("orchestra-plans-").FullName;
	private static readonly PlanRunIdentity Run = new("run1", "workflow", "1", "manual", DateTimeOffset.UtcNow);
	private static AgentPlanRevision Revision(int n, string step = "step") => new()
	{
		Run = Run,
		StepName = step,
		StepExecutionId = "execution",
		StreamId = "stream",
		Revision = n,
		CapturedAt = DateTimeOffset.UtcNow,
		Plan = new() { Provider = "test", SessionId = "session", Source = "todos", Tasks = [new() { Title = "Task " + n }] },
	};

	[Fact]
	public async Task History_IsReadableBeforeRunCompletionAndAfterStoreRestart_WithPagination()
	{
		using (var store = new FileSystemRunStore(_root))
		{
			await store.AppendPlanAsync(Revision(1));
			await store.AppendPlanAsync(Revision(2, "other"));
			await store.AppendPlanAsync(Revision(3));
			File.Exists(Path.Combine(store.ResolveRunDirectory(Run.OrchestrationName, Run.OrchestrationVersion, Run.TriggeredBy, Run.StartedAt, Run.RunId), "run.json")).Should().BeFalse();
		}
		using var restarted = new FileSystemRunStore(_root);
		var first = await restarted.ReadPlansAsync("run1", "step", limit: 1);
		first.Items.Single().Revision.Should().Be(1);
		first.NextCursor.Should().NotBeNull();
		var next = await restarted.ReadPlansAsync("run1", "step", first.NextCursor!.Value);
		next.Items.Single().Revision.Should().Be(3);
		next.NextCursor.Should().BeNull();
	}

	[Fact]
	public async Task PartialTail_IsIgnoredAndRepairedBeforeTheNextAppend()
	{
		using var store = new FileSystemRunStore(_root);
		await store.AppendPlanAsync(Revision(1));
		var path = Path.Combine(store.ResolveRunDirectory(Run.OrchestrationName, Run.OrchestrationVersion, Run.TriggeredBy, Run.StartedAt, Run.RunId), FileSystemRunStore.PlanHistoryFileName);
		await File.AppendAllTextAsync(path, "{\"interrupted\":");
		var partial = await store.ReadPlansAsync("run1");
		partial.Items.Should().ContainSingle();
		partial.IsPartial.Should().BeTrue();
		await store.AppendPlanAsync(Revision(2));
		var repaired = await store.ReadPlansAsync("run1");
		repaired.Items.Select(r => r.Revision).Should().Equal(1, 2);
		repaired.IsPartial.Should().BeFalse();
	}

	[Fact]
	public async Task ConcurrentStepUpdates_DoNotInterleaveJsonRecords()
	{
		using var store = new FileSystemRunStore(_root);
		await Task.WhenAll(Enumerable.Range(1, 40).Select(n => store.AppendPlanAsync(Revision(n, "step-" + n))));
		var page = await store.ReadPlansAsync("run1");
		page.Items.Should().HaveCount(40);
		page.IsPartial.Should().BeFalse();
		page.Items.Select(r => r.Revision).Should().OnlyHaveUniqueItems();
	}

	[Theory]
	[InlineData("workflow")]
	[InlineData(".workflow")]
	public async Task Lookup_UsesStoredIdentity_WhenRunIdsShareAFolderSuffix(string name)
	{
		using var store = new FileSystemRunStore(_root);
		await store.AppendPlanAsync(Revision(1) with { Run = Run with { OrchestrationName = name } });
		await store.AppendPlanAsync(Revision(2) with { Run = Run with { OrchestrationName = name, RunId = "prefix_run1" } });
		(await store.ReadPlansAsync("run1")).Items.Single().Revision.Should().Be(1);
		(await store.ReadPlansAsync("prefix_run1")).Items.Single().Revision.Should().Be(2);
	}

	[Fact]
	public async Task History_SkipsCorruptCommittedRows_AndRequiresANewlineCommit()
	{
		using var store = new FileSystemRunStore(_root);
		await store.AppendPlanAsync(Revision(1));
		var path = Path.Combine(store.ResolveRunDirectory(Run.OrchestrationName, Run.OrchestrationVersion, Run.TriggeredBy, Run.StartedAt, Run.RunId), FileSystemRunStore.PlanHistoryFileName);
		await File.AppendAllTextAsync(path, "not-json\n{\"run\":null,\"stepName\":\"step\",\"stepExecutionId\":\"id\",\"streamId\":\"s\",\"plan\":null}\n");
		await store.AppendPlanAsync(Revision(2));
		await File.AppendAllTextAsync(path, JsonSerializer.Serialize(Revision(3), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
		var page = await store.ReadPlansAsync("run1");
		page.Items.Select(p => p.Revision).Should().Equal(1, 2);
		page.IsPartial.Should().BeTrue();
	}

	[Theory]
	[InlineData("../run")]
	[InlineData("*")]
	public async Task UnsafeLookupIds_AreRejected(string id)
	{
		using var store = new FileSystemRunStore(_root);
		Func<Task> read = () => store.ReadPlansAsync(id);
		await read.Should().ThrowAsync<ArgumentException>();
	}

	public void Dispose() => Directory.Delete(_root, true);
}
