using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// <see cref="OrchestrationChangeNotifier"/> turns registry and trigger mutations into one
/// coalesced <c>orchestrations-changed</c> dashboard event.
/// </summary>
public class OrchestrationChangeNotifierTests : IDisposable
{
	private readonly string _tempDir;
	private readonly OrchestrationRegistry _registry;
	private readonly TriggerManager _triggerManager;
	private readonly DashboardEventBroadcaster _broadcaster = new(NullLogger<DashboardEventBroadcaster>.Instance);

	public OrchestrationChangeNotifierTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-change-notifier-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_registry = new OrchestrationRegistry(Path.Combine(_tempDir, "registry.json"), NullLogger<OrchestrationRegistry>.Instance);
		_triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			new ConcurrentDictionary<string, ActiveExecutionInfo>(),
			agentBuilder: null!,
			scheduler: new OrchestrationScheduler(),
			loggerFactory: NullLoggerFactory.Instance,
			logger: NullLogger<TriggerManager>.Instance,
			dataPath: _tempDir,
			runStore: Substitute.For<IRunStore>(),
			checkpointStore: Substitute.For<ICheckpointStore>(),
			launcher: Substitute.For<IChildOrchestrationLauncher>());
	}

	public void Dispose()
	{
		_broadcaster.Dispose();
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort */ }
		}
		GC.SuppressFinalize(this);
	}

	private OrchestrationChangeNotifier CreateNotifier(TimeSpan? window = null)
		=> new(_registry, _triggerManager, _broadcaster, NullLogger<OrchestrationChangeNotifier>.Instance, window ?? TimeSpan.FromMilliseconds(50));

	private string WriteOrchestration(string name)
	{
		var path = Path.Combine(_tempDir, $"{name}.json");
		File.WriteAllText(path, $$"""
			{ "name": "{{name}}", "description": "d", "steps": [ { "name": "s", "type": "command", "command": "echo" } ] }
			""");
		return path;
	}

	private static async Task<List<SseEvent>> DrainAsync(ChannelReader<SseEvent> reader, TimeSpan quietPeriod)
	{
		var events = new List<SseEvent>();
		while (true)
		{
			using var cts = new CancellationTokenSource(quietPeriod);
			try
			{
				var evt = await reader.ReadAsync(cts.Token);
				events.Add(evt);
			}
			catch (OperationCanceledException)
			{
				return events;
			}
		}
	}

	[Fact]
	public async Task RegistryRegister_PublishesOrchestrationsChanged()
	{
		var reader = _broadcaster.Subscribe()!;
		using var notifier = CreateNotifier();
		notifier.Start();

		_registry.Register(WriteOrchestration("alpha"), persist: false);

		var events = await DrainAsync(reader, TimeSpan.FromMilliseconds(600));
		events.Should().ContainSingle(e => e.Type == "orchestrations-changed")
			.Which.Data.Should().Contain("registry:registered");
	}

	[Fact]
	public async Task TriggerEnableDisable_PublishesOrchestrationsChanged()
	{
		var path = WriteOrchestration("beta");
		var reg = _triggerManager.RegisterTrigger(path, new ManualTriggerConfig { Type = TriggerType.Manual, Enabled = true }, source: TriggerSource.Json);
		var reader = _broadcaster.Subscribe()!;
		using var notifier = CreateNotifier();
		notifier.Start();

		_triggerManager.SetTriggerEnabled(reg.Id, false);

		var events = await DrainAsync(reader, TimeSpan.FromMilliseconds(600));
		events.Should().ContainSingle(e => e.Type == "orchestrations-changed")
			.Which.Data.Should().Contain("trigger:disabled");
	}

	[Fact]
	public async Task Burst_IsCoalescedIntoOneEvent_NamedAfterTheFirstChange()
	{
		var reader = _broadcaster.Subscribe()!;
		using var notifier = CreateNotifier(TimeSpan.FromMilliseconds(150));
		notifier.Start();

		for (var i = 0; i < 5; i++)
			_registry.Register(WriteOrchestration($"burst-{i}"), persist: false);
		_registry.Remove(_registry.GetAll().First().Id);

		var events = await DrainAsync(reader, TimeSpan.FromMilliseconds(800));
		events.Where(e => e.Type == "orchestrations-changed").Should().ContainSingle("six mutations inside the window yield one event")
			.Which.Data.Should().Contain("registry:registered");
	}

	[Fact]
	public async Task DirectorySync_PublishesOnce_NotPerFile()
	{
		var dir = Path.Combine(_tempDir, "scan");
		Directory.CreateDirectory(dir);
		for (var i = 0; i < 4; i++)
			File.WriteAllText(Path.Combine(dir, $"o{i}.json"), $$"""{ "name": "o{{i}}", "description": "d", "steps": [ { "name": "s", "type": "command", "command": "echo" } ] }""");
		var reasons = new List<string>();
		_registry.Changed += reasons.Add;

		var result = _registry.SyncDirectory(dir);

		result.Added.Should().Be(4);
		reasons.Should().Equal("synced");

		// And a sync that changes nothing stays silent.
		reasons.Clear();
		_registry.SyncDirectory(dir);
		reasons.Should().BeEmpty();
		await Task.CompletedTask;
	}

	[Fact]
	public async Task Dispose_StopsPublishing()
	{
		var reader = _broadcaster.Subscribe()!;
		var notifier = CreateNotifier();
		notifier.Start();
		notifier.Dispose();

		_registry.Register(WriteOrchestration("gamma"), persist: false);

		var events = await DrainAsync(reader, TimeSpan.FromMilliseconds(400));
		events.Should().NotContain(e => e.Type == "orchestrations-changed");
	}

	[Fact]
	public void ThrowingHandler_DoesNotBreakTheMutation()
	{
		_registry.Changed += _ => throw new InvalidOperationException("listener bug");

		var act = () => _registry.Register(WriteOrchestration("delta"), persist: false);

		act.Should().NotThrow("a misbehaving listener must never fail a registry write");
		_registry.GetAll().Should().ContainSingle(e => e.Orchestration.Name == "delta");
	}
}
