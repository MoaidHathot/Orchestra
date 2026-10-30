using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

public sealed class PlanExecutionLifecycleTests : IDisposable
{
	private readonly string _directory = Directory.CreateTempSubdirectory("orchestra-plan-lifecycle-").FullName;

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LoopIterations_KeepTheirOwnPlans_IncludingAnInterruptedTarget(bool cancel)
	{
		using var store = new FileSystemRunStore(_directory);
		using var reporter = new SseReporter();
		var subscription = reporter.SubscribeWithSnapshot();
		var builder = new ScriptedBuilder(async (step, attempt, writer, token) =>
		{
			if (cancel && step == "draft" && attempt == 2)
			{
				Publish(writer, step, attempt, "in_progress");
				try
				{ await Task.Delay(Timeout.Infinite, token); }
				finally { Publish(writer, step, attempt, "pending"); }
			}
			Publish(writer, step, attempt, "completed");
			return new() { Content = step == "check" && attempt == 1 ? "Try again" : "PASS" };
		});
		var definition = OrchestrationParser.ParseOrchestration("""
			{"name":"loop-plans","description":"Loop identity","defaultModel":"test","steps":[
			  {"name":"draft","type":"Prompt","systemPrompt":"","userPrompt":"Draft"},
			  {"name":"check","type":"Prompt","systemPrompt":"","userPrompt":"Check","dependsOn":["draft"],
			   "loop":{"target":"draft","exitPattern":"PASS","maxIterations":2}}
			]}
			""", []);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, reporter, NullLoggerFactory.Instance, runStore: store);
		var running = executor.ExecuteAsync(definition, executionIdOverride: "loop-run", cancellationToken: cancellation.Token);
		if (cancel)
		{
			while (true)
			{
				var evt = await subscription.Future!.ReadAsync(cancellation.Token);
				if (evt.Type != "step-plan-updated")
					continue;
				using var json = JsonDocument.Parse(evt.Data);
				var revision = json.RootElement.GetProperty("revision");
				if (revision.GetProperty("stepName").GetString() == "draft" && revision.TryGetProperty("loopIteration", out var iteration) && iteration.GetInt32() == 1)
					break;
			}
			await cancellation.CancelAsync();
		}
		await running.WaitAsync(TimeSpan.FromSeconds(15));
		var saved = (await store.GetRunAsync("loop-plans", "loop-run"))!;
		var original = saved.AllStepRecords["draft"].Trace!.Plans.Single();
		original.LoopIteration.Should().BeNull();
		original.Plan.Tasks.Single().Title.Should().Be("draft execution 1");
		original.Plan.Tasks.Single().Status.Should().Be("completed");
		var latest = saved.StepRecords["draft"].Trace!.Plans.Single();
		latest.LoopIteration.Should().Be(1);
		latest.Plan.Tasks.Single().Title.Should().Be("draft execution 2");
		latest.StepExecutionId.Should().NotBe(original.StepExecutionId);
		saved.AllStepRecords["draft:iteration-1"].Trace!.Plans.Single().Should().BeEquivalentTo(latest);
		saved.StepRecords["check"].Trace!.Plans.Should().OnlyContain(p => p.StepName == "check");
		if (cancel)
		{
			saved.StepRecords["draft"].Status.Should().Be(ExecutionStatus.Cancelled);
			latest.Plan.Tasks.Single().Status.Should().Be("pending", "cancellation must not invent a completed plan");
		}
	}

	[Fact]
	public async Task Retry_KeepsBothExecutionHistories_AndOnlyTheFinalAttemptInTheStepTrace()
	{
		using var store = new FileSystemRunStore(_directory);
		using var reporter = new SseReporter();
		var builder = new ScriptedBuilder((step, attempt, writer, _) =>
		{
			Publish(writer, step, attempt, attempt == 1 ? "blocked" : "completed");
			return attempt == 1 ? Task.FromException<AgentResult>(new InvalidOperationException("Retryable failure")) : Task.FromResult(new AgentResult { Content = "done" });
		});
		var definition = OrchestrationParser.ParseOrchestration("""
			{"name":"retry-plans","description":"Retry identity","defaultModel":"test","steps":[
			  {"name":"work","type":"Prompt","systemPrompt":"","userPrompt":"Work","retry":{"maxRetries":1,"backoffSeconds":0}}
			]}
			""", []);
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, reporter, NullLoggerFactory.Instance, runStore: store);
		await executor.ExecuteAsync(definition, executionIdOverride: "retry-run");
		var saved = (await store.GetRunAsync("retry-plans", "retry-run"))!;
		var history = await store.ReadPlansAsync("retry-run");
		history.Items.Should().HaveCount(2);
		history.Items.Select(p => p.StepExecutionId).Should().OnlyHaveUniqueItems();
		saved.StepRecords["work"].Trace!.Plans.Single().Should().BeEquivalentTo(history.Items.Last());
	}

	[Fact]
	public async Task CancellationBeforeFirstSnapshot_WaitsForTheStartedPlanCapture()
	{
		using var store = new FileSystemRunStore(_directory);
		using var reporter = new SseReporter();
		var subscription = reporter.SubscribeWithSnapshot();
		var builder = new ScriptedBuilder(async (step, attempt, writer, token) =>
		{
			if (step == "slow")
			{
				writer.TryWrite(new() { Type = AgentEventType.PlanCaptureStarted });
				writer.TryWrite(new() { Type = AgentEventType.MessageDelta, Content = "ready" });
			}
			try
			{ await Task.Delay(Timeout.Infinite, token); }
			finally
			{
				if (step == "slow")
				{
					await Task.Delay(100, CancellationToken.None);
					Publish(writer, step, attempt, "pending");
				}
			}
			return new() { Content = "unreachable" };
		});
		var definition = OrchestrationParser.ParseOrchestration("""
			{"name":"cancel-plans","description":"Final capture","defaultModel":"test","steps":[
			  {"name":"slow","type":"Prompt","systemPrompt":"","userPrompt":"Work"},
			  {"name":"fast","type":"Prompt","systemPrompt":"","userPrompt":"Work","capturePlans":false}
			]}
			""", []);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, reporter, NullLoggerFactory.Instance, runStore: store);
		var running = executor.ExecuteAsync(definition, executionIdOverride: "cancel-run", cancellationToken: cancellation.Token);
		while ((await subscription.Future!.ReadAsync(cancellation.Token)).Type != "content-delta")
		{ }
		await cancellation.CancelAsync();
		await running.WaitAsync(TimeSpan.FromSeconds(15));
		var saved = (await store.GetRunAsync("cancel-plans", "cancel-run"))!;
		saved.StepRecords["slow"].Trace!.Plans.Single().Plan.Tasks.Single().Status.Should().Be("pending");
		(await store.ReadPlansAsync("cancel-run", "slow")).Items.Should().ContainSingle();
	}

	private static void Publish(ChannelWriter<AgentEvent> writer, string step, int attempt, string status)
		=> writer.TryWrite(new()
		{
			Type = AgentEventType.PlanUpdated,
			Plan = new()
			{
				Provider = "test",
				SessionId = $"{step}-{attempt}",
				Source = "todos",
				Tasks = [new() { Id = "task", Title = $"{step} execution {attempt}", Status = status }],
			}
		});

	private sealed class ScriptedBuilder(Func<string, int, ChannelWriter<AgentEvent>, CancellationToken, Task<AgentResult>> execute) : AgentBuilder
	{
		private readonly ConcurrentDictionary<string, int> _attempts = new();
		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("test");
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
			=> Task.FromResult<IAgent>(new ScriptedAgent((writer, token) => execute(config.EngineToolCtx!.StepName!,
				_attempts.AddOrUpdate(config.EngineToolCtx.StepName!, 1, (_, count) => count + 1), writer, token)));
	}
	private sealed class ScriptedAgent(Func<ChannelWriter<AgentEvent>, CancellationToken, Task<AgentResult>> execute) : IAgent
	{
		public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			async Task<AgentResult> RunAsync()
			{
				try
				{ return await execute(channel.Writer, cancellationToken); }
				finally { channel.Writer.TryComplete(); }
			}
			return new(channel.Reader, RunAsync());
		}
	}

	public void Dispose() => Directory.Delete(_directory, true);
}
