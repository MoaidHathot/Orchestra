using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Interface for parsing a custom step type from JSON.
/// Implement this interface to add support for deserializing new step types
/// from orchestration definition files.
/// </summary>
public interface IStepTypeParser
{
	/// <summary>
	/// The step type string this parser handles (case-insensitive match against the "type" property).
	/// </summary>
	string TypeName { get; }

	/// <summary>
	/// Parses the JSON element into a concrete <see cref="OrchestrationStep"/>.
	/// </summary>
	/// <param name="root">The JSON element representing the step.</param>
	/// <param name="context">Parsing context providing base directory for resolving file references.</param>
	OrchestrationStep Parse(JsonElement root, StepParseContext context);
}
