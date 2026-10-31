using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Parser for the <c>Approval</c> step type. Handles deserialization of
/// <see cref="ApprovalOrchestrationStep"/> from orchestration JSON / YAML.
/// </summary>
public sealed class ApprovalStepTypeParser : IStepTypeParser
{
	public string TypeName => "Approval";

	public OrchestrationStep Parse(JsonElement root, StepParseContext context)
	{
		var stepName = root.GetProperty("name").GetString()!;

		var prompt = root.TryGetProperty("prompt", out var promptProp)
			? promptProp.GetString()!
			: throw new JsonException(
				$"Approval step '{stepName}' is missing required property 'prompt'.");

		var choices = root.TryGetProperty("choices", out var choicesProp) && choicesProp.ValueKind == JsonValueKind.Array
			? choicesProp.EnumerateArray().Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
			: [];

		var onTimeout = root.TryGetProperty("onTimeout", out var ot) && ot.ValueKind == JsonValueKind.String
			? ParseOnTimeout(ot.GetString()!, stepName)
			: ApprovalTimeoutBehavior.Fail;

		var defaultResponse = root.TryGetProperty("defaultResponse", out var dr) && dr.ValueKind == JsonValueKind.String
			? dr.GetString()
			: null;

		// Validate: defaultResponse onTimeout requires a defaultResponse value.
		if (onTimeout == ApprovalTimeoutBehavior.DefaultResponse && string.IsNullOrEmpty(defaultResponse))
		{
			throw new JsonException(
				$"Approval step '{stepName}' has onTimeout=defaultResponse but no 'defaultResponse' value supplied.");
		}

		var baseFields = StepBaseFields.Parse(root);

		return new ApprovalOrchestrationStep
		{
			Name = baseFields.Name,
			Type = OrchestrationStepType.Approval,
			DependsOn = baseFields.DependsOn,
			Enabled = baseFields.Enabled,
			When = baseFields.When,
			TimeoutSeconds = baseFields.TimeoutSeconds,
			Retry = baseFields.Retry,
			Parameters = StepBaseFields.ParseParameterNames(root),
			Prompt = prompt,
			Choices = choices,
			PreferChoice = root.TryGetProperty("preferChoice", out var preferChoice) && preferChoice.GetBoolean(),
			OnTimeout = onTimeout,
			DefaultResponse = defaultResponse,
		};
	}

	private static ApprovalTimeoutBehavior ParseOnTimeout(string value, string stepName)
	{
		return value.Trim().ToLowerInvariant() switch
		{
			"fail" => ApprovalTimeoutBehavior.Fail,
			"defaultresponse" or "default_response" or "default-response" => ApprovalTimeoutBehavior.DefaultResponse,
			"cancel" => ApprovalTimeoutBehavior.Cancel,
			_ => throw new JsonException(
				$"Approval step '{stepName}' has unknown onTimeout '{value}'. Expected 'fail', 'defaultResponse', or 'cancel'."),
		};
	}
}
