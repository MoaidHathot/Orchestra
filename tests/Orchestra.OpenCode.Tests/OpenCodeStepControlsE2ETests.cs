using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

[Trait("Category", "E2E")]
public sealed class OpenCodeStepControlsE2ETests
{
	private static string Model => Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_TEST_MODEL") ?? "github-copilot/claude-opus-4.8";

	[OpenCodeAvailableFact]
	public async Task Autopilot_SkillsInstructionsJsonAndDownstreamCommand_UseDefinitionDirectory()
	{
		using var fixture = new Fixture("structured-autopilot");
		var marker = "POLICY-" + Guid.NewGuid().ToString("N");
		fixture.Write("facts.json", "{\"a\":4,\"b\":7}");
		fixture.Write("instructions/AGENTS.md", "Read facts.json and use the sum-policy skill. Never modify fixture inputs.\n");
		fixture.Write("skills/sum-policy/SKILL.md", "---\nname: sum-policy\ndescription: Calculate the policy-adjusted sum of facts.\n---\nRead references/policy.json relative to this skill. Add adjustment to a+b from facts.json; return count and marker from the policy.\n");
		fixture.Write("skills/sum-policy/references/policy.json", JsonSerializer.Serialize(new { adjustment = 3, marker }));
		var result = await fixture.RunAsync(new object[]
		{
			new { name = "analyze", type = "Prompt", systemPrompt = "", userPrompt = "Calculate the adjusted sum with the sum-policy skill and actual facts.json. Return count and marker. Finish and verify the task.",
				skillDirectories = new[] { "./skills" }, instructionDirectories = new[] { "./instructions" },
				executionPolicy = new { mode = "autopilot", maxContinues = 2 }, timeoutSeconds = 180,
				responseSchema = Element("""{"type":"object","properties":{"count":{"type":"integer"},"marker":{"type":"string"}},"required":["count","marker"],"additionalProperties":false}""") },
			new { name = "consume", type = "Command", dependsOn = new[] { "analyze" }, command = "pwsh", arguments = new[] { "-NoProfile", "-Command", "Write-Output '{{analyze.output.count}}'; (Get-Location).Path" } },
		});
		result.Status.Should().Be(ExecutionStatus.Succeeded, JsonSerializer.Serialize(result.StepResults));
		var answer = Element(result.StepResults["analyze"].Content);
		answer.GetProperty("count").GetInt32().Should().Be(14);
		answer.GetProperty("marker").GetString().Should().Be(marker);
		result.StepResults["consume"].Content.Should().Contain("14").And.Contain(fixture.Root);
		Directory.Exists(Path.Combine(fixture.Root, ".opencode", "skills")).Should().BeFalse();
	}

	[OpenCodeAvailableFact]
	public async Task NativeQuestion_RoutesThroughOperator_ThenReturnsValidatedJson()
	{
		using var fixture = new Fixture("native-question");
		var token = "ANSWER-" + Guid.NewGuid().ToString("N");
		var waiter = new AnswerWaiter(token);
		var result = await fixture.RunAsync(new object[] { new
		{
			name = "ask", type = "Prompt", systemPrompt = "", humanInput = true, timeoutSeconds = 120,
			userPrompt = "Use the native question tool to ask the operator one free-text question for their secret answer token, with no predefined options. Return their exact token as answer. Do not guess it.",
			responseSchema = Element("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}"""),
		} }, waiter);
		result.Status.Should().Be(ExecutionStatus.Succeeded, result.StepResults["ask"].ErrorMessage);
		Element(result.StepResults["ask"].Content).GetProperty("answer").GetString().Should().Be(token);
		waiter.Count.Should().BeGreaterThan(0);
	}

	[OpenCodeAvailableFact]
	public async Task SubagentAndToolRecovery_CompleteParentTaskAndProduceRunnableCode()
	{
		using var fixture = new Fixture("coding-recovery");
		var waiter = new AnswerWaiter("approve");
		var result = await fixture.RunAsync(new object[]
		{
			new { name = "code", type = "Prompt", systemPrompt = "", timeoutSeconds = 180, failOnToolError = false,
				permissionPolicy = new { mode = "requireHumanApproval" },
				executionPolicy = new { mode = "autopilot", maxContinues = 2 },
				responseSchema = Element("""{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}"""),
				userPrompt = "First use the native read tool on missing-spec.txt (it intentionally does not exist), then recover from that error. Delegate to the reviewer subagent to compute 17+25. Create sum.ps1 containing Write-Output (17 + 25). Execute it using pwsh -NoProfile -File sum.ps1 to verify it prints 42. Report the result only after the child and verification finish.",
				subagents = new[] { new { name = "reviewer", description = "Checks the sum", prompt = "Compute the requested sum and return it. Do not modify files." } } },
			new { name = "verify", type = "Script", dependsOn = new[] { "code" }, shell = "pwsh", script = "& './sum.ps1'" },
		}, waiter);
		result.Status.Should().Be(ExecutionStatus.Succeeded, JsonSerializer.Serialize(result.StepResults));
		result.StepResults["verify"].Content.Trim().Should().Be("42");
		Element(result.StepResults["code"].Content).GetProperty("count").GetInt32().Should().Be(42);
		result.StepResults["code"].Trace!.ToolCalls.Should().Contain(t => t.ToolName == "read" && t.Success == false);
		waiter.Count.Should().BeGreaterThan(0);
	}

	private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
	private sealed class Fixture(string name) : IDisposable
	{
		public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "opencode", "orchestra-v2-" + Guid.NewGuid().ToString("N"))).FullName;
		public void Write(string path, string content)
		{
			var full = Path.Combine(Root, path);
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllText(full, content);
		}
		public async Task<OrchestrationResult> RunAsync(object[] steps, IHumanInputWaiter? waiter = null)
		{
			Write("workflow.json", JsonSerializer.Serialize(new { name, description = "V2 live contract", defaultModel = Model, steps }));
			await using var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance,
				new OpenCodeAgentPoolOptions { DefaultMaxInstancesPerRun = 1, SwapBudgetPerStep = 0 });
			var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, NullOrchestrationReporter.Instance,
				NullLoggerFactory.Instance, dataPath: Path.Combine(Root, "data"), humanInputWaiter: waiter);
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
			var result = await executor.ExecuteAsync(OrchestrationParser.ParseOrchestrationFile(Path.Combine(Root, "workflow.json"), []), cancellationToken: timeout.Token);
			Write("result.json", JsonSerializer.Serialize(result));
			return result;
		}
		public void Dispose()
		{
			if (Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_TEST_EVIDENCE_DIR") is { Length: > 0 } evidence)
			{
				var destination = Path.Combine(evidence, name + "-" + Path.GetFileName(Root));
				Directory.CreateDirectory(destination);
				foreach (var file in new[] { "workflow.json", "result.json", "sum.ps1" })
					if (File.Exists(Path.Combine(Root, file)))
						File.Copy(Path.Combine(Root, file), Path.Combine(destination, file));
			}
			// Windows can retain a child's working-directory handle briefly after its parent exits.
			for (var attempt = 0; attempt < 30; attempt++)
			{
				try
				{ Directory.Delete(Root, true); return; }
				catch (IOException) when (attempt < 29) { Thread.Sleep(100); }
				catch (IOException) when (Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any())
				{
					// Another OpenCode service can retain a watcher handle after the private
					// server exits. All fixture contents are gone; an empty-directory handle
					// must not replace the actual test outcome with a teardown-only failure.
					return;
				}
			}
		}
	}

	private sealed class AnswerWaiter(string answer) : IHumanInputWaiter
	{
		public int Count { get; private set; }
		public Task<UserInputResponse> WaitAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken)
		{
			Count++;
			return Task.FromResult(new UserInputResponse { Reply = answer, RespondedAt = DateTimeOffset.UtcNow });
		}
		public bool TryComplete(string orchestrationName, string runId, string stepName, UserInputResponse response) => false;
		public bool TryCancel(string orchestrationName, string runId, string stepName) => false;
		public void BeginWait(string runId, string stepName) { }
		public void EndWait(string runId, string stepName) { }
	}
}
