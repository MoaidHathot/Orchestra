using System.Text.Json;
using Spectre.Console;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Renders a <see cref="JsonElement"/> result either as pretty JSON to stdout (the default,
/// machine-friendly for piping into <c>jq</c>) or as a Spectre.Console table (the
/// <c>--format table</c> human-friendly view).
///
/// The table renderer mirrors the legacy behaviour: it knows how to unwrap the common
/// server envelopes (<c>{orchestrations: [...]}</c>, <c>{runs: [...]}</c>, etc.) and falls
/// back to a property/value grid for opaque object responses.
/// </summary>
public static class OutputWriter
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	public static void Write(JsonElement result, string? format)
	{
		if (string.Equals(format, "table", StringComparison.OrdinalIgnoreCase))
		{
			PrintAsTable(result);
		}
		else
		{
			Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
		}
	}

	public static void PrintAsTable(JsonElement result)
	{
		var table = new Table();
		table.Border(TableBorder.Rounded);

		JsonElement? arrayToRender = null;

		if (result.ValueKind == JsonValueKind.Array)
		{
			arrayToRender = result;
		}
		else if (result.ValueKind == JsonValueKind.Object)
		{
			foreach (var prop in result.EnumerateObject())
			{
				if (prop.Value.ValueKind == JsonValueKind.Array)
				{
					// The server wraps lists in named envelopes. The first array property is
					// (in practice) always the payload — we pick that one greedily.
					arrayToRender = prop.Value;
					break;
				}
			}
		}

		if (arrayToRender.HasValue)
		{
			RenderArrayAsTable(arrayToRender.Value, table);
		}
		else
		{
			table.AddColumn("Property");
			table.AddColumn("Value");
			foreach (var prop in result.EnumerateObject())
			{
				table.AddRow(
					Markup.Escape(prop.Name),
					Markup.Escape(FormatValue(prop.Value)));
			}
		}

		AnsiConsole.Write(table);
	}

	private static void RenderArrayAsTable(JsonElement array, Table table)
	{
		var items = array.EnumerateArray().ToList();
		if (items.Count == 0)
		{
			table.AddColumn("(empty)");
			return;
		}

		// Use the union of keys present on any item so heterogeneous results still render.
		var columns = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var item in items)
		{
			if (item.ValueKind != JsonValueKind.Object) continue;
			foreach (var prop in item.EnumerateObject())
			{
				if (seen.Add(prop.Name))
				{
					columns.Add(prop.Name);
				}
			}
		}

		if (columns.Count == 0)
		{
			// Scalar-only array (e.g., list of strings).
			table.AddColumn("value");
			foreach (var item in items)
			{
				table.AddRow(Markup.Escape(FormatValue(item)));
			}
			return;
		}

		foreach (var col in columns)
		{
			table.AddColumn(Markup.Escape(col));
		}

		foreach (var item in items)
		{
			var values = columns.Select(col =>
				item.ValueKind == JsonValueKind.Object && item.TryGetProperty(col, out var value)
					? Markup.Escape(FormatValue(value))
					: string.Empty).ToArray();
			table.AddRow(values);
		}
	}

	private static string FormatValue(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Array => $"[{value.GetArrayLength()} items]",
		JsonValueKind.Object => "{...}",
		JsonValueKind.Null => string.Empty,
		_ => value.ToString() ?? string.Empty,
	};
}
