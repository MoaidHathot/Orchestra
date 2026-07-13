using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// End-to-end coverage that a Script step's control signal actually drives the orchestration:
/// <c>complete</c> halts the whole run (remaining steps Cancelled) and <c>no_action</c> skips
/// dependents — exactly like the LLM engine tools, but from a deterministic pwsh script.
/// </summary>
[Collection(ScriptProcessExecutionCollection.Name)]
public class ScriptControlOrchestrationTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();

	private static ScriptOrchestrationStep Pwsh(string name, string script, string[]? dependsOn = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Script,
		DependsOn = dependsOn ?? [],
		Parameters = [],
		Shell = "pwsh",
		Script = script,
		Arguments = [],
		Environment = [],
	};

	private Orchestration BuildOrchestration(params OrchestrationStep[] steps) => new()
	{
		Name = "script-control-test",
		Description = "Script control-channel end-to-end",
		Steps = steps,
		TimeoutSeconds = 600,
	};

	private OrchestrationExecutor NewExecutor() =>
		new(_scheduler, new MockAgentBuilder(), Substitute.For<IOrchestrationReporter>(), NullLoggerFactory.Instance);

	[Fact]
	public async Task ScriptComplete_HaltsOrchestration_AndCancelsDownstream()
	{
		var orchestration = BuildOrchestration(
			Pwsh("gate", "Orchestra-Complete -Status success -Reason 'Inbox is empty'"),
			Pwsh("downstream", "Write-Output 'should not run'", dependsOn: ["gate"]));

		var result = await NewExecutor().ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["gate"].OrchestrationCompleteRequested.Should().BeTrue();
		result.StepResults["gate"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["downstream"].Status.Should().Be(ExecutionStatus.Cancelled,
			"early completion cancels steps that had not started");
	}

	[Fact]
	public async Task ScriptNoAction_SkipsDependents()
	{
		var orchestration = BuildOrchestration(
			Pwsh("gate", "Orchestra-SetStatus -Status no_action -Reason 'Nothing to do'"),
			Pwsh("downstream", "Write-Output 'should not run'", dependsOn: ["gate"]));

		var result = await NewExecutor().ExecuteAsync(orchestration);

		result.StepResults["gate"].Status.Should().Be(ExecutionStatus.NoAction);
		result.StepResults["downstream"].Status.Should().Be(ExecutionStatus.Skipped,
			"a NoAction dependency skips its dependents");
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ScriptComplete_Failed_FailsOrchestration()
	{
		var orchestration = BuildOrchestration(
			Pwsh("gate", "Orchestra-Complete -Status failed -Reason 'fatal'"),
			Pwsh("downstream", "Write-Output 'nope'", dependsOn: ["gate"]));

		var result = await NewExecutor().ExecuteAsync(orchestration);

		result.StepResults["gate"].Status.Should().Be(ExecutionStatus.Failed);
		result.Status.Should().Be(ExecutionStatus.Failed);
	}
}
