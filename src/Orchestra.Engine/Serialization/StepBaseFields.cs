using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// The fields every step type shares (<see cref="OrchestrationStep"/>), parsed once here so the
/// seven <see cref="IStepTypeParser"/> implementations cannot drift — adding a base field such as
/// <c>when</c> is a single edit instead of seven.
/// </summary>
/// <remarks>
/// <see cref="Parameters"/> is intentionally not included: the Orchestration step type uses the
/// same JSON key for a <em>map</em> of child parameters, so each parser decides how to read it.
/// </remarks>
public sealed record StepBaseFields(
	string Name,
	string[] DependsOn,
	bool Enabled,
	string? When,
	int? TimeoutSeconds,
	RetryPolicy? Retry)
{
	/// <summary>Reads the shared fields from a step's JSON object.</summary>
	public static StepBaseFields Parse(JsonElement root)
	{
		return new StepBaseFields(
			Name: root.GetProperty("name").GetString()!,
			DependsOn: root.TryGetProperty("dependsOn", out var deps)
				? deps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Enabled: !root.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
			When: root.TryGetProperty("when", out var when) && when.ValueKind == JsonValueKind.String
				? NormalizeWhen(when.GetString())
				: null,
			TimeoutSeconds: root.TryGetProperty("timeoutSeconds", out var ts)
				? ts.GetInt32()
				: null,
			Retry: root.TryGetProperty("retry", out var retry)
				? PromptStepTypeParser.DeserializeRetryPolicy(retry)
				: null);
	}

	/// <summary>Reads the string-array form of <c>parameters</c> used by every step type except Orchestration.</summary>
	public static string[] ParseParameterNames(JsonElement root)
	{
		return root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array
			? parameters.EnumerateArray().Select(e => e.GetString()!).ToArray()
			: [];
	}

	/// <summary>
	/// Reads <c>maxOutputBytes</c> for the output-capturing step types (Command, Script, Http).
	/// Absent/null keeps the engine default; 0 disables the cap; negative values are rejected.
	/// </summary>
	public static int? ParseMaxOutputBytes(JsonElement root, string stepName)
	{
		if (!root.TryGetProperty("maxOutputBytes", out var element) || element.ValueKind == JsonValueKind.Null)
			return null;

		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
			throw new JsonException($"Step '{stepName}': maxOutputBytes must be an integer number of bytes (0 disables the cap).");

		if (value < 0)
			throw new JsonException($"Step '{stepName}': maxOutputBytes must be 0 (no cap) or a positive number of bytes, got {value}.");

		return value;
	}

	private static string? NormalizeWhen(string? expression)
	{
		var trimmed = expression?.Trim();
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}
}
