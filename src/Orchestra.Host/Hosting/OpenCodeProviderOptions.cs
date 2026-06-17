namespace Orchestra.Host.Hosting;

/// <summary>
/// OpenCode-provider-specific runtime options exposed in <c>orchestra.json</c>. The host
/// project is provider-neutral and does not depend on <c>Orchestra.OpenCode</c>; the
/// composition root (<c>Orchestra.Server</c> / <c>Orchestra.Exec</c>) maps these values onto
/// <c>OpenCodeAgentPoolOptions</c> at <c>OpenCodeAgentBuilder</c> construction time. Defaults
/// mirror those of <c>OpenCodeAgentPoolOptions</c> so leaving the section empty produces the
/// same behaviour as code-only construction.
/// </summary>
public sealed class OpenCodeProviderOptions
{
	/// <summary>
	/// Explicit path to the <c>opencode</c> binary. When null, the adapter resolves
	/// <c>ORCHESTRA_OPENCODE_PATH</c> then <c>opencode</c> on PATH. The OpenCode provider always
	/// spawns its own server (there is no connect-only mode).
	/// </summary>
	public string? CliPath { get; set; }

	/// <summary>Hostname the spawned server binds to. Default <c>127.0.0.1</c>.</summary>
	public string? Hostname { get; set; }

	/// <summary>Optional HTTP basic-auth password (OpenCode <c>OPENCODE_SERVER_PASSWORD</c>). Supports <c>${ENV}</c>.</summary>
	public string? ServerPassword { get; set; }

	/// <summary>Basic-auth username paired with <see cref="ServerPassword"/>. Default <c>opencode</c>.</summary>
	public string? ServerUsername { get; set; }

	/// <summary>
	/// Provider applied to bare model ids (no <c>provider/</c> prefix) when a step is routed to
	/// OpenCode. Default <c>github-copilot</c>, so <c>claude-opus-4.8</c> resolves to
	/// <c>github-copilot/claude-opus-4.8</c>.
	/// </summary>
	public string? FallbackProvider { get; set; }

	/// <summary>Seconds to wait for a spawned server's health endpoint. Default 60.</summary>
	public int? StartupTimeoutSeconds { get; set; }

	/// <summary>
	/// When false, OpenCode steps run without the Orchestra engine-tool MCP bridge
	/// (orchestra_set_status / complete / save_file / read_file / request_user_input). Default true.
	/// </summary>
	public bool? EngineToolBridgeEnabled { get; set; }
}
