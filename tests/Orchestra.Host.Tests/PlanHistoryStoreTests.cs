using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

public class PlanHistoryStoreTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("orchestra-plans-").FullName;
	private static readonly PlanRunIdentity Run = new("run1", "workflow", "1", "manual", DateTimeOffset.UtcNow);
	private static AgentPlanRevision Revision(int n, string step = "step") => new()
	{
		Run = Run, StepName = step, StepExecutionId = "execution", StreamId = "stream", Revision = n,
		CapturedAt = DateTimeOffset.UtcNow, Plan = new() { Provider = "test", SessionId = "session", Source = "todos", Tasks = [new() { Title = "Task " + n }] },
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
