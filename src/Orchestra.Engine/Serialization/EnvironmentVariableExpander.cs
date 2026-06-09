using System.Text.RegularExpressions;

namespace Orchestra.Engine.Serialization;

/// <summary>
/// Expands environment-variable references inside JSON configuration text before
/// deserialization. Supports two syntaxes, matching the convention already used by
/// the mcp-proxy SDK so the same authoring patterns work across both products:
///
/// <list type="bullet">
///   <item><description><c>${VAR}</c> — inline substitution. The match is replaced in
///   place; usable anywhere inside any JSON string value
///   (e.g. <c>"https://server/${TENANT_ID}/api"</c>).</description></item>
///   <item><description><c>"env:VAR"</c> — whole-value substitution. The entire
///   quoted string is replaced with the env var's value as a fresh quoted JSON
///   string. Use when the value is a plain identifier
///   (e.g. <c>"clientSecret": "env:MY_SECRET"</c>).</description></item>
/// </list>
///
/// When a referenced variable is not set in the process environment, the expander
/// throws <see cref="EnvironmentVariableExpansionException"/> with the variable
/// name and source path so misconfigurations surface at load time instead of as
/// confusing downstream failures (e.g. an MCP child process receiving the literal
/// string <c>${TENANT_ID}</c> as its tenant id and silently failing to authenticate).
/// </summary>
/// <remarks>
/// The expander is intentionally text-level (regex over the raw JSON string).
/// Operating before <c>JsonSerializer.Deserialize</c> means a single substitution
/// pass covers every field in the document — no per-field plumbing required.
/// String escaping inside the env-var value is the caller's responsibility; values
/// containing JSON metacharacters (<c>"</c>, <c>\</c>, control chars) should be
/// quoted in the source environment.
/// </remarks>
public static partial class EnvironmentVariableExpander
{
	/// <summary>
	/// Expands <c>${VAR}</c> and <c>"env:VAR"</c> references inside <paramref name="json"/>
	/// against the current process environment.
	/// </summary>
	/// <param name="json">The raw JSON text to expand. Returned unchanged when no
	/// references are present.</param>
	/// <param name="sourcePath">Optional source path used to make missing-variable
	/// errors actionable. Pass the on-disk file path when expanding a config file;
	/// pass a descriptive label (e.g. <c>"&lt;inline&gt;"</c>) when expanding a
	/// string literal.</param>
	/// <returns>The JSON with all references expanded.</returns>
	/// <exception cref="EnvironmentVariableExpansionException">Thrown when any
	/// referenced variable is not set in the process environment.</exception>
	public static string Expand(string json, string? sourcePath = null)
	{
		ArgumentNullException.ThrowIfNull(json);

		if (json.Length == 0)
			return json;

		// First pass: "env:VAR" — whole-string substitution. Done before the inline
		// pass so a value like "env:SOME_VAR" does not get its leading quote left
		// over after the inline regex runs.
		json = EnvColonPattern().Replace(json, match =>
		{
			var varName = match.Groups[1].Value;
			var value = Environment.GetEnvironmentVariable(varName)
				?? throw new EnvironmentVariableExpansionException(varName, sourcePath, "env:VAR");

			// Re-emit as a JSON-escaped quoted string. JsonEncodedText handles the
			// minimum required escaping (" \ control chars), which is enough for
			// the values typical of env-driven config (GUIDs, URLs, secrets).
			return System.Text.Json.JsonEncodedText.Encode(value).ToString() is var encoded
				? $"\"{encoded}\""
				: $"\"{value}\"";
		});

		// Second pass: ${VAR} — inline substitution inside any string value.
		json = DollarBracePattern().Replace(json, match =>
		{
			var varName = match.Groups[1].Value;
			var value = Environment.GetEnvironmentVariable(varName)
				?? throw new EnvironmentVariableExpansionException(varName, sourcePath, "${VAR}");

			// Inline insertion: escape only the characters that would break out of
			// the surrounding JSON string. We can't use JsonEncodedText here
			// because we're writing into the middle of an existing quoted string.
			return EscapeForJsonStringInterior(value);
		});

		return json;
	}

	/// <summary>
	/// Escapes a value for safe insertion into the interior of a JSON string literal.
	/// Handles the minimal set required by RFC 8259: backslash, double-quote, and
	/// the C0 control characters (U+0000–U+001F).
	/// </summary>
	private static string EscapeForJsonStringInterior(string value)
	{
		// Fast path: nothing to escape.
		var needsEscape = false;
		foreach (var c in value)
		{
			if (c == '"' || c == '\\' || c < ' ')
			{
				needsEscape = true;
				break;
			}
		}
		if (!needsEscape)
			return value;

		var sb = new System.Text.StringBuilder(value.Length + 8);
		foreach (var c in value)
		{
			switch (c)
			{
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				case '\b': sb.Append("\\b"); break;
				case '\f': sb.Append("\\f"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (c < ' ')
						sb.Append("\\u").Append(((int)c).ToString("x4"));
					else
						sb.Append(c);
					break;
			}
		}
		return sb.ToString();
	}

	[GeneratedRegex(@"""env:([A-Za-z_][A-Za-z0-9_]*)""")]
	private static partial Regex EnvColonPattern();

	[GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
	private static partial Regex DollarBracePattern();
}

/// <summary>
/// Raised by <see cref="EnvironmentVariableExpander"/> when a referenced
/// environment variable is not set. The exception carries the variable name,
/// source location, and the syntax that triggered the lookup so authors can
/// identify the offending reference at a glance.
/// </summary>
public sealed class EnvironmentVariableExpansionException : InvalidOperationException
{
	/// <summary>
	/// The name of the missing environment variable.
	/// </summary>
	public string VariableName { get; }

	/// <summary>
	/// Path to the source file that referenced the variable, or a descriptive
	/// label when the source was a string literal. <c>null</c> if no source was
	/// supplied to <see cref="EnvironmentVariableExpander.Expand"/>.
	/// </summary>
	public string? SourcePath { get; }

	/// <summary>
	/// The syntax form used at the reference site (e.g. <c>"${VAR}"</c> or
	/// <c>"env:VAR"</c>).
	/// </summary>
	public string Syntax { get; }

	internal EnvironmentVariableExpansionException(string variableName, string? sourcePath, string syntax)
		: base(BuildMessage(variableName, sourcePath, syntax))
	{
		VariableName = variableName;
		SourcePath = sourcePath;
		Syntax = syntax;
	}

	private static string BuildMessage(string variableName, string? sourcePath, string syntax)
	{
		var location = sourcePath is null
			? string.Empty
			: $" in '{sourcePath}'";
		return $"Environment variable '{variableName}' referenced via {syntax}{location} is not set. " +
			"Set the variable in your environment, or remove the reference from the config file.";
	}
}
