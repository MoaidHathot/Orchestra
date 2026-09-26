using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Hooks;

/// <summary>
/// Unit tests for <see cref="HookRuntime"/> and <see cref="HookEventNames"/>.
/// </summary>
/// <remarks>
/// Before these tests existed, <c>HookRuntime.ToEventName</c> had a private switch that was
/// missing <see cref="HookEventType.StepAwaitingInput"/>. Every <c>step.awaitingInput</c> hook
/// therefore threw while building its payload and never ran its script. The round-trip
/// test below fails the build the moment a new enum member is added without a wire name.
/// </remarks>
public class HookRuntimeTests
{
	#region HookEventNames

	[Fact]
	public void HookEventNames_EveryEnumMember_HasWireNameThatRoundTrips()
	{
		foreach (var eventType in Enum.GetValues<HookEventType>())
		{
			var wire = HookEventNames.ToWireName(eventType);
			wire.Should().NotBeNullOrWhiteSpace($"{eventType} must have a wire name");

			HookEventNames.TryParse(wire, out var parsed).Should().BeTrue($"'{wire}' must parse back");
			parsed.Should().Be(eventType);
		}
	}

	[Theory]
	[InlineData("step.awaitingInput")]
	[InlineData("step.awaiting-input")]
	[InlineData("step.awaiting_input")]
	[InlineData("STEP.AWAITINGINPUT")]
	public void HookEventNames_AwaitingInputAliases_Parse(string alias)
	{
		HookEventNames.TryParse(alias, out var parsed).Should().BeTrue();
		parsed.Should().Be(HookEventType.StepAwaitingInput);
	}

	[Fact]
	public void HookEventNames_UnknownName_DoesNotParse()
	{
		HookEventNames.TryParse("step.finished", out _).Should().BeFalse();
		HookEventNames.TryParse(null, out _).Should().BeFalse();
	}

	[Fact]
	public void HookEventTypeJsonConverter_RoundTripsEveryMember()
	{
		foreach (var eventType in Enum.GetValues<HookEventType>())
		{
			var json = JsonSerializer.Serialize(eventType);
			JsonSerializer.Deserialize<HookEventType>(json).Should().Be(eventType);
		}
	}

	#endregion

	#region Payload eventType

	[Fact]
	public async Task ExecuteAsync_StepAwaitingInputHook_RunsScriptWithAwaitingInputEventType()
	{
		var outputPath = TempPath("awaiting");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.StepAwaitingInput, outputPath, name: "notify");
			var awaiting = StepRecord("review", ExecutionStatus.AwaitingInput, content: "Approve deploy?");
			var context = Context(currentStep: awaiting, stepRecords: [awaiting]);

			var executions = await runtime.ExecuteAsync([hook], HookEventType.StepAwaitingInput, context);

			executions.Should().ContainSingle();
			executions[0].Status.Should().Be(ExecutionStatus.Succeeded, executions[0].ErrorMessage);
			executions[0].HookName.Should().Be("notify");
			executions[0].EventType.Should().Be(HookEventType.StepAwaitingInput);
			executions[0].StepName.Should().Be("review");

			var payload = await ReadPayloadAsync(outputPath);
			payload.GetProperty("eventType").GetString().Should().Be("step.awaitingInput");
			payload.GetProperty("step").GetProperty("name").GetString().Should().Be("review");
			payload.GetProperty("step").GetProperty("status").GetString().Should().Be("AwaitingInput");
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	[Fact]
	public async Task ExecuteAsync_UnnamedStepAwaitingInputHook_UsesWireNameAsHookName()
	{
		// Regression: with hook.Name == null, BuildExecutionRecord falls back to the wire
		// name. That path used to throw for StepAwaitingInput and escape the runtime entirely.
		var outputPath = TempPath("awaiting-unnamed");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.StepAwaitingInput, outputPath, name: null);
			var awaiting = StepRecord("review", ExecutionStatus.AwaitingInput);
			var context = Context(currentStep: awaiting, stepRecords: [awaiting]);

			var executions = await runtime.ExecuteAsync([hook], HookEventType.StepAwaitingInput, context);

			executions.Should().ContainSingle();
			executions[0].HookName.Should().Be("step.awaitingInput");
			executions[0].Status.Should().Be(ExecutionStatus.Succeeded, executions[0].ErrorMessage);
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	[Fact]
	public async Task ExecuteAsync_HookForDifferentEvent_IsNotRun()
	{
		var outputPath = TempPath("other-event");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.StepFailure, outputPath);
			var record = StepRecord("a", ExecutionStatus.Succeeded);
			var context = Context(currentStep: record, stepRecords: [record]);

			var executions = await runtime.ExecuteAsync([hook], HookEventType.StepSuccess, context);

			executions.Should().BeEmpty();
			File.Exists(outputPath).Should().BeFalse();
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	#endregion

	#region when filter

	[Fact]
	public async Task ExecuteAsync_WhenFilterMatchAll_RequiresEveryNamedStepToMatch()
	{
		var outputPath = TempPath("match-all");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.OrchestrationAfter, outputPath);
			hook.When = new HookWhenFilter
			{
				Steps = new HookStepCondition
				{
					Names = ["a", "b"],
					Status = HookStepStatusFilter.Failed,
					Match = HookStepMatch.All,
				}
			};

			var a = StepRecord("a", ExecutionStatus.Failed);
			var b = StepRecord("b", ExecutionStatus.Succeeded);
			var context = Context(currentStep: null, stepRecords: [a, b], orchestrationStatus: ExecutionStatus.Failed);

			var executions = await runtime.ExecuteAsync([hook], HookEventType.OrchestrationAfter, context);

			executions.Should().BeEmpty("only one of the two named steps failed");
			File.Exists(outputPath).Should().BeFalse();
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	[Fact]
	public async Task ExecuteAsync_WhenFilterMatchAny_RunsWhenOneNamedStepMatches()
	{
		var outputPath = TempPath("match-any");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.OrchestrationAfter, outputPath);
			hook.When = new HookWhenFilter
			{
				Steps = new HookStepCondition
				{
					Names = ["a", "b"],
					Status = HookStepStatusFilter.Failed,
					Match = HookStepMatch.Any,
				}
			};

			var a = StepRecord("a", ExecutionStatus.Failed);
			var b = StepRecord("b", ExecutionStatus.Succeeded);
			var context = Context(currentStep: null, stepRecords: [a, b], orchestrationStatus: ExecutionStatus.Failed);

			var executions = await runtime.ExecuteAsync([hook], HookEventType.OrchestrationAfter, context);

			executions.Should().ContainSingle();
			executions[0].Status.Should().Be(ExecutionStatus.Succeeded, executions[0].ErrorMessage);
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	[Fact]
	public async Task ExecuteAsync_WhenFilterWithoutNames_AppliesToCurrentStep()
	{
		var outputPath = TempPath("current-filter");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.StepAfter, outputPath);
			hook.When = new HookWhenFilter
			{
				Steps = new HookStepCondition { Status = HookStepStatusFilter.NonSucceeded }
			};

			var ok = StepRecord("ok", ExecutionStatus.Succeeded);
			var context = Context(currentStep: ok, stepRecords: [ok]);

			var executions = await runtime.ExecuteAsync([hook], HookEventType.StepAfter, context);

			executions.Should().BeEmpty("the current step succeeded, so a nonSucceeded filter must not match");
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	#endregion

	#region payload.steps selectors

	[Fact]
	public async Task ExecuteAsync_TerminalSelector_IncludesOnlyTerminalSteps()
	{
		var outputPath = TempPath("terminal");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.OrchestrationAfter, outputPath);
			hook.Payload = new HookPayloadOptions
			{
				Detail = HookPayloadDetail.Compact,
				Steps = new HookStepSelection(HookStepSelector.Terminal),
			};

			var a = StepRecord("a", ExecutionStatus.Succeeded);
			var b = StepRecord("b", ExecutionStatus.Succeeded);
			var context = Context(currentStep: null, stepRecords: [a, b], terminalSteps: ["b"]);

			await runtime.ExecuteAsync([hook], HookEventType.OrchestrationAfter, context);

			var payload = await ReadPayloadAsync(outputPath);
			var names = payload.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray();
			names.Should().BeEquivalentTo(["b"]);
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	[Fact]
	public async Task ExecuteAsync_NonSucceededSelector_ExcludesSucceededSteps()
	{
		var outputPath = TempPath("nonsucceeded");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.OrchestrationAfter, outputPath);
			hook.Payload = new HookPayloadOptions
			{
				Detail = HookPayloadDetail.Compact,
				Steps = new HookStepSelection(HookStepSelector.NonSucceeded),
			};

			var ok = StepRecord("ok", ExecutionStatus.Succeeded);
			var failed = StepRecord("failed", ExecutionStatus.Failed);
			var skipped = StepRecord("skipped", ExecutionStatus.Skipped);
			var context = Context(currentStep: null, stepRecords: [ok, failed, skipped]);

			await runtime.ExecuteAsync([hook], HookEventType.OrchestrationAfter, context);

			var payload = await ReadPayloadAsync(outputPath);
			var names = payload.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray();
			names.Should().BeEquivalentTo(["failed", "skipped"]);
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	[Fact]
	public async Task ExecuteAsync_NamedSelector_IncludesOnlyNamedSteps()
	{
		var outputPath = TempPath("named");
		try
		{
			var runtime = CreateRuntime();
			var hook = ScriptHook(HookEventType.OrchestrationAfter, outputPath);
			hook.Payload = new HookPayloadOptions
			{
				Detail = HookPayloadDetail.Standard,
				Steps = new HookStepSelection(["B"]),
			};

			var a = StepRecord("a", ExecutionStatus.Succeeded);
			var b = StepRecord("b", ExecutionStatus.Succeeded, content: "b output");
			var context = Context(currentStep: null, stepRecords: [a, b]);

			await runtime.ExecuteAsync([hook], HookEventType.OrchestrationAfter, context);

			var payload = await ReadPayloadAsync(outputPath);
			var steps = payload.GetProperty("steps").EnumerateArray().ToArray();
			steps.Should().ContainSingle();
			steps[0].GetProperty("name").GetString().Should().Be("b", "names are matched case-insensitively");
			steps[0].GetProperty("content").GetString().Should().Be("b output", "standard detail includes content");
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	[Fact]
	public async Task ExecuteAsync_IncludeRefs_AddsApiAndMcpRefsWhenServerUrlKnown()
	{
		var outputPath = TempPath("refs");
		try
		{
			var runtime = CreateRuntime(serverUrl: "http://localhost:5000/");
			var hook = ScriptHook(HookEventType.OrchestrationAfter, outputPath);
			hook.Payload = new HookPayloadOptions { IncludeRefs = true };

			var context = Context(currentStep: null, stepRecords: [], runId: "run-1");

			await runtime.ExecuteAsync([hook], HookEventType.OrchestrationAfter, context);

			var payload = await ReadPayloadAsync(outputPath);
			var refs = payload.GetProperty("refs");
			refs.GetProperty("api").GetProperty("run").GetString().Should().Be("http://localhost:5000/api/history/test-orch/run-1");
			refs.GetProperty("mcp").EnumerateArray().First().GetProperty("tool").GetString().Should().Be("get_run");
		}
		finally
		{
			Cleanup(outputPath);
		}
	}

	#endregion

	#region failure policy

	[Fact]
	public async Task ExecuteAsync_FailingScriptWithIgnorePolicy_ReturnsFailedRecordWithoutThrowing()
	{
		var runtime = CreateRuntime();
		var hook = new HookDefinition
		{
			Name = "boom",
			On = HookEventType.OrchestrationAfter,
			FailurePolicy = HookFailurePolicy.Ignore,
			Action = new HookAction
			{
				Type = HookActionType.Script,
				Shell = "pwsh",
				Script = "exit 3",
			}
		};
		var context = Context(currentStep: null, stepRecords: []);

		var executions = await runtime.ExecuteAsync([hook], HookEventType.OrchestrationAfter, context);

		executions.Should().ContainSingle();
		executions[0].Status.Should().Be(ExecutionStatus.Failed);
		executions[0].ErrorMessage.Should().NotBeNullOrEmpty();
		executions[0].FailurePolicy.Should().Be(HookFailurePolicy.Ignore);
	}

	[Fact]
	public async Task ExecuteAsync_FailingScriptWithWarnPolicy_ReturnsFailedRecordWithoutThrowing()
	{
		var runtime = CreateRuntime();
		var hook = new HookDefinition
		{
			Name = "boom",
			On = HookEventType.OrchestrationAfter,
			FailurePolicy = HookFailurePolicy.Warn,
			Action = new HookAction
			{
				Type = HookActionType.Script,
				Shell = "pwsh",
				Script = "exit 3",
			}
		};
		var context = Context(currentStep: null, stepRecords: []);

		var executions = await runtime.ExecuteAsync([hook], HookEventType.OrchestrationAfter, context);

		executions.Should().ContainSingle();
		executions[0].Status.Should().Be(ExecutionStatus.Failed);
	}

	#endregion

	#region helpers

	private static HookRuntime CreateRuntime(string? serverUrl = null)
		=> new(NullLoggerFactory.Instance, serverUrl, NullOrchestrationReporter.Instance);

	private static HookDefinition ScriptHook(HookEventType on, string outputPath, string? name = "hook")
	{
		return new HookDefinition
		{
			Name = name,
			On = on,
			Action = new HookAction
			{
				Type = HookActionType.Script,
				Shell = "pwsh",
				Script = $"$input | Set-Content -Path '{outputPath.Replace("'", "''")}'",
			}
		};
	}

	private static StepRunRecord StepRecord(string name, ExecutionStatus status, string content = "")
	{
		var now = DateTimeOffset.UtcNow;
		return new StepRunRecord
		{
			StepName = name,
			Status = status,
			StartedAt = now,
			CompletedAt = now,
			Content = content,
			ErrorMessage = status == ExecutionStatus.Failed ? "boom" : null,
		};
	}

	private static HookExecutionContext Context(
		StepRunRecord? currentStep,
		StepRunRecord[] stepRecords,
		string[]? terminalSteps = null,
		ExecutionStatus? orchestrationStatus = null,
		string runId = "run-1")
	{
		var orchestration = new Orchestration
		{
			Name = "test-orch",
			Description = "test",
			Steps = [],
		};

		var startedAt = DateTimeOffset.UtcNow;
		return new HookExecutionContext
		{
			Orchestration = orchestration,
			ExecutionContext = new OrchestrationExecutionContext
			{
				OrchestrationInfo = new OrchestrationInfo(orchestration.Name, orchestration.Version, runId, startedAt),
			},
			RunId = runId,
			RunStartedAt = startedAt,
			OrchestrationStatus = orchestrationStatus,
			StepRecords = stepRecords.ToDictionary(r => r.StepName, StringComparer.Ordinal),
			TerminalStepNames = terminalSteps ?? stepRecords.Select(r => r.StepName).ToArray(),
			CurrentStepRecord = currentStep,
		};
	}

	private static string TempPath(string tag)
		=> Path.Combine(Path.GetTempPath(), $"orchestra-hookruntime-{tag}-{Guid.NewGuid():N}.json");

	private static async Task<JsonElement> ReadPayloadAsync(string path)
	{
		File.Exists(path).Should().BeTrue($"hook script should have written '{path}'");
		var json = await File.ReadAllTextAsync(path);
		return JsonDocument.Parse(json).RootElement.Clone();
	}

	private static void Cleanup(string path)
	{
		if (File.Exists(path))
			File.Delete(path);
	}

	#endregion
}
