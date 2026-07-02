using FluentAssertions;

namespace Orchestra.Copilot.Tests;

/// <summary>
/// Tests that <see cref="CopilotAgentSwapOptions"/> snapshots the MCP-startup timeout from the
/// pool options with the expected clamping. The timeout bounds a single session create/resume so
/// a hung inline MCP stdio server can't leave a step "running" indefinitely.
/// </summary>
public class CopilotAgentSwapOptionsTests
{
    [Fact]
    public void FromPoolOptions_CarriesMcpStartupTimeout()
    {
        var options = new CopilotAgentPoolOptions { McpStartupTimeout = TimeSpan.FromSeconds(45) };

        var swap = CopilotAgentSwapOptions.FromPoolOptions(options);

        swap.McpStartupTimeout.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void FromPoolOptions_DefaultMcpStartupTimeout_Is120Seconds()
    {
        // The built-in default is generous enough to absorb a first-run package restore (dnx/NuGet)
        // while still bounding a true hang.
        var swap = CopilotAgentSwapOptions.FromPoolOptions(new CopilotAgentPoolOptions());

        swap.McpStartupTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void FromPoolOptions_NegativeMcpStartupTimeout_ClampsToZero()
    {
        var options = new CopilotAgentPoolOptions { McpStartupTimeout = TimeSpan.FromSeconds(-5) };

        var swap = CopilotAgentSwapOptions.FromPoolOptions(options);

        swap.McpStartupTimeout.Should().Be(TimeSpan.Zero, "a negative timeout disables the guard rather than throwing");
    }

    [Fact]
    public void FromPoolOptions_ZeroMcpStartupTimeout_StaysZeroToDisableGuard()
    {
        var options = new CopilotAgentPoolOptions { McpStartupTimeout = TimeSpan.Zero };

        var swap = CopilotAgentSwapOptions.FromPoolOptions(options);

        swap.McpStartupTimeout.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void DefaultMaxInstancesPerRun_Is8_MatchingOpenCode()
    {
        // Kept in sync with Orchestra.OpenCode.OpenCodeAgentPoolOptions (8) so both providers
        // behave identically out of the box.
        new CopilotAgentPoolOptions().DefaultMaxInstancesPerRun.Should().Be(8);
    }
}
