namespace Orchestra.Engine;

/// <summary>
/// Supported types for orchestration input parameters.
/// </summary>
public enum InputType
{
	String,
	Boolean,
	Number,
}

/// <summary>
/// Defines a single typed input parameter for an orchestration.
/// Used in the <c>"inputs"</c> section of the orchestration JSON to provide
/// a strongly-typed schema for parameters, enabling validation, documentation,
/// and MCP tool schema generation.
/// </summary>
/// <remarks>
/// When <c>inputs</c> is defined on the orchestration, it becomes the authoritative
/// source of truth for parameter definitions. Step-level <c>parameters</c> arrays
/// still declare which inputs each step needs, but the schema (types, defaults,
/// descriptions, enum values) comes from the orchestration-level <c>inputs</c>.
/// </remarks>
public class InputDefinition
{
	/// <summary>
	/// The data type of this input. Defaults to <see cref="InputType.String"/>.
	/// </summary>
	public InputType Type { get; init; } = InputType.String;

	/// <summary>
	/// Human-readable description of this input.
	/// Used for documentation and as the tool parameter description in MCP.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Whether this input is required. Defaults to true.
	/// When true, the orchestration will fail validation if the input is not provided.
	/// When false, the input is optional and will use <see cref="Default"/> if not provided.
	/// </summary>
	public bool Required { get; init; } = true;

	/// <summary>
	/// Default value for optional inputs (when <see cref="Required"/> is false).
	/// The value is always stored as a string and converted to the appropriate type
	/// based on <see cref="Type"/> during validation.
	/// Ignored when <see cref="Required"/> is true.
	/// </summary>
	public string? Default { get; init; }

	/// <summary>
	/// Allowed values for this input. When non-empty, the input value must be
	/// one of these values. Useful for constraining inputs to a known set of options.
	/// </summary>
	public string[] Enum { get; init; } = [];

	/// <summary>
	/// UI hint indicating this input benefits from a multiline text area
	/// rather than a single-line input. Only meaningful for <see cref="InputType.String"/> inputs.
	/// Defaults to false. Has no effect on validation or execution — purely a display hint.
	/// </summary>
	public bool Multiline { get; init; }
}
