using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Persistence;
using Orchestra.OpenCode;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>Real provider → engine → live reporter → durable run store, including cancellation.</summary>
[Trait("Category", "E2E")]
public sealed class ProviderPlanPersistenceE2ETests
{
	[ProviderPlanE2ETheory]
	[InlineData("copilot", false)]
	[InlineData("copilot", true)]
	[InlineData("opencode", false)]
	[InlineData("opencode", true)]
	public async Task LivePlanHistory_SurvivesCompletionOrCancellation(string provider, bool cancel)
	{
		var root = Directory.CreateTempSubdirectory("orchestra-provider-plan-").FullName;
		try
		{
			using var store = new FileSystemRunStore(root);
			using var reporter = new SseReporter();
			await using var copilot = new CopilotAgentBuilder(NullLoggerFactory.Instance,
				new CopilotAgentPoolOptions { DefaultMinInstances = 0, DefaultMaxInstancesPerRun = 1, CliSwapBudgetPerStep = 0 });
			await using var opencode = new OpenCodeAgentBuilder(NullLoggerFactory.Instance,
				new OpenCodeAgentPoolOptions { DefaultMinInstances = 0, DefaultMaxInstancesPerRun = 1, SwapBudgetPerStep = 0 });
			var registry = new AgentProviderRegistry(new Dictionary<string, AgentBuilder>
			{
				[provider] = provider == "copilot" ? copilot : opencode,
			}, provider);
			var source = provider == "copilot" ? "sql-todos" : "orchestra-plan-tool";
			var model = provider == "copilot"
				? Environment.GetEnvironmentVariable("ORCHESTRA_COPILOT_TEST_MODEL") ?? "claude-opus-4.8"
				: Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_TEST_MODEL") ?? "github-copilot/claude-opus-4.8";
			var planning = provider == "copilot"
				? "Use the native sql tool and session todos table to record tasks with IDs calc and verify, titles Calculate and Verify, and status pending. Then in a separate tool call set calc to in_progress. Use the same table to update both tasks to done after verification."
				: "Use orchestra_update_plan to record tasks with IDs calc and verify, titles Calculate and Verify, and statuses in_progress and pending. Update the complete task list with both tasks completed after verification.";
			var work = cancel
				? "After recording the initial plan, run a shell command that waits 30 seconds before calculating 17+25. Then verify the answer."
				: "Calculate 17+25 with a shell tool and verify the answer equals 42. Keep the plan current as instructed, then return 42.";
			var definition = new Orchestration
			{
				Name = "provider-plan-e2e",
				Description = "Live plan durability",
				DefaultProvider = provider,
				Steps = [new PromptOrchestrationStep
				{
					Name = "work", Type = OrchestrationStepType.Prompt, Model = model, WorkingDirectory = root,
					SystemPrompt = "Use the requested tools and maintain an explicit plan for this verification.",
					UserPrompt = planning + "\n" + work,
				}],
			};
			var runId = Guid.NewGuid().ToString("N");
			var subscription = reporter.SubscribeWithSnapshot();
			using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			var executor = new OrchestrationExecutor(new OrchestrationScheduler(), registry, reporter, NullLoggerFactory.Instance, runStore: store);
			var execution = executor.ExecuteAsync(definition, executionIdOverride: runId, cancellationToken: cancellation.Token);
			var cancelledAfterPlan = false;
			if (cancel)
			{
				try
				{
					while (true)
					{
						var read = subscription.Future!.ReadAsync(cancellation.Token).AsTask();
						await Task.WhenAny(read, execution);
						if (execution.IsCompleted && !read.IsCompleted)
						{
							await cancellation.CancelAsync();
							try
							{ await read; }
							catch (OperationCanceledException) { }
							break;
						}
						var evt = await read;
						if (evt.Type != "step-plan-updated")
							continue;
						using var json = JsonDocument.Parse(evt.Data);
						var plan = json.RootElement.GetProperty("revision").GetProperty("plan");
						if (plan.GetProperty("source").GetString() != source || plan.GetProperty("tasks").GetArrayLength() == 0)
							continue;
						(await store.ReadPlansAsync(runId)).Items.Should().NotBeEmpty("the live event follows its durable append");
						cancelledAfterPlan = true;
						await cancellation.CancelAsync();
						break;
					}
				}
				catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { /* settle and save failure evidence below */ }
			}
			var result = await execution;
			var history = await store.ReadPlansAsync(runId, "work");
			var saved = (await store.GetRunAsync(definition.Name, runId))!;
			if (Environment.GetEnvironmentVariable("ORCHESTRA_PLAN_EVIDENCE_DIR") is { Length: > 0 } evidence)
			{
				var output = Path.Combine(evidence, $"{provider}-{(cancel ? "cancelled" : "completed")}-{runId}");
				Directory.CreateDirectory(output);
				await File.WriteAllTextAsync(Path.Combine(output, "run.json"), JsonSerializer.Serialize(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
				await File.WriteAllTextAsync(Path.Combine(output, "history.json"), JsonSerializer.Serialize(history, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
			}
			result.Status.Should().Be(cancel ? ExecutionStatus.Cancelled : ExecutionStatus.Succeeded,
				string.Join("; ", result.StepResults.Values.Select(r => $"{r.ErrorMessage} {r.Content}")));
			if (cancel)
				cancelledAfterPlan.Should().BeTrue("the test must cancel after a real live plan, not its own deadline");
			var plans = history.Items.Where(r => r.Plan.Source == source && r.Plan.Tasks.Count > 0).ToList();
			plans.Should().NotBeEmpty();
			var latest = saved.StepRecords["work"].Trace!.Plans.Single(p => p.Plan.Source == source);
			latest.Should().BeEquivalentTo(plans.Last());
			reporter.GetCurrentSnapshot().Steps["work"].Plans.Single(p => p.Plan.Source == source).Should().BeEquivalentTo(latest);
			if (!cancel)
			{
				result.StepResults["work"].Content.Should().Contain("42");
				plans.Should().Contain(p => p.Plan.Tasks.Any(t => t.Status == "pending" || t.Status == "in_progress"));
				latest.Plan.Tasks.Should().HaveCount(2).And.OnlyContain(t => t.Status == "completed");
			}
		}
		finally
		{
			// Windows can briefly retain a terminated shell's working-directory handle.
			for (var attempt = 0; Directory.Exists(root); attempt++)
			{
				try { Directory.Delete(root, true); }
				catch (IOException) when (attempt < 30) { await Task.Delay(100); }
			}
		}
	}
}

public sealed class ProviderPlanE2ETheoryAttribute : TheoryAttribute
{
	public ProviderPlanE2ETheoryAttribute()
	{
		if (Environment.GetEnvironmentVariable("ORCHESTRA_PLAN_E2E") != "1")
			Skip = "Set ORCHESTRA_PLAN_E2E=1 to run plan persistence against authenticated Copilot and OpenCode.";
	}
}
