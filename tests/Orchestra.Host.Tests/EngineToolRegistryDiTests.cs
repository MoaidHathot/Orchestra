using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for WI-E: EngineToolRegistry wired through DI.
/// Verifies that EngineToolRegistry is registered as a singleton with default tools,
/// that AddEngineTools allows customization, and that TriggerManager receives the registry.
/// </summary>
public class EngineToolRegistryDiTests
{
	[Fact]
	public void EngineToolRegistry_CreateDefault_HasFourBuiltInTools()
	{
		// Act
		var registry = EngineToolRegistry.CreateDefault();

		// Assert
		registry.Count.Should().Be(4);
		registry.TryGet("orchestra_set_status", out _).Should().BeTrue();
		registry.TryGet("orchestra_complete", out _).Should().BeTrue();
		registry.TryGet("orchestra_save_file", out _).Should().BeTrue();
		registry.TryGet("orchestra_read_file", out _).Should().BeTrue();
	}

	[Fact]
	public void EngineToolRegistry_Register_AddsCustomTool()
	{
		// Arrange
		var registry = EngineToolRegistry.CreateDefault();
		var customTool = new TestEngineTool("custom_tool");

		// Act
		registry.Register(customTool);

		// Assert
		registry.Count.Should().Be(5);
		registry.TryGet("custom_tool", out var found).Should().BeTrue();
		found.Should().BeSameAs(customTool);
	}

	[Fact]
	public void EngineToolRegistry_Register_ReplacesExistingTool()
	{
		// Arrange
		var registry = EngineToolRegistry.CreateDefault();
		var replacement = new TestEngineTool("orchestra_set_status");

		// Act
		registry.Register(replacement);

		// Assert — count stays the same (replaced, not added)
		registry.Count.Should().Be(4);
		registry.TryGet("orchestra_set_status", out var found).Should().BeTrue();
		found.Should().BeSameAs(replacement);
	}

	[Fact]
	public void AddEngineTools_RegistersCustomToolsAlongsideDefaults()
	{
		// Arrange
		var services = new ServiceCollection();
		services.AddEngineTools(registry =>
		{
			registry.Register(new TestEngineTool("my_custom_tool"));
		});

		// Act
		using var sp = services.BuildServiceProvider();
		var registry = sp.GetRequiredService<EngineToolRegistry>();

		// Assert — 4 built-in + 1 custom
		registry.Count.Should().Be(5);
		registry.TryGet("my_custom_tool", out _).Should().BeTrue();
		registry.TryGet("orchestra_set_status", out _).Should().BeTrue();
	}

	[Fact]
	public void AddEngineTools_CalledBeforeAddOrchestraHost_CustomRegistryWins()
	{
		// Arrange — AddEngineTools should take precedence over AddOrchestraHost's default
		var services = new ServiceCollection();
		services.AddEngineTools(registry =>
		{
			registry.Register(new TestEngineTool("early_tool"));
		});

		// Simulate what AddOrchestraHost does (check that it doesn't overwrite)
		if (!services.Any(d => d.ServiceType == typeof(EngineToolRegistry)))
		{
			services.AddSingleton(EngineToolRegistry.CreateDefault());
		}

		// Act
		using var sp = services.BuildServiceProvider();
		var registry = sp.GetRequiredService<EngineToolRegistry>();

		// Assert — custom registry is preserved
		registry.TryGet("early_tool", out _).Should().BeTrue();
		registry.Count.Should().Be(5); // 4 default + 1 custom
	}

	[Fact]
	public void TriggerManager_AcceptsEngineToolRegistry()
	{
		// Arrange — verify TriggerManager constructor accepts registry parameter
		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-etri-tests-{Guid.NewGuid():N}");
		var runsDir = Path.Combine(tempDir, "runs");
		Directory.CreateDirectory(runsDir);

		try
		{
			var registry = EngineToolRegistry.CreateDefault();
			registry.Register(new TestEngineTool("injected_tool"));

			// Act — should not throw
			var triggerManager = new TriggerManager(
				new ConcurrentDictionary<string, CancellationTokenSource>(),
				new ConcurrentDictionary<string, ActiveExecutionInfo>(),
				agentBuilder: null!,
				scheduler: null!,
				loggerFactory: NullLoggerFactory.Instance,
				logger: new NullLogger<TriggerManager>(),
				runsDir: runsDir,
				runStore: null!,
				checkpointStore: null!,
				engineToolRegistry: registry,
				dataPath: tempDir);

			// Assert — TriggerManager created successfully with custom registry
			triggerManager.Should().NotBeNull();
		}
		finally
		{
			try { Directory.Delete(tempDir, recursive: true); }
			catch { /* best-effort */ }
		}
	}

	[Fact]
	public void TriggerManager_UsesDefaultRegistry_WhenNoneProvided()
	{
		// Arrange — verify TriggerManager works without explicit registry
		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-etri-default-{Guid.NewGuid():N}");
		var runsDir = Path.Combine(tempDir, "runs");
		Directory.CreateDirectory(runsDir);

		try
		{
			// Act — should not throw (uses default internally)
			var triggerManager = new TriggerManager(
				new ConcurrentDictionary<string, CancellationTokenSource>(),
				new ConcurrentDictionary<string, ActiveExecutionInfo>(),
				agentBuilder: null!,
				scheduler: null!,
				loggerFactory: NullLoggerFactory.Instance,
				logger: new NullLogger<TriggerManager>(),
				runsDir: runsDir,
				runStore: null!,
				checkpointStore: null!,
				dataPath: tempDir);

			// Assert
			triggerManager.Should().NotBeNull();
		}
		finally
		{
			try { Directory.Delete(tempDir, recursive: true); }
			catch { /* best-effort */ }
		}
	}

	[Fact]
	public void EngineToolRegistry_TryGet_IsCaseInsensitive()
	{
		// Arrange
		var registry = EngineToolRegistry.CreateDefault();

		// Act & Assert
		registry.TryGet("ORCHESTRA_SET_STATUS", out _).Should().BeTrue();
		registry.TryGet("Orchestra_Set_Status", out _).Should().BeTrue();
		registry.TryGet("ORCHESTRA_COMPLETE", out _).Should().BeTrue();
	}

	[Fact]
	public void EngineToolRegistry_GetAll_ReturnsAllRegisteredTools()
	{
		// Arrange
		var registry = EngineToolRegistry.CreateDefault();
		registry.Register(new TestEngineTool("extra"));

		// Act
		var all = registry.GetAll();

		// Assert
		all.Count.Should().Be(5);
	}

	/// <summary>
	/// Simple test engine tool for DI verification.
	/// </summary>
	private sealed class TestEngineTool : IEngineTool
	{
		public string Name { get; }
		public string Description => $"Test tool: {Name}";
		public string ParametersSchema => """{"type":"object"}""";

		public TestEngineTool(string name) => Name = name;

		public string Execute(string arguments, EngineToolContext context) => "ok";
	}
}
