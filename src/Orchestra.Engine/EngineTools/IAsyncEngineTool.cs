namespace Orchestra.Engine;

/// <summary>An engine tool whose work can await I/O or human input without blocking a dispatch thread.</summary>
public interface IAsyncEngineTool : IEngineTool
{
	Task<string> ExecuteAsync(string arguments, EngineToolContext context, CancellationToken cancellationToken);
}
