using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

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
		.Register(new CommandStepTypeParser())
		.Register(new ScriptStepTypeParser())
		.Register(new OrchestrationStepTypeParser());

	private static readonly StepParseContext s_defaultContext = new(BaseDirectory: null);

	private static readonly JsonSerializerOptions s_options = CreateOptions(s_defaultParserRegistry, s_defaultContext);

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
			.Register(new CommandStepTypeParser())
			.Register(new ScriptStepTypeParser())
			.Register(new OrchestrationStepTypeParser());
	}

	private static JsonSerializerOptions CreateOptions(StepTypeParserRegistry parserRegistry, StepParseContext context)
	{
		return new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			Converters =
			{
				new OrchestrationStepConverter(parserRegistry, context),
				new McpConverter(),
				new TriggerConfigConverter(),
				new HookEventTypeJsonConverter(),
				new HookStepSelectionJsonConverter(),
				new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
			},
		};
	}

	public static Orchestration ParseOrchestration(string json, Mcp[] availableMcps)
	{
		var variables = ExtractVariables(json);
		var context = new StepParseContext(BaseDirectory: null, Variables: variables);
		var options = CreateOptions(s_defaultParserRegistry, context);

		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");

		HookDefinitionResolver.ApplyBaseDirectory(orchestration.Hooks, baseDirectory: null);
		ResolveStepMcps(orchestration, availableMcps);
		return orchestration;
	}

	/// <summary>
	/// Parses orchestration JSON using a custom step type parser registry.
	/// Use this overload when you have registered custom step types.
	/// </summary>
	public static Orchestration ParseOrchestration(string json, Mcp[] availableMcps, StepTypeParserRegistry parserRegistry)
	{
		var variables = ExtractVariables(json);
		var context = new StepParseContext(BaseDirectory: null, Variables: variables);
		var options = CreateOptions(parserRegistry, context);

		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");

		HookDefinitionResolver.ApplyBaseDirectory(orchestration.Hooks, baseDirectory: null);
		ResolveStepMcps(orchestration, availableMcps);
		return orchestration;
	}

	public static Orchestration ParseOrchestrationFile(string path, Mcp[] availableMcps)
	{
		var json = ReadAsJson(path);
		var variables = ExtractVariables(json);
		var context = new StepParseContext(
			BaseDirectory: Path.GetDirectoryName(Path.GetFullPath(path)),
			Variables: variables);
		var options = CreateOptions(s_defaultParserRegistry, context);

		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");

		HookDefinitionResolver.ApplyBaseDirectory(orchestration.Hooks, Path.GetDirectoryName(Path.GetFullPath(path)));
		ResolveStepMcps(orchestration, availableMcps);
		return orchestration;
	}

	/// <summary>
	/// Parses orchestration file using a custom step type parser registry.
	/// </summary>
	public static Orchestration ParseOrchestrationFile(string path, Mcp[] availableMcps, StepTypeParserRegistry parserRegistry)
	{
		var json = ReadAsJson(path);
		var variables = ExtractVariables(json);
		var context = new StepParseContext(
			BaseDirectory: Path.GetDirectoryName(Path.GetFullPath(path)),
			Variables: variables);
		var options = CreateOptions(parserRegistry, context);

		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");

		HookDefinitionResolver.ApplyBaseDirectory(orchestration.Hooks, Path.GetDirectoryName(Path.GetFullPath(path)));
		ResolveStepMcps(orchestration, availableMcps);
		return orchestration;
	}

	/// <summary>
	/// Parses orchestration structure without resolving MCP references.
	/// Useful for metadata extraction (e.g., folder scan) where MCP configs are unavailable.
	/// </summary>
	public static Orchestration ParseOrchestrationMetadataOnly(string json)
	{
		var context = new StepParseContext(BaseDirectory: null, MetadataOnly: true);
		var options = CreateOptions(s_defaultParserRegistry, context);

		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");

		HookDefinitionResolver.ApplyBaseDirectory(orchestration.Hooks, baseDirectory: null);
		return orchestration;
	}

	/// <summary>
	/// Parses orchestration structure from file without resolving MCP references.
	/// </summary>
	public static Orchestration ParseOrchestrationFileMetadataOnly(string path)
	{
		var json = ReadAsJson(path);
		var context = new StepParseContext(BaseDirectory: Path.GetDirectoryName(Path.GetFullPath(path)), MetadataOnly: true);
		var options = CreateOptions(s_defaultParserRegistry, context);

		var orchestration = JsonSerializer.Deserialize<Orchestration>(json, options)
			?? throw new InvalidOperationException("Failed to deserialize orchestration JSON.");

		HookDefinitionResolver.ApplyBaseDirectory(orchestration.Hooks, Path.GetDirectoryName(Path.GetFullPath(path)));
		return orchestration;
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
		// Merge global MCPs with inline MCPs from the orchestration.
		// Inline MCPs take priority on name conflicts (override global definitions).
		// This allows orchestrations to shadow a global MCP with a custom inline version.
		var lookup = new Dictionary<string, Mcp>(StringComparer.OrdinalIgnoreCase);
		foreach (var mcp in availableMcps)
			lookup[mcp.Name] = mcp; // global MCPs first
		foreach (var mcp in orchestration.Mcps)
			lookup[mcp.Name] = mcp; // inline overrides global

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

			// Optional per-server timeout (seconds). Allows YAML authors to configure long
			// timeouts for MCP servers that host long-running tools (e.g., orchestra MCP's
			// invoke_orchestration in sync mode).
			TimeSpan? timeout = null;
			if (root.TryGetProperty("timeoutSeconds", out var ts) && ts.ValueKind == JsonValueKind.Number)
			{
				var seconds = ts.GetDouble();
				if (seconds > 0) timeout = TimeSpan.FromSeconds(seconds);
			}

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
					Timeout = timeout,
				},
				McpType.Remote => new RemoteMcp
				{
					Name = name,
					Type = McpType.Remote,
					Endpoint = root.GetProperty("endpoint").GetString()!,
					Headers = root.TryGetProperty("headers", out var headers)
						? headers.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
						: [],
					Timeout = timeout,
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
		private readonly StepParseContext _context;

		public OrchestrationStepConverter(StepTypeParserRegistry parserRegistry, StepParseContext context)
		{
			_parserRegistry = parserRegistry;
			_context = context;
		}

		public override OrchestrationStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			using var doc = JsonDocument.ParseValue(ref reader);
			var root = doc.RootElement;

			var typeName = root.TryGetProperty("type", out var typeProp)
				? typeProp.GetString()!
				: throw new JsonException("Missing 'type' property on orchestration step.");

			var step = _parserRegistry.TryParse(typeName, root, _context);
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

			var inputHandlerModel = root.TryGetProperty("inputHandlerModel", out var ihmProp)
				? ihmProp.GetString()
				: null;

			return type switch
			{
				TriggerType.Scheduler => new SchedulerTriggerConfig
				{
					Type = TriggerType.Scheduler,
					Enabled = enabled,
					InputHandlerPrompt = inputHandlerPrompt,
					InputHandlerModel = inputHandlerModel,
					Cron = root.TryGetProperty("cron", out var cron) ? cron.GetString() : null,
					IntervalSeconds = root.TryGetProperty("intervalSeconds", out var interval) ? interval.GetInt32() : null,
					MaxRuns = root.TryGetProperty("maxRuns", out var maxRuns) ? maxRuns.GetInt32() : null,
				},
				TriggerType.Loop => new LoopTriggerConfig
				{
					Type = TriggerType.Loop,
					Enabled = enabled,
					InputHandlerPrompt = inputHandlerPrompt,
					InputHandlerModel = inputHandlerModel,
					DelaySeconds = root.TryGetProperty("delaySeconds", out var delay) ? delay.GetInt32() : 0,
					MaxIterations = root.TryGetProperty("maxIterations", out var maxIter) ? maxIter.GetInt32() : null,
					ContinueOnFailure = root.TryGetProperty("continueOnFailure", out var cof) && cof.GetBoolean(),
				},
		TriggerType.Webhook => new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = enabled,
			InputHandlerPrompt = inputHandlerPrompt,
			InputHandlerModel = inputHandlerModel,
			Secret = root.TryGetProperty("secret", out var secret) ? secret.GetString() : null,
			MaxConcurrent = root.TryGetProperty("maxConcurrent", out var maxConc) ? maxConc.GetInt32() : 1,
			Response = root.TryGetProperty("response", out var responseProp)
				? ParseWebhookResponseConfig(responseProp)
				: null,
		},
			TriggerType.Manual => new ManualTriggerConfig
			{
				Type = TriggerType.Manual,
				Enabled = enabled,
				InputHandlerPrompt = inputHandlerPrompt,
				InputHandlerModel = inputHandlerModel,
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

	// ── YAML Support ──

	/// <summary>
	/// File extensions recognized as YAML orchestration files.
	/// </summary>
	private static readonly string[] s_yamlExtensions = [".yaml", ".yml"];

	/// <summary>
	/// The set of file extensions recognized as orchestration files (JSON and YAML).
	/// </summary>
	public static readonly string[] OrchestrationFileExtensions = [".json", ".yaml", ".yml"];

	/// <summary>
	/// Returns true if the file extension indicates a YAML file.
	/// </summary>
	public static bool IsYamlFile(string path)
	{
		var ext = Path.GetExtension(path);
		return s_yamlExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Reads an orchestration file and returns its content as a JSON string.
	/// YAML files (.yaml, .yml) are converted to JSON; JSON files are returned as-is.
	/// </summary>
	private static string ReadAsJson(string path)
	{
		var content = File.ReadAllText(path);
		return IsYamlFile(path) ? ConvertYamlToJson(content) : content;
	}

	/// <summary>
	/// Converts a YAML string to a JSON string.
	/// Uses YamlDotNet's JSON-compatible serializer to ensure output is valid JSON
	/// that can be parsed by System.Text.Json.
	/// </summary>
	public static string ConvertYamlToJson(string yaml)
	{
		var deserializer = new DeserializerBuilder()
			.WithAttemptingUnquotedStringTypeDeserialization()
			.Build();
		var yamlObject = deserializer.Deserialize(new StringReader(yaml));

		if (yamlObject is null)
			throw new InvalidOperationException("YAML content is empty or null.");

		var serializer = new SerializerBuilder()
			.JsonCompatible()
			.Build();

		return serializer.Serialize(yamlObject);
	}

	/// <summary>
	/// Gets all orchestration files (JSON and YAML) from a directory.
	/// </summary>
	public static string[] GetOrchestrationFiles(string directory, SearchOption searchOption = SearchOption.TopDirectoryOnly)
	{
		return Directory.GetFiles(directory, "*.*", searchOption)
			.Where(f => OrchestrationFileExtensions.Any(ext =>
				f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
			.ToArray();
	}

	/// <summary>
	/// Pre-extracts the <c>variables</c> dictionary from the raw orchestration JSON.
	/// This allows <c>{{vars.*}}</c> expressions in file paths (e.g., <c>systemPromptFile</c>)
	/// to be resolved at parse time, before the full deserialization pass runs.
	/// Returns null when no variables are defined.
	/// </summary>
	private static IReadOnlyDictionary<string, string>? ExtractVariables(string json)
	{
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("variables", out var vars) || vars.ValueKind != JsonValueKind.Object)
				return null;

			var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var prop in vars.EnumerateObject())
			{
				if (prop.Value.ValueKind == JsonValueKind.String)
					dict[prop.Name] = prop.Value.GetString()!;
			}

			return dict.Count > 0 ? dict : null;
		}
		catch
		{
			// If we cannot pre-parse the JSON for variables, proceed without them.
			// The main deserialization pass will surface any real JSON errors.
			return null;
		}
	}
}
