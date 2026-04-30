namespace Orchestra.Engine;

/// <summary>
/// A point-in-time snapshot of a Copilot quota / entitlement bucket as reported by
/// the SDK on <c>AssistantUsageEvent</c> (SDK 0.3.0). Mirrors
/// <c>GitHub.Copilot.SDK.AssistantUsageQuotaSnapshot</c> but is engine-agnostic so
/// non-Copilot agents (or future SDK versions) can populate it too.
/// </summary>
/// <param name="EntitlementRequests">Total requests included in the active plan.</param>
/// <param name="UsedRequests">Requests consumed so far in the current period.</param>
/// <param name="RemainingPercentage">0.0 - 1.0 fraction of entitlement still available.</param>
/// <param name="Overage">Requests consumed beyond entitlement (billed separately).</param>
/// <param name="IsUnlimitedEntitlement">True when the bucket is effectively unmetered.</param>
/// <param name="UsageAllowedWithExhaustedQuota">Whether new requests are allowed after quota exhaustion.</param>
/// <param name="OverageAllowedWithExhaustedQuota">Whether overage billing is permitted after quota exhaustion.</param>
/// <param name="ResetDate">When the quota window rolls over (UTC). Null when not surfaced.</param>
public sealed record AgentQuotaSnapshot(
	double EntitlementRequests,
	double UsedRequests,
	double RemainingPercentage,
	double Overage,
	bool IsUnlimitedEntitlement,
	bool UsageAllowedWithExhaustedQuota,
	bool OverageAllowedWithExhaustedQuota,
	DateTimeOffset? ResetDate);
