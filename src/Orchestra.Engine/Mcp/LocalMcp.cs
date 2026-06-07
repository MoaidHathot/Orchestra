namespace Orchestra.Engine;

public class LocalMcp : Mcp
{
	public required string Command { get; init; }
	public required string[] Arguments { get; init; }
	public string? WorkingDirectory { get; init; }

	/// <summary>
	/// Optional environment variables passed to the spawned MCP stdio process. The map is
	/// merged on top of the inherited process environment by the underlying transport
	/// (the Copilot SDK's <c>McpStdioServerConfig.Env</c> for per-session inline MCPs,
	/// or the MCP proxy's stdio launcher for global MCPs). Common use cases:
	/// <list type="bullet">
	///   <item>API-key injection (e.g., <c>OPENAI_API_KEY = "{{env.OPENAI_API_KEY}}"</c>
	///   reading from the host's environment via Orchestra's <c>env.*</c> template).</item>
	///   <item>Per-orchestration overrides such as <c>NODE_ENV = "production"</c>.</item>
	///   <item>Feature flags that target a specific MCP server invocation without
	///   contaminating the host environment.</item>
	/// </list>
	/// </summary>
	/// <remarks>
	/// Values may contain Orchestra template expressions (<c>{{env.X}}</c>,
	/// <c>{{param.foo}}</c>, <c>{{step.output}}</c>) and are resolved by the executor at
	/// step-launch time before the dictionary is forwarded to the underlying transport.
	/// The dictionary is null when no environment override is configured (matches the
	/// behaviour of an empty JSON object), so consumers should null-check before iterating.
	/// </remarks>
	public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
