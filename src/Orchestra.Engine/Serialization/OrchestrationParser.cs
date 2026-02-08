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
		var lookup = availableMcps.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

		foreach (var step in orchestration.Steps)
		{
			if (step is PromptOrchestrationStep promptStep && promptStep.AllowedMcpNames.Length > 0)
			{
				var resolved = new Mcp[promptStep.AllowedMcpNames.Length];
				for (var i = 0; i < promptStep.AllowedMcpNames.Length; i++)
				{
					var name = promptStep.AllowedMcpNames[i];
					if (!lookup.TryGetValue(name, out var mcp))
						throw new InvalidOperationException(
							$"MCP '{name}' referenced by step '{step.Name}' is not defined in MCP configuration.");
					resolved[i] = mcp;
				}
				promptStep.AllowedMcps = resolved;
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
			AllowedMcpNames = root.TryGetProperty("mcps", out var mcps)
				? mcps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Parameters = root.TryGetProperty("parameters", out var parameters)
				? parameters.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			};
		}
	}
}
