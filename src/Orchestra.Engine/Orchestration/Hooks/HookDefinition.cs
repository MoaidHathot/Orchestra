using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestra.Engine;

[JsonConverter(typeof(HookEventTypeJsonConverter))]
public enum HookEventType
{
	OrchestrationSuccess,
	OrchestrationFailure,
	OrchestrationAfter,
	StepSuccess,
	StepFailure,
	StepAfter,
}

public enum HookFailurePolicy
{
	Ignore,
	Warn,
}

public enum HookPayloadDetail
{
	Compact,
	Standard,
	Full,
}

public enum HookStepSelector
{
	None,
	Current,
	Failed,
	NonSucceeded,
	Terminal,
	All,
}

public enum HookStepStatusFilter
{
	Any,
	Succeeded,
	Failed,
	Cancelled,
	Skipped,
	NoAction,
	NonSucceeded,
}

public enum HookStepMatch
{
	Any,
	All,
}

public enum HookActionType
{
	Script,
}

public class HookDefinition
{
	public string? Name { get; set; }

	public HookEventType On { get; set; }

	public HookWhenFilter? When { get; set; }

	public HookPayloadOptions Payload { get; set; } = new();

	public required HookAction Action { get; set; }

	public HookFailurePolicy FailurePolicy { get; set; } = HookFailurePolicy.Warn;

	internal HookSource Source { get; set; } = HookSource.Orchestration;
}

public class HookWhenFilter
{
	public HookStepCondition? Steps { get; set; }
}

public class HookStepCondition
{
	public string[] Names { get; set; } = [];

	public HookStepStatusFilter Status { get; set; } = HookStepStatusFilter.Any;

	public HookStepMatch Match { get; set; } = HookStepMatch.Any;
}

public class HookPayloadOptions
{
	public HookPayloadDetail Detail { get; set; } = HookPayloadDetail.Compact;

	public HookStepSelection? Steps { get; set; }

	public bool IncludeRefs { get; set; }
}

[JsonConverter(typeof(HookStepSelectionJsonConverter))]
public sealed class HookStepSelection
{
	public HookStepSelection(HookStepSelector selector)
	{
		Selector = selector;
	}

	public HookStepSelection(string[] names)
	{
		Names = names;
	}

	public HookStepSelector? Selector { get; }

	public string[]? Names { get; }
}

public class HookAction
{
	public HookActionType Type { get; set; } = HookActionType.Script;

	public string? Shell { get; set; }

	public string? Script { get; set; }

	public string? ScriptFile { get; set; }

	public string[] Arguments { get; set; } = [];

	public string? WorkingDirectory { get; set; }

	public Dictionary<string, string> Environment { get; set; } = [];

	public bool IncludeStdErr { get; set; }

	internal string? BaseDirectory { get; set; }
}

public static class HookDefinitionResolver
{
	public static void ApplyBaseDirectory(HookDefinition[] hooks, string? baseDirectory)
	{
		if (hooks.Length == 0 || string.IsNullOrWhiteSpace(baseDirectory))
			return;

		foreach (var hook in hooks)
		{
			if (hook.Action.ScriptFile is not null && !Path.IsPathRooted(hook.Action.ScriptFile))
			{
				hook.Action.ScriptFile = Path.GetFullPath(Path.Combine(baseDirectory, hook.Action.ScriptFile));
			}

			if (hook.Action.WorkingDirectory is not null && !Path.IsPathRooted(hook.Action.WorkingDirectory))
			{
				hook.Action.WorkingDirectory = Path.GetFullPath(Path.Combine(baseDirectory, hook.Action.WorkingDirectory));
			}

			hook.Action.BaseDirectory = baseDirectory;
		}
	}
}

public sealed class HookEventTypeJsonConverter : JsonConverter<HookEventType>
{
	public override HookEventType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		var value = reader.GetString();
		return value?.Trim().ToLowerInvariant() switch
		{
			"orchestration.success" => HookEventType.OrchestrationSuccess,
			"orchestration.failure" => HookEventType.OrchestrationFailure,
			"orchestration.after" => HookEventType.OrchestrationAfter,
			"step.success" => HookEventType.StepSuccess,
			"step.failure" => HookEventType.StepFailure,
			"step.after" => HookEventType.StepAfter,
			_ => throw new JsonException($"Unknown hook event '{value}'.")
		};
	}

	public override void Write(Utf8JsonWriter writer, HookEventType value, JsonSerializerOptions options)
	{
		writer.WriteStringValue(value switch
		{
			HookEventType.OrchestrationSuccess => "orchestration.success",
			HookEventType.OrchestrationFailure => "orchestration.failure",
			HookEventType.OrchestrationAfter => "orchestration.after",
			HookEventType.StepSuccess => "step.success",
			HookEventType.StepFailure => "step.failure",
			HookEventType.StepAfter => "step.after",
			_ => throw new JsonException($"Unknown hook event '{value}'.")
		});
	}
}

public sealed class HookStepSelectionJsonConverter : JsonConverter<HookStepSelection>
{
	public override HookStepSelection? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.String)
		{
			var selectorText = reader.GetString();
			if (string.IsNullOrWhiteSpace(selectorText))
				throw new JsonException("Hook payload step selector cannot be empty.");

			if (!Enum.TryParse<HookStepSelector>(selectorText, ignoreCase: true, out var selector))
				throw new JsonException($"Unknown hook step selector '{selectorText}'.");

			return new HookStepSelection(selector);
		}

		if (reader.TokenType == JsonTokenType.StartArray)
		{
			var names = JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [];
			return new HookStepSelection(names);
		}

		throw new JsonException("Hook payload 'steps' must be a selector string or an array of step names.");
	}

	public override void Write(Utf8JsonWriter writer, HookStepSelection value, JsonSerializerOptions options)
	{
		if (value.Selector is { } selector)
		{
			writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(selector.ToString()));
			return;
		}

		JsonSerializer.Serialize(writer, value.Names ?? [], options);
	}
}
