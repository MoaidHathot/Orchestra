using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>Shared normalization, deduplication, persistence, and publication for provider plans.</summary>
public sealed partial class AgentPlanTracker(
	PlanRunIdentity run, string stepName, int? loopIteration, IOrchestrationReporter reporter,
	IPlanHistoryStore? store, ILogger logger)
{
	private readonly string _executionId = Guid.NewGuid().ToString("N");
	private readonly Dictionary<string, AgentPlanRevision> _latest = new(StringComparer.Ordinal);
	private long _revision;
	public IReadOnlyList<AgentPlanRevision> Latest => _latest.Values.ToArray();

	public async Task ObserveAsync(AgentPlanSnapshot snapshot)
	{
		var identity = JsonSerializer.Serialize(new[] { snapshot.Provider, snapshot.SessionId, snapshot.Source, snapshot.ActorId });
		var streamId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
		// Own all collections: neither SDK buffers nor callers may mutate saved observations.
		snapshot = snapshot with { Tasks = snapshot.Tasks.ToArray(), Dependencies = snapshot.Dependencies.ToArray() };
		if (_latest.TryGetValue(streamId, out var prior))
		{
			if (snapshot.CaptureError is not null)
				snapshot = prior.Plan with { CaptureError = snapshot.CaptureError };
			if (JsonSerializer.Serialize(prior.Plan) == JsonSerializer.Serialize(snapshot)) return;
		}
		var revision = new AgentPlanRevision
		{
			Run = run, StepName = stepName, StepExecutionId = _executionId, LoopIteration = loopIteration,
			StreamId = streamId, Revision = ++_revision, CapturedAt = DateTimeOffset.UtcNow, Plan = snapshot,
		};
		_latest[streamId] = revision;
		if (store is not null)
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
			try { await store.AppendPlanAsync(revision, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
			catch (Exception ex)
			{
				LogPersistenceFailed(logger, ex, run.RunId, stepName);
				reporter.ReportSessionWarning("plan_history_write_failed", $"Plan history could not be saved for step '{stepName}': {ex.Message}");
			}
		}
		reporter.ReportPlanUpdated(revision);
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Could not append plan history for run {RunId}, step {StepName}")]
	private static partial void LogPersistenceFailed(ILogger logger, Exception ex, string runId, string stepName);
}
