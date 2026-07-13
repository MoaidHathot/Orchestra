using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Serializes tests that mutate the process-global <c>ORCHESTRA_CONFIG_PATH</c> /
/// <c>XDG_CONFIG_HOME</c> environment variables. Those variables drive config discovery, so
/// concurrent mutation across xUnit's parallel collections lets one test observe another test's
/// value — e.g. <see cref="StartExternalServicesTests"/> reading an empty global-MCP set while
/// <see cref="OrchestraConfigLoaderTests"/> has (transiently) cleared the path. Placing both in a
/// single collection with parallelization disabled makes them run serially and never alongside
/// any other collection, eliminating the race.
/// </summary>
[CollectionDefinition("HostConfigEnvironment", DisableParallelization = true)]
public sealed class HostConfigEnvironmentCollection
{
	public const string Name = "HostConfigEnvironment";
}
