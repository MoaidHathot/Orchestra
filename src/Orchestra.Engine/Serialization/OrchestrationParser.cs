using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestra.Engine;

public static class OrchestrationParser
{
	private static readonly JsonSerializerOptions s_options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters =
		{
			new OrchestrationStepConverter(),
			new McpConverter(),
			new TriggerConfigConverter(),
			new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
		},
	};

	public static Orchestration ParseOrchestration(string json, Mcp[] availableMcps)
	{
		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, s_options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");

		ResolveStepMcps(orchestration, availableMcps);
		return orchestration;
	}

	public static Orchestration ParseOrchestrationFile(string path, Mcp[] availableMcps)
	{
		var json = File.ReadAllText(path);
		return ParseOrchestration(json, availableMcps);
	}

	/// <summary>
	/// Parses orchestration structure without resolving MCP references.
	/// Useful for metadata extraction (e.g., folder scan) where MCP configs are unavailable.
	/// </summary>
	public static Orchestration ParseOrchestrationMetadataOnly(string json)
	{
		return JsonSerializer.Deserialize<Orchestration>(json, s_options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");
	}

	/// <summary>
	/// Parses orchestration structure from file without resolving MCP references.
	/// </summary>
	public static Orchestration ParseOrchestrationFileMetadataOnly(string path)
	{
		var json = File.ReadAllText(path);
		return ParseOrchestrationMetadataOnly(json);
	}

	public static Mcp[] ParseMcps(string json)
	{
		var doc = JsonSerializer.Deserialize<McpConfigDocument>(json, s_options)
			?? throw new InvalidOperationException("Failed to deserialize MCP JSON.");

		return doc.Mcps;
	}

	public static Mcp[] ParseMcpFile(string path)
	{
		var json = File.ReadAllText(path);
		return ParseMcps(json);
	}

	private sealed class McpConfigDocument
	{
		public required Mcp[] Mcps { get; init; }
	}

	private static void ResolveStepMcps(Orchestration orchestration, Mcp[] availableMcps)
	{
		// Merge inline MCPs from orchestration with externally provided MCPs.
		// External MCPs take priority on name conflicts (override inline definitions).
		var lookup = new Dictionary<string, Mcp>(StringComparer.OrdinalIgnoreCase);
		foreach (var mcp in orchestration.Mcps)
			lookup[mcp.Name] = mcp;
		foreach (var mcp in availableMcps)
			lookup[mcp.Name] = mcp; // external overrides inline

		foreach (var step in orchestration.Steps)
		{
			if (step is PromptOrchestrationStep promptStep && promptStep.McpNames.Length > 0)
			{
				var resolved = new Mcp[promptStep.McpNames.Length];
				for (var i = 0; i < promptStep.McpNames.Length; i++)
				{
					var name = promptStep.McpNames[i];
					if (!lookup.TryGetValue(name, out var mcp))
						throw new InvalidOperationException(
							$"MCP '{name}' referenced by step '{step.Name}' is not defined in MCP configuration.");
					resolved[i] = mcp;
				}
				promptStep.Mcps = resolved;
			}
		}
	}

	private sealed class McpConverter : JsonConverter<Mcp>
	{
		public override Mcp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			using var doc = JsonDocument.ParseValue(ref reader);
			var root = doc.RootElement;

			var type = root.TryGetProperty("type", out var typeProp)
				? Enum.Parse<McpType>(typeProp.GetString()!, ignoreCase: true)
				: throw new JsonException("Missing 'type' property on MCP entry.");

			var name = root.GetProperty("name").GetString()!;

			return type switch
			{
				McpType.Local => new LocalMcp
				{
					Name = name,
					Type = McpType.Local,
					Command = root.GetProperty("command").GetString()!,
					Arguments = root.TryGetProperty("arguments", out var args)
						? args.EnumerateArray().Select(e => e.GetString()!).ToArray()
						: [],
				},
				McpType.Remote => new RemoteMcp
				{
					Name = name,
					Type = McpType.Remote,
					Endpoint = root.GetProperty("endpoint").GetString()!,
					Headers = root.TryGetProperty("headers", out var headers)
						? headers.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
						: [],
				},
				_ => throw new JsonException($"Unknown MCP type: '{type}'."),
			};
		}

		public override void Write(Utf8JsonWriter writer, Mcp value, JsonSerializerOptions options)
		{
			JsonSerializer.Serialize(writer, value, value.GetType(), options);
		}
	}

	private sealed class OrchestrationStepConverter : JsonConverter<OrchestrationStep>
	{
		public override OrchestrationStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			using var doc = JsonDocument.ParseValue(ref reader);
			var root = doc.RootElement;

			var type = root.TryGetProperty("type", out var typeProp)
				? Enum.Parse<OrchestrationStepType>(typeProp.GetString()!, ignoreCase: true)
				: throw new JsonException("Missing 'type' property on orchestration step.");

			return type switch
			{
				OrchestrationStepType.Prompt => DeserializePromptStep(root),
				_ => throw new JsonException($"Unknown orchestration step type: '{type}'."),
			};
		}

		public override void Write(Utf8JsonWriter writer, OrchestrationStep value, JsonSerializerOptions options)
		{
			JsonSerializer.Serialize(writer, value, value.GetType(), options);
		}

		private static PromptOrchestrationStep DeserializePromptStep(JsonElement root)
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
				Parameters = root.TryGetProperty("parameters", out var parameters)
				? parameters.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
				Loop = root.TryGetProperty("loop", out var loop)
					? DeserializeLoopConfig(loop)
					: null,
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
	}

	private sealed class TriggerConfigConverter : JsonConverter<TriggerConfig>
	{
		public override TriggerConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			using var doc = JsonDocument.ParseValue(ref reader);
			var root = doc.RootElement;

			var type = root.TryGetProperty("type", out var typeProp)
				? Enum.Parse<TriggerType>(typeProp.GetString()!, ignoreCase: true)
				: throw new JsonException("Missing 'type' property on trigger configuration.");

			var enabled = root.TryGetProperty("enabled", out var enabledProp)
				? enabledProp.GetBoolean()
				: true;

			var inputHandlerPrompt = root.TryGetProperty("inputHandlerPrompt", out var ihpProp)
				? ihpProp.GetString()
				: null;

			return type switch
			{
				TriggerType.Scheduler => new SchedulerTriggerConfig
				{
					Type = TriggerType.Scheduler,
					Enabled = enabled,
					InputHandlerPrompt = inputHandlerPrompt,
					Cron = root.TryGetProperty("cron", out var cron) ? cron.GetString() : null,
					IntervalSeconds = root.TryGetProperty("intervalSeconds", out var interval) ? interval.GetInt32() : null,
					MaxRuns = root.TryGetProperty("maxRuns", out var maxRuns) ? maxRuns.GetInt32() : null,
				},
				TriggerType.Loop => new LoopTriggerConfig
				{
					Type = TriggerType.Loop,
					Enabled = enabled,
					InputHandlerPrompt = inputHandlerPrompt,
					DelaySeconds = root.TryGetProperty("delaySeconds", out var delay) ? delay.GetInt32() : 0,
					MaxIterations = root.TryGetProperty("maxIterations", out var maxIter) ? maxIter.GetInt32() : null,
					ContinueOnFailure = root.TryGetProperty("continueOnFailure", out var cof) && cof.GetBoolean(),
				},
				TriggerType.Webhook => new WebhookTriggerConfig
				{
					Type = TriggerType.Webhook,
					Enabled = enabled,
					InputHandlerPrompt = inputHandlerPrompt,
					Secret = root.TryGetProperty("secret", out var secret) ? secret.GetString() : null,
					MaxConcurrent = root.TryGetProperty("maxConcurrent", out var maxConc) ? maxConc.GetInt32() : 1,
				},
				_ => throw new JsonException($"Unknown trigger type: '{type}'."),
			};
		}

		public override void Write(Utf8JsonWriter writer, TriggerConfig value, JsonSerializerOptions options)
		{
			JsonSerializer.Serialize(writer, value, value.GetType(), options);
		}
	}
}
