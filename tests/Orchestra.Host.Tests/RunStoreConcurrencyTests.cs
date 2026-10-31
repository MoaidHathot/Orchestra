using System.Collections;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

public class RunStoreConcurrencyTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task BusyRun_DoesNotBlockPersistenceForAnotherRunOfTheSameOrchestration(bool appendPlan)
	{
		var directory = Directory.CreateTempSubdirectory("orchestra-run-concurrency-").FullName;
		using var release = new ManualResetEventSlim();
		var serializing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			using var store = new FileSystemRunStore(directory);
			var first = Task.Run(() => store.SaveRunAsync(Record("busy", new BlockingSteps(serializing, release)), CancellationToken.None));
			Task? independent = null;
			try
			{
				await serializing.Task.WaitAsync(TimeSpan.FromSeconds(5));
				independent = appendPlan
					? store.AppendPlanAsync(new AgentPlanRevision
					{
						Run = new("independent", "same-workflow", "1.0.0", "manual", DateTimeOffset.UtcNow),
						StepName = "step", StepExecutionId = "attempt", StreamId = "stream", Revision = 1,
						Plan = new() { Provider = "test", SessionId = "session", Source = "todos" },
					})
					: store.SaveRunAsync(Record("independent", new Dictionary<string, StepRunRecord>()), CancellationToken.None);
				await independent.WaitAsync(TimeSpan.FromSeconds(3));
			}
			finally
			{
				release.Set();
				await first.WaitAsync(TimeSpan.FromSeconds(5));
				if (independent is not null) await independent.WaitAsync(TimeSpan.FromSeconds(5));
			}
			if (appendPlan)
				(await store.ReadPlansAsync("independent")).Items.Should().ContainSingle();
			else
				(await store.GetRunAsync("same-workflow", "independent"))!.Status.Should().Be(ExecutionStatus.Succeeded);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static OrchestrationRunRecord Record(string id, IReadOnlyDictionary<string, StepRunRecord> steps) => new()
	{
		RunId = id, OrchestrationName = "same-workflow", StartedAt = DateTimeOffset.UtcNow,
		CompletedAt = DateTimeOffset.UtcNow, Status = ExecutionStatus.Succeeded,
		FinalContent = "done", StepRecords = new Dictionary<string, StepRunRecord>(), AllStepRecords = steps,
	};

	// Delay actual serialization while the store owns its publication gate. This gives
	// a deterministic concurrency test without timing a large disk write or requiring
	// operating-system-specific file locking behavior.
	private sealed class BlockingSteps(TaskCompletionSource entered, ManualResetEventSlim release) : IReadOnlyDictionary<string, StepRunRecord>
	{
		private readonly Dictionary<string, StepRunRecord> _values = new()
		{
			["step"] = new() { StepName = "step", Status = ExecutionStatus.Succeeded, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, Content = "done" },
		};
		public int Count => _values.Count;
		public IEnumerable<string> Keys => _values.Keys;
		public IEnumerable<StepRunRecord> Values => _values.Values;
		public StepRunRecord this[string key] => _values[key];
		public bool ContainsKey(string key) => _values.ContainsKey(key);
		public bool TryGetValue(string key, out StepRunRecord value) => _values.TryGetValue(key, out value!);
		public IEnumerator<KeyValuePair<string, StepRunRecord>> GetEnumerator()
		{
			entered.TrySetResult();
			release.Wait();
			return _values.GetEnumerator();
		}
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
