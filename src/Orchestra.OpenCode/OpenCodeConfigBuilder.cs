using System.Text;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// The OpenCode <c>agent</c>-config patch needed to apply a step's reasoning level and/or
/// sub-agents, plus the name of the primary agent to reference on the prompt request.
/// </summary>
internal sealed record OpenCodeAgentPlan(IReadOnlyDictionary<string, object> ConfigPatch, string PrimaryAgentName);

/// <summary>
/// Maps Orchestra's per-step <see cref="ReasoningLevel"/> and inline <see cref="Subagent"/>s
/// onto OpenCode's config surface. OpenCode addresses both through configured agents
/// (reasoning is a pass-through model option; sub-agents are <c>mode: subagent</c> entries the
/// primary agent invokes via the Task tool), so this builds a <c>PATCH /config</c> body that
/// (re)defines a per-run primary agent <c>orchestra-primary</c> carrying the system prompt +
/// <c>reasoningEffort</c>, plus one <c>orchestra-sub-*</c> entry per sub-agent. The primary
/// agent's <c>permission.task</c> allow-list scopes delegation to exactly this step's
/// sub-agents (denying any left over on a reused worker).
/// </summary>
internal static class OpenCodeConfigBuilder
{
	public const string PrimaryAgentName = "orchestra-primary";
	private const string SubagentPrefix = "orchestra-sub-";

	/// <summary>
	/// Builds the agent plan, or returns null when the step uses neither reasoning nor
	/// sub-agents (the simple model+system prompt path is used in that case).
	/// </summary>
	public static OpenCodeAgentPlan? Build(
		OpenCodeModelRef model,
		string? systemPrompt,
		ReasoningLevel? reasoningLevel,
		IReadOnlyList<Subagent> subagents,
		string fallbackProvider)
	{
		var hasSubagents = subagents.Count > 0;
		if (reasoningLevel is null && !hasSubagents)
			return null;

		var agents = new Dictionary<string, object>(StringComparer.Ordinal);

		var primary = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["mode"] = "primary",
			["model"] = model.ToString(),
			["description"] = "Orchestra step primary agent",
		};
		if (!string.IsNullOrWhiteSpace(systemPrompt))
			primary["prompt"] = systemPrompt;
		if (reasoningLevel is { } level)
			primary["reasoningEffort"] = level.ToString().ToLowerInvariant();

		if (hasSubagents)
		{
			var taskPermission = new Dictionary<string, object?>(StringComparer.Ordinal) { ["*"] = "deny" };
			var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var sub in subagents)
			{
				var name = UniqueSubagentName(sub.Name, usedNames);
				var subModel = ResolveSubagentModel(sub.Model, model, fallbackProvider);

				var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["mode"] = "subagent",
					["model"] = subModel,
					["description"] = string.IsNullOrWhiteSpace(sub.Description)
						? (sub.DisplayName ?? sub.Name)
						: sub.Description,
					["prompt"] = sub.Prompt,
				};
				if (sub.Tools is { Length: > 0 })
				{
					// Orchestra restricts a sub-agent to a tool allow-list; OpenCode tool flags are
					// a deny-list, so enable exactly the listed tools. (Unlisted built-ins remain
					// at their defaults — OpenCode has no "deny all others" tool flag.)
					var tools = new Dictionary<string, object?>(StringComparer.Ordinal);
					foreach (var tool in sub.Tools)
						tools[tool] = true;
					entry["tools"] = tools;
				}

				agents[name] = entry;
				taskPermission[name] = "allow";
			}

			primary["permission"] = new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["task"] = taskPermission,
			};
		}

		agents[PrimaryAgentName] = primary;
		var patch = new Dictionary<string, object>(StringComparer.Ordinal) { ["agent"] = agents };
		return new OpenCodeAgentPlan(patch, PrimaryAgentName);
	}

	private static string ResolveSubagentModel(string? subModel, OpenCodeModelRef mainModel, string fallbackProvider)
	{
		if (string.IsNullOrWhiteSpace(subModel))
			return mainModel.ToString();
		return OpenCodeModelRef.Parse(subModel, fallbackProvider).ToString();
	}

	private static string UniqueSubagentName(string rawName, HashSet<string> used)
	{
		var slug = Slugify(rawName);
		var candidate = SubagentPrefix + slug;
		var i = 2;
		while (!used.Add(candidate))
		{
			candidate = $"{SubagentPrefix}{slug}-{i}";
			i++;
		}
		return candidate;
	}

	internal static string Slugify(string value)
	{
		var sb = new StringBuilder(value.Length);
		var lastDash = false;
		foreach (var ch in value.Trim().ToLowerInvariant())
		{
			if (char.IsLetterOrDigit(ch))
			{
				sb.Append(ch);
				lastDash = false;
			}
			else if (!lastDash)
			{
				sb.Append('-');
				lastDash = true;
			}
		}
		var result = sb.ToString().Trim('-');
		return result.Length == 0 ? "agent" : result;
	}
}
