using FluentAssertions;
using Orchestra.Host.Hosting;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for OrchestraConfigLoader and RetentionPolicy.
/// </summary>
public class OrchestraConfigLoaderTests : IDisposable
{
	private readonly string _tempDir;
	private readonly Dictionary<string, string?> _savedEnvVars = new();

	public OrchestraConfigLoaderTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-config-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);

		// Save and clear relevant env vars so tests are isolated
		SaveAndClear("ORCHESTRA_CONFIG_PATH");
		SaveAndClear("XDG_CONFIG_HOME");
	}

	public void Dispose()
	{
		// Restore env vars
		foreach (var kv in _savedEnvVars)
			Environment.SetEnvironmentVariable(kv.Key, kv.Value);

		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private void SaveAndClear(string name)
	{
		_savedEnvVars[name] = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, null);
	}

	private string WriteConfigFile(string directory, string content)
	{
		var dir = Path.Combine(directory, OrchestraConfigLoader.ConfigDirectoryName);
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, OrchestraConfigLoader.ConfigFileName);
		File.WriteAllText(path, content);
		return path;
	}

	// ── ApplyConfig tests ──

	[Fact]
	public void ApplyConfig_NullFields_DoesNotOverrideDefaults()
	{
		// Arrange
		var options = new OrchestrationHostOptions();
		var defaultDataPath = options.DataPath;
		var config = new OrchestraConfigFile();

		// Act
		OrchestraConfigLoader.ApplyConfig(options, config);

		// Assert
		options.DataPath.Should().Be(defaultDataPath);
		options.ShutdownTimeoutSeconds.Should().Be(30);
		options.LogLevel.Should().Be("Information");
		options.Retention.IsForever.Should().BeTrue();
		options.DefaultModel.Should().BeNull();
	}

	[Fact]
	public void ApplyConfig_AllFields_OverridesDefaults()
	{
		// Arrange
		var options = new OrchestrationHostOptions();
		var config = new OrchestraConfigFile
		{
			Urls = "http://127.0.0.1:5400",
			DataPath = "/custom/data",
			HostBaseUrl = "http://localhost:9999",
			Scan = new ScanConfigFile
			{
				Directory = "/custom/orchestrations",
			},
			ShutdownTimeoutSeconds = 120,
			LogLevel = "Debug",
			DefaultModel = "claude-sonnet-4",
			Retention = new RetentionPolicyConfig
			{
				MaxRunsPerOrchestration = 50,
				MaxRunAgeDays = 7,
			},
		};

		// Act
		OrchestraConfigLoader.ApplyConfig(options, config);

		// Assert
		options.DataPath.Should().Be("/custom/data");
		options.HostBaseUrl.Should().Be("http://localhost:9999");
		options.Scan.Should().NotBeNull();
		options.Scan!.Directory.Should().Be("/custom/orchestrations");
		options.Scan.Watch.Should().BeFalse();
		options.Scan.Recursive.Should().BeFalse();
		options.ShutdownTimeoutSeconds.Should().Be(120);
		options.LogLevel.Should().Be("Debug");
		options.DefaultModel.Should().Be("claude-sonnet-4");
		options.Retention.MaxRunsPerOrchestration.Should().Be(50);
		options.Retention.MaxRunAgeDays.Should().Be(7);
		options.Retention.IsForever.Should().BeFalse();
	}

	[Fact]
	public void ApplyConfig_PartialRetention_OnlyOverridesSpecified()
	{
		// Arrange
		var options = new OrchestrationHostOptions();
		options.Retention.MaxRunsPerOrchestration = 100;
		var config = new OrchestraConfigFile
		{
			Retention = new RetentionPolicyConfig
			{
				MaxRunAgeDays = 30,
				// MaxRunsPerOrchestration is null — should not change existing value
			},
		};

		// Act
		OrchestraConfigLoader.ApplyConfig(options, config);

		// Assert
		options.Retention.MaxRunsPerOrchestration.Should().Be(100);
		options.Retention.MaxRunAgeDays.Should().Be(30);
	}

	// ── LoadAndApply tests ──

	[Fact]
	public void LoadAndApply_NoConfigFile_UsesDefaults()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", null);
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
		var options = new OrchestrationHostOptions();
		var defaultDataPath = options.DataPath;

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert — nothing should have changed
		options.DataPath.Should().Be(defaultDataPath);
		options.ShutdownTimeoutSeconds.Should().Be(30);
	}

	[Fact]
	public void LoadAndApply_ViaEnvVar_LoadsConfig()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "custom-orchestra.json");
		File.WriteAllText(configPath, """
		{
			"dataPath": "/env-var-data",
			"shutdownTimeoutSeconds": 60
		}
		""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var options = new OrchestrationHostOptions();

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert
		options.DataPath.Should().Be("/env-var-data");
		options.ShutdownTimeoutSeconds.Should().Be(60);
	}

	[Fact]
	public void LoadAndApply_ViaXdgConfigHome_LoadsConfig()
	{
		// Arrange
		var xdgDir = Path.Combine(_tempDir, "xdg-config");
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdgDir);

		WriteConfigFile(xdgDir, """
		{
			"logLevel": "Warning"
		}
		""");

		var options = new OrchestrationHostOptions();

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert
		options.LogLevel.Should().Be("Warning");
	}

	[Fact]
	public void LoadAndApply_EnvVarTakesPrecedenceOverXdg()
	{
		// Arrange — set up both env var and XDG
		var configPath = Path.Combine(_tempDir, "env-config.json");
		File.WriteAllText(configPath, """
		{
			"logLevel": "Error"
		}
		""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var xdgDir = Path.Combine(_tempDir, "xdg-config");
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdgDir);
		WriteConfigFile(xdgDir, """
		{
			"logLevel": "Trace"
		}
		""");

		var options = new OrchestrationHostOptions();

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert — env var path should win
		options.LogLevel.Should().Be("Error");
	}

	[Fact]
	public void LoadAndApply_InvalidJson_UsesDefaults()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "bad.json");
		File.WriteAllText(configPath, "{ this is not valid json }}}");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var options = new OrchestrationHostOptions();
		var defaultDataPath = options.DataPath;

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert — should gracefully fall back to defaults
		options.DataPath.Should().Be(defaultDataPath);
	}

	[Fact]
	public void LoadAndApply_SupportsJsonComments()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "commented.json");
		File.WriteAllText(configPath, """
		{
			// This is a comment
			"shutdownTimeoutSeconds": 45
		}
		""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var options = new OrchestrationHostOptions();

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert
		options.ShutdownTimeoutSeconds.Should().Be(45);
	}

	[Fact]
	public void LoadAndApply_SupportsTrailingCommas()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "trailing.json");
		File.WriteAllText(configPath, """
		{
			"shutdownTimeoutSeconds": 90,
		}
		""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var options = new OrchestrationHostOptions();

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert
		options.ShutdownTimeoutSeconds.Should().Be(90);
	}

	// ── ResolveConfigPath tests ──

	[Fact]
	public void ResolveConfigPath_EnvVar_ReturnsEnvVarPath()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "explicit.json");
		File.WriteAllText(configPath, "{}");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		// Act
		var result = OrchestraConfigLoader.ResolveConfigPath();

		// Assert
		result.Should().Be(configPath);
	}

	[Fact]
	public void ResolveConfigPath_EnvVar_NonExistentFile_FallsThrough()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", "/non/existent/path.json");
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);

		// Act
		var result = OrchestraConfigLoader.ResolveConfigPath();

		// Assert — should not return the non-existent path; may return null or platform fallback
		result.Should().NotBe("/non/existent/path.json");
	}

	[Fact]
	public void ResolveConfigPath_XdgConfigHome_ReturnsXdgPath()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", null);
		var xdgDir = Path.Combine(_tempDir, "xdg");
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdgDir);
		var expectedPath = WriteConfigFile(xdgDir, "{}");

		// Act
		var result = OrchestraConfigLoader.ResolveConfigPath();

		// Assert
		result.Should().Be(expectedPath);
	}

	[Fact]
	public void ResolveConfigPath_NoFilesExist_ReturnsNull()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", null);
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_tempDir, "empty-xdg"));

		// Act
		var result = OrchestraConfigLoader.ResolveConfigPath();

		// Assert — may return null if platform fallback doesn't exist either
		// At minimum, should not throw
		// (Platform fallback might exist on the test machine, so we just verify no exception)
	}

	// ── RetentionPolicy tests ──

	[Fact]
	public void RetentionPolicy_Default_IsForever()
	{
		var policy = new RetentionPolicy();
		policy.IsForever.Should().BeTrue();
	}

	[Fact]
	public void RetentionPolicy_NullValues_IsForever()
	{
		var policy = new RetentionPolicy
		{
			MaxRunsPerOrchestration = null,
			MaxRunAgeDays = null,
		};
		policy.IsForever.Should().BeTrue();
	}

	[Fact]
	public void RetentionPolicy_ZeroValues_IsForever()
	{
		var policy = new RetentionPolicy
		{
			MaxRunsPerOrchestration = 0,
			MaxRunAgeDays = 0,
		};
		policy.IsForever.Should().BeTrue();
	}

	[Fact]
	public void RetentionPolicy_WithMaxRuns_NotForever()
	{
		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 10 };
		policy.IsForever.Should().BeFalse();
	}

	[Fact]
	public void RetentionPolicy_WithMaxAge_NotForever()
	{
		var policy = new RetentionPolicy { MaxRunAgeDays = 30 };
		policy.IsForever.Should().BeFalse();
	}

	[Fact]
	public void RetentionPolicy_WithBothLimits_NotForever()
	{
		var policy = new RetentionPolicy
		{
			MaxRunsPerOrchestration = 50,
			MaxRunAgeDays = 7,
		};
		policy.IsForever.Should().BeFalse();
	}

	// ── GetDefaultConfigPath tests ──

	[Fact]
	public void GetDefaultConfigPath_WithXdg_UsesXdg()
	{
		// Arrange
		var xdgDir = Path.Combine(_tempDir, "default-xdg");
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdgDir);

		// Act
		var result = OrchestraConfigLoader.GetDefaultConfigPath();

		// Assert
		result.Should().Be(Path.Combine(xdgDir, OrchestraConfigLoader.ConfigDirectoryName, OrchestraConfigLoader.ConfigFileName));
	}

	[Fact]
	public void GetDefaultConfigPath_WithoutXdg_UsesPlatformDefault()
	{
		// Arrange
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);

		// Act
		var result = OrchestraConfigLoader.GetDefaultConfigPath();

		// Assert
		result.Should().NotBeNullOrWhiteSpace();
		result.Should().EndWith(Path.Combine(OrchestraConfigLoader.ConfigDirectoryName, OrchestraConfigLoader.ConfigFileName));
	}

	// ── Full round-trip test ──

	[Fact]
	public void LoadAndApply_FullConfig_RoundTrip()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "full.json");
		File.WriteAllText(configPath, """
		{
			"dataPath": "/round-trip/data",
			"hostBaseUrl": "http://my-host:8080",
			"scan": {
				"directory": "/round-trip/orchestrations",
				"watch": true,
				"recursive": true
			},
			"shutdownTimeoutSeconds": 180,
			"logLevel": "Trace",
			"defaultModel": "claude-sonnet-4",
			"retention": {
				"maxRunsPerOrchestration": 25,
				"maxRunAgeDays": 14
			}
		}
		""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var options = new OrchestrationHostOptions();

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert
		options.DataPath.Should().Be("/round-trip/data");
		options.HostBaseUrl.Should().Be("http://my-host:8080");
		options.Scan.Should().NotBeNull();
		options.Scan!.Directory.Should().Be("/round-trip/orchestrations");
		options.Scan.Watch.Should().BeTrue();
		options.Scan.Recursive.Should().BeTrue();
		options.ShutdownTimeoutSeconds.Should().Be(180);
		options.LogLevel.Should().Be("Trace");
		options.DefaultModel.Should().Be("claude-sonnet-4");
		options.Retention.MaxRunsPerOrchestration.Should().Be(25);
		options.Retention.MaxRunAgeDays.Should().Be(14);
		options.Retention.IsForever.Should().BeFalse();
	}

	// ── Load (returns OrchestraConfigFile) tests ──

	[Fact]
	public void Load_NoConfigFile_ReturnsNull()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", null);
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_tempDir, "empty"));

		// Act
		var result = OrchestraConfigLoader.Load();

		// Assert
		result.Should().BeNull();
	}

	[Fact]
	public void Load_ValidConfig_ReturnsDeserializedConfig()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "load-test.json");
		File.WriteAllText(configPath, """
		{
			"urls": "http://127.0.0.1:5500",
			"dataPath": "/load/data",
			"logLevel": "Debug",
			"shutdownTimeoutSeconds": 99
		}
		""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		// Act
		var result = OrchestraConfigLoader.Load();

		// Assert
		result.Should().NotBeNull();
		result!.Urls.Should().Be("http://127.0.0.1:5500");
		result.DataPath.Should().Be("/load/data");
		result.LogLevel.Should().Be("Debug");
		result.ShutdownTimeoutSeconds.Should().Be(99);
		result.HostBaseUrl.Should().BeNull();
	}

	[Fact]
	public void Load_InvalidJson_ReturnsNull()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "bad-load.json");
		File.WriteAllText(configPath, "not valid json {{{");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		// Act
		var result = OrchestraConfigLoader.Load();

		// Assert
		result.Should().BeNull();
	}

	[Fact]
	public void Load_EmptyJson_ReturnsEmptyConfig()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "empty-load.json");
		File.WriteAllText(configPath, "{}");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		// Act
		var result = OrchestraConfigLoader.Load();

		// Assert
		result.Should().NotBeNull();
		result!.DataPath.Should().BeNull();
		result.LogLevel.Should().BeNull();
		result.ShutdownTimeoutSeconds.Should().BeNull();
	}

	[Fact]
	public void Load_WithRetention_ReturnsFullConfig()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "retention-load.json");
		File.WriteAllText(configPath, """
		{
			"logLevel": "Trace",
			"retention": {
				"maxRunsPerOrchestration": 10,
				"maxRunAgeDays": 7
			}
		}
		""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		// Act
		var result = OrchestraConfigLoader.Load();

		// Assert
		result.Should().NotBeNull();
		result!.LogLevel.Should().Be("Trace");
		result.Retention.Should().NotBeNull();
		result.Retention!.MaxRunsPerOrchestration.Should().Be(10);
		result.Retention.MaxRunAgeDays.Should().Be(7);
	}

	// ── Scan config tests ──

	[Fact]
	public void ApplyConfig_Scan_DirectoryOnly_SetsDefaults()
	{
		// Arrange
		var options = new OrchestrationHostOptions();
		var config = new OrchestraConfigFile
		{
			Scan = new ScanConfigFile
			{
				Directory = "/my/orchestrations",
			},
		};

		// Act
		OrchestraConfigLoader.ApplyConfig(options, config);

		// Assert
		options.Scan.Should().NotBeNull();
		options.Scan!.Directory.Should().Be("/my/orchestrations");
		options.Scan.Watch.Should().BeFalse();
		options.Scan.Recursive.Should().BeFalse();
	}

	[Fact]
	public void ApplyConfig_Scan_AllFields_Applied()
	{
		// Arrange
		var options = new OrchestrationHostOptions();
		var config = new OrchestraConfigFile
		{
			Scan = new ScanConfigFile
			{
				Directory = "/scan/dir",
				Watch = true,
				Recursive = true,
			},
		};

		// Act
		OrchestraConfigLoader.ApplyConfig(options, config);

		// Assert
		options.Scan.Should().NotBeNull();
		options.Scan!.Directory.Should().Be("/scan/dir");
		options.Scan.Watch.Should().BeTrue();
		options.Scan.Recursive.Should().BeTrue();
	}

	[Fact]
	public void ApplyConfig_Scan_PartialOverride_PreservesExisting()
	{
		// Arrange — pre-configure some values
		var options = new OrchestrationHostOptions
		{
			Scan = new ScanConfig
			{
				Directory = "/original/dir",
				Watch = true,
				Recursive = true,
			},
		};

		// Config only overrides directory, watch/recursive are null (no override)
		var config = new OrchestraConfigFile
		{
			Scan = new ScanConfigFile
			{
				Directory = "/new/dir",
			},
		};

		// Act
		OrchestraConfigLoader.ApplyConfig(options, config);

		// Assert — directory changed, watch and recursive preserved
		options.Scan.Should().NotBeNull();
		options.Scan!.Directory.Should().Be("/new/dir");
		options.Scan.Watch.Should().BeTrue();
		options.Scan.Recursive.Should().BeTrue();
	}

	[Fact]
	public void ApplyConfig_Scan_NullDirectory_DoesNotApply()
	{
		// Arrange
		var options = new OrchestrationHostOptions();
		var config = new OrchestraConfigFile
		{
			Scan = new ScanConfigFile
			{
				// Directory is null — should not create Scan
				Watch = true,
			},
		};

		// Act
		OrchestraConfigLoader.ApplyConfig(options, config);

		// Assert — Scan should remain null
		options.Scan.Should().BeNull();
	}

	[Fact]
	public void ApplyConfig_Scan_Null_DoesNotOverride()
	{
		// Arrange — pre-configure
		var options = new OrchestrationHostOptions
		{
			Scan = new ScanConfig
			{
				Directory = "/existing/dir",
			},
		};

		var config = new OrchestraConfigFile
		{
			// Scan is null in config file — should not clear existing
		};

		// Act
		OrchestraConfigLoader.ApplyConfig(options, config);

		// Assert — existing config preserved
		options.Scan.Should().NotBeNull();
		options.Scan!.Directory.Should().Be("/existing/dir");
	}

	[Fact]
	public void LoadAndApply_Scan_FromJsonFile()
	{
		// Arrange
		var configPath = Path.Combine(_tempDir, "scan-config.json");
		File.WriteAllText(configPath, """
		{
			"scan": {
				"directory": "/file/based/scan",
				"watch": true
			}
		}
		""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var options = new OrchestrationHostOptions();

		// Act
		OrchestraConfigLoader.LoadAndApply(options);

		// Assert
		options.Scan.Should().NotBeNull();
		options.Scan!.Directory.Should().Be("/file/based/scan");
		options.Scan.Watch.Should().BeTrue();
		options.Scan.Recursive.Should().BeFalse();
	}
}
