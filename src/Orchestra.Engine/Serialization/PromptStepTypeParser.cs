using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Prompt step type JSON.
/// Handles deserialization of <see cref="PromptOrchestrationStep"/> from orchestration JSON.
/// Supports both inline prompt values and file-based prompt loading via <c>*File</c> properties.
/// </summary>
public sealed partial class PromptStepTypeParser : IStepTypeParser
{
	private const int MaxVariablePathResolutionPasses = 10;

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
			Model = root.TryGetProperty("model", out var model)
				? model.GetString()!
				: null!,
			McpNames = root.TryGetProperty("mcps", out var mcps)
				? mcps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			ReasoningLevel = root.TryGetProperty("reasoningLevel", out var rl)
				? Enum.Parse<ReasoningLevel>(rl.GetString()!, ignoreCase: true)
				: null,
			ReasoningSummary = root.TryGetProperty("reasoningSummary", out var rs)
				? Enum.Parse<ReasoningSummaryLevel>(rs.GetString()!, ignoreCase: true)
				: null,
			ContextTier = root.TryGetProperty("contextTier", out var ct)
				? Enum.Parse<ContextTier>(ct.GetString()!, ignoreCase: true)
				: null,
			WorkingDirectory = root.TryGetProperty("workingDirectory", out var wd)
				? wd.GetString()
				: null,
			GitHubToken = root.TryGetProperty("githubToken", out var ght)
				? ght.GetString()
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
				? skillDirs.EnumerateArray().Select(e => ResolveSkillDirectoryPath(e.GetString()!, context)).ToArray()
				: [],
			InfiniteSessions = root.TryGetProperty("infiniteSessions", out var infSessions)
				? DeserializeInfiniteSessionConfig(infSessions)
				: null,
			SystemPromptSections = root.TryGetProperty("systemPromptSections", out var sps)
				? DeserializeSystemPromptSections(sps)
				: null,
			Attachments = root.TryGetProperty("attachments", out var attachments)
				? attachments.EnumerateArray().Select(e => DeserializeAttachment(e, context)).ToArray()
				: [],
			EnableTools = root.TryGetProperty("enableTools", out var enableTools) && enableTools.ValueKind == JsonValueKind.Array
				? enableTools.EnumerateArray().Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
				: null,
			HumanInput = root.TryGetProperty("humanInput", out var humanInput)
				? humanInput.GetBoolean()
				: null,
			// null = inherit Orchestration.DefaultFailOnToolError; explicit bool overrides.
			// Mirror the read pattern of e.g. `Enabled` — TryGetProperty + .GetBoolean — but
			// preserve null so the step-level value can distinguish "unset" from "false".
			FailOnToolError = root.TryGetProperty("failOnToolError", out var failOnToolError)
				? failOnToolError.GetBoolean()
				: null,
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
	internal static string ResolveVarsInPath(string path, IReadOnlyDictionary<string, string>? variables)
	{
		if (variables is null || !path.Contains("{{vars.", StringComparison.OrdinalIgnoreCase))
			return path;

		var resolved = path;
		for (var i = 0; i < MaxVariablePathResolutionPasses; i++)
		{
			var replaced = VarsPattern().Replace(resolved, match =>
			{
				var varName = match.Groups["name"].Value;
				return variables.TryGetValue(varName, out var value) ? value : match.Value;
			});

			if (string.Equals(replaced, resolved, StringComparison.Ordinal))
				return replaced;

			resolved = replaced;
		}

		return resolved;
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"\{\{vars\.(?<name>[^}]+)\}\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled)]
	private static partial System.Text.RegularExpressions.Regex VarsPattern();

	/// <summary>
	/// Expands vars expressions and resolves non-templated relative paths from the orchestration base directory.
	/// Other template expressions are left for runtime resolution.
	/// </summary>
	internal static string ResolvePathRelativeToBaseDirectory(string path, StepParseContext context)
	{
		var expanded = ResolveVarsInPath(path, context.Variables);
		if (expanded.Contains("{{") || Path.IsPathRooted(expanded))
			return expanded;

		return context.BaseDirectory is not null
			? Path.GetFullPath(Path.Combine(context.BaseDirectory, expanded))
			: expanded;
	}

	/// <summary>
	/// Resolves a skill directory path relative to the orchestration file's base directory.
	/// Paths containing template expressions (e.g., <c>{{param.dir}}</c>) are left as-is
	/// since they will be resolved at execution time by <see cref="TemplateResolver"/>.
	/// Paths containing <c>{{vars.*}}</c> are expanded first, then resolved relative to the base directory.
	/// </summary>
	private static string ResolveSkillDirectoryPath(string path, StepParseContext context)
	{
		return ResolvePathRelativeToBaseDirectory(path, context);
	}

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

	private static InfiniteSessionConfig DeserializeInfiniteSessionConfig(JsonElement element)
	{
		return new InfiniteSessionConfig
		{
			Enabled = element.TryGetProperty("enabled", out var e) ? e.GetBoolean() : null,
			BackgroundCompactionThreshold = element.TryGetProperty("backgroundCompactionThreshold", out var bct) ? bct.GetDouble() : null,
			BufferExhaustionThreshold = element.TryGetProperty("bufferExhaustionThreshold", out var bet) ? bet.GetDouble() : null,
		};
	}

	private static Dictionary<string, SystemPromptSectionOverride> DeserializeSystemPromptSections(JsonElement element)
	{
		var dict = new Dictionary<string, SystemPromptSectionOverride>(StringComparer.OrdinalIgnoreCase);
		foreach (var prop in element.EnumerateObject())
		{
			dict[prop.Name] = new SystemPromptSectionOverride
			{
				Action = Enum.Parse<SystemPromptSectionAction>(prop.Value.GetProperty("action").GetString()!, ignoreCase: true),
				Content = prop.Value.TryGetProperty("content", out var c) ? c.GetString() : null,
			};
		}
		return dict;
	}

	private static ImageAttachment DeserializeAttachment(JsonElement element, StepParseContext context)
	{
		var type = element.GetProperty("type").GetString()!;
		return type.ToLowerInvariant() switch
		{
			"file" => new FileImageAttachment
			{
				Path = ResolveAttachmentPath(element.GetProperty("path").GetString()!, context),
				DisplayName = element.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
			},
			"blob" => new BlobImageAttachment
			{
				Data = element.GetProperty("data").GetString()!,
				MimeType = element.GetProperty("mimeType").GetString()!,
				DisplayName = element.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
			},
			_ => throw new JsonException($"Unknown attachment type: '{type}'. Expected 'file' or 'blob'."),
		};
	}

	private static string ResolveAttachmentPath(string path, StepParseContext context)
	{
		return ResolvePathRelativeToBaseDirectory(path, context);
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
