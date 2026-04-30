using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
using Orchestra.Host.McpServer;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Services;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for <see cref="ChildOrchestrationLauncher"/> — the central in-process child
/// orchestration launcher used by DataPlaneTools, TriggerManager, and ExecutionApi.
/// </summary>
public sealed class ChildOrchestrationLauncherTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _dataPath;
	private readonly OrchestrationRegistry _registry;
	private readonly FileSystemRunStore _runStore;
	private readonly OrchestrationHostOptions _hostOptions;
	private readonly OrchestrationScheduler _scheduler;
	private readonly McpServerOptions _mcpOptions;
	private readonly McpManager _mcpManager;
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeExecutions;
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _activeExecutionInfos;

	public ChildOrchestrationLauncherTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-launcher-tests-{Guid.NewGuid():N}");
		_dataPath = Path.Combine(_tempDir, "data");
		Directory.CreateDirectory(_dataPath);

		_registry = new OrchestrationRegistry(
			persistPath: Path.Combine(_dataPath, "registered-orchestrations.json"),
			logger: NullLogger<OrchestrationRegistry>.Instance);

		_runStore = new FileSystemRunStore(_dataPath, NullLogger<FileSystemRunStore>.Instance);
		_hostOptions = new OrchestrationHostOptions { DataPath = _dataPath };
		_scheduler = new OrchestrationScheduler();
		_mcpOptions = new McpServerOptions { MaxNestingDepth = 5 };
		_mcpManager = new McpManager(NullLogger<McpManager>.Instance);
		_activeExecutions = new ConcurrentDictionary<string, CancellationTokenSource>();
		_activeExecutionInfos = new ConcurrentDictionary<string, ActiveExecutionInfo>();
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort */ }
		}
	}

	// ── Helpers ──────────────────────────────────────────────────────────────

	/// <summary>
	/// Writes a Transform-only orchestration to disk (no Prompt steps so AgentBuilder isn't invoked)
	/// and registers it in the registry.
	/// </summary>
	private OrchestrationEntry RegisterTransformOrchestration(
		string name,
		string template = "Hello {{param.who}}",
		string[]? inputs = null)
	{
		var inputsJson = inputs is null
			? string.Empty
			: ", \"inputs\": { " + string.Join(", ", inputs.Select(i => $"\"{i}\": {{ \"type\": \"string\", \"required\": true }}")) + " }";

		var json = $$"""
		{
			"name": "{{name}}",
			"description": "Test orchestration",
			"version": "1.0.0"
			{{inputsJson}},
			"steps": [
				{
					"name": "transform-step",
					"type": "Transform",
					"template": "{{template}}",
					"contentType": "text/plain"
				}
			]
		}
		""";

		var path = Path.Combine(_tempDir, $"{name}.json");
		File.WriteAllText(path, json);

		_registry.Register(path);
		var entry = _registry.GetAll().Single(e => e.Orchestration.Name == name);
		return entry;
	}

	private ChildOrchestrationLauncher CreateLauncher()
	{
		return new ChildOrchestrationLauncher(
			_registry,
			agentBuilder: new TestAgentBuilder(),
			_scheduler,
			NullLoggerFactory.Instance,
			_runStore,
			_hostOptions,
			EngineToolRegistry.CreateDefault(),
			_mcpOptions,
			new SseReporterFactory(),
			_mcpManager,
			_activeExecutions,
			_activeExecutionInfos);
	}

	// ── Tests ────────────────────────────────────────────────────────────────

	[Fact]
	public async Task LaunchAsync_OrchestrationNotFound_ThrowsLaunchException()
	{
		var launcher = CreateLauncher();

		var act = async () => await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = "does-not-exist",
		});

		var ex = await act.Should().ThrowAsync<ChildOrchestrationLaunchException>();
		ex.Which.ErrorCode.Should().Be(ChildOrchestrationLaunchException.OrchestrationNotFound);
	}

	[Fact]
	public async Task LaunchAsync_LookupByOrchestrationName_Succeeds()
	{
		// Authors writing "orchestration: my-name" in YAML reference orchestrations by name,
		// not by the registry's auto-generated ID. The launcher must fall back to name-based
		// lookup for that ergonomic surface.
		var entry = RegisterTransformOrchestration("name-lookup-test", template: "named");
		var launcher = CreateLauncher();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Orchestration.Name, // Use the NAME, not the registry ID
			Mode = ChildLaunchMode.Sync,
		});

		handle.OrchestrationName.Should().Be("name-lookup-test");
		var result = await handle.Completion;
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task LaunchAsync_ParseError_ThrowsLaunchException()
	{
		// Write a malformed orchestration file directly (registry won't validate at registration)
		var path = Path.Combine(_tempDir, "bad-orchestration.json");
		File.WriteAllText(path, "{ this is not valid json");

		var launcher = CreateLauncher();

		var act = async () => await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = "bad",
			OrchestrationPath = path, // Use path override to skip registry
		});

		var ex = await act.Should().ThrowAsync<ChildOrchestrationLaunchException>();
		ex.Which.ErrorCode.Should().Be(ChildOrchestrationLaunchException.ParseFailed);
	}

	[Fact]
	public async Task LaunchAsync_SyncSuccess_ProducesTerminalResult()
	{
		var entry = RegisterTransformOrchestration("sync-success", template: "static-result");
		var launcher = CreateLauncher();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			Mode = ChildLaunchMode.Sync,
			TriggeredBy = "test",
		});

		var result = await handle.Completion;

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.ExecutionId.Should().Be(handle.ExecutionId);
		result.OrchestrationName.Should().Be(entry.Orchestration.Name);
		result.OrchestrationResult.Should().NotBeNull();
		result.FinalContent.Should().Contain("static-result");
		result.ErrorMessage.Should().BeNull();
		result.TimedOut.Should().BeFalse();
	}

	[Fact]
	public async Task LaunchAsync_RegistersActiveExecutionInfo_WithNestingMetadata()
	{
		var entry = RegisterTransformOrchestration("register-info", template: "v");
		var launcher = CreateLauncher();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			Mode = ChildLaunchMode.Sync,
			TriggeredBy = "test",
			UserMetadata = new Dictionary<string, string> { ["correlationId"] = "abc" },
		});

		_activeExecutionInfos.Should().ContainKey(handle.ExecutionId);
		var info = _activeExecutionInfos[handle.ExecutionId];
		info.OrchestrationId.Should().Be(entry.Id);
		info.OrchestrationName.Should().Be(entry.Orchestration.Name);
		info.TriggeredBy.Should().Be("test");
		info.NestingMetadata.Should().NotBeNull();
		info.NestingMetadata!.Depth.Should().Be(0);
		info.NestingMetadata.RootExecutionId.Should().Be(handle.ExecutionId);
		info.NestingMetadata.UserMetadata.Should().ContainKey("correlationId");

		_activeExecutions.Should().ContainKey(handle.ExecutionId);

		// Drive the run to completion to keep the test self-contained
		await handle.Completion;
	}

	[Fact]
	public async Task LaunchAsync_NestedChild_RecordsParentLineage_AndIncrementsDepth()
	{
		var entry = RegisterTransformOrchestration("nested-child", template: "x");
		var launcher = CreateLauncher();

		// Pretend a parent execution exists in the active dictionaries.
		var parentExecId = "parent-exec";
		using var parentCts = new CancellationTokenSource();
		_activeExecutions[parentExecId] = parentCts;
		_activeExecutionInfos[parentExecId] = new ActiveExecutionInfo
		{
			ExecutionId = parentExecId,
			OrchestrationId = "parent-orch",
			OrchestrationName = "parent-orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "test",
			CancellationTokenSource = parentCts,
			Reporter = NullOrchestrationReporter.Instance,
			NestingMetadata = new ExecutionMetadata
			{
				RootExecutionId = parentExecId,
				Depth = 0,
			},
		};

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			ParentContext = new ParentExecutionContext { ParentExecutionId = parentExecId },
			Mode = ChildLaunchMode.Sync,
		});

		var info = _activeExecutionInfos[handle.ExecutionId];
		info.NestingMetadata.Should().NotBeNull();
		info.NestingMetadata!.Depth.Should().Be(1);
		info.NestingMetadata.ParentExecutionId.Should().Be(parentExecId);
		info.NestingMetadata.RootExecutionId.Should().Be(parentExecId);

		await handle.Completion;
	}

	[Fact]
	public async Task LaunchAsync_ExceedsMaxNestingDepth_ThrowsLaunchException()
	{
		_mcpOptions.MaxNestingDepth = 2;
		var entry = RegisterTransformOrchestration("depth-test", template: "x");
		var launcher = CreateLauncher();

		// Simulate a parent at depth=2; the child would be at depth=3 which exceeds max.
		var parentExecId = "parent-deep";
		using var parentCts = new CancellationTokenSource();
		_activeExecutions[parentExecId] = parentCts;
		_activeExecutionInfos[parentExecId] = new ActiveExecutionInfo
		{
			ExecutionId = parentExecId,
			OrchestrationId = "parent-orch",
			OrchestrationName = "parent-orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "test",
			CancellationTokenSource = parentCts,
			Reporter = NullOrchestrationReporter.Instance,
			NestingMetadata = new ExecutionMetadata
			{
				RootExecutionId = "root",
				Depth = 2,
			},
		};

		var act = async () => await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			ParentContext = new ParentExecutionContext { ParentExecutionId = parentExecId },
			Mode = ChildLaunchMode.Sync,
		});

		var ex = await act.Should().ThrowAsync<ChildOrchestrationLaunchException>();
		ex.Which.ErrorCode.Should().Be(ChildOrchestrationLaunchException.MaxNestingDepthExceeded);
	}

	[Fact]
	public async Task LaunchAsync_PathOverride_ParsesFromExplicitPath()
	{
		// Note: we register under a different ID than what we'll request, to verify the path
		// override bypasses the registry lookup entirely (path is what gets parsed).
		var path = Path.Combine(_tempDir, "by-path.json");
		var json = """
		{
			"name": "by-path",
			"description": "Test",
			"version": "1.0.0",
			"steps": [
				{
					"name": "t",
					"type": "Transform",
					"template": "from-path",
					"contentType": "text/plain"
				}
			]
		}
		""";
		File.WriteAllText(path, json);

		var launcher = CreateLauncher();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = "id-not-in-registry", // Would fail registry lookup
			OrchestrationPath = path,
			Mode = ChildLaunchMode.Sync,
		});

		var result = await handle.Completion;
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.OrchestrationName.Should().Be("by-path");
	}

	[Fact]
	public async Task LaunchAsync_AsyncMode_ReturnsHandleBeforeCompletion()
	{
		var entry = RegisterTransformOrchestration("async-mode", template: "v");
		var launcher = CreateLauncher();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			Mode = ChildLaunchMode.Async,
		});

		// Handle is available immediately; completion task may or may not have already finished.
		handle.ExecutionId.Should().NotBeNullOrWhiteSpace();
		handle.OrchestrationName.Should().Be(entry.Orchestration.Name);
		handle.Completion.Should().NotBeNull();

		var result = await handle.Completion;
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task LaunchAsync_CallerSuppliedReporter_IsUsed()
	{
		var entry = RegisterTransformOrchestration("custom-reporter", template: "v");
		var launcher = CreateLauncher();

		var customReporter = new SseReporter();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			Reporter = customReporter,
			Mode = ChildLaunchMode.Sync,
		});

		handle.Reporter.Should().BeSameAs(customReporter);
		await handle.Completion;
	}

	[Fact]
	public async Task LaunchAsync_PreExecutionParameterTransform_ReplacesParameters()
	{
		var entry = RegisterTransformOrchestration(
			"transform-params",
			template: "result-{{param.who}}",
			inputs: ["who"]);
		var launcher = CreateLauncher();

		var transformInvoked = 0;
		Func<CancellationToken, Task<Dictionary<string, string>?>> transform = _ =>
		{
			Interlocked.Increment(ref transformInvoked);
			return Task.FromResult<Dictionary<string, string>?>(new Dictionary<string, string>
			{
				["who"] = "world",
			});
		};

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			Parameters = new Dictionary<string, string> { ["who"] = "raw" },
			PreExecutionParameterTransform = transform,
			Mode = ChildLaunchMode.Sync,
		});

		var result = await handle.Completion;
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		transformInvoked.Should().Be(1);
		result.FinalContent.Should().Contain("result-world"); // Used post-transform value, not "raw"

		// And the active executionInfo's Parameters should reflect the transformed values.
		_activeExecutionInfos.TryGetValue(handle.ExecutionId, out var info);
		info?.Parameters.Should().ContainKey("who").WhoseValue.Should().Be("world");
	}

	[Fact]
	public async Task LaunchAsync_ParentCancellation_PropagatesToChild()
	{
		var entry = RegisterTransformOrchestration("cancel-test", template: "v");
		var launcher = CreateLauncher();

		// Set up a fake parent execution whose CTS we can cancel from outside.
		var parentExecId = "parent-cancellation";
		using var parentCts = new CancellationTokenSource();
		_activeExecutions[parentExecId] = parentCts;
		_activeExecutionInfos[parentExecId] = new ActiveExecutionInfo
		{
			ExecutionId = parentExecId,
			OrchestrationId = "p",
			OrchestrationName = "p",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "test",
			CancellationTokenSource = parentCts,
			Reporter = NullOrchestrationReporter.Instance,
			NestingMetadata = new ExecutionMetadata { RootExecutionId = parentExecId, Depth = 0 },
		};

		// Cancel BEFORE launching: the child's linked CTS should already be cancelled.
		parentCts.Cancel();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			ParentContext = new ParentExecutionContext { ParentExecutionId = parentExecId },
			Mode = ChildLaunchMode.Sync,
		});

		var result = await handle.Completion;
		result.Status.Should().BeOneOf(ExecutionStatus.Cancelled, ExecutionStatus.Failed);
	}

	[Fact]
	public async Task LaunchAsync_ProvidedExternalToken_CancelledBeforeLaunch_ChildIsCancelled()
	{
		var entry = RegisterTransformOrchestration("cancel-token", template: "v");
		var launcher = CreateLauncher();

		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			Mode = ChildLaunchMode.Sync,
		}, cts.Token);

		var result = await handle.Completion;
		result.Status.Should().BeOneOf(ExecutionStatus.Cancelled, ExecutionStatus.Failed);
	}

	[Fact]
	public async Task LaunchAsync_HandleExecutionId_AppearsInActiveExecutions_BeforeCompletion()
	{
		var entry = RegisterTransformOrchestration("registration-timing", template: "v");
		var launcher = CreateLauncher();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			Mode = ChildLaunchMode.Async,
		});

		// The launcher must register the execution synchronously so callers can
		// observe it (e.g., to wire SSE subscriptions or callbacks) before any
		// background work runs to completion.
		_activeExecutions.Should().ContainKey(handle.ExecutionId);
		_activeExecutionInfos.Should().ContainKey(handle.ExecutionId);

		await handle.Completion;
	}

	[Fact]
	public async Task LaunchAsync_NestedChildRun_PersistsLineageOnRunRecord()
	{
		// Phase 5 verification: the launcher must propagate ParentExecutionContext into the
		// engine, and the engine must record parentExecutionId, parentStepName, rootExecutionId,
		// and nestingDepth on the persisted OrchestrationRunRecord.
		var entry = RegisterTransformOrchestration("lineage-child", template: "v");
		var launcher = CreateLauncher();

		var parentExecId = "parent-for-lineage";
		using var parentCts = new CancellationTokenSource();
		_activeExecutions[parentExecId] = parentCts;
		_activeExecutionInfos[parentExecId] = new ActiveExecutionInfo
		{
			ExecutionId = parentExecId,
			OrchestrationId = "parent",
			OrchestrationName = "parent",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "test",
			CancellationTokenSource = parentCts,
			Reporter = NullOrchestrationReporter.Instance,
			NestingMetadata = new ExecutionMetadata { RootExecutionId = parentExecId, Depth = 0 },
		};

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			ParentContext = new ParentExecutionContext
			{
				ParentExecutionId = parentExecId,
				ParentStepName = "invoke-child",
			},
			Mode = ChildLaunchMode.Sync,
		});

		await handle.Completion;

		// Read the persisted run record and assert lineage fields.
		var run = await _runStore.GetRunAsync(entry.Orchestration.Name, handle.ExecutionId);
		run.Should().NotBeNull();
		run!.ParentExecutionId.Should().Be(parentExecId);
		run.ParentStepName.Should().Be("invoke-child");
		run.RootExecutionId.Should().Be(parentExecId);
		run.NestingDepth.Should().Be(1);
	}

	[Fact]
	public async Task LaunchAsync_TopLevelRun_RootExecutionIdEqualsRunId_DepthZero()
	{
		var entry = RegisterTransformOrchestration("toplevel-lineage", template: "v");
		var launcher = CreateLauncher();

		var handle = await launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = entry.Id,
			Mode = ChildLaunchMode.Sync,
		});

		var terminal = await handle.Completion;
		terminal.Status.Should().Be(ExecutionStatus.Succeeded);

		var run = await _runStore.GetRunAsync(entry.Orchestration.Name, handle.ExecutionId);
		run.Should().NotBeNull();
		run!.ParentExecutionId.Should().BeNull();
		run.ParentStepName.Should().BeNull();
		run.RootExecutionId.Should().Be(handle.ExecutionId, "top-level runs are their own root");
		run.NestingDepth.Should().Be(0);
	}

	// ── Test agent builder (never invoked for Transform-only orchestrations) ──

	private sealed class TestAgentBuilder : AgentBuilder
	{
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
			=> throw new NotImplementedException("TestAgentBuilder should not be invoked for Transform-only orchestrations.");

		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
			=> throw new NotImplementedException("TestAgentBuilder should not be invoked for Transform-only orchestrations.");
	}
}
