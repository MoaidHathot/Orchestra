using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Prompt step type JSON.
/// Handles deserialization of <see cref="PromptOrchestrationStep"/> from orchestration JSON.
/// Supports both inline prompt values and file-based prompt loading via <c>*File</c> properties.
/// </summary>
public sealed partial class PromptStepTypeParser : IStepTypeParser
{
	public string TypeName => "Prompt";

	public OrchestrationStep Parse(JsonElement root, StepParseContext context)
	{
		var stepName = root.GetProperty("name").GetString()!;

		return new PromptOrchestrationStep
		{
			Name = stepName,
			Type = OrchestrationStepType.Prompt,
			DependsOn = root.TryGetProperty("dependsOn", out var deps)
				? deps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Enabled = !root.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
			SystemPrompt = ResolveRequiredPrompt(root, "systemPrompt", stepName, context),
			UserPrompt = ResolveRequiredPrompt(root, "userPrompt", stepName, context),
			InputHandlerPrompt = ResolveOptionalPrompt(root, "inputHandlerPrompt", stepName, context),
			OutputHandlerPrompt = ResolveOptionalPrompt(root, "outputHandlerPrompt", stepName, context),
			Model = root.GetProperty("model").GetString()!,
			McpNames = root.TryGetProperty("mcps", out var mcps)
				? mcps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			ReasoningLevel = root.TryGetProperty("reasoningLevel", out var rl)
				? Enum.Parse<ReasoningLevel>(rl.GetString()!, ignoreCase: true)
				: null,
			SystemPromptMode = root.TryGetProperty("systemPromptMode", out var spm)
				? Enum.Parse<SystemPromptMode>(spm.GetString()!, ignoreCase: true)
				: null,
			TimeoutSeconds = root.TryGetProperty("timeoutSeconds", out var ts)
				? ts.GetInt32()
				: null,
			Retry = root.TryGetProperty("retry", out var retry)
				? DeserializeRetryPolicy(retry)
				: null,
			Parameters = root.TryGetProperty("parameters", out var parameters)
				? parameters.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Loop = root.TryGetProperty("loop", out var loop)
				? DeserializeLoopConfig(loop)
				: null,
			Subagents = root.TryGetProperty("subagents", out var subagents)
				? subagents.EnumerateArray().Select(e => DeserializeSubagent(e, stepName, context)).ToArray()
				: [],
			SkillDirectories = root.TryGetProperty("skillDirectories", out var skillDirs)
				? skillDirs.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
		};
	}

	/// <summary>
	/// Resolves a required prompt value from either an inline property or a file reference.
	/// Exactly one of <paramref name="propertyName"/> or <c>{propertyName}File</c> must be specified.
	/// </summary>
	private static string ResolveRequiredPrompt(JsonElement root, string propertyName, string stepName, StepParseContext context)
	{
		var filePropertyName = propertyName + "File";
		var hasInline = root.TryGetProperty(propertyName, out var inlineValue);
		var hasFile = root.TryGetProperty(filePropertyName, out var fileValue);

		if (hasInline && hasFile)
			throw new JsonException(
				$"Step '{stepName}': Cannot specify both '{propertyName}' and '{filePropertyName}'. Use one or the other.");

		if (hasFile)
			return ReadPromptFile(fileValue.GetString()!, filePropertyName, stepName, context);

		if (hasInline)
			return inlineValue.GetString()!;

		throw new JsonException(
			$"Step '{stepName}': Either '{propertyName}' or '{filePropertyName}' is required.");
	}

	/// <summary>
	/// Resolves an optional prompt value from either an inline property or a file reference.
	/// At most one of <paramref name="propertyName"/> or <c>{propertyName}File</c> may be specified.
	/// </summary>
	private static string? ResolveOptionalPrompt(JsonElement root, string propertyName, string stepName, StepParseContext context)
	{
		var filePropertyName = propertyName + "File";
		var hasInline = root.TryGetProperty(propertyName, out var inlineValue);
		var hasFile = root.TryGetProperty(filePropertyName, out var fileValue);

		if (hasInline && hasFile)
			throw new JsonException(
				$"Step '{stepName}': Cannot specify both '{propertyName}' and '{filePropertyName}'. Use one or the other.");

		if (hasFile)
			return ReadPromptFile(fileValue.GetString()!, filePropertyName, stepName, context);

		return hasInline ? inlineValue.GetString() : null;
	}

	/// <summary>
	/// Reads a prompt file, resolving the path relative to the orchestration base directory.
	/// Expands <c>{{vars.*}}</c> expressions in the file path using pre-extracted variables.
	/// Validates that the file exists and is readable at parse time (fail fast).
	/// </summary>
	private static string ReadPromptFile(string filePath, string propertyName, string stepName, StepParseContext context)
	{
		// In metadata-only mode, skip file I/O entirely. Prompt files may reference
		// template expressions (e.g., {{vars.promptsDir}}/file.md) that are not
		// resolved during metadata parsing, so attempting to read them would fail.
		if (context.MetadataOnly)
			return string.Empty;

		// Expand {{vars.*}} expressions in the file path using pre-extracted variables.
		var expandedPath = ResolveVarsInPath(filePath, context.Variables);

		var resolvedPath = Path.IsPathRooted(expandedPath)
			? expandedPath
			: context.BaseDirectory is not null
				? Path.GetFullPath(Path.Combine(context.BaseDirectory, expandedPath))
				: Path.GetFullPath(expandedPath);

		if (!File.Exists(resolvedPath))
			throw new JsonException(
				$"Step '{stepName}': File not found for '{propertyName}': {resolvedPath}");

		try
		{
			return File.ReadAllText(resolvedPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new JsonException(
				$"Step '{stepName}': Failed to read file for '{propertyName}': {resolvedPath} — {ex.Message}", ex);
		}
	}

	/// <summary>
	/// Replaces <c>{{vars.name}}</c> placeholders in a path string using the provided variables.
	/// Returns the original string unchanged if no variables are available or no expressions match.
	/// </summary>
	private static string ResolveVarsInPath(string path, IReadOnlyDictionary<string, string>? variables)
	{
		if (variables is null || !path.Contains("{{vars.", StringComparison.OrdinalIgnoreCase))
			return path;

		return VarsPattern().Replace(path, match =>
		{
			var varName = match.Groups["name"].Value;
			return variables.TryGetValue(varName, out var value) ? value : match.Value;
		});
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"\{\{vars\.(?<name>[^}]+)\}\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)]
	private static partial System.Text.RegularExpressions.Regex VarsPattern();

	private static Subagent DeserializeSubagent(JsonElement element, string stepName, StepParseContext context)
	{
		var subagentName = element.GetProperty("name").GetString()!;
		var qualifiedName = $"{stepName}/subagent:{subagentName}";

		return new Subagent
		{
			Name = subagentName,
			DisplayName = element.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
			Description = element.TryGetProperty("description", out var desc) ? desc.GetString() : null,
			Prompt = ResolveRequiredPrompt(element, "prompt", qualifiedName, context),
			Tools = element.TryGetProperty("tools", out var tools)
				? tools.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: null,
			McpNames = element.TryGetProperty("mcps", out var mcps)
				? mcps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Infer = element.TryGetProperty("infer", out var infer) ? infer.GetBoolean() : true,
		};
	}

	private static LoopConfig DeserializeLoopConfig(JsonElement element)
	{
		return new LoopConfig
		{
			Target = element.GetProperty("target").GetString()!,
			MaxIterations = element.GetProperty("maxIterations").GetInt32(),
			ExitPattern = element.GetProperty("exitPattern").GetString()!,
		};
	}

	internal static RetryPolicy DeserializeRetryPolicy(JsonElement element)
	{
		return new RetryPolicy
		{
			MaxRetries = element.TryGetProperty("maxRetries", out var mr) ? mr.GetInt32() : 3,
			BackoffSeconds = element.TryGetProperty("backoffSeconds", out var bs) ? bs.GetDouble() : 1.0,
			BackoffMultiplier = element.TryGetProperty("backoffMultiplier", out var bm) ? bm.GetDouble() : 2.0,
			RetryOnTimeout = !element.TryGetProperty("retryOnTimeout", out var rot) || rot.GetBoolean(),
		};
	}
}
