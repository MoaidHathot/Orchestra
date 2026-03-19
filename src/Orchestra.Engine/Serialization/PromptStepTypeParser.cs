using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for Prompt step type JSON.
/// Handles deserialization of <see cref="PromptOrchestrationStep"/> from orchestration JSON.
/// </summary>
public sealed class PromptStepTypeParser : IStepTypeParser
{
	public string TypeName => "Prompt";

	public OrchestrationStep Parse(JsonElement root)
	{
		return new PromptOrchestrationStep
		{
			Name = root.GetProperty("name").GetString()!,
			Type = OrchestrationStepType.Prompt,
			DependsOn = root.TryGetProperty("dependsOn", out var deps)
				? deps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			SystemPrompt = root.GetProperty("systemPrompt").GetString()!,
			UserPrompt = root.GetProperty("userPrompt").GetString()!,
			InputHandlerPrompt = root.TryGetProperty("inputHandlerPrompt", out var ihp) ? ihp.GetString() : null,
			OutputHandlerPrompt = root.TryGetProperty("outputHandlerPrompt", out var ohp) ? ohp.GetString() : null,
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
				? subagents.EnumerateArray().Select(DeserializeSubagent).ToArray()
				: [],
		};
	}

	private static Subagent DeserializeSubagent(JsonElement element)
	{
		return new Subagent
		{
			Name = element.GetProperty("name").GetString()!,
			DisplayName = element.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
			Description = element.TryGetProperty("description", out var desc) ? desc.GetString() : null,
			Prompt = element.GetProperty("prompt").GetString()!,
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
