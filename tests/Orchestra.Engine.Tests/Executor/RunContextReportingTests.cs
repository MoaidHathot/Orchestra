using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Verifies the run-context contract that live viewers depend on: the engine reports the run's
/// identity, trigger source and run directory as soon as the run exists (not only at the end),
/// and the persisted record ends up with the same directory.
/// </summary>
public class RunContextReportingTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	[Fact]
	public async Task ExecuteAsync_ReportsRunContextAtStartAndAtEnd_WithRunDirectoryFromStore()
	{
		// Arrange - a store that resolves run directories the way a file-system store would.
		var records = new List<OrchestrationRunRecord>();
		var runStore = new DirectoryAwareRunStore(records);
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithResponse("ok"), _reporter, _loggerFactory, runStore: runStore);
		var orchestration = new Orchestration
		{
			Name = "ctx-orch",
			Description = "Reports run context early.",
			Version = "2.1.0",
			Steps = [TestOrchestrations.CreatePromptStep("only")],
		};

		var contexts = new List<RunContext>();
		_reporter.When(r => r.ReportRunContext(Arg.Any<RunContext>())).Do(call => contexts.Add(call.Arg<RunContext>()));

		// Act
		await executor.ExecuteAsync(orchestration, triggeredBy: "scheduler");

		// Assert - first report happens before any step ran, last one carries the complete context.
		contexts.Should().HaveCount(2);
		var early = contexts[0];
		var final = contexts[1];

		early.OrchestrationName.Should().Be("ctx-orch");
		early.OrchestrationVersion.Should().Be("2.1.0");
		early.TriggeredBy.Should().Be("scheduler");
		early.RunId.Should().Be(final.RunId);
		early.StartedAt.Should().Be(final.StartedAt);
		early.DataDirectory.Should().Be(runStore.ResolveRunDirectory("ctx-orch", "2.1.0", "scheduler", early.StartedAt, early.RunId));
		early.DataDirectory.Should().Contain("ctx-orch_2.1.0_scheduler_");

		final.DataDirectory.Should().Be(early.DataDirectory);
		final.TriggeredBy.Should().Be("scheduler");

		records.Should().ContainSingle();
		records[0].Context.Should().NotBeNull();
		records[0].Context!.DataDirectory.Should().Be(early.DataDirectory);
		records[0].TriggeredBy.Should().Be("scheduler");

		// The early report must precede the first step.
		var sequence = _reporter.ReceivedCalls()
			.Select(c => c.GetMethodInfo().Name)
			.Where(n => n is nameof(IOrchestrationReporter.ReportRunContext) or nameof(IOrchestrationReporter.ReportStepStarted))
			.ToList();
		sequence.Should().Equal(
			nameof(IOrchestrationReporter.ReportRunContext),
			nameof(IOrchestrationReporter.ReportStepStarted),
			nameof(IOrchestrationReporter.ReportRunContext));
	}

	[Fact]
	public async Task ExecuteAsync_StoreWithoutDirectories_ReportsNullDataDirectory()
	{
		// Arrange - the default IRunStore implementation resolves no directory (e.g. NullRunStore),
		// so the context must simply carry null rather than a made-up path.
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithResponse("ok"), _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "ctx-null",
			Description = "No run store.",
			Steps = [TestOrchestrations.CreatePromptStep("only")],
		};

		var contexts = new List<RunContext>();
		_reporter.When(r => r.ReportRunContext(Arg.Any<RunContext>())).Do(call => contexts.Add(call.Arg<RunContext>()));

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert
		contexts.Should().HaveCount(2);
		contexts.Should().OnlyContain(c => c.DataDirectory == null);
		contexts.Should().OnlyContain(c => c.TriggeredBy == "manual");
	}

	private sealed class DirectoryAwareRunStore(List<OrchestrationRunRecord> sink) : IRunStore
	{
		public Task SaveRunAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default)
		{
			sink.Add(record);
			return Task.CompletedTask;
		}

		public Task<OrchestrationRunRecord?> GetRunAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
			=> Task.FromResult<OrchestrationRunRecord?>(sink.FirstOrDefault(r => r.OrchestrationName == orchestrationName && r.RunId == runId));

		public Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsAsync(string orchestrationName, int? limit = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>(sink.Where(r => r.OrchestrationName == orchestrationName).ToList());

		public Task<IReadOnlyList<OrchestrationRunRecord>> ListAllRunsAsync(int? limit = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>(sink.ToList());

		public Task<IReadOnlyList<OrchestrationRunRecord>> ListRunsByTriggerAsync(string triggerId, int? limit = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<OrchestrationRunRecord>>([]);

		public Task<bool> DeleteRunAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
			=> Task.FromResult(false);

		public string? ResolveRunDirectory(string orchestrationName, string orchestrationVersion, string triggeredBy, DateTimeOffset startedAt, string runId)
			=> Path.Combine("X:", "runs", orchestrationName, $"{orchestrationName}_{orchestrationVersion}_{triggeredBy}_{startedAt:yyyyMMdd-HHmmss}_{runId}");
	}
}
