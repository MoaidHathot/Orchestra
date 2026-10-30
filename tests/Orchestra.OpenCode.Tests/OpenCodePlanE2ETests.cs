using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

[Trait("Category", "E2E")]
public class OpenCodePlanE2ETests
{
	[OpenCodeAvailableFact]
	public async Task ExplicitPlanTool_StreamsTaskProgressAndMarkdownBeforeCompletion()
	{
		var directory = Directory.CreateTempSubdirectory("orchestra-live-plans-").FullName;
		var observations = new List<AgentPlanSnapshot>();
		try
		{
			await using var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, new OpenCodeAgentPoolOptions { DefaultMaxInstancesPerRun = 1, SwapBudgetPerStep = 0 });
			await using var scope = await builder.CreateRunScopeAsync();
			var agent = await builder.BuildAgentAsync(new AgentBuildConfig
			{
				Model = Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_TEST_MODEL") ?? "github-copilot/claude-opus-4.8",
				WorkingDirectory = directory,
				SystemPrompt = "Use the available orchestration planning tool to track the requested work.",
			});
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			var task = agent.SendAsync("Use orchestra_update_plan to publish two tasks with IDs calc and verify, titles Calculate and Verify, and statuses in_progress and pending. Include a Markdown plan. Calculate 17+25 with the shell. Then update the entire plan with both tasks completed and return 42.", timeout.Token);
			await foreach (var evt in task.WithCancellation(timeout.Token))
			{
				if (evt.Plan is { } plan)
					observations.Add(plan);
			}
			(await task.GetResultAsync()).Content.Should().Contain("42");
			observations.Should().Contain(p => p.Tasks.Any(t => t.Status == "in_progress"));
			observations.Last().Tasks.Should().HaveCount(2).And.OnlyContain(t => t.Status == "completed");
			observations.Should().Contain(p => !string.IsNullOrWhiteSpace(p.Markdown));
			observations.Should().OnlyContain(p => p.Provider == "opencode" && p.Shared);
		}
		finally
		{
			if (Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_TEST_EVIDENCE_DIR") is { Length: > 0 } output)
			{
				Directory.CreateDirectory(output);
				File.WriteAllText(Path.Combine(output, "opencode-plans-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(observations));
			}
			try
			{ Directory.Delete(directory, true); }
			catch (IOException) when (!Directory.EnumerateFileSystemEntries(directory).Any()) { }
		}
	}
}
