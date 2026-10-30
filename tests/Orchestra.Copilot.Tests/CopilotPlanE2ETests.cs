using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

[Trait("Category", "E2E")]
public class CopilotPlanE2ETests
{
	private static string Model => Environment.GetEnvironmentVariable("ORCHESTRA_COPILOT_TEST_MODEL") ?? "claude-opus-4.8";

	[Fact]
	public async Task NativeTodoMutations_AreObservedAndFinalStateIsCaptured()
	{
		var root = Directory.CreateTempSubdirectory("orchestra-copilot-plans-").FullName;
		var observations = new List<AgentPlanSnapshot>();
		try
		{
			var cli = await CopilotCliBootstrap.EnsureAsync();
			await using var builder = new CopilotAgentBuilder(NullLoggerFactory.Instance,
				new CopilotAgentPoolOptions { DefaultMaxInstancesPerRun = 1, CliSwapBudgetPerStep = 0 }, new Factory(cli, root));
			await using var scope = await builder.CreateRunScopeAsync();
			var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = Model, WorkingDirectory = root, SystemPrompt = "", SystemPromptMode = SystemPromptMode.Append });
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			var task = agent.SendAsync("Use the native sql tool and its session todos table to track two tasks with IDs calc and verify and titles Calculate and Verify. First insert them as pending, then in a separate tool call set calc to in_progress. Calculate 17+25 and verify it equals 42. Finally update both tasks to done. Return the result. Use session-local SQL, not persistent memory.", timeout.Token);
			await foreach (var evt in task.WithCancellation(timeout.Token))
			{
				if (evt.Plan is { } plan)
					observations.Add(plan);
			}
			(await task.GetResultAsync()).Content.Should().Contain("42");
			var updates = observations.Where(p => p.Source == "sql-todos" && p.Tasks.Count > 0).ToList();
			updates.Should().NotBeEmpty();
			updates.Last().Tasks.Should().Contain(t => t.Id == "calc" && t.Status == "completed");
			updates.Last().Tasks.Should().Contain(t => t.Id == "verify" && t.Status == "completed");
			updates.Should().Contain(p => p.Tasks.Any(t => t.Status == "pending" || t.Status == "in_progress"));
		}
		finally { SaveEvidence("copilot-todos", observations); Directory.Delete(root, true); }
	}

	[Fact]
	public async Task NativePlanDocument_ChangesAndDeletionProduceSnapshots()
	{
		var root = Directory.CreateTempSubdirectory("orchestra-copilot-document-").FullName;
		var observations = new ConcurrentQueue<AgentPlanSnapshot>();
		try
		{
			var cli = await CopilotCliBootstrap.EnsureAsync();
			await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(cli), BaseDirectory = root });
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
			await client.StartAsync(timeout.Token);
			AgentPlanMonitor? monitor = null;
			await using var session = await client.CreateSessionAsync(new SessionConfig
			{
				Model = Model,
				OnPermissionRequest = PermissionHandler.ApproveAll,
				Memory = new MemoryConfiguration { Enabled = false },
				EnableSessionStore = false,
				OnEvent = evt => { if (evt is SessionPlanChangedEvent) monitor?.RequestRefresh(); },
			}, timeout.Token);
			var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			monitor = new AgentPlanMonitor(new CopilotSdkSessionAdapter(session).ReadPlansAsync, plan =>
			{
				observations.Enqueue(plan);
				if (plan.Markdown == "# Plan\nVerify the result.")
					changed.TrySetResult();
				if (changed.Task.IsCompleted && plan.Source == "plan-document" && plan.Markdown is null)
					deleted.TrySetResult();
			}, NullLogger.Instance);
			await using var monitorLifetime = monitor;
#pragma warning disable GHCP001
			await session.Rpc.Plan.UpdateAsync("# Plan\nVerify the result.", timeout.Token);
			await changed.Task.WaitAsync(timeout.Token);
			await session.Rpc.Plan.DeleteAsync(timeout.Token);
			await deleted.Task.WaitAsync(timeout.Token);
#pragma warning restore GHCP001
		}
		finally { SaveEvidence("copilot-document", observations); Directory.Delete(root, true); }
	}

	private static void SaveEvidence(string name, IEnumerable<AgentPlanSnapshot> observations)
	{
		if (Environment.GetEnvironmentVariable("ORCHESTRA_COPILOT_TEST_EVIDENCE_DIR") is not { Length: > 0 } directory)
			return;
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(observations));
	}
	private sealed class Factory(string cli, string home) : ICopilotClientFactory
	{
		public ICopilotClient CreateClient() => new CopilotSdkClientAdapter(new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(cli), BaseDirectory = home }));
	}
}
