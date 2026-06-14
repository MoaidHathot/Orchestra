using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Orchestra.Portal.Tests;

/// <summary>
/// Integration tests asserting the Portal host honors <c>orchestra.json</c>'s <c>logLevel</c>,
/// overriding the <c>appsettings.json</c> <c>Logging:LogLevel:Default</c> baseline.
/// </summary>
public class PortalLogLevelTests
{
	private const string ProbeCategory = "PortalLogLevelTests.Probe";

	[Fact]
	public void OrchestraJson_LogLevelWarning_OverridesAppsettingsInformationDefault()
	{
		using var factory = new PortalWarningLogLevelWebApplicationFactory();
		// Force host construction so orchestra.json is loaded and applied.
		_ = factory.CreateClient();

		var logger = factory.Services
			.GetRequiredService<ILoggerFactory>()
			.CreateLogger(ProbeCategory);

		logger.IsEnabled(LogLevel.Information).Should().BeFalse(
			"orchestra.json set logLevel=Warning, which must override appsettings.json's Information default");
		logger.IsEnabled(LogLevel.Warning).Should().BeTrue(
			"Warning is at/above the configured minimum level");
	}

	[Fact]
	public void OrchestraJson_NoLogLevel_KeepsAppsettingsInformationDefault()
	{
		using var factory = new PortalWebApplicationFactory();
		_ = factory.CreateClient();

		var logger = factory.Services
			.GetRequiredService<ILoggerFactory>()
			.CreateLogger(ProbeCategory);

		logger.IsEnabled(LogLevel.Information).Should().BeTrue(
			"with no logLevel in orchestra.json the appsettings.json Information default applies");
	}
}
