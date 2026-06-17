using System.Text;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// The generated <c>opencode.json</c> config for a step (agents for reasoning/sub-agents, the
/// <c>mcp</c> section for the step's MCP servers, …) plus the primary agent name to reference on
/// the prompt request. Applied by spawning a dedicated server with this config; see
/// <see cref="OpenCodeConfigBuilder"/>.
/// </summary>
internal sealed record OpenCodeStepPlan(IReadOnlyDictionary<string, object> Config, string? PrimaryAgentName)
{
	/// <summary>True when this plan contributes any opencode.json config (agents and/or MCPs).</summary>
	public bool HasConfig => Config.Count > 0;
}

/// <summary>
/// Maps a step's OpenCode-relevant <see cref="AgentBuildConfig"/> fields onto an
/// <c>opencode.json</c> config object:
/// <list type="bullet">
///   <item><b>Reasoning + inline sub-agents</b> → an <c>agent</c> section: a primary agent
///   (<c>orchestra-primary</c>) carrying the system prompt + <c>reasoningEffort</c>, plus one
///   <c>mode: subagent</c> entry per <see cref="Subagent"/>, with the primary's
///   <c>permission.task</c> allow-list scoping delegation.</item>
///   <item><b>Step MCP servers</b> → an <c>mcp</c> section (<c>type:"local"</c> stdio or
///   <c>type:"remote"</c> http).</item>
/// </list>
/// The config is applied by spawning a dedicated server (runtime config patches don't register
/// usable agents), so any step producing a non-empty config runs on its own server instance.
/// </summary>
internal static class OpenCodeConfigBuilder
{
	public const string PrimaryAgentName = "orchestra-primary";
	private const string SubagentPrefix = "orchestra-sub-";

	public static OpenCodeStepPlan Build(
		OpenCodeModelRef model,
		string? systemPrompt,
		ReasoningLevel? reasoningLevel,
		IReadOnlyList<Subagent> subagents,
		IReadOnlyList<Mcp> mcps,
		IReadOnlyList<string> excludedTools,
		string fallbackProvider)
	{
		var config = new Dictionary<string, object>(StringComparer.Ordinal);
		var primaryAgentName = BuildAgentSection(config, model, systemPrompt, reasoningLevel, subagents, excludedTools, fallbackProvider);
		BuildMcpSection(config, mcps);
		return new OpenCodeStepPlan(config, primaryAgentName);
	}

	/// <summary>Adds the <c>agent</c> section; returns the primary agent name, or null when no reasoning, sub-agents, or excluded tools are requested.</summary>
	private static string? BuildAgentSection(
		Dictionary<string, object> config,
		OpenCodeModelRef model,
		string? systemPrompt,
		ReasoningLevel? reasoningLevel,
		IReadOnlyList<Subagent> subagents,
		IReadOnlyList<string> excludedTools,
		string fallbackProvider)
	{
		var hasSubagents = subagents.Count > 0;
		var hasExcludedTools = excludedTools.Count > 0;
		if (reasoningLevel is null && !hasSubagents && !hasExcludedTools)
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
		if (hasExcludedTools)
		{
			// OpenCode disables a tool for an agent via `tools: { <name>: false }`. Tool names use
			// OpenCode's vocabulary (bash, edit, write, read, webfetch, …) — distinct from Copilot's.
			var tools = new Dictionary<string, object?>(StringComparer.Ordinal);
			foreach (var tool in excludedTools)
			{
				if (!string.IsNullOrWhiteSpace(tool))
					tools[tool] = false;
			}

			if (tools.Count > 0)
				primary["tools"] = tools;
		}

		if (hasSubagents)
		{
			var taskPermission = new Dictionary<string, object?>(StringComparer.Ordinal) { ["*"] = "deny" };
			var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var sub in subagents)
			{
				var name = UniqueSubagentName(sub.Name, usedNames);
				var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["mode"] = "subagent",
					["model"] = ResolveSubagentModel(sub.Model, model, fallbackProvider),
					["description"] = string.IsNullOrWhiteSpace(sub.Description) ? (sub.DisplayName ?? sub.Name) : sub.Description,
					["prompt"] = sub.Prompt,
				};
				if (sub.Tools is { Length: > 0 })
				{
					var tools = new Dictionary<string, object?>(StringComparer.Ordinal);
					foreach (var tool in sub.Tools)
						tools[tool] = true;
					entry["tools"] = tools;
				}

				agents[name] = entry;
				taskPermission[name] = "allow";
			}

			primary["permission"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["task"] = taskPermission };
		}

		agents[PrimaryAgentName] = primary;
		config["agent"] = agents;
		return PrimaryAgentName;
	}

	/// <summary>Adds the <c>mcp</c> section mapping Orchestra <see cref="Mcp"/>s to OpenCode local/remote MCP entries.</summary>
	private static void BuildMcpSection(Dictionary<string, object> config, IReadOnlyList<Mcp> mcps)
	{
		if (mcps.Count == 0)
			return;

		var section = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach (var mcp in mcps)
		{
			var entry = new Dictionary<string, object?>(StringComparer.Ordinal) { ["enabled"] = true };
			if (mcp.Timeout is { } timeout && timeout > TimeSpan.Zero)
				entry["timeout"] = (long)timeout.TotalMilliseconds;

			switch (mcp)
			{
				case LocalMcp local:
					entry["type"] = "local";
					entry["command"] = new List<string> { local.Command }.Concat(local.Arguments ?? []).ToList();
					if (local.Environment is { Count: > 0 })
						entry["environment"] = local.Environment.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
					if (!string.IsNullOrWhiteSpace(local.WorkingDirectory))
						entry["cwd"] = local.WorkingDirectory;
					break;
				case RemoteMcp remote:
					entry["type"] = "remote";
					entry["url"] = remote.Endpoint;
					if (remote.Headers is { Count: > 0 })
						entry["headers"] = remote.Headers.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
					break;
				default:
					continue; // unknown MCP type — skip rather than emit an invalid entry
			}

			section[mcp.Name] = entry;
		}

		if (section.Count > 0)
			config["mcp"] = section;
	}

	private static string ResolveSubagentModel(string? subModel, OpenCodeModelRef mainModel, string fallbackProvider)
		=> string.IsNullOrWhiteSpace(subModel)
			? mainModel.ToString()
			: OpenCodeModelRef.Parse(subModel, fallbackProvider).ToString();

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
