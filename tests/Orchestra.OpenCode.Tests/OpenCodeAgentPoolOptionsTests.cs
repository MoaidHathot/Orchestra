using FluentAssertions;

namespace Orchestra.OpenCode.Tests;

/// <summary>
/// Locks in the OpenCode provider pool defaults, in particular the max-instances value which is
/// intentionally kept in sync with <c>Orchestra.Copilot.CopilotAgentPoolOptions</c> (8) so both
/// providers behave identically when an orchestration/host does not request explicit
/// <c>agentPool</c> values.
/// </summary>
public class OpenCodeAgentPoolOptionsTests
{
    [Fact]
    public void DefaultMaxInstancesPerRun_Is8_MatchingCopilot()
    {
        new OpenCodeAgentPoolOptions().DefaultMaxInstancesPerRun.Should().Be(8);
    }

    [Fact]
    public void DefaultsMirrorProviderNeutralExpectations()
    {
        var options = new OpenCodeAgentPoolOptions();

        options.DefaultMinInstances.Should().Be(1);
        options.DefaultMaxSessionsPerInstance.Should().Be(1);
        options.DefaultIdleTimeoutSeconds.Should().Be(120);
    }
}
