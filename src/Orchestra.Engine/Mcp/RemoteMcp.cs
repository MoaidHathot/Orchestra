namespace Orchestra.Engine;

public class RemoteMcp : Mcp
{
	public required string Endpoint { get; init; }
	public required Dictionary<string, string> Headers { get; init; } = [];
}
