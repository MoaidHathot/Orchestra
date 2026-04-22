using Orchestra.Playground.Copilot.Portal;

await PortalApp.RunAsync(args, typeof(Program));

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
