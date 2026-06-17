using System.Text.Json;
using Microsoft.Extensions.AI;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// Exposes an Orchestra <see cref="IEngineTool"/> to OpenCode as an
/// <see cref="AIFunction"/> (used to build an MCP tool the OpenCode server can call back into).
/// The tool DEFINITION (name / description / JSON schema) is fixed at construction; at invoke
/// time the call is dispatched to the engine tool of the same name in the worker's currently
/// leased step (<see cref="EngineToolContextHolder.Current"/>), preserving per-step semantics
/// and the engine tool's hand-authored schema. Mirrors <c>Orchestra.Copilot.EngineToolAIFunction</c>.
/// </summary>
internal sealed class OpenCodeEngineToolFunction : AIFunction
{
	private readonly IEngineTool _definition;
	private readonly EngineToolContextHolder _holder;
	private readonly JsonElement _schema;

	public OpenCodeEngineToolFunction(IEngineTool definition, EngineToolContextHolder holder)
	{
		_definition = definition;
		_holder = holder;
		using var doc = JsonDocument.Parse(definition.ParametersSchema);
		_schema = doc.RootElement.Clone();
	}

	public override string Name => _definition.Name;
	public override string Description => _definition.Description;
	public override JsonElement JsonSchema => _schema;

	protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var binding = _holder.Current;
		if (binding is null)
			return new ValueTask<object?>($"Engine tool '{_definition.Name}' is unavailable: no active orchestration step.");

		var tool = binding.Tools.FirstOrDefault(t => string.Equals(t.Name, _definition.Name, StringComparison.OrdinalIgnoreCase));
		if (tool is null)
			return new ValueTask<object?>($"Engine tool '{_definition.Name}' is not enabled for this step.");

		var argsJson = JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value));
		var result = tool.Execute(argsJson, binding.Context);
		return new ValueTask<object?>(result);
	}
}
