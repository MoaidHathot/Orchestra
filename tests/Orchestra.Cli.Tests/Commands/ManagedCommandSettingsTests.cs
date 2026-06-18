using FluentAssertions;
using Orchestra.Cli.Commands;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="ManagedCommandSettings"/> — the <c>--mode</c> mapping and validation
/// shared by every managed Group-A verb. The mapping is case-insensitive and defaults to auto;
/// an unrecognized value fails validation rather than silently defaulting.
/// </summary>
public class ManagedCommandSettingsTests
{
	[Theory]
	[InlineData(null, "Auto")]
	[InlineData("", "Auto")]
	[InlineData("auto", "Auto")]
	[InlineData("AUTO", "Auto")]
	[InlineData("  auto  ", "Auto")]
	[InlineData("existing", "Existing")]
	[InlineData("Existing", "Existing")]
	[InlineData("isolated", "Isolated")]
	[InlineData("ISOLATED", "Isolated")]
	public void ResolveMode_MapsKnownValues(string? mode, string expected)
	{
		// Compare on the enum name so this public theory doesn't expose the internal ExecMode type.
		new ManagedCommandSettings { Mode = mode! }.ResolveMode().ToString().Should().Be(expected);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("auto")]
	[InlineData("existing")]
	[InlineData("isolated")]
	[InlineData("AUTO")]
	public void Validate_AcceptsKnownModes(string? mode)
	{
		new ManagedCommandSettings { Mode = mode! }.Validate().Successful.Should().BeTrue();
	}

	[Theory]
	[InlineData("bogus")]
	[InlineData("spawn")]
	[InlineData("remote")]
	public void Validate_RejectsUnknownMode(string mode)
	{
		var result = new ManagedCommandSettings { Mode = mode }.Validate();
		result.Successful.Should().BeFalse();
		result.Message.Should().Contain("auto, existing, or isolated");
	}

	[Fact]
	public void ResolveMode_FallsBackToAuto_ForInvalidValue()
	{
		// Defensive: even if an invalid value somehow slips past validation, ResolveMode is total.
		new ManagedCommandSettings { Mode = "bogus" }.ResolveMode().ToString().Should().Be("Auto");
	}
}
