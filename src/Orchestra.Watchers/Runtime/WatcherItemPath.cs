using System.Text.Json.Nodes;

namespace Orchestra.Watchers.Runtime;

/// <summary>
/// The JSONPath subset understood by <c>items.jsonPath</c>: <c>$</c> root, <c>.name</c> or
/// <c>['name']</c> / <c>["name"]</c> member access, <c>[n]</c> array index and <c>[*]</c> wildcard.
/// A wildcard enumerates the array at that point; every segment after it applies to each element.
/// Filters, slices, recursive descent and unions are not supported - watcher output is meant to
/// be shaped by the fetcher, not re-queried by Orchestra.
/// </summary>
public sealed class WatcherItemPath
{
	private readonly IReadOnlyList<Segment> _segments;

	private WatcherItemPath(IReadOnlyList<Segment> segments, string source)
	{
		_segments = segments;
		Source = source;
	}

	/// <summary>The original expression.</summary>
	public string Source { get; }

	/// <summary>Parses an expression; throws <see cref="FormatException"/> for unsupported syntax.</summary>
	public static WatcherItemPath Parse(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		var text = path.Trim();
		if (text[0] != '$')
			throw new FormatException($"jsonPath '{path}' must start with '$'.");

		var segments = new List<Segment>();
		var i = 1;
		while (i < text.Length)
		{
			var c = text[i];
			if (c == '.')
			{
				i++;
				if (i < text.Length && text[i] == '.')
					throw new FormatException($"jsonPath '{path}': recursive descent ('..') is not supported.");

				var start = i;
				while (i < text.Length && text[i] != '.' && text[i] != '[')
					i++;
				var name = text[start..i];
				if (name.Length == 0)
					throw new FormatException($"jsonPath '{path}': expected a property name after '.'.");
				if (name == "*")
					throw new FormatException($"jsonPath '{path}': '.*' is not supported; use '[*]' to enumerate an array.");
				segments.Add(Segment.Member(name));
			}
			else if (c == '[')
			{
				var close = text.IndexOf(']', i);
				if (close < 0)
					throw new FormatException($"jsonPath '{path}': unterminated '['.");

				var inner = text[(i + 1)..close].Trim();
				i = close + 1;

				if (inner == "*")
				{
					segments.Add(Segment.Wildcard());
				}
				else if (inner.Length >= 2 && (inner[0] == '\'' && inner[^1] == '\'' || inner[0] == '"' && inner[^1] == '"'))
				{
					segments.Add(Segment.Member(inner[1..^1]));
				}
				else if (int.TryParse(inner, out var index) && index >= 0)
				{
					segments.Add(Segment.Index(index));
				}
				else
				{
					throw new FormatException($"jsonPath '{path}': unsupported bracket expression '[{inner}]' (use [n], [*] or ['name']).");
				}
			}
			else
			{
				throw new FormatException($"jsonPath '{path}': unexpected character '{c}' at position {i}.");
			}
		}

		return new WatcherItemPath(segments, text);
	}

	/// <summary>
	/// Resolves the path against <paramref name="root"/>. Elements enumerated by a wildcard become
	/// separate items; without a wildcard the resolved value is the single item (an array at the end
	/// is <em>also</em> enumerated, so <c>$.items</c> and <c>$.items[*]</c> mean the same thing).
	/// Missing members produce no items rather than an error.
	/// </summary>
	public IReadOnlyList<JsonNode?> Select(JsonNode? root)
	{
		var current = new List<JsonNode?> { root };
		foreach (var segment in _segments)
		{
			var next = new List<JsonNode?>();
			foreach (var node in current)
			{
				switch (segment.Kind)
				{
					case SegmentKind.Member:
						if (node is JsonObject obj && obj.TryGetPropertyValue(segment.Name!, out var member))
							next.Add(member);
						break;

					case SegmentKind.Index:
						if (node is JsonArray arr && segment.Position < arr.Count)
							next.Add(arr[segment.Position]);
						break;

					case SegmentKind.Wildcard:
						if (node is JsonArray all)
							next.AddRange(all);
						break;
				}
			}

			current = next;
		}

		if (_segments.Count == 0 || _segments[^1].Kind != SegmentKind.Wildcard)
		{
			// Trailing array without an explicit wildcard: enumerate it, as authors expect.
			if (current.Count == 1 && current[0] is JsonArray trailing)
				return trailing.ToList();

			// A resolved null (e.g. "items": null) means "nothing new", not one null item.
			if (current.Count == 1 && current[0] is null)
				return [];
		}

		return current;
	}

	private enum SegmentKind { Member, Index, Wildcard }

	private readonly record struct Segment(SegmentKind Kind, string? Name, int Position)
	{
		public static Segment Member(string name) => new(SegmentKind.Member, name, 0);
		public static Segment Index(int position) => new(SegmentKind.Index, null, position);
		public static Segment Wildcard() => new(SegmentKind.Wildcard, null, 0);
	}
}
