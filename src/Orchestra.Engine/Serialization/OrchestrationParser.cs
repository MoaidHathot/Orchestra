using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestra.Engine;

public static class OrchestrationParser
{
	/// <summary>
	/// Default step type parser registry with built-in step types.
	/// </summary>
	private static readonly StepTypeParserRegistry s_defaultParserRegistry = new StepTypeParserRegistry()
		.Register(new PromptStepTypeParser())
		.Register(new HttpStepTypeParser())
		.Register(new TransformStepTypeParser())
		.Register(new CommandStepTypeParser());

	private static readonly JsonSerializerOptions s_options = CreateOptions(s_defaultParserRegistry);

	/// <summary>
	/// Creates a <see cref="StepTypeParserRegistry"/> pre-populated with all built-in step type parsers.
	/// Use this as a base when registering custom step type parsers.
	/// </summary>
	public static StepTypeParserRegistry CreateDefaultParserRegistry()
	{
		return new StepTypeParserRegistry()
			.Register(new PromptStepTypeParser())
			.Register(new HttpStepTypeParser())
			.Register(new TransformStepTypeParser())
			.Register(new CommandStepTypeParser());
	}

	private static JsonSerializerOptions CreateOptions(StepTypeParserRegistry parserRegistry)
	{
		return new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			Converters =
			{
				new OrchestrationStepConverter(parserRegistry),
				new McpConverter(),
				new TriggerConfigConverter(),
				new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
			},
		};
	}

	public static Orchestration ParseOrchestration(string json, Mcp[] availableMcps)
	{
		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, s_options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");

		ResolveStepMcps(orchestration, availableMcps);
		return orchestration;
	}

	/// <summary>
	/// Parses orchestration JSON using a custom step type parser registry.
	/// Use this overload when you have registered custom step types.
	/// </summary>
	public static Orchestration ParseOrchestration(string json, Mcp[] availableMcps, StepTypeParserRegistry parserRegistry)
	{
		var options = CreateOptions(parserRegistry);
		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, options)
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
	/// Parses orchestration file using a custom step type parser registry.
	/// </summary>
	public static Orchestration ParseOrchestrationFile(string path, Mcp[] availableMcps, StepTypeParserRegistry parserRegistry)
	{
		var json = File.ReadAllText(path);
		return ParseOrchestration(json, availableMcps, parserRegistry);
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
			if (step is PromptOrchestrationStep promptStep)
			{
				// Resolve MCPs for the step itself
				if (promptStep.McpNames.Length > 0)
				{
					promptStep.Mcps = ResolveMcpNames(promptStep.McpNames, step.Name, lookup);
				}

				// Resolve MCPs for each subagent
				foreach (var subagent in promptStep.Subagents)
				{
					if (subagent.McpNames.Length > 0)
					{
						subagent.Mcps = ResolveMcpNames(
							subagent.McpNames,
							$"{step.Name}/subagent:{subagent.Name}",
							lookup);
					}
				}
			}
		}
	}

	private static Mcp[] ResolveMcpNames(string[] mcpNames, string context, Dictionary<string, Mcp> lookup)
	{
		var resolved = new Mcp[mcpNames.Length];
		for (var i = 0; i < mcpNames.Length; i++)
		{
			var name = mcpNames[i];
			if (!lookup.TryGetValue(name, out var mcp))
				throw new InvalidOperationException(
					$"MCP '{name}' referenced by '{context}' is not defined in MCP configuration.");
			resolved[i] = mcp;
		}
		return resolved;
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
					WorkingDirectory = root.TryGetProperty("workingDirectory", out var wd) ? wd.GetString() : null,
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
		private readonly StepTypeParserRegistry _parserRegistry;

		public OrchestrationStepConverter(StepTypeParserRegistry parserRegistry)
		{
			_parserRegistry = parserRegistry;
		}

		public override OrchestrationStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			using var doc = JsonDocument.ParseValue(ref reader);
			var root = doc.RootElement;

			var typeName = root.TryGetProperty("type", out var typeProp)
				? typeProp.GetString()!
				: throw new JsonException("Missing 'type' property on orchestration step.");

			var step = _parserRegistry.TryParse(typeName, root);
			if (step is not null)
				return step;

			throw new JsonException($"Unknown orchestration step type: '{typeName}'. No parser registered for this type.");
		}

		public override void Write(Utf8JsonWriter writer, OrchestrationStep value, JsonSerializerOptions options)
		{
			JsonSerializer.Serialize(writer, value, value.GetType(), options);
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
			Response = root.TryGetProperty("response", out var responseProp)
				? ParseWebhookResponseConfig(responseProp)
				: null,
		},
			TriggerType.Email => new EmailTriggerConfig
			{
				Type = TriggerType.Email,
				Enabled = enabled,
				InputHandlerPrompt = inputHandlerPrompt,
				FolderPath = root.TryGetProperty("folderPath", out var folderPath) ? folderPath.GetString() ?? "Inbox" : "Inbox",
				PollIntervalSeconds = root.TryGetProperty("pollIntervalSeconds", out var pollInterval) ? pollInterval.GetInt32() : 60,
				MaxItemsPerPoll = root.TryGetProperty("maxItemsPerPoll", out var maxItems) ? maxItems.GetInt32() : 10,
				SubjectContains = root.TryGetProperty("subjectContains", out var subjectContains) ? subjectContains.GetString() : null,
				SenderContains = root.TryGetProperty("senderContains", out var senderContains) ? senderContains.GetString() : null,
			},
			_ => throw new JsonException($"Unknown trigger type: '{type}'."),
			};
		}

		private static WebhookResponseConfig ParseWebhookResponseConfig(JsonElement element)
		{
			return new WebhookResponseConfig
			{
				WaitForResult = element.TryGetProperty("waitForResult", out var wfr) && wfr.GetBoolean(),
				ResponseTemplate = element.TryGetProperty("responseTemplate", out var rt) ? rt.GetString() : null,
				TimeoutSeconds = element.TryGetProperty("timeoutSeconds", out var ts) ? ts.GetInt32() : 120,
			};
		}

		public override void Write(Utf8JsonWriter writer, TriggerConfig value, JsonSerializerOptions options)
		{
			JsonSerializer.Serialize(writer, value, value.GetType(), options);
		}
	}
}
