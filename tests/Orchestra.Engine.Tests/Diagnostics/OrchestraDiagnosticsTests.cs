using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Diagnostics;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Diagnostics;

/// <summary>
/// The engine's <see cref="OrchestraDiagnostics"/> source and meter: spans and metrics emitted by
/// <see cref="OrchestrationExecutor"/> for runs, steps and retries. Listeners are created per
/// test and filtered to this test's orchestration name, so parallel tests do not interfere.
/// </summary>
public class OrchestraDiagnosticsTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = SerializingReporterProxy.CreateRecording();

	/// <summary>Collects every measurement of the orchestra.* instruments while alive.</summary>
	private sealed class MetricCapture : IDisposable
	{
		private readonly MeterListener _listener = new();
		private readonly List<(string Instrument, double Value, Dictionary<string, string?> Tags)> _points = [];

		public MetricCapture()
		{
			_listener.InstrumentPublished = (instrument, listener) =>
			{
				if (instrument.Meter.Name == OrchestraDiagnostics.SourceName)
					listener.EnableMeasurementEvents(instrument);
			};
			_listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
			_listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
			_listener.Start();
		}

		private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
		{
			var dict = new Dictionary<string, string?>();
			foreach (var tag in tags)
				dict[tag.Key] = tag.Value?.ToString();
			lock (_points)
				_points.Add((instrument.Name, value, dict));
		}

		public IReadOnlyList<(string Instrument, double Value, Dictionary<string, string?> Tags)> For(string orchestration)
		{
			lock (_points)
				return _points.Where(p => p.Tags.TryGetValue("orchestra.orchestration", out var o) && o == orchestration).ToList();
		}

		public void Dispose() => _listener.Dispose();
	}

	/// <summary>Collects every orchestra.* activity while alive.</summary>
	private sealed class ActivityCapture : IDisposable
	{
		private readonly ActivityListener _listener;
		private readonly List<Activity> _activities = [];

		public ActivityCapture()
		{
			_listener = new ActivityListener
			{
				ShouldListenTo = source => source.Name == OrchestraDiagnostics.SourceName,
				Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
				ActivityStopped = activity => { lock (_activities) _activities.Add(activity); },
			};
			ActivitySource.AddActivityListener(_listener);
		}

		public IReadOnlyList<Activity> Stopped
		{
			get { lock (_activities) return _activities.ToList(); }
		}

		public void Dispose() => _listener.Dispose();
	}

	private static Orchestration TwoSteps(string name) => new()
	{
		Name = name,
		Description = "two steps",
		Steps =
		[
			TestOrchestrations.CreatePromptStep("first"),
			TestOrchestrations.CreatePromptStep("second", dependsOn: ["first"]),
		],
	};

	[Fact]
	public async Task SuccessfulRun_EmitsRunAndStepSpans_WithTags()
	{
		var name = $"diag-{Guid.NewGuid():N}";
		using var activities = new ActivityCapture();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithResponse("ok"), _reporter, NullLoggerFactory.Instance);

		var result = await executor.ExecuteAsync(TwoSteps(name)).WaitAsync(TimeSpan.FromSeconds(20));
		result.Status.Should().Be(ExecutionStatus.Succeeded);

		var mine = activities.Stopped.Where(a => a.GetTagItem("orchestra.orchestration") as string == name).ToList();
		var run = mine.Should().ContainSingle(a => a.OperationName == "orchestra.run").Subject;
		run.GetTagItem("orchestra.run.id").Should().NotBeNull("the executor tags the span once it assigns the run id");
		run.GetTagItem("orchestra.status").Should().Be("Succeeded");
		run.Status.Should().NotBe(ActivityStatusCode.Error);

		var steps = mine.Where(a => a.OperationName == "orchestra.step").ToList();
		steps.Select(s => s.GetTagItem("orchestra.step.name") as string).Should().BeEquivalentTo(["first", "second"]);
		steps.Should().OnlyContain(s => s.ParentId == run.Id, "step spans nest under the run span");
		steps.Should().OnlyContain(s => (string?)s.GetTagItem("orchestra.step.type") == "Prompt");
		steps.Should().OnlyContain(s => (string?)s.GetTagItem("orchestra.status") == "Succeeded");
	}

	[Fact]
	public async Task SuccessfulRun_RecordsRunAndStepMetrics_AndActiveReturnsToZero()
	{
		var name = $"diag-{Guid.NewGuid():N}";
		using var metrics = new MetricCapture();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithResponse("ok"), _reporter, NullLoggerFactory.Instance);

		await executor.ExecuteAsync(TwoSteps(name)).WaitAsync(TimeSpan.FromSeconds(20));

		var points = metrics.For(name);
		points.Where(p => p.Instrument == "orchestra.runs.started").Sum(p => p.Value).Should().Be(1);
		points.Where(p => p.Instrument == "orchestra.runs.completed").Should().ContainSingle()
			.Which.Tags.Should().Contain("orchestra.status", "Succeeded");
		points.Where(p => p.Instrument == "orchestra.runs.active").Sum(p => p.Value).Should().Be(0, "+1 at start, -1 at completion");
		points.Where(p => p.Instrument == "orchestra.run.duration").Should().ContainSingle().Which.Value.Should().BeGreaterThanOrEqualTo(0);

		var stepPoints = points.Where(p => p.Instrument == "orchestra.steps.completed").ToList();
		stepPoints.Sum(p => p.Value).Should().Be(2);
		stepPoints.Should().OnlyContain(p => p.Tags["orchestra.step.type"] == "Prompt" && p.Tags["orchestra.status"] == "Succeeded");
		points.Count(p => p.Instrument == "orchestra.step.duration").Should().Be(2);
		points.Should().NotContain(p => p.Instrument == "orchestra.steps.retries");
	}

	[Fact]
	public async Task FailedStepWithRetries_RecordsRetries_AndErrorStatus()
	{
		var name = $"diag-{Guid.NewGuid():N}";
		using var metrics = new MetricCapture();
		using var activities = new ActivityCapture();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithException(new InvalidOperationException("nope")), _reporter, NullLoggerFactory.Instance);
		var orchestration = new Orchestration
		{
			Name = name,
			Description = "fails twice",
			DefaultRetryPolicy = new RetryPolicy { MaxRetries = 2, BackoffSeconds = 0 },
			Steps = [TestOrchestrations.CreatePromptStep("flaky")],
		};

		var result = await executor.ExecuteAsync(orchestration).WaitAsync(TimeSpan.FromSeconds(20));
		result.Status.Should().Be(ExecutionStatus.Failed);

		var points = metrics.For(name);
		points.Where(p => p.Instrument == "orchestra.steps.retries").Sum(p => p.Value).Should().Be(2);
		points.Where(p => p.Instrument == "orchestra.runs.completed").Should().ContainSingle().Which.Tags["orchestra.status"].Should().Be("Failed");
		points.Where(p => p.Instrument == "orchestra.steps.completed").Should().ContainSingle().Which.Tags["orchestra.status"].Should().Be("Failed");

		var run = activities.Stopped.Single(a => a.OperationName == "orchestra.run" && a.GetTagItem("orchestra.orchestration") as string == name);
		run.Status.Should().Be(ActivityStatusCode.Error);
		var step = activities.Stopped.Single(a => a.OperationName == "orchestra.step" && a.ParentId == run.Id);
		step.Status.Should().Be(ActivityStatusCode.Error);
		step.GetTagItem("orchestra.error.category").Should().NotBeNull();
	}

	[Fact]
	public async Task ValidationFailure_HappensBeforeTheRunStarts_SoNothingIsRecorded()
	{
		// An unresolvable template is rejected before BeginRun: the run never existed, so the
		// started/completed counters must not move.
		var name = $"diag-{Guid.NewGuid():N}";
		using var metrics = new MetricCapture();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithResponse("ok"), _reporter, NullLoggerFactory.Instance);
		var orchestration = new Orchestration
		{
			Name = name,
			Description = "bad template",
			Steps = [TestOrchestrations.CreatePromptStep("s", userPrompt: "{{does-not-exist.output}}")],
		};

		var act = () => executor.ExecuteAsync(orchestration).WaitAsync(TimeSpan.FromSeconds(20));

		await act.Should().ThrowAsync<Exception>();
		metrics.For(name).Should().NotContain(p => p.Instrument == "orchestra.runs.started");
	}

	[Fact]
	public async Task CancelledRun_IsRecordedAsCancelled_AndActiveGaugeReturnsToZero()
	{
		var name = $"diag-{Guid.NewGuid():N}";
		using var metrics = new MetricCapture();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithResponse("ok"), _reporter, NullLoggerFactory.Instance);
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// The executor may surface a pre-cancelled token either as a Cancelled result or as an
		// OperationCanceledException; both must close the run scope.
		try
		{
			await executor.ExecuteAsync(TwoSteps(name), cancellationToken: cts.Token).WaitAsync(TimeSpan.FromSeconds(20));
		}
		catch (OperationCanceledException)
		{
		}

		var points = metrics.For(name);
		points.Where(p => p.Instrument == "orchestra.runs.started").Sum(p => p.Value).Should().Be(1);
		points.Where(p => p.Instrument == "orchestra.runs.active").Sum(p => p.Value).Should().Be(0, "the active gauge must not leak on cancellation");
		points.Where(p => p.Instrument == "orchestra.runs.completed").Should().ContainSingle().Which.Tags["orchestra.status"].Should().Be("Cancelled");
	}

	[Fact]
	public void WithoutListeners_InstrumentationIsInert()
	{
		// No ActivityListener -> StartActivity returns null; the helpers must cope.
		var step = TestOrchestrations.CreatePromptStep("s");

		using var scope = OrchestraDiagnostics.BeginRun("no-listener", null, isResume: false);
		var activity = OrchestraDiagnostics.BeginStep("no-listener", step);
		activity.Should().BeNull();
		OrchestraDiagnostics.EndStep(activity, "no-listener", step, ExecutionResult.Succeeded("x"), DateTimeOffset.UtcNow);
		scope.Complete(new OrchestrationResult { Status = ExecutionStatus.Succeeded, Results = new Dictionary<string, ExecutionResult>(), StepResults = new Dictionary<string, ExecutionResult>() });
	}
}
