namespace Orchestra.Engine;

public class RemoteMcp : Mcp
{
	public required string Endpoint { get; init; }
	public required Dictionary<string, string> Headers { get; init; } = [];
	public EntraMcpAuth? Auth { get; init; }
	public string[]? AllowedTools { get; init; }

	[System.Text.Json.Serialization.JsonIgnore]
	public bool RequiresHost => Auth is not null || AllowedTools is not null;

	public void EnsureAgentReady()
	{
		if (RequiresHost) throw new InvalidOperationException($"Managed MCP '{Name}' must be resolved by the Orchestra host before creating an agent session.");
	}
}
