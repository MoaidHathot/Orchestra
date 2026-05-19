using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for <c>Orchestration</c> step type JSON.
/// Handles deserialization of <see cref="OrchestrationInvocationStep"/> in both
/// single-child mode and forEach fan-out mode.
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

		// forEach fan-out fields (optional, mutually validating)
		string? forEach = null;
		if (root.TryGetProperty("forEach", out var feProp) && feProp.ValueKind == JsonValueKind.String)
			forEach = feProp.GetString();

		string? forEachPath = null;
		if (root.TryGetProperty("forEachPath", out var fePathProp) && fePathProp.ValueKind == JsonValueKind.String)
			forEachPath = fePathProp.GetString();

		string? itemParameter = null;
		if (root.TryGetProperty("itemParameter", out var ipProp) && ipProp.ValueKind == JsonValueKind.String)
			itemParameter = ipProp.GetString();

		int? maxConcurrency = null;
		if (root.TryGetProperty("maxConcurrency", out var mcProp) && mcProp.ValueKind == JsonValueKind.Number)
			maxConcurrency = mcProp.GetInt32();

		var continueOnItemFailure = true;
		if (root.TryGetProperty("continueOnItemFailure", out var coifProp))
		{
			if (coifProp.ValueKind == JsonValueKind.True) continueOnItemFailure = true;
			else if (coifProp.ValueKind == JsonValueKind.False) continueOnItemFailure = false;
			else if (coifProp.ValueKind == JsonValueKind.String)
			{
				var s = coifProp.GetString();
				if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) continueOnItemFailure = true;
				else if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) continueOnItemFailure = false;
				else throw new JsonException($"Orchestration step 'continueOnItemFailure' must be a boolean (got '{s}').");
			}
		}

		// forEach requires itemParameter
		if (!string.IsNullOrWhiteSpace(forEach) && string.IsNullOrWhiteSpace(itemParameter))
		{
			throw new JsonException("Orchestration step with 'forEach' must also specify 'itemParameter' (the child parameter name that carries the raw per-item JSON).");
		}
		if (string.IsNullOrWhiteSpace(forEach) && !string.IsNullOrWhiteSpace(itemParameter))
		{
			throw new JsonException("Orchestration step has 'itemParameter' but no 'forEach'; remove 'itemParameter' or add 'forEach'.");
		}
		if (maxConcurrency is <= 0)
		{
			throw new JsonException($"Orchestration step 'maxConcurrency' must be a positive integer (got {maxConcurrency}).");
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
			ForEach = forEach,
			ForEachPath = forEachPath,
			ItemParameter = itemParameter,
			MaxConcurrency = maxConcurrency,
			ContinueOnItemFailure = continueOnItemFailure,
		};
	}
}
