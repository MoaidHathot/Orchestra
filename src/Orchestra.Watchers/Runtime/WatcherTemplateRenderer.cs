using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Orchestra.Watchers.Runtime;

/// <summary>
/// Renders <c>{{item}}</c> and <c>{{item.path.to.field}}</c> placeholders against a fetched item.
/// </summary>
/// <remarks>
/// <para>
/// The engine's template renderer is bound to an orchestration execution context (step outputs,
/// parameters, run metadata) and cannot be driven from a bare JSON node, so this deliberately
/// small renderer exists for the watcher's two templating sites: dedup keys and trigger
/// parameter maps. The placeholder syntax matches the engine's (<c>{{ ... }}</c>, whitespace
/// tolerated); only the <c>item</c> root is available here.
/// </para>
/// <para>
/// Rendering rules: <c>{{item}}</c> is the whole item as compact JSON; a path selects nested
/// members with <c>.</c> and array elements with <c>[n]</c>; strings render verbatim (no quotes),
/// numbers/booleans render as their JSON text, objects and arrays as compact JSON. A missing
/// path, a <c>null</c> value, or an unknown root renders as the literal string <c>null</c> - it
/// never throws, so a fetcher that omits a field cannot stall the watcher.
/// </para>
/// </remarks>
public static partial class WatcherTemplateRenderer
{
	private const string NullLiteral = "null";

	[GeneratedRegex(@"\{\{\s*([^{}]*?)\s*\}\}", RegexOptions.CultureInvariant)]
	private static partial Regex PlaceholderPattern();

	/// <summary>Renders every placeholder in <paramref name="template"/>.</summary>
	public static string Render(string template, JsonNode? item)
	{
		ArgumentNullException.ThrowIfNull(template);

		if (!template.Contains("{{", StringComparison.Ordinal))
			return template;

		return PlaceholderPattern().Replace(template, match => RenderExpression(match.Groups[1].Value, item));
	}

	/// <summary>Renders every value of <paramref name="templates"/>; keys pass through unchanged.</summary>
	public static Dictionary<string, string> RenderAll(IReadOnlyDictionary<string, string>? templates, JsonNode? item)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		if (templates is null)
			return result;

		foreach (var (key, template) in templates)
			result[key] = Render(template, item);

		return result;
	}

	/// <summary>
	/// The default dedup key when <c>dedup.key</c> is omitted: the first 16 hex characters of the
	/// SHA-256 of the item's canonical JSON (object members sorted, compact separators).
	/// </summary>
	public static string CanonicalHash(JsonNode? item)
	{
		var canonical = Canonicalize(item);
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
		return Convert.ToHexString(hash)[..16].ToLowerInvariant();
	}

	/// <summary>Compact JSON with object members in ordinal key order at every level.</summary>
	public static string Canonicalize(JsonNode? node)
	{
		return Sort(node)?.ToJsonString() ?? NullLiteral;

		static JsonNode? Sort(JsonNode? current)
		{
			switch (current)
			{
				case JsonObject obj:
					var sorted = new JsonObject();
					foreach (var (key, value) in obj.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
						sorted[key] = Sort(value);
					return sorted;

				case JsonArray arr:
					var copy = new JsonArray();
					foreach (var element in arr)
						copy.Add(Sort(element));
					return copy;

				default:
					return current?.DeepClone();
			}
		}
	}

	private static string RenderExpression(string expression, JsonNode? item)
	{
		var trimmed = expression.Trim();
		if (trimmed.Length == 0)
			return NullLiteral;

		if (trimmed == "item")
			return Stringify(item);

		if (!trimmed.StartsWith("item.", StringComparison.Ordinal) && !trimmed.StartsWith("item[", StringComparison.Ordinal))
			return NullLiteral;

		var path = trimmed[4..];
		return Stringify(Resolve(item, path));
	}

	private static JsonNode? Resolve(JsonNode? node, string path)
	{
		var current = node;
		var i = 0;
		while (i < path.Length && current is not null)
		{
			var c = path[i];
			if (c == '.')
			{
				i++;
				var start = i;
				while (i < path.Length && path[i] != '.' && path[i] != '[')
					i++;
				var name = path[start..i];
				if (name.Length == 0)
					return null;
				current = current is JsonObject obj && obj.TryGetPropertyValue(name, out var member) ? member : null;
			}
			else if (c == '[')
			{
				var close = path.IndexOf(']', i);
				if (close < 0)
					return null;
				var inner = path[(i + 1)..close].Trim();
				i = close + 1;

				if (inner.Length >= 2 && (inner[0] == '\'' && inner[^1] == '\'' || inner[0] == '"' && inner[^1] == '"'))
				{
					var name = inner[1..^1];
					current = current is JsonObject obj && obj.TryGetPropertyValue(name, out var member) ? member : null;
				}
				else if (int.TryParse(inner, out var index) && index >= 0)
				{
					current = current is JsonArray arr && index < arr.Count ? arr[index] : null;
				}
				else
				{
					return null;
				}
			}
			else
			{
				return null;
			}
		}

		return i >= path.Length ? current : null;
	}

	private static string Stringify(JsonNode? node)
	{
		switch (node)
		{
			case null:
				return NullLiteral;

			case JsonValue value:
				if (value.TryGetValue<string>(out var text))
					return text;
				if (value.TryGetValue<JsonElement>(out var element))
				{
					return element.ValueKind switch
					{
						JsonValueKind.String => element.GetString() ?? NullLiteral,
						JsonValueKind.Null => NullLiteral,
						_ => element.GetRawText(),
					};
				}
				return value.ToJsonString();

			default:
				return node.ToJsonString();
		}
	}
}
