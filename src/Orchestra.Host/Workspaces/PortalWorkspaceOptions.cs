namespace Orchestra.Host.Workspaces;

public sealed class PortalWorkspaceOptions
{
	public string AssetsDirectory { get; init; } = AppContext.BaseDirectory;
	public required string ConfigPath { get; init; }
	public string ConfigSource { get; init; } = "None";
	public Dictionary<string, string> Overrides { get; init; } = [];
}
