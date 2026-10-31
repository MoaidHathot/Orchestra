using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Orchestra.Host.Workspaces;

/// <summary>Patches object members while retaining unrelated JSONC text, comments and references.</summary>
internal static class JsoncEditor
{
	public static string Patch(string text, JsonObject changes)
	{
		var bytes = Encoding.UTF8.GetBytes(text);
		var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Configuration must be a JSON object.");
		var remaining = changes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
		var edits = new List<(int Start, int End, string Text)>();
		var count = 0;
		var lastEnd = 1;
		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a configuration property.");
			var name = reader.GetString()!;
			reader.Read();
			var start = (int)reader.TokenStartIndex;
			reader.Skip();
			lastEnd = (int)reader.BytesConsumed;
			count++;
			if (remaining.Remove(name, out var value))
			{
				var old = Encoding.UTF8.GetString(bytes.AsSpan(start, lastEnd - start));
				var replacement = value is JsonObject nested && old.TrimStart().StartsWith('{')
					? Patch(old, nested) : value?.ToJsonString() ?? "null";
				edits.Add((start, lastEnd, replacement));
			}
		}
		var end = (int)reader.TokenStartIndex;
		if (remaining.Count > 0)
		{
			var gap = Encoding.UTF8.GetString(bytes.AsSpan(lastEnd, end - lastEnd));
			var significant = Regex.Replace(gap, @"/\*[\s\S]*?\*/|//[^\r\n]*", "").Trim();
			if (count > 0 && !significant.Contains(',')) edits.Add((lastEnd, lastEnd, ","));
			var additions = string.Join(",\n", remaining.Select(p => "  " + JsonSerializer.Serialize(p.Key) + ": " + (p.Value?.ToJsonString() ?? "null")));
			edits.Add((end, end, "\n" + additions + "\n"));
		}
		using var stream = new MemoryStream();
		var position = 0;
		foreach (var edit in edits.OrderBy(e => e.Start))
		{
			stream.Write(bytes.AsSpan(position, edit.Start - position));
			stream.Write(Encoding.UTF8.GetBytes(edit.Text));
			position = edit.End;
		}
		stream.Write(bytes.AsSpan(position));
		return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n");
	}
}
