using System.Text.Json;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Client-side filter for the <c>orchestra list</c> response. The Host's <c>GET /api/orchestrations</c>
/// endpoint does not support server-side filtering (it returns the full registry every time),
/// so we narrow it here instead. Pure JSON-in / JSON-out so it can be unit-tested without
/// hitting the network.
///
/// Filter semantics:
/// <list type="bullet">
///   <item><c>Filter</c>: case-insensitive substring match against any of <c>name</c>,
///         <c>description</c>, or <c>path</c>. Matches if ANY of those fields contains the text.</item>
///   <item><c>Tags</c>: orchestration must carry ALL listed tags (AND semantics). Matching is
///         case-insensitive on the trimmed tag value. Useful for narrowing to a single
///         profile selection.</item>
///   <item><c>Enabled</c>: tri-state. <c>true</c> keeps only enabled orchestrations,
///         <c>false</c> keeps only disabled, <c>null</c> keeps both.</item>
/// </list>
/// All filters are conjunctive — passing multiple narrows the result.
/// </summary>
public static class OrchestrationFilter
{
	public sealed record Criteria(
		string? Filter = null,
		IReadOnlyList<string>? Tags = null,
		bool? Enabled = null)
	{
		public bool IsEmpty =>
			string.IsNullOrWhiteSpace(Filter)
			&& (Tags is null || Tags.Count == 0)
			&& Enabled is null;
	}

	/// <summary>
	/// Applies <paramref name="criteria"/> to the response returned by
	/// <see cref="OrchestraClient.ListOrchestrationsAsync"/>. The response is expected to be
	/// either an array of orchestration objects or an envelope of the form
	/// <c>{ count: N, orchestrations: [...] }</c>. Returns an envelope with the filtered list
	/// so the caller's <c>--format</c> renderer behaves identically.
	/// </summary>
	public static JsonElement Apply(JsonElement response, Criteria criteria)
	{
		if (criteria.IsEmpty)
		{
			return response;
		}

		var items = ExtractItems(response);
		if (items is null)
		{
			// Unknown shape — return as-is rather than swallow the response.
			return response;
		}

		var filtered = items.Where(item => Matches(item, criteria)).ToArray();

		// Mirror the server envelope so consumers (jq, table renderer) keep working.
		var payload = new
		{
			count = filtered.Length,
			orchestrations = filtered,
		};
		return JsonSerializer.SerializeToElement(payload, s_filterJsonOptions);
	}

	private static readonly JsonSerializerOptions s_filterJsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	private static List<JsonElement>? ExtractItems(JsonElement response)
	{
		if (response.ValueKind == JsonValueKind.Array)
		{
			return response.EnumerateArray().ToList();
		}
		if (response.ValueKind == JsonValueKind.Object
			&& response.TryGetProperty("orchestrations", out var arr)
			&& arr.ValueKind == JsonValueKind.Array)
		{
			return arr.EnumerateArray().ToList();
		}
		return null;
	}

	private static bool Matches(JsonElement item, Criteria criteria)
	{
		if (item.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		if (!string.IsNullOrWhiteSpace(criteria.Filter))
		{
			var needle = criteria.Filter.Trim();
			var name = GetString(item, "name");
			var description = GetString(item, "description");
			var path = GetString(item, "path");

			if (!ContainsCi(name, needle)
				&& !ContainsCi(description, needle)
				&& !ContainsCi(path, needle))
			{
				return false;
			}
		}

		if (criteria.Tags is { Count: > 0 })
		{
			if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
			{
				return false;
			}

			var itemTags = tags.EnumerateArray()
				.Where(t => t.ValueKind == JsonValueKind.String)
				.Select(t => t.GetString()?.Trim() ?? string.Empty)
				.Where(t => t.Length > 0)
				.ToList();

			foreach (var required in criteria.Tags)
			{
				if (string.IsNullOrWhiteSpace(required))
				{
					continue;
				}
				var trimmed = required.Trim();
				if (!itemTags.Any(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase)))
				{
					return false;
				}
			}
		}

		if (criteria.Enabled is { } wanted)
		{
			if (!item.TryGetProperty("enabled", out var enabledEl)
				|| enabledEl.ValueKind != JsonValueKind.True && enabledEl.ValueKind != JsonValueKind.False)
			{
				// Field missing — exclude if a state was requested rather than guess.
				return false;
			}
			if (enabledEl.GetBoolean() != wanted)
			{
				return false;
			}
		}

		return true;
	}

	private static string? GetString(JsonElement item, string property) =>
		item.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
			? el.GetString()
			: null;

	private static bool ContainsCi(string? haystack, string needle) =>
		!string.IsNullOrEmpty(haystack)
		&& haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
