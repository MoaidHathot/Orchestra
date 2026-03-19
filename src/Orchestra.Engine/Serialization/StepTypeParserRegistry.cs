using System.Collections.Concurrent;
using System.Text.Json;

namespace Orchestra.Engine;

/// <summary>
/// Registry that maps step type names to their <see cref="IStepTypeParser"/> implementations.
/// Used by the <see cref="OrchestrationParser"/> to support extensible step type deserialization.
/// </summary>
public sealed class StepTypeParserRegistry
{
	private readonly ConcurrentDictionary<string, IStepTypeParser> _parsers = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Registers a parser for a step type.
	/// Overwrites any previously registered parser for the same type name.
	/// </summary>
	public StepTypeParserRegistry Register(IStepTypeParser parser)
	{
		_parsers[parser.TypeName] = parser;
		return this;
	}

	/// <summary>
	/// Attempts to parse the step type from JSON.
	/// Returns null if no parser is registered for the type.
	/// </summary>
	public OrchestrationStep? TryParse(string typeName, JsonElement root)
	{
		if (_parsers.TryGetValue(typeName, out var parser))
			return parser.Parse(root);

		return null;
	}

	/// <summary>
	/// Returns whether a parser is registered for the given type name.
	/// </summary>
	public bool IsRegistered(string typeName) => _parsers.ContainsKey(typeName);

	/// <summary>
	/// Returns all registered type names.
	/// </summary>
	public IReadOnlyCollection<string> RegisteredTypes => [.. _parsers.Keys];
}
