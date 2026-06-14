#pragma warning disable GHCP001 // SandboxConfig is an evaluation-only SDK API.
using FluentAssertions;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests the mapping from Orchestra's <see cref="SandboxPolicy"/> onto the SDK's SandboxConfig
/// applied via the session options-update RPC.
/// </summary>
public class CopilotSandboxMappingTests
{
	[Fact]
	public void BuildSandboxConfig_MapsFilesystemAndNetwork()
	{
		var policy = new SandboxPolicy
		{
			Enabled = true,
			Filesystem = new SandboxFilesystemPolicy
			{
				ReadonlyPaths = ["/src"],
				ReadwritePaths = ["/tmp/work"],
				DeniedPaths = ["/etc/secrets"],
			},
			Network = new SandboxNetworkPolicy
			{
				AllowedHosts = ["api.github.com"],
				BlockedHosts = ["evil.example"],
				AllowOutbound = false,
				AllowLocalNetwork = false,
			},
		};

		var sc = CopilotSdkSessionAdapter.BuildSandboxConfigCore(policy);

		sc.Enabled.Should().BeTrue();
		sc.UserPolicy.Should().NotBeNull();
		sc.UserPolicy!.Filesystem!.ReadonlyPaths.Should().BeEquivalentTo("/src");
		sc.UserPolicy.Filesystem.ReadwritePaths.Should().BeEquivalentTo("/tmp/work");
		sc.UserPolicy.Filesystem.DeniedPaths.Should().BeEquivalentTo("/etc/secrets");
		sc.UserPolicy.Network!.AllowedHosts.Should().BeEquivalentTo("api.github.com");
		sc.UserPolicy.Network.BlockedHosts.Should().BeEquivalentTo("evil.example");
		sc.UserPolicy.Network.AllowOutbound.Should().BeFalse();
		sc.UserPolicy.Network.AllowLocalNetwork.Should().BeFalse();
	}

	[Fact]
	public void BuildSandboxConfig_EmptySections_LeavesPolicyMinimal()
	{
		var sc = CopilotSdkSessionAdapter.BuildSandboxConfigCore(new SandboxPolicy { Enabled = true });

		sc.Enabled.Should().BeTrue();
		sc.UserPolicy.Should().NotBeNull();
		sc.UserPolicy!.Filesystem.Should().BeNull();
		sc.UserPolicy.Network.Should().BeNull();
	}
}
