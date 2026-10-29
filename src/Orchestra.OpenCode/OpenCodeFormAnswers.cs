using System.Globalization;
using System.Text.Json;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>Adapts V2 forms to Orchestra's single-question human-input surface.</summary>
internal static class OpenCodeFormAnswers
{
	public static async Task<Dictionary<string, object>?> ReadAsync(JsonElement form, EngineToolContext context, CancellationToken cancellationToken)
	{
		var answers = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach (var field in form.GetProperty("fields").EnumerateArray())
		{
			var key = field.GetProperty("key").GetString()!;
			if (field.TryGetProperty("when", out var conditions) && conditions.EnumerateArray().Any(c => !Matches(c, answers)))
				continue;
			if (field.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean())
			{
				if (field.TryGetProperty("default", out var value))
					answers[key] = value.Clone();
				continue;
			}
			var type = field.GetProperty("type").GetString();
			if (type == "external")
				throw new NotSupportedException("This OpenCode form requires an external browser action, which Orchestra's question interface does not support.");
			var options = field.TryGetProperty("options", out var choices)
				? choices.EnumerateArray().Select(o => (Value: o.GetProperty("value").GetString()!, Label: o.GetProperty("label").GetString()!, Description: Text(o, "description"))).ToArray()
				: [];
			var prompt = string.Join("\n", new[] { Text(form, "title"), Text(field, "title"), Text(field, "description") }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
			if (type == "multiselect")
				prompt += "\nSelect one or more values, separated by commas (or reply with a JSON string array).";
			foreach (var option in options)
				if (!string.IsNullOrWhiteSpace(option.Description))
					prompt += $"\n{option.Label}: {option.Description}";
			if (field.TryGetProperty("default", out var defaultValue))
				prompt += $"\nDefault: {defaultValue}";
			var labels = type == "boolean" ? new[] { "yes", "no" } : options.Length == 0 ? null : options.Select(o => o.Label).ToArray();
			for (var attempt = 0; ; attempt++)
			{
				var response = await context.RequestHumanInputAsync(prompt, labels, PendingInputKind.Elicitation, cancellationToken).ConfigureAwait(false);
				if (response is null)
					return null;
				var reply = response.ResolveContent();
				if (string.IsNullOrWhiteSpace(reply) && field.TryGetProperty("default", out var fallback))
				{
					answers[key] = fallback.Clone();
					break;
				}
				try
				{
					var answer = ConvertValue(type, reply, options.Select(o => (o.Value, o.Label)).ToArray());
					if (options.Length > 0 && (!field.TryGetProperty("custom", out var custom) || !custom.GetBoolean()))
					{
						var selected = answer is string[] many ? many : [answer.ToString()!];
						if (selected.Any(item => !options.Any(o => o.Value == item)))
							throw new FormatException("Choose one of the offered values.");
					}
					answers[key] = answer;
					break;
				}
				catch (FormatException) when (attempt < 2) { prompt = $"Please enter a valid {type} value.\n" + prompt; }
			}
		}
		return answers;
	}

	internal static object ConvertValue(string? type, string reply, (string Value, string Label)[] options)
	{
		string Resolve(string text) => options.FirstOrDefault(o => o.Label.Equals(text, StringComparison.Ordinal)).Value ?? text;
		switch (type)
		{
			case "string":
				return Resolve(reply);
			case "boolean":
				if (bool.TryParse(reply.Trim(), out var boolean))
					return boolean;
				if (reply.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
					return true;
				if (reply.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
					return false;
				break;
			case "integer":
				if (long.TryParse(reply, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
					return integer;
				break;
			case "number":
				if (double.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
					return number;
				break;
			case "multiselect":
				try
				{
					var values = reply.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<string[]>(reply)!
						: reply.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
					if (values.Any(value => value is null))
						throw new FormatException("Selections must be strings, not null.");
					return values.Select(Resolve).ToArray();
				}
				catch (JsonException ex) { throw new FormatException("Expected a JSON string array or comma-separated choices.", ex); }
			default:
				throw new NotSupportedException($"Unsupported OpenCode form field type '{type}'.");
		}
		throw new FormatException($"Expected a {type} answer.");
	}

	private static bool Matches(JsonElement condition, Dictionary<string, object> answers)
	{
		var exists = answers.TryGetValue(condition.GetProperty("key").GetString()!, out var answer);
		var equal = exists && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(answer), condition.GetProperty("value"));
		return condition.GetProperty("op").GetString() == "neq" ? !equal : equal;
	}

	private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
}
