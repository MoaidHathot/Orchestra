namespace Orchestra.Engine;

public class Mcp
{
	public required string Name { get; init; }
	public required McpType Type { get; init; }

	/// <summary>
	/// Optional per-server timeout for tool calls. When set, the MCP client (e.g., the
	/// Copilot SDK's <c>McpServerConfig.Timeout</c>) is configured to use this value
	/// instead of its default. Use this for MCP servers that host long-running tools
	/// (e.g., the orchestra MCP server's <c>invoke_orchestration</c> tool in sync mode)
	/// to avoid premature transport-level timeouts.
	/// </summary>
	/// <remarks>
	/// At YAML/JSON parse time, <c>timeoutSeconds</c> can be supplied either as a numeric
	/// literal (in which case it lands directly in this property) or as a template-string
	/// expression (in which case it is captured in <see cref="TimeoutTemplate"/> and this
	/// property stays <c>null</c> until step execution resolves the template).
	/// </remarks>
	public TimeSpan? Timeout { get; init; }

	/// <summary>
	/// Optional template expression for the per-server timeout. Populated when the YAML/JSON
	/// <c>timeoutSeconds</c> field was supplied as a string (e.g. <c>"{{param.foo}}"</c> or
	/// <c>"{{validate-inputs.output.controllerMcpTimeoutSeconds}}"</c>) instead of a numeric
	/// literal. Resolved at step execution time by <see cref="TemplateResolver.ResolveStaticMcp"/>
	/// and parsed to an integer count of seconds; the resolved value materialises in
	/// <see cref="Timeout"/> on the cloned MCP instance returned by the resolver.
	/// <para>
	/// At parse time, only one of <see cref="Timeout"/> or <see cref="TimeoutTemplate"/> is
	/// populated (the YAML can supply either a number or a string, not both). Authors that
	/// need step-output-derived timeouts (for example, deriving a per-orchestration MCP
	/// transport budget from a <c>validate-inputs</c> Script step) use the template form.
	/// </para>
	/// </summary>
	public string? TimeoutTemplate { get; init; }
}
