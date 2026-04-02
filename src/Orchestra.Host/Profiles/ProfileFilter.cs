namespace Orchestra.Host.Profiles;

/// <summary>
/// Defines which orchestrations a profile matches.
/// An orchestration matches if its effective tags intersect with the filter's tags
/// (or the filter uses the "*" wildcard), or its ID is explicitly included.
/// Excluded IDs take precedence over all inclusion rules.
/// </summary>
public class ProfileFilter
{
	/// <summary>
	/// Tags to match against orchestration effective tags.
	/// Use "*" as a wildcard to match all orchestrations.
	/// An orchestration matches if any of its effective tags appear in this list.
	/// </summary>
	public string[] Tags { get; set; } = [];

	/// <summary>
	/// Explicit orchestration IDs to include, regardless of tags.
	/// </summary>
	public string[] OrchestrationIds { get; set; } = [];

	/// <summary>
	/// Explicit orchestration IDs to exclude, taking precedence over all other rules.
	/// </summary>
	public string[] ExcludeOrchestrationIds { get; set; } = [];

	/// <summary>
	/// Whether this filter contains the wildcard tag "*" that matches all orchestrations.
	/// </summary>
	public bool IsWildcard => Tags.Any(t => t == "*");

	/// <summary>
	/// Evaluates whether an orchestration matches this filter.
	/// </summary>
	/// <param name="orchestrationId">The orchestration's unique ID.</param>
	/// <param name="effectiveTags">The orchestration's effective tags (author + host-managed).</param>
	/// <returns>True if the orchestration matches this filter.</returns>
	public bool Matches(string orchestrationId, string[] effectiveTags)
	{
		// Excluded IDs always take precedence
		if (ExcludeOrchestrationIds.Contains(orchestrationId, StringComparer.OrdinalIgnoreCase))
			return false;

		// Wildcard matches everything
		if (IsWildcard)
			return true;

		// Explicit ID inclusion
		if (OrchestrationIds.Contains(orchestrationId, StringComparer.OrdinalIgnoreCase))
			return true;

		// Tag intersection
		if (Tags.Length > 0 && effectiveTags.Length > 0)
		{
			foreach (var filterTag in Tags)
			{
				foreach (var orchTag in effectiveTags)
				{
					if (string.Equals(filterTag, orchTag, StringComparison.OrdinalIgnoreCase))
						return true;
				}
			}
		}

		return false;
	}
}
