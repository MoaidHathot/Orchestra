using Orchestra.Engine;
using Orchestra.Host.Hosting;

namespace Orchestra.Cli.Validation;

/// <summary>
/// The global MCP definitions (<c>orchestra.mcp.json</c> beside the effective <c>orchestra.json</c>)
/// for host-less commands such as <c>validate</c> and <c>run --dry-run</c>. The parser rejects a
/// step that references an MCP it cannot see, so without this catalog every orchestration that
/// uses a globally declared MCP would fail validation with "not defined in MCP configuration"
/// even though the host accepts it. Resolution follows the host's own sidecar rule
/// (<see cref="OrchestraConfigLoader.ResolveSidecar"/>).
/// </summary>
internal static class GlobalMcpCatalog
{
	/// <summary>
	/// Loads the global MCP definitions. Returns an empty array when no sidecar exists. When the
	/// sidecar exists but cannot be parsed, <paramref name="error"/> describes the problem and
	/// the array is empty so callers can report it instead of a misleading downstream error.
	/// </summary>
	public static Mcp[] Load(out string? error)
	{
		error = null;

		string? path;
		try
		{
			path = OrchestraConfigLoader.ResolveSidecar(OrchestraConfigLoader.McpConfigFileName).EffectivePath;
		}
		catch (Exception ex)
		{
			error = $"could not resolve {OrchestraConfigLoader.McpConfigFileName}: {ex.Message}";
			return [];
		}

		if (path is null)
			return [];

		try
		{
			return OrchestrationParser.ParseMcpFile(path);
		}
		catch (Exception ex)
		{
			error = $"could not load {path}: {ex.Message}";
			return [];
		}
	}
}
