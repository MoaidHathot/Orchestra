using System.Text.Json;
using Microsoft.Extensions.AI;
using Orchestra.Engine;

namespace Orchestra.Copilot;

/// <summary>
/// Wraps an <see cref="IEngineTool"/> as an <see cref="AIFunction"/> for use
/// with the Copilot SDK's <see cref="GitHub.Copilot.SDK.SessionConfig.Tools"/>.
/// </summary>
internal sealed class EngineToolAIFunction : AIFunction
{
	private readonly IEngineTool _tool;
	private readonly EngineToolContext _context;
	private readonly JsonElement _jsonSchema;

	public EngineToolAIFunction(IEngineTool tool, EngineToolContext context)
	{
		_tool = tool;
		_context = context;
		_jsonSchema = JsonDocument.Parse(tool.ParametersSchema).RootElement;
	}

	public override string Name => _tool.Name;

	public override string Description => _tool.Description;

	public override JsonElement JsonSchema => _jsonSchema;

	protected override ValueTask<object?> InvokeCoreAsync(
		AIFunctionArguments arguments,
		CancellationToken cancellationToken)
	{
		// Serialize arguments back to JSON string for the engine tool
		var argsJson = JsonSerializer.Serialize(
			arguments.ToDictionary(kv => kv.Key, kv => kv.Value));

		var result = _tool.Execute(argsJson, _context);
		return new ValueTask<object?>(result);
	}
}
