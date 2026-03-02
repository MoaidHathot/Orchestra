namespace Orchestra.Engine;

public class LocalMcp : Mcp
{
	public required string Command { get; init; }
	public required string[] Arguments { get; init; }
	public string? WorkingDirectory { get; init; }
}
