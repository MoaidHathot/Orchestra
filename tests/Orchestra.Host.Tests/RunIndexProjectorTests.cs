using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for <see cref="RunIndexProjector"/> — the streaming <c>run.json</c> reader that builds a
/// <see cref="RunIndex"/> without materializing the full record.
/// </summary>
/// <remarks>
/// The critical property is <b>equivalence</b>: the streaming projection must produce exactly what
/// deserializing the whole record produced, or the index silently changes meaning. Several tests
/// therefore assert against the eager path rather than against hand-written expectations.
/// </remarks>
public class RunIndexProjectorTests
{
	private static readonly JsonSerializerOptions s_writeOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() },
	};

	private const string Folder = @"C:\runs\orch\folder";

	private static StepRunRecord Step(
		string name,
		ExecutionStatus status,
		DateTimeOffset startedAt,
		string? error = null,
		string content = "") => new()
		{
			StepName = name,
			Status = status,
			StartedAt = startedAt,
			CompletedAt = startedAt.AddSeconds(1),
			Content = content,
			ErrorMessage = error,
		};

	private static OrchestrationRunRecord Record(
		ExecutionStatus status = ExecutionStatus.Succeeded,
		Dictionary<string, StepRunRecord>? steps = null,
		CancellationDetails? cancellation = null,
		IReadOnlyList<HookExecutionRecord>? hooks = null,
		string runId = "run123",
		string orchestrationName = "test-orch")
	{
		steps ??= [];
		return new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestrationName,
			OrchestrationVersion = "2.1.0",
			TriggeredBy = "scheduler",
			TriggerId = "trigger-abc",
			StartedAt = new DateTimeOffset(2026, 5, 13, 20, 44, 18, TimeSpan.Zero),
			CompletedAt = new DateTimeOffset(2026, 5, 13, 21, 14, 18, TimeSpan.Zero),
			Status = status,
			FinalContent = "final",
			CompletionReason = "done-early",
			CompletedByStep = "step1",
			IsIncomplete = true,
			Cancellation = cancellation,
			HookExecutions = hooks ?? [],
			RetriedFromRunId = "prev-run",
			RetryMode = "failed",
			ParentExecutionId = "parent-1",
			ParentStepName = "invoke-child",
			RootExecutionId = "root-1",
			NestingDepth = 2,
			StepRecords = steps,
			AllStepRecords = steps,
		};
	}

	private static HookExecutionRecord Hook(string name, DateTimeOffset? at = null)
	{
		var t = at ?? new DateTimeOffset(2026, 5, 13, 20, 0, 0, TimeSpan.Zero);
		return new HookExecutionRecord
		{
			HookName = name,
			EventType = HookEventType.OrchestrationSuccess,
			Source = HookSource.Orchestration,
			Status = ExecutionStatus.Succeeded,
			StartedAt = t,
			CompletedAt = t,
		};
	}

	private static RunIndex? ProjectOf(OrchestrationRunRecord record)
	{
		var json = JsonSerializer.SerializeToUtf8Bytes(record, s_writeOptions);
		return RunIndexProjector.Project(json, Folder);
	}

	// ── Field mapping ──

	[Fact]
	public void Project_MapsEveryScalarField()
	{
		var index = ProjectOf(Record());

		index.Should().NotBeNull();
		index!.RunId.Should().Be("run123");
		index.OrchestrationName.Should().Be("test-orch");
		index.OrchestrationVersion.Should().Be("2.1.0");
		index.TriggeredBy.Should().Be("scheduler");
		index.TriggerId.Should().Be("trigger-abc");
		index.StartedAt.Should().Be(new DateTimeOffset(2026, 5, 13, 20, 44, 18, TimeSpan.Zero));
		index.CompletedAt.Should().Be(new DateTimeOffset(2026, 5, 13, 21, 14, 18, TimeSpan.Zero));
		index.Status.Should().Be(ExecutionStatus.Succeeded);
		index.FolderPath.Should().Be(Folder);
		index.CompletionReason.Should().Be("done-early");
		index.CompletedByStep.Should().Be("step1");
		index.IsIncomplete.Should().BeTrue();
		index.RetriedFromRunId.Should().Be("prev-run");
		index.RetryMode.Should().Be("failed");
		index.ParentExecutionId.Should().Be("parent-1");
		index.ParentStepName.Should().Be("invoke-child");
		index.RootExecutionId.Should().Be("root-1");
		index.NestingDepth.Should().Be(2);
	}

	[Fact]
	public void Project_CountsHookExecutionsWithoutMaterializingThem()
	{
		var hooks = new List<HookExecutionRecord>
		{
			Hook("h1"),
			Hook("h2"),
		};

		ProjectOf(Record(hooks: hooks))!.HookExecutionCount.Should().Be(2);
	}

	[Fact]
	public void Project_RehydratesCancellationDetails()
	{
		var record = Record(
			status: ExecutionStatus.Cancelled,
			cancellation: new CancellationDetails
			{
				Kind = CancellationCauseKind.External,
				Detail = "cancelled by caller",
				CallerReason = "user asked",
			});

		var index = ProjectOf(record);

		index!.Cancellation.Should().NotBeNull();
		index.Cancellation!.Kind.Should().Be(CancellationCauseKind.External);
		index.Cancellation.Detail.Should().Be("cancelled by caller");
		index.Cancellation.CallerReason.Should().Be("user asked");
	}

	// ── Failure extraction ──

	[Fact]
	public void Project_FailedRun_PicksEarliestFailedStepWithAMessage()
	{
		var t0 = new DateTimeOffset(2026, 5, 13, 20, 0, 0, TimeSpan.Zero);
		var steps = new Dictionary<string, StepRunRecord>
		{
			["late"] = Step("late", ExecutionStatus.Failed, t0.AddMinutes(5), "later boom"),
			["early"] = Step("early", ExecutionStatus.Failed, t0.AddMinutes(1), "first boom"),
			["ok"] = Step("ok", ExecutionStatus.Succeeded, t0),
			["silent"] = Step("silent", ExecutionStatus.Failed, t0, error: null),
		};

		var index = ProjectOf(Record(ExecutionStatus.Failed, steps));

		index!.FailedStepName.Should().Be("early");
		index.ErrorMessage.Should().Be("first boom", "a failed step without a message is skipped");
	}

	[Fact]
	public void Project_CancelledRun_PicksEarliestCancelledStep()
	{
		var t0 = new DateTimeOffset(2026, 5, 13, 20, 0, 0, TimeSpan.Zero);
		var steps = new Dictionary<string, StepRunRecord>
		{
			["b"] = Step("b", ExecutionStatus.Cancelled, t0.AddMinutes(2), "cancelled by caller"),
			["a"] = Step("a", ExecutionStatus.Succeeded, t0),
		};

		var index = ProjectOf(Record(ExecutionStatus.Cancelled, steps));

		index!.FailedStepName.Should().Be("b");
		index.ErrorMessage.Should().Be("cancelled by caller");
	}

	[Fact]
	public void Project_CancelledRunWithNoCancelledStep_ReportsCancelled()
	{
		var steps = new Dictionary<string, StepRunRecord>
		{
			["a"] = Step("a", ExecutionStatus.Succeeded, DateTimeOffset.UtcNow),
		};

		var index = ProjectOf(Record(ExecutionStatus.Cancelled, steps));

		index!.FailedStepName.Should().BeNull();
		index.ErrorMessage.Should().Be("Cancelled");
	}

	[Fact]
	public void Project_SucceededRun_HasNoFailureInfo()
	{
		var steps = new Dictionary<string, StepRunRecord>
		{
			["a"] = Step("a", ExecutionStatus.Failed, DateTimeOffset.UtcNow, "ignored"),
		};

		var index = ProjectOf(Record(ExecutionStatus.Succeeded, steps));

		index!.FailedStepName.Should().BeNull();
		index.ErrorMessage.Should().BeNull("a succeeded run reports no failure regardless of step state");
	}

	// ── Skipping the expensive subtrees ──

	[Fact]
	public void Project_IgnoresLargeStepContentAndUnknownProperties()
	{
		// Stand-in for trace/conversationHistory: a payload far larger than everything indexed.
		var huge = new string('x', 2_000_000);
		var steps = new Dictionary<string, StepRunRecord>
		{
			["big"] = Step("big", ExecutionStatus.Failed, DateTimeOffset.UtcNow, "boom", content: huge),
		};

		var index = ProjectOf(Record(ExecutionStatus.Failed, steps));

		index.Should().NotBeNull();
		index!.ErrorMessage.Should().Be("boom");
	}

	[Fact]
	public void Project_ToleratesUnknownTopLevelProperties()
	{
		const string json = """
			{
			  "runId": "r1",
			  "orchestrationName": "o1",
			  "somethingNewFromAFutureVersion": { "nested": [1, 2, 3] },
			  "status": "Succeeded",
			  "startedAt": "2026-05-13T20:00:00+00:00",
			  "completedAt": "2026-05-13T20:01:00+00:00"
			}
			""";

		var index = RunIndexProjector.Project(System.Text.Encoding.UTF8.GetBytes(json), Folder);

		index.Should().NotBeNull();
		index!.RunId.Should().Be("r1");
	}

	// ── Rejection ──

	[Theory]
	[InlineData("")]
	[InlineData("{ not json")]
	[InlineData("[]")]
	[InlineData("null")]
	[InlineData("""{"orchestrationName":"o1"}""")]   // no runId
	[InlineData("""{"runId":"r1"}""")]               // no orchestration name
	public void Project_UnusableDocument_ReturnsNull(string json)
	{
		RunIndexProjector.Project(System.Text.Encoding.UTF8.GetBytes(json), Folder).Should().BeNull();
	}

	[Fact]
	public void Project_NumericStatus_IsUnderstood()
	{
		// Older records wrote the enum as a number.
		var json = $$"""
			{"runId":"r1","orchestrationName":"o1","status":{{(int)ExecutionStatus.Failed}},
			 "startedAt":"2026-05-13T20:00:00+00:00","completedAt":"2026-05-13T20:01:00+00:00"}
			""";

		var index = RunIndexProjector.Project(System.Text.Encoding.UTF8.GetBytes(json), Folder);

		index!.Status.Should().Be(ExecutionStatus.Failed);
	}

	// ── Equivalence with the eager path ──

	[Theory]
	[InlineData(ExecutionStatus.Succeeded)]
	[InlineData(ExecutionStatus.Failed)]
	[InlineData(ExecutionStatus.Cancelled)]
	public void Project_MatchesTheEagerDeserializationPath(ExecutionStatus status)
	{
		var t0 = new DateTimeOffset(2026, 5, 13, 20, 0, 0, TimeSpan.Zero);
		var steps = new Dictionary<string, StepRunRecord>
		{
			["one"] = Step("one", ExecutionStatus.Succeeded, t0, content: new string('a', 5000)),
			["two"] = Step("two", status, t0.AddMinutes(1), "boom", content: new string('b', 5000)),
			["three"] = Step("three", status, t0.AddMinutes(3), "later boom"),
		};
		var record = Record(status, steps, hooks: [
			Hook("h", t0)]);

		var json = JsonSerializer.SerializeToUtf8Bytes(record, s_writeOptions);
		var streamed = RunIndexProjector.Project(json, Folder);
		var eager = EagerProject(json, Folder);

		eager.Should().NotBeNull("the reference implementation must succeed for the comparison to mean anything");
		streamed.Should().BeEquivalentTo(eager!,
			options => options.Excluding(i => i.Duration),
			"the streaming projection must be indistinguishable from full deserialization");
	}

	/// <summary>
	/// Reference implementation: deserialize the whole record and project, exactly as the store did
	/// before the streaming reader existed.
	/// </summary>
	private static RunIndex? EagerProject(byte[] utf8Json, string folderPath)
	{
		var options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			Converters = { new JsonStringEnumConverter() },
		};

		var record = JsonSerializer.Deserialize<OrchestrationRunRecord>(utf8Json, options);
		if (record is null) return null;

		var (stepName, errorMessage) = ExtractFailureInfoEagerly(record);

		return new RunIndex
		{
			RunId = record.RunId,
			OrchestrationName = record.OrchestrationName,
			OrchestrationVersion = record.OrchestrationVersion,
			TriggeredBy = record.TriggeredBy,
			StartedAt = record.StartedAt,
			CompletedAt = record.CompletedAt,
			Status = record.Status,
			TriggerId = record.TriggerId,
			FolderPath = folderPath,
			FailedStepName = stepName,
			ErrorMessage = errorMessage,
			CompletionReason = record.CompletionReason,
			CompletedByStep = record.CompletedByStep,
			IsIncomplete = record.IsIncomplete,
			Cancellation = record.Cancellation,
			HookExecutionCount = record.HookExecutions.Count,
			RetriedFromRunId = record.RetriedFromRunId,
			RetryMode = record.RetryMode,
			ParentExecutionId = record.ParentExecutionId,
			ParentStepName = record.ParentStepName,
			RootExecutionId = record.RootExecutionId,
			NestingDepth = record.NestingDepth,
		};
	}

	private static (string? StepName, string? ErrorMessage) ExtractFailureInfoEagerly(OrchestrationRunRecord record)
	{
		if (record.Status == ExecutionStatus.Cancelled)
		{
			var cancelled = record.AllStepRecords.Values
				.Where(s => s.Status == ExecutionStatus.Cancelled && !string.IsNullOrEmpty(s.ErrorMessage))
				.OrderBy(s => s.StartedAt)
				.FirstOrDefault();

			return cancelled != null ? (cancelled.StepName, cancelled.ErrorMessage) : (null, "Cancelled");
		}

		if (record.Status != ExecutionStatus.Failed)
			return (null, null);

		var failed = record.AllStepRecords.Values
			.Where(s => s.Status == ExecutionStatus.Failed && !string.IsNullOrEmpty(s.ErrorMessage))
			.OrderBy(s => s.StartedAt)
			.FirstOrDefault();

		return failed != null ? (failed.StepName, failed.ErrorMessage) : (null, null);
	}
}
