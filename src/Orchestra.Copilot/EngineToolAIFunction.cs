using System.Text.Json;
using Microsoft.Extensions.AI;
using Orchestra.Engine;

namespace Orchestra.Copilot;

/// <summary>
/// Wraps an <see cref="IEngineTool"/> as an <see cref="AIFunction"/> for use
/// with the Copilot SDK's <see cref="GitHub.Copilot.SessionConfig.Tools"/>.
/// </summary>
internal sealed class EngineToolAIFunction : AIFunction
{
	// SDK 1.0.0 routes the "this tool is host-trusted; skip the per-call permission
	// prompt" signal through the function's AdditionalProperties dictionary under
	// the key "skip_permission" (this is the same mechanism CopilotTool.DefineTool
	// uses internally when CopilotToolOptions.SkipPermission is set; see the SDK
	// 1.0.0 README's "If you want to use AIFunctionFactory.Create directly" section).
	// We pre-build it once and expose it as a fixed read-only view so every engine
	// tool instance shares the same opt-in without per-call allocations.
	private static readonly IReadOnlyDictionary<string, object?> s_skipPermissionProps =
		new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["skip_permission"] = true,
		};

	private readonly IEngineTool _tool;
	private readonly EngineToolContext _context;
	private readonly JsonElement _jsonSchema;

	public EngineToolAIFunction(IEngineTool tool, EngineToolContext context)
	{
		_tool = tool;
		_context = context;

		// Parse and clone the schema so the intermediate JsonDocument can be disposed.
		// Without Clone(), the JsonElement holds a reference to the document's pooled memory.
		using var doc = JsonDocument.Parse(tool.ParametersSchema);
		_jsonSchema = doc.RootElement.Clone();
	}

	public override string Name => _tool.Name;

	public override string Description => _tool.Description;

	public override JsonElement JsonSchema => _jsonSchema;

	/// <summary>
	/// Tells the Copilot SDK 1.0.0 runtime to bypass the per-call permission prompt for
	/// this tool. Engine tools are trusted host functions (they only mutate the engine's
	/// own state) so a permission gate would be a UX paper-cut without any actual safety
	/// benefit.
	/// </summary>
	public override IReadOnlyDictionary<string, object?> AdditionalProperties => s_skipPermissionProps;

	protected override ValueTask<object?> InvokeCoreAsync(
		AIFunctionArguments arguments,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Serialize arguments back to JSON string for the engine tool
		var argsJson = JsonSerializer.Serialize(
			arguments.ToDictionary(kv => kv.Key, kv => kv.Value));

		var result = _tool.Execute(argsJson, _context);
		return new ValueTask<object?>(result);
	}
}
