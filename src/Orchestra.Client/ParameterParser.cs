namespace Orchestra.Client;

/// <summary>
/// Parses repeated <c>--param key=value</c> flags into a dictionary. Last write wins for
/// duplicate keys, matching the legacy CLI behaviour. Empty or malformed entries (no
/// <c>=</c>) are silently ignored so a stray flag doesn't fail the run.
/// </summary>
public static class ParameterParser
{
	public static Dictionary<string, string>? Parse(string[]? raw)
	{
		if (raw is null || raw.Length == 0)
		{
			return null;
		}

		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var entry in raw)
		{
			if (string.IsNullOrWhiteSpace(entry))
			{
				continue;
			}

			var idx = entry.IndexOf('=');
			if (idx <= 0)
			{
				continue;
			}

			var key = entry[..idx];
			var value = entry[(idx + 1)..];
			result[key] = value;
		}

		return result.Count > 0 ? result : null;
	}
}
