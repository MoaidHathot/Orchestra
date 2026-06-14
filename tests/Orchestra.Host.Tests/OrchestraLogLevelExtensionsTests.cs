using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orchestra.Host.Hosting;
using Orchestra.Host.Logging;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for <see cref="OrchestraLogLevelExtensions"/> — the helper that makes
/// orchestra.json's <c>logLevel</c> authoritative across every Orchestra host.
/// </summary>
public class OrchestraLogLevelExtensionsTests
{
	// ── ResolveLogLevel ──

	[Theory]
	[InlineData("Warning", LogLevel.Warning)]
	[InlineData("warning", LogLevel.Warning)]
	[InlineData("WARNING", LogLevel.Warning)]
	[InlineData("Debug", LogLevel.Debug)]
	[InlineData("trace", LogLevel.Trace)]
	[InlineData("Information", LogLevel.Information)]
	[InlineData("Error", LogLevel.Error)]
	[InlineData("Critical", LogLevel.Critical)]
	[InlineData("None", LogLevel.None)]
	public void ResolveLogLevel_ParsesCaseInsensitively(string value, LogLevel expected)
	{
		var config = new OrchestraConfigFile { LogLevel = value };

		config.ResolveLogLevel().Should().Be(expected);
	}

	[Fact]
	public void ResolveLogLevel_NullConfig_ReturnsFallback()
	{
		OrchestraConfigFile? config = null;

		config.ResolveLogLevel().Should().Be(LogLevel.Information);
	}

	[Fact]
	public void ResolveLogLevel_NullLogLevel_ReturnsFallback()
	{
		var config = new OrchestraConfigFile { LogLevel = null };

		config.ResolveLogLevel().Should().Be(LogLevel.Information);
	}

	[Fact]
	public void ResolveLogLevel_InvalidValue_ReturnsFallback()
	{
		var config = new OrchestraConfigFile { LogLevel = "not-a-level" };

		config.ResolveLogLevel().Should().Be(LogLevel.Information);
	}

	[Fact]
	public void ResolveLogLevel_InvalidValue_RespectsCustomFallback()
	{
		var config = new OrchestraConfigFile { LogLevel = "bogus" };

		config.ResolveLogLevel(LogLevel.Warning).Should().Be(LogLevel.Warning);
	}

	// ── ApplyOrchestraLogLevel ──

	[Fact]
	public void ApplyOrchestraLogLevel_OverridesAppsettingsDefault()
	{
		using var configuration = BuildConfigurationWithDefault("Information");
		var config = new OrchestraConfigFile { LogLevel = "Warning" };

		configuration.ApplyOrchestraLogLevel(config);

		configuration[OrchestraLogLevelExtensions.DefaultLogLevelConfigKey].Should().Be("Warning");
	}

	[Fact]
	public void ApplyOrchestraLogLevel_IsCaseInsensitive()
	{
		using var configuration = BuildConfigurationWithDefault("Information");
		var config = new OrchestraConfigFile { LogLevel = "warning" };

		configuration.ApplyOrchestraLogLevel(config);

		// Normalized to the canonical enum name so the logging filter binder parses it.
		configuration[OrchestraLogLevelExtensions.DefaultLogLevelConfigKey].Should().Be("Warning");
	}

	[Fact]
	public void ApplyOrchestraLogLevel_NullConfig_LeavesAppsettingsDefault()
	{
		using var configuration = BuildConfigurationWithDefault("Information");

		configuration.ApplyOrchestraLogLevel(null);

		configuration[OrchestraLogLevelExtensions.DefaultLogLevelConfigKey].Should().Be("Information");
	}

	[Fact]
	public void ApplyOrchestraLogLevel_NullLogLevel_LeavesAppsettingsDefault()
	{
		using var configuration = BuildConfigurationWithDefault("Information");
		var config = new OrchestraConfigFile { LogLevel = null };

		configuration.ApplyOrchestraLogLevel(config);

		configuration[OrchestraLogLevelExtensions.DefaultLogLevelConfigKey].Should().Be("Information");
	}

	[Fact]
	public void ApplyOrchestraLogLevel_InvalidValue_LeavesAppsettingsDefault()
	{
		using var configuration = BuildConfigurationWithDefault("Information");
		var config = new OrchestraConfigFile { LogLevel = "not-a-level" };

		configuration.ApplyOrchestraLogLevel(config);

		// A typo must not clobber the existing default down to Information.
		configuration[OrchestraLogLevelExtensions.DefaultLogLevelConfigKey].Should().Be("Information");
	}

	[Fact]
	public void ApplyOrchestraLogLevel_PreservesPerCategoryEntries()
	{
		using var configuration = new ConfigurationManager();
		configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Logging:LogLevel:Default"] = "Information",
			["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
		});
		var config = new OrchestraConfigFile { LogLevel = "Error" };

		configuration.ApplyOrchestraLogLevel(config);

		configuration["Logging:LogLevel:Default"].Should().Be("Error");
		configuration["Logging:LogLevel:Microsoft.AspNetCore"].Should().Be("Warning");
	}

	[Fact]
	public void ApplyOrchestraLogLevel_NullConfiguration_Throws()
	{
		var config = new OrchestraConfigFile { LogLevel = "Warning" };

		var act = () => ((IConfigurationManager)null!).ApplyOrchestraLogLevel(config);

		act.Should().Throw<ArgumentNullException>();
	}

	private static ConfigurationManager BuildConfigurationWithDefault(string defaultLevel)
	{
		var configuration = new ConfigurationManager();
		configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Logging:LogLevel:Default"] = defaultLevel,
		});
		return configuration;
	}
}
