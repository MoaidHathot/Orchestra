using System.Text.Json.Serialization;

namespace Orchestra.Engine;

/// <summary>Optional run-store capability for append-only plan observations, including unfinished runs.</summary>
public interface IPlanHistoryStore
{
	Task AppendPlanAsync(AgentPlanRevision revision, CancellationToken cancellationToken = default);
	Task<PlanHistoryPage> ReadPlansAsync(string runId, string? stepName = null, long after = 0, int limit = 100, CancellationToken cancellationToken = default);
}

public sealed record PlanHistoryPage(
	IReadOnlyList<AgentPlanRevision> Items,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? NextCursor,
	bool IsPartial = false);
