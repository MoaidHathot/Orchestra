using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Services;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

public sealed class DurableOrchestrationRecoveryTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _dataPath;
	private readonly FileSystemRunStore _runStore;
	private readonly FileSystemCheckpointStore _checkpointStore;
	private readonly OrchestrationRegistry _registry;
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeExecutions = new();
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _activeExecutionInfos = new();

	public DurableOrchestrationRecoveryTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-durable-recovery-{Guid.NewGuid():N}");
		_dataPath = Path.Combine(_tempDir, "data");
		Directory.CreateDirectory(_dataPath);

		_runStore = new FileSystemRunStore(_dataPath, NullLogger<FileSystemRunStore>.Instance);
		_checkpointStore = new FileSystemCheckpointStore(_dataPath, NullLogger<FileSystemCheckpointStore>.Instance);
		_registry = new OrchestrationRegistry(
			persistPath: Path.Combine(_dataPath, "registered-orchestrations.json"),
			logger: NullLogger<OrchestrationRegistry>.Instance,
			dataPath: _dataPath);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { }
		}
	}

	[Fact]
	public async Task ResumeFromCheckpointAsync_RestartsIncompleteRunAndDeletesCheckpointOnSuccess()
	{
		// Arrange
		var entry = RegisterTransformOrchestration("durable-resume", "final={{first.output}}/{{param.name}}");
		var checkpoint = new CheckpointData
		{
			RunId = "resume-run-001",
			OrchestrationName = "durable-resume",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CheckpointedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
			Parameters = new Dictionary<string, string> { ["name"] = "Moaid" },
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["first"] = new() { Status = ExecutionStatus.Succeeded, Content = "checkpointed" },
			},
		};
		await _checkpointStore.SaveCheckpointAsync(checkpoint);
		var manager = CreateTriggerManager();

		// Act
		var executionId = await manager.ResumeFromCheckpointAsync(entry, checkpoint);
		var active = WaitForActiveExecution(executionId!);
		await WaitUntilAsync(() => active.Status is HostExecutionStatus.Completed or HostExecutionStatus.Failed or HostExecutionStatus.Cancelled);

		// Assert
		active.Status.Should().Be(HostExecutionStatus.Completed);
		var run = await _runStore.GetRunAsync("durable-resume", "resume-run-001");
		run.Should().NotBeNull();
		run!.TriggeredBy.Should().Be("resume");
		run.Status.Should().Be(ExecutionStatus.Succeeded);
		run.StepRecords["first"].Content.Should().Be("checkpointed");
		run.StepRecords["second"].Content.Should().Be("final=checkpointed/Moaid");

		var remaining = await _checkpointStore.LoadCheckpointAsync("durable-resume", "resume-run-001");
		remaining.Should().BeNull("successful resume should clean up the durable checkpoint");
	}

	[Fact]
	public async Task StopAsync_MarksActiveExecutionsAsHostShutdownInterruptions()
	{
		// Arrange
		var manager = CreateTriggerManager();
		using var cts = new CancellationTokenSource();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "active-run",
			OrchestrationId = "orch-id",
			OrchestrationName = "orch-name",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
		};
		_activeExecutions[info.ExecutionId] = cts;
		_activeExecutionInfos[info.ExecutionId] = info;

		// Act
		await manager.StopAsync(CancellationToken.None);

		// Assert
		info.CancellationCauseOverride.Should().NotBeNull();
		info.CancellationCauseOverride!.Kind.Should().Be(CancellationCauseKind.HostShutdown);
		cts.IsCancellationRequested.Should().BeTrue();
	}

	private OrchestrationEntry RegisterTransformOrchestration(string name, string secondTemplate)
	{
		var orchestration = new
		{
			name,
			description = "Durable recovery test",
			version = "1.0.0",
			inputs = new Dictionary<string, object>
			{
				["name"] = new { type = "string", required = true },
			},
			steps = new object[]
			{
				new Dictionary<string, object?>
				{
					["name"] = "first",
					["type"] = "Transform",
					["template"] = "original",
					["contentType"] = "text/plain",
				},
				new Dictionary<string, object?>
				{
					["name"] = "second",
					["type"] = "Transform",
					["dependsOn"] = new[] { "first" },
					["template"] = secondTemplate,
					["contentType"] = "text/plain",
				},
			},
		};
		var json = JsonSerializer.Serialize(orchestration);

		var path = Path.Combine(_tempDir, $"{name}.json");
		File.WriteAllText(path, json);
		return _registry.Register(path);
	}

	private TriggerManager CreateTriggerManager()
	{
		var runsDir = Path.Combine(_dataPath, "runs");
		Directory.CreateDirectory(runsDir);

		return new TriggerManager(
			_activeExecutions,
			_activeExecutionInfos,
			agentBuilder: new ThrowingAgentBuilder(),
			scheduler: new OrchestrationScheduler(),
			loggerFactory: NullLoggerFactory.Instance,
			logger: NullLogger<TriggerManager>.Instance,
			runsDir: runsDir,
			runStore: _runStore,
			checkpointStore: _checkpointStore,
			launcher: null!,
			executionCallback: new DefaultExecutionCallback(new NullReporterFactory()),
			dataPath: _dataPath);
	}

	private ActiveExecutionInfo WaitForActiveExecution(string executionId)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (_activeExecutionInfos.TryGetValue(executionId, out var info))
				return info;

			Task.Delay(25).GetAwaiter().GetResult();
		}

		throw new TimeoutException($"Execution '{executionId}' did not become active.");
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (condition()) return;
			await Task.Delay(25);
		}

		throw new TimeoutException("Condition was not met before timeout.");
	}

	private sealed class ThrowingAgentBuilder : AgentBuilder
	{
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("Prompt steps should not execute in this test.");

		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("Prompt steps should not execute in this test.");

		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("throwing");
	}

	private sealed class NullReporterFactory : IOrchestrationReporterFactory
	{
		public IOrchestrationReporter Create() => NullOrchestrationReporter.Instance;
	}
}
