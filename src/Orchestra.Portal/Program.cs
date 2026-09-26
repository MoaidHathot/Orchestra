namespace Orchestra.Portal;

/// <summary>
/// Entry point for running the portal host directly (<c>dotnet run</c> in this project) and the
/// type <c>WebApplicationFactory&lt;Program&gt;</c> binds to in the portal integration tests.
/// Declared in the <c>Orchestra.Portal</c> namespace rather than as top-level statements so it
/// cannot collide with <c>Orchestra.Server</c>'s global <c>Program</c> when a test project
/// references both hosts.
/// </summary>
public class Program
{
	public static Task Main(string[] args) => PortalApp.RunAsync(args, typeof(Program));
}
