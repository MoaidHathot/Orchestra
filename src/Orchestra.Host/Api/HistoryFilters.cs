using Microsoft.AspNetCore.Http;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// Filter values parsed from <c>/api/history</c> query parameters.
/// </summary>
/// <remarks>
/// All fields are optional; <see langword="null"/> means "no filter on this dimension".
/// The <see cref="Roots"/> tri-state is intentionally nullable rather than a default
/// boolean so the API can distinguish "show only roots", "show only children", and
/// "no filter".
/// </remarks>
public sealed record HistoryFilters
{
	/// <summary>Allow-list of origin kinds; <see langword="null"/> = all origins permitted.</summary>
	public required HashSet<RunOriginKind>? Origins { get; init; }

	/// <summary>
	/// <see langword="true"/> = only runs without a parent (roots);
	/// <see langword="false"/> = only runs with a parent (children);
	/// <see langword="null"/> = no scope filter.
	/// </summary>
	public required bool? Roots { get; init; }

	/// <summary>Allow-list of <c>ExecutionStatus</c> names (case-insensitive); <see langword="null"/> = all statuses permitted.</summary>
	public required HashSet<string>? Statuses { get; init; }

	/// <summary>
	/// <see langword="true"/> = only favorited runs; <see langword="false"/> = only non-favorited;
	/// <see langword="null"/> = no favorite filter.
	/// </summary>
	public bool? Favorites { get; init; }

	/// <summary>
	/// Allow-list of run annotation tags; <see langword="null"/> = no tag filter.
	/// </summary>
	/// <remarks>
	/// Matching is <b>OR</b>: a run matches when it carries <i>any</i> of the requested tags.
	/// This suits the primary use — "show me everything tagged connect" — and is the semantic
	/// used consistently across every run-tag surface (REST, CLI, MCP).
	/// </remarks>
	public HashSet<string>? Tags { get; init; }

	/// <summary><see langword="true"/> when at least one filter is non-null.</summary>
	public bool HasAnyFilter =>
		Origins is not null || Roots is not null || Statuses is not null
		|| Favorites is not null || Tags is not null;
}

/// <summary>
/// Helpers for parsing and applying <see cref="HistoryFilters"/> against active and stored runs.
/// </summary>
public static class HistoryFilterParser
{
	/// <summary>
	/// Parses the <c>?origins=</c>, <c>?roots=</c>, <c>?statuses=</c>, <c>?favorites=</c> and
	/// <c>?tags=</c> query parameters into a <see cref="HistoryFilters"/> value.
	/// </summary>
	/// <remarks>
	/// Comma-separated multi-value parameters use a strict allow-list (unknown tokens are
	/// dropped silently). An empty allow-list — e.g. <c>origins=</c> with no tokens —
	/// is treated the same as the parameter being absent (no filter).
	/// </remarks>
	public static HistoryFilters Parse(string? origins, bool? roots, string? statuses, bool? favorites = null, string? tags = null)
	{
		HashSet<RunOriginKind>? originSet = null;
		if (!string.IsNullOrWhiteSpace(origins))
		{
			var parsed = RunOriginClassifier.ParseWireValues(origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
			if (parsed.Count > 0)
				originSet = parsed;
		}

		HashSet<string>? statusSet = null;
		if (!string.IsNullOrWhiteSpace(statuses))
		{
			var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var token in statuses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				parsed.Add(token);
			if (parsed.Count > 0)
				statusSet = parsed;
		}

		HashSet<string>? tagSet = null;
		if (!string.IsNullOrWhiteSpace(tags))
		{
			var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var token in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				parsed.Add(token);
			if (parsed.Count > 0)
				tagSet = parsed;
		}

		return new HistoryFilters
		{
			Origins = originSet,
			Roots = roots,
			Statuses = statusSet,
			Favorites = favorites,
			Tags = tagSet,
		};
	}

	/// <summary>
	/// Returns <see langword="true"/> when the annotation-backed filters (favorites, tags) match.
	/// </summary>
	/// <param name="annotation">The run's annotation, or <see langword="null"/> when unannotated.</param>
	private static bool MatchesAnnotation(RunAnnotation? annotation, HistoryFilters filters)
	{
		if (filters.Favorites is { } wantFavorite)
		{
			if ((annotation?.Favorite == true) != wantFavorite)
				return false;
		}

		if (filters.Tags is { } wantTags)
		{
			// OR: any requested tag present is a match.
			var runTags = annotation?.Tags;
			if (runTags is null || runTags.Length == 0)
				return false;
			if (!runTags.Any(wantTags.Contains))
				return false;
		}

		return true;
	}

	/// <summary>
	/// Returns <see langword="true"/> when an <see cref="ActiveExecutionInfo"/> matches all filters.
	/// </summary>
	public static bool Matches(ActiveExecutionInfo info, HistoryFilters filters, RunAnnotation? annotation = null)
	{
		if (filters.Origins is { } origins)
		{
			var origin = RunOriginClassifier.Classify(info.TriggeredBy);
			if (!origins.Contains(origin))
				return false;
		}

		if (filters.Roots is { } rootsOnly)
		{
			var hasParent = info.NestingMetadata?.ParentExecutionId is not null;
			if (rootsOnly == hasParent)
				return false;
		}

		if (filters.Statuses is { } statuses)
		{
			if (!statuses.Contains(info.Status.ToString()))
				return false;
		}

		return MatchesAnnotation(annotation, filters);
	}

	/// <summary>
	/// Returns <see langword="true"/> when a stored <see cref="RunIndex"/> matches all filters.
	/// </summary>
	public static bool Matches(RunIndex index, HistoryFilters filters, RunAnnotation? annotation = null)
	{
		if (filters.Origins is { } origins)
		{
			var origin = RunOriginClassifier.Classify(index.TriggeredBy);
			if (!origins.Contains(origin))
				return false;
		}

		if (filters.Roots is { } rootsOnly)
		{
			var hasParent = !string.IsNullOrEmpty(index.ParentExecutionId);
			if (rootsOnly == hasParent)
				return false;
		}

		if (filters.Statuses is { } statuses)
		{
			if (!statuses.Contains(index.Status.ToString()))
				return false;
		}

		return MatchesAnnotation(annotation, filters);
	}
}
