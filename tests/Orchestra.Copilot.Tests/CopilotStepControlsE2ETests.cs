using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

/// <summary>Real runtime tests for discovery, isolated recall, human input and native structured responses.</summary>
[Trait("Category", "E2E")]
public sealed class CopilotStepControlsE2ETests
{
	private static string Model => Environment.GetEnvironmentVariable("ORCHESTRA_COPILOT_TEST_MODEL") ?? "claude-opus-4.8";

	[Fact]
	public async Task OrdinaryTextStep_CompletesAfterNativeToolUse()
	{
		using var fixture = new Fixture("ordinary-text");
		var token = "TOKEN-" + Guid.NewGuid().ToString("N");
		fixture.Write("workspace/token.txt", token);
		fixture.Write("definitions/workflow.json", JsonSerializer.Serialize(new
		{
			name = "ordinary-text",
			description = "Ordinary agent completion",
			defaultModel = Model,
			steps = new[] { new
			{
				name = "read", type = "Prompt", systemPrompt = "", systemPromptMode = "Append",
				workingDirectory = fixture.Workspace, timeoutSeconds = 120,
				userPrompt = "Read token.txt with the native file-reading tool, then return its exact contents. Do not call orchestra_set_status or orchestra_complete.",
			} },
		}));
		var result = await fixture.RunAsync("definitions/workflow.json", engineTools: new EngineToolRegistry());
		result.Status.Should().Be(ExecutionStatus.Succeeded, result.StepResults["read"].ErrorMessage);
		result.StepResults["read"].Content.Should().Contain(token);
		fixture.ExecutedTools().Should().Contain("view").And.NotContain("orchestra_set_status").And.NotContain("orchestra_complete");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task StructuredStep_UsesWorkingDirectoryInstructionsSkillsAndSql_WithoutPersistentRecall(bool automaticDiscovery)
	{
		using var fixture = new Fixture(automaticDiscovery ? "automatic" : "explicit");
		var rootMarker = "ROOT-" + Guid.NewGuid().ToString("N");
		var nestedMarker = "NESTED-" + Guid.NewGuid().ToString("N");
		var explicitMarker = "EXPLICIT-" + Guid.NewGuid().ToString("N");
		var policyCode = "POLICY-" + Guid.NewGuid().ToString("N");
		fixture.Write("workspace/AGENTS.md", $"Set rootMarker to {rootMarker} in your final response. Do not modify fixture files.\n");
		fixture.Write("workspace/nested/AGENTS.md", $"When reading files here, set nestedMarker to {nestedMarker} in your final response.\n");
		fixture.Write("definitions/instructions/AGENTS.md", $"Set explicitMarker to {explicitMarker} in your final response.\n");
		fixture.Write("workspace/nested/facts.json", "{\"a\":4,\"b\":7}\n");
		fixture.Write("workspace/.github/skills/sum-policy/SKILL.md", """
			---
			name: sum-policy
			description: Compute the adjusted sum of this project's facts.
			---
			Read references/policy.json relative to this skill. Add its adjustment to a+b
			from nested/facts.json and return that as amount. Use its code as policyCode.
			""");
		fixture.Write("workspace/.github/skills/sum-policy/references/policy.json", JsonSerializer.Serialize(new { adjustment = 3, code = policyCode }));

		var schema = JsonSerializer.Deserialize<JsonElement>("""
			{"type":"object","properties":{"amount":{"type":"integer"},"rootMarker":{"type":"string"},"nestedMarker":{"type":"string"},"explicitMarker":{"type":"string"},"policyCode":{"type":"string"}},"required":["amount","rootMarker","nestedMarker","explicitMarker","policyCode"],"additionalProperties":false}
			""");
		var definition = new
		{
			name = "step-controls",
			description = "Exercise the real Copilot harness",
			defaultModel = Model,
			defaultProvider = "copilot",
			defaultSystemPromptMode = "Append",
			defaultEnableConfigDiscovery = automaticDiscovery,
			defaultEnableOnDemandInstructionDiscovery = automaticDiscovery,
			steps = new object[]
			{
				new
				{
					name = "analyze", type = "Prompt", systemPrompt = "",
					userPrompt = "Use the sum-policy skill and read nested/facts.json. Track this task with the session sql todo tool and mark it done after computing the result. Return the amount, policyCode, and markers required by the instructions. Use actual file contents; do not guess. Do not modify files.",
					workingDirectory = fixture.Workspace, reasoningLevel = "High", timeoutSeconds = 180,
					skillDirectories = automaticDiscovery ? Array.Empty<string>() : ["../workspace/.github/skills"],
					instructionDirectories = automaticDiscovery ? new[] { "./instructions" } : ["./instructions", "../workspace/nested"],
					responseSchema = schema,
				},
				new { name = "consume", type = "Transform", dependsOn = new[] { "analyze" }, template = "{{analyze.output.amount}}" },
			},
		};
		fixture.Write("definitions/workflow.json", JsonSerializer.Serialize(definition));

		var result = await fixture.RunAsync("definitions/workflow.json");
		result.Status.Should().Be(ExecutionStatus.Succeeded, result.StepResults["analyze"].ErrorMessage);
		var answer = JsonSerializer.Deserialize<JsonElement>(result.StepResults["analyze"].Content);
		answer.GetProperty("amount").GetInt32().Should().Be(14);
		answer.GetProperty("rootMarker").GetString().Should().Be(rootMarker);
		answer.GetProperty("nestedMarker").GetString().Should().Be(nestedMarker);
		answer.GetProperty("explicitMarker").GetString().Should().Be(explicitMarker);
		answer.GetProperty("policyCode").GetString().Should().Be(policyCode);
		result.StepResults["consume"].Content.Should().Be("14");

		var calls = fixture.ReadModelCalls();
		calls.Should().NotBeEmpty();
		fixture.OfferedTools(calls[0]).Should().Contain("sql").And.Contain("skill")
			.And.NotContain("session_store_sql").And.NotContain("store_memory").And.NotContain("vote_memory");
		calls[0].GetProperty("gen_ai.system_instructions").GetString().Should().Contain("non-interactive mode");
		fixture.ExecutedTools().Should().Contain("sql").And.Contain("skill");
	}

	[Fact]
	public async Task HumanInput_EnablesNativeAskUser_AndStillDisablesPersistentRecall()
	{
		using var fixture = new Fixture("human-input");
		var answer = "ANSWER-" + Guid.NewGuid().ToString("N");
		var waiter = new AnswerWaiter(answer);
		fixture.Write("definitions/workflow.json", JsonSerializer.Serialize(new
		{
			name = "human-input",
			description = "Native ask_user integration",
			defaultModel = Model,
			steps = new[] { new
			{
				name = "ask", type = "Prompt", systemPrompt = "", systemPromptMode = "Append",
				humanInput = true, workingDirectory = fixture.Workspace, timeoutSeconds = 120,
				userPrompt = "Use the native ask_user tool to ask the operator for the secret answer token. Do not guess it. Return the operator's exact token as answer.",
				responseSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}"""),
			} },
		}));
		var result = await fixture.RunAsync("definitions/workflow.json", waiter);
		result.Status.Should().Be(ExecutionStatus.Succeeded, result.StepResults["ask"].ErrorMessage);
		JsonSerializer.Deserialize<JsonElement>(result.StepResults["ask"].Content).GetProperty("answer").GetString().Should().Be(answer);
		waiter.Count.Should().BeGreaterThan(0);
		fixture.ExecutedTools().Should().Contain("ask_user");
		var tools = fixture.OfferedTools(fixture.ReadModelCalls()[0]);
		tools.Should().Contain("ask_user").And.NotContain("session_store_sql").And.NotContain("store_memory");
	}

	[Fact]
	public async Task Autopilot_CompletesWithNativeJson_AndDefaultsToDefinitionDirectory()
	{
		using var fixture = new Fixture("autopilot-json");
		fixture.Write("definitions/facts.json", "{\"a\":17,\"b\":25}");
		fixture.Write("definitions/workflow.json", JsonSerializer.Serialize(new
		{
			name = "autopilot",
			description = "Native bounded continuation",
			defaultModel = Model,
			steps = new[] { new
			{
				name = "compute", type = "Prompt", systemPrompt = "", systemPromptMode = "Append", timeoutSeconds = 150,
				userPrompt = "Read facts.json using native tools, add a and b, and return count. Complete this task fully.",
				executionPolicy = new { mode = "autopilot", maxContinues = 2 },
				responseSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}"""),
			} },
		}));
		var result = await fixture.RunAsync("definitions/workflow.json");
		result.Status.Should().Be(ExecutionStatus.Succeeded, result.StepResults["compute"].ErrorMessage);
		JsonSerializer.Deserialize<JsonElement>(result.StepResults["compute"].Content).GetProperty("count").GetInt32().Should().Be(42);
		fixture.ExecutedTools().Should().Contain("task_complete");
	}

	[Fact]
	public async Task SubagentWork_ReturnsRootStructuredResponse()
	{
		using var fixture = new Fixture("subagent-json");
		fixture.Write("definitions/workflow.json", JsonSerializer.Serialize(new
		{
			name = "subagent-json",
			description = "Root response isolation",
			defaultModel = Model,
			steps = new[] { new
			{
				name = "compute", type = "Prompt", systemPrompt = "", systemPromptMode = "Append", timeoutSeconds = 150,
				userPrompt = "Delegate to the reviewer subagent to compute 17 + 25. Wait for the reviewer, then return its numeric result as count in your own final response.",
				subagents = new[] { new { name = "reviewer", description = "Checks arithmetic", prompt = "Compute the requested sum. Return the number in a short sentence.", model = Model } },
				responseSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}"""),
			} },
		}));
		var result = await fixture.RunAsync("definitions/workflow.json");
		result.Status.Should().Be(ExecutionStatus.Succeeded, result.StepResults["compute"].ErrorMessage);
		JsonSerializer.Deserialize<JsonElement>(result.StepResults["compute"].Content).GetProperty("count").GetInt32().Should().Be(42);
		fixture.ExecutedTools().Should().Contain("task");
	}

	private sealed class Fixture : IDisposable
	{
		private readonly string _name;
		private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode", "orchestra-controls-" + Guid.NewGuid().ToString("N"));
		public string Workspace => Path.Combine(_root, "workspace");
		private string TelemetryPath => Path.Combine(_root, "telemetry.jsonl");

		public Fixture(string name)
		{
			_name = name;
			Directory.CreateDirectory(Workspace);
			Directory.CreateDirectory(Path.Combine(_root, "definitions"));
			Write("home/config.json", JsonSerializer.Serialize(new { trusted_folders = new[] { Workspace } }));
			var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
			foreach (var arg in new[] { "init", "--quiet", Workspace })
				start.ArgumentList.Add(arg);
			using var git = Process.Start(start)!;
			git.WaitForExit(15_000).Should().BeTrue();
			git.ExitCode.Should().Be(0);
		}

		public void Write(string path, string content)
		{
			var fullPath = Path.Combine(_root, path);
			Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
			File.WriteAllText(fullPath, content.Replace("\r\n", "\n"));
		}

		public async Task<OrchestrationResult> RunAsync(string definitionPath, IHumanInputWaiter? waiter = null, EngineToolRegistry? engineTools = null)
		{
			var cliPath = await CopilotCliBootstrap.EnsureAsync();
			var factory = new TestClientFactory(cliPath, Path.Combine(_root, "home"), TelemetryPath);
			await using var builder = new CopilotAgentBuilder(NullLoggerFactory.Instance,
				new CopilotAgentPoolOptions { DefaultMaxInstancesPerRun = 1, CliSwapBudgetPerStep = 0 }, factory);
			var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, NullOrchestrationReporter.Instance,
				NullLoggerFactory.Instance, dataPath: Path.Combine(_root, "data"), humanInputWaiter: waiter, engineToolRegistry: engineTools);
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
			var result = await executor.ExecuteAsync(OrchestrationParser.ParseOrchestrationFile(Path.Combine(_root, definitionPath), []), cancellationToken: timeout.Token);
			File.WriteAllText(Path.Combine(_root, "result.json"), JsonSerializer.Serialize(result));
			return result;
		}

		private List<JsonElement> ReadSpans() => File.ReadLines(TelemetryPath)
			.Select(line => JsonSerializer.Deserialize<JsonElement>(line))
			.Where(row => row.TryGetProperty("type", out var type) && type.GetString() == "span")
			.Select(row => row.GetProperty("attributes").Clone()).ToList();

		public List<JsonElement> ReadModelCalls() => ReadSpans()
			.Where(row => row.TryGetProperty("gen_ai.operation.name", out var name) && name.GetString() == "chat").ToList();

		public string[] OfferedTools(JsonElement call) => JsonSerializer.Deserialize<JsonElement>(call.GetProperty("gen_ai.tool.definitions").GetString()!)
			.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray();

		public string[] ExecutedTools() => ReadSpans().Where(row => row.TryGetProperty("gen_ai.tool.name", out _))
			.Select(row => row.GetProperty("gen_ai.tool.name").GetString()!).ToArray();

		public void Dispose()
		{
			if (Environment.GetEnvironmentVariable("ORCHESTRA_COPILOT_TEST_EVIDENCE_DIR") is { Length: > 0 } evidence)
			{
				var destination = Path.Combine(evidence, _name + "-" + Path.GetFileName(_root));
				Directory.CreateDirectory(destination);
				foreach (var name in new[] { "telemetry.jsonl", "result.json" })
					if (File.Exists(Path.Combine(_root, name)))
						File.Copy(Path.Combine(_root, name), Path.Combine(destination, name));
			}
			Directory.Delete(_root, recursive: true);
		}
	}

	private sealed class TestClientFactory(string cliPath, string home, string telemetryPath) : ICopilotClientFactory
	{
		public ICopilotClient CreateClient() => new CopilotSdkClientAdapter(new CopilotClient(new CopilotClientOptions
		{
			Connection = RuntimeConnection.ForStdio(cliPath),
			BaseDirectory = home,
			Telemetry = new TelemetryConfig { FilePath = telemetryPath, ExporterType = "file", CaptureContent = true },
		}));
	}

	private sealed class AnswerWaiter(string answer) : IHumanInputWaiter
	{
		public int Count { get; private set; }
		public Task<Orchestra.Engine.UserInputResponse> WaitAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken)
		{
			Count++;
			return Task.FromResult(new Orchestra.Engine.UserInputResponse { Reply = answer, RespondedAt = DateTimeOffset.UtcNow });
		}
		public bool TryComplete(string orchestrationName, string runId, string stepName, Orchestra.Engine.UserInputResponse response) => false;
		public bool TryCancel(string orchestrationName, string runId, string stepName) => false;
		public void BeginWait(string runId, string stepName) { }
		public void EndWait(string runId, string stepName) { }
	}
}
