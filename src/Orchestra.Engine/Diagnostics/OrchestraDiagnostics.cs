using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Orchestra.Engine.Diagnostics;

/// <summary>
/// The engine's OpenTelemetry-compatible instrumentation: one <see cref="System.Diagnostics.ActivitySource"/>
/// for run/step spans and one <see cref="System.Diagnostics.Metrics.Meter"/> for counters and
/// histograms. Built on the BCL only, so the engine stays dependency-free; a host opts in by
/// subscribing a listener or an OpenTelemetry SDK to <see cref="SourceName"/>. With no
/// listener attached every call here is a cheap no-op.
/// </summary>
public static class OrchestraDiagnostics
{
	/// <summary>Name shared by the <see cref="ActivitySource"/> and the <see cref="Meter"/>.</summary>
	public const string SourceName = "Orchestra.Engine";

	private static readonly string s_version = typeof(OrchestraDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

	/// <summary>Spans: <c>orchestra.run</c> (one per orchestration run) and <c>orchestra.step</c> (one per step attempt set).</summary>
	public static ActivitySource ActivitySource { get; } = new(SourceName, s_version);

	/// <summary>Metrics; instrument names are listed on the individual properties.</summary>
	public static Meter Meter { get; } = new(SourceName, s_version);

	/// <summary><c>orchestra.runs.started</c> - runs begun, tagged <c>orchestra.orchestration</c>.</summary>
	public static Counter<long> RunsStarted { get; } = Meter.CreateCounter<long>("orchestra.runs.started", unit: "{run}", description: "Orchestration runs started.");

	/// <summary><c>orchestra.runs.completed</c> - runs finished, tagged <c>orchestra.orchestration</c> and <c>orchestra.status</c>.</summary>
	public static Counter<long> RunsCompleted { get; } = Meter.CreateCounter<long>("orchestra.runs.completed", unit: "{run}", description: "Orchestration runs completed, by terminal status.");

	/// <summary><c>orchestra.runs.active</c> - runs currently executing.</summary>
	public static UpDownCounter<long> RunsActive { get; } = Meter.CreateUpDownCounter<long>("orchestra.runs.active", unit: "{run}", description: "Orchestration runs currently executing.");

	/// <summary><c>orchestra.run.duration</c> - wall-clock seconds per run, tagged like <see cref="RunsCompleted"/>.</summary>
	public static Histogram<double> RunDuration { get; } = Meter.CreateHistogram<double>("orchestra.run.duration", unit: "s", description: "Orchestration run duration.");

	/// <summary><c>orchestra.steps.completed</c> - steps finished, tagged <c>orchestra.orchestration</c>, <c>orchestra.step.type</c> and <c>orchestra.status</c>.</summary>
	public static Counter<long> StepsCompleted { get; } = Meter.CreateCounter<long>("orchestra.steps.completed", unit: "{step}", description: "Steps completed, by type and terminal status.");

	/// <summary><c>orchestra.step.duration</c> - seconds per step including retries and queueing, tagged like <see cref="StepsCompleted"/>.</summary>
	public static Histogram<double> StepDuration { get; } = Meter.CreateHistogram<double>("orchestra.step.duration", unit: "s", description: "Step duration including retries.");

	/// <summary><c>orchestra.steps.retries</c> - retry attempts, tagged <c>orchestra.orchestration</c>, <c>orchestra.step.type</c> and <c>orchestra.error.category</c>.</summary>
	public static Counter<long> StepRetries { get; } = Meter.CreateCounter<long>("orchestra.steps.retries", unit: "{attempt}", description: "Step retry attempts.");

	/// <summary>Starts the <c>orchestra.run</c> span and bumps the started/active counters.</summary>
	public static RunScope BeginRun(string orchestrationName, string? triggerId, bool isResume)
	{
		var activity = ActivitySource.StartActivity("orchestra.run", ActivityKind.Internal);
		activity?.SetTag("orchestra.orchestration", orchestrationName);
		if (triggerId is not null)
			activity?.SetTag("orchestra.trigger.id", triggerId);
		if (isResume)
			activity?.SetTag("orchestra.run.resumed", true);

		var tags = new TagList { { "orchestra.orchestration", orchestrationName } };
		RunsStarted.Add(1, tags);
		RunsActive.Add(1, tags);
		return new RunScope(activity, orchestrationName, Stopwatch.GetTimestamp());
	}

	/// <summary>Tags the current run span with the run id once the executor has assigned it.</summary>
	public static void TagCurrentRun(string runId)
	{
		var current = Activity.Current;
		if (current?.OperationName == "orchestra.run")
			current.SetTag("orchestra.run.id", runId);
	}

	/// <summary>Starts the <c>orchestra.step</c> span under the current run span.</summary>
	public static Activity? BeginStep(string orchestrationName, OrchestrationStep step)
	{
		var activity = ActivitySource.StartActivity("orchestra.step", ActivityKind.Internal);
		activity?.SetTag("orchestra.orchestration", orchestrationName);
		activity?.SetTag("orchestra.step.name", step.Name);
		activity?.SetTag("orchestra.step.type", step.Type.ToString());
		return activity;
	}

	/// <summary>Records a finished step on the counters/histogram and closes its span.</summary>
	public static void EndStep(Activity? activity, string orchestrationName, OrchestrationStep step, ExecutionResult result, DateTimeOffset startedAt)
	{
		var tags = new TagList
		{
			{ "orchestra.orchestration", orchestrationName },
			{ "orchestra.step.type", step.Type.ToString() },
			{ "orchestra.status", result.Status.ToString() },
		};
		StepsCompleted.Add(1, tags);
		StepDuration.Record((DateTimeOffset.UtcNow - startedAt).TotalSeconds, tags);

		if (activity is null)
			return;

		activity.SetTag("orchestra.status", result.Status.ToString());
		if (result.Status is ExecutionStatus.Failed)
		{
			activity.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
			if (result.ErrorCategory is { } category)
				activity.SetTag("orchestra.error.category", category.ToString());
		}
		activity.Dispose();
	}

	/// <summary>Counts one retry attempt of <paramref name="step"/>.</summary>
	public static void RecordRetry(string orchestrationName, OrchestrationStep step, StepErrorCategory? category)
	{
		StepRetries.Add(1, new TagList
		{
			{ "orchestra.orchestration", orchestrationName },
			{ "orchestra.step.type", step.Type.ToString() },
			{ "orchestra.error.category", (category ?? StepErrorCategory.Unknown).ToString() },
		});
	}

	/// <summary>
	/// The open <c>orchestra.run</c> span plus what is needed to record its completion.
	/// Dispose without calling <see cref="Complete"/> (an exception escaped) records the run as
	/// <c>Faulted</c>.
	/// </summary>
	public sealed class RunScope(Activity? activity, string orchestrationName, long startTimestamp) : IDisposable
	{
		private bool _completed;

		/// <summary>Records a run that produced a result.</summary>
		public void Complete(OrchestrationResult result)
		{
			Record(result.Status.ToString(), result.Status is ExecutionStatus.Failed ? result.Cancellation?.Reason : null, isError: result.Status is ExecutionStatus.Failed);
		}

		/// <summary>Records a run that ended with an exception (timeout, cancellation, validation).</summary>
		public void Fault(Exception exception)
		{
			var status = exception is OperationCanceledException ? "Cancelled" : "Faulted";
			Record(status, exception.Message, isError: status == "Faulted");
		}

		private void Record(string status, string? description, bool isError)
		{
			if (_completed)
				return;
			_completed = true;

			var tags = new TagList
			{
				{ "orchestra.orchestration", orchestrationName },
				{ "orchestra.status", status },
			};
			RunsCompleted.Add(1, tags);
			RunDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
			RunsActive.Add(-1, new TagList { { "orchestra.orchestration", orchestrationName } });

			if (activity is null)
				return;
			activity.SetTag("orchestra.status", status);
			if (isError)
				activity.SetStatus(ActivityStatusCode.Error, description);
		}

		public void Dispose()
		{
			if (!_completed)
				Record("Faulted", "run scope disposed without a result", isError: true);
			activity?.Dispose();
		}
	}
}
