using Xunit;

namespace Orchestra.Portal.Tests;

/// <summary>
/// Serializes every Portal test class that boots a host through
/// <c>PortalWebApplicationFactory</c>.
/// </summary>
/// <remarks>
/// The factory sets <c>ORCHESTRA_CONFIG_PATH</c>, <c>ASPNETCORE_URLS</c> and <c>DOTNET_URLS</c>
/// so the host under test reads a fixture config instead of the developer's real one, then
/// restores them. Those are process-wide, so two classes booting factories in parallel can
/// restore each other's values mid-test — the host then binds the wrong URL or loads the wrong
/// configuration, and the failure looks like a product bug rather than a harness race.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PortalHostEnvironmentCollection
{
	public const string Name = "portal-host-environment";
}
