using Xunit;

namespace Orchestra.Engine.Tests;

/// <summary>
/// Serializes test classes that mutate the process <c>PATH</c> / <c>PATHEXT</c> while probing
/// executable resolution.
/// </summary>
/// <remarks>
/// <see cref="Mcp.ExecutableResolverTests"/> and
/// <see cref="Mcp.InlineMcpCommandResolutionIntegrationTests"/> both prepend a temp directory to
/// <c>PATH</c> and restore it afterwards. Without a shared collection xUnit runs them in
/// parallel, so one class's restore can land between the other's set and assert — the resolver
/// then fails to find a shim that was, moments earlier, on the path. Nothing in the product is
/// wrong when that happens, which is exactly what makes it expensive to diagnose.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessPathEnvironmentCollection
{
	public const string Name = "process-path-environment";
}
