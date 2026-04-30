using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for <c>Orchestration</c> step type JSON.
/// Handles deserialization of <see cref="OrchestrationInvocationStep"/>.
/// </summary>
public sealed class OrchestrationStepTypeParser : IStepTypeParser
{
	public string TypeName => "Orchestration";

	public OrchestrationStep Parse(JsonElement root, StepParseContext context)
	{
		// Validate orchestration field
		if (!root.TryGetProperty("orchestration", out var orchProp)
			|| orchProp.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace(orchProp.GetString()))
		{
			throw new JsonException("Orchestration step requires a non-empty 'orchestration' property naming the child orchestration.");
		}

		// Parse mode (default: sync)
		var mode = OrchestrationInvocationMode.Sync;
		if (root.TryGetProperty("mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String)
		{
			var modeStr = modeProp.GetString()!;
			if (string.Equals(modeStr, "sync", StringComparison.OrdinalIgnoreCase))
				mode = OrchestrationInvocationMode.Sync;
			else if (string.Equals(modeStr, "async", StringComparison.OrdinalIgnoreCase))
				mode = OrchestrationInvocationMode.Async;
			else
				throw new JsonException($"Orchestration step 'mode' must be 'sync' or 'async' (got '{modeStr}').");
		}

		// Parameters: object with string values
		var childParameters = new Dictionary<string, string>(StringComparer.Ordinal);
		if (root.TryGetProperty("parameters", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object)
		{
			foreach (var prop in paramsProp.EnumerateObject())
			{
				if (prop.Value.ValueKind == JsonValueKind.String)
				{
					childParameters[prop.Name] = prop.Value.GetString()!;
				}
				else
				{
					// Allow non-string values by serializing them; downstream the engine treats
					// child parameters as strings.
					childParameters[prop.Name] = prop.Value.GetRawText();
				}
			}
		}

		return new OrchestrationInvocationStep
		{
			Name = root.GetProperty("name").GetString()!,
			Type = OrchestrationStepType.Orchestration,
			DependsOn = root.TryGetProperty("dependsOn", out var deps)
				? deps.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
			Enabled = !root.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
			OrchestrationName = orchProp.GetString()!,
			ChildParameters = childParameters,
			Mode = mode,
			InputHandlerPrompt = root.TryGetProperty("inputHandlerPrompt", out var ihp)
				? ihp.GetString()
				: null,
			InputHandlerModel = root.TryGetProperty("inputHandlerModel", out var ihm)
				? ihm.GetString()
				: null,
			TimeoutSeconds = root.TryGetProperty("timeoutSeconds", out var ts)
				? ts.GetInt32()
				: null,
			Retry = root.TryGetProperty("retry", out var retry)
				? PromptStepTypeParser.DeserializeRetryPolicy(retry)
				: null,
			Parameters = root.TryGetProperty("paramRefs", out var paramRefs)
				? paramRefs.EnumerateArray().Select(e => e.GetString()!).ToArray()
				: [],
		};
	}
}
