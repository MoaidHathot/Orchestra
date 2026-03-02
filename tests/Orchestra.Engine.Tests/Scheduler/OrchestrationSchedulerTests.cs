using FluentAssertions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Scheduler;

public class OrchestrationSchedulerTests
{
	private readonly OrchestrationScheduler _scheduler = new();

	#region Valid DAG Tests

	[Fact]
	public void Schedule_EmptyOrchestration_ReturnsEmptySchedule()
	{
		// Arrange
		var orchestration = TestOrchestrations.Empty();

		// Act
		var schedule = _scheduler.Schedule(orchestration);

		// Assert
		schedule.Entries.Should().BeEmpty();
	}

	[Fact]
	public void Schedule_SingleStep_ReturnsSingleEntry()
	{
		// Arrange
		var orchestration = TestOrchestrations.SingleStep();

		// Act
		var schedule = _scheduler.Schedule(orchestration);

		// Assert
		schedule.Entries.Should().HaveCount(1);
		schedule.Entries[0].Steps.Should().HaveCount(1);
		schedule.Entries[0].Steps[0].Name.Should().Be("step1");
	}

	[Fact]
	public void Schedule_LinearChain_ReturnsThreeLayers()
	{
		// Arrange
		var orchestration = TestOrchestrations.LinearChain();

		// Act
		var schedule = _scheduler.Schedule(orchestration);

		// Assert
		schedule.Entries.Should().HaveCount(3);

		schedule.Entries[0].Steps.Should().HaveCount(1);
		schedule.Entries[0].Steps[0].Name.Should().Be("A");

		schedule.Entries[1].Steps.Should().HaveCount(1);
		schedule.Entries[1].Steps[0].Name.Should().Be("B");

		schedule.Entries[2].Steps.Should().HaveCount(1);
		schedule.Entries[2].Steps[0].Name.Should().Be("C");
	}

	[Fact]
	public void Schedule_ParallelSteps_ReturnsSingleLayerWithAllSteps()
	{
		// Arrange
		var orchestration = TestOrchestrations.ParallelSteps();

		// Act
		var schedule = _scheduler.Schedule(orchestration);

		// Assert
		schedule.Entries.Should().HaveCount(1);
		schedule.Entries[0].Steps.Should().HaveCount(3);

		var stepNames = schedule.Entries[0].Steps.Select(s => s.Name).ToArray();
		stepNames.Should().Contain("A");
		stepNames.Should().Contain("B");
		stepNames.Should().Contain("C");
	}

	[Fact]
	public void Schedule_DiamondDag_ReturnsThreeLayers()
	{
		// Arrange
		var orchestration = TestOrchestrations.DiamondDag();

		// Act
		var schedule = _scheduler.Schedule(orchestration);

		// Assert
		schedule.Entries.Should().HaveCount(3);

		// Layer 1: A (no dependencies)
		schedule.Entries[0].Steps.Should().HaveCount(1);
		schedule.Entries[0].Steps[0].Name.Should().Be("A");

		// Layer 2: B and C (both depend on A)
		schedule.Entries[1].Steps.Should().HaveCount(2);
		var layer2Names = schedule.Entries[1].Steps.Select(s => s.Name).ToArray();
		layer2Names.Should().Contain("B");
		layer2Names.Should().Contain("C");

		// Layer 3: D (depends on B and C)
		schedule.Entries[2].Steps.Should().HaveCount(1);
		schedule.Entries[2].Steps[0].Name.Should().Be("D");
	}

	[Fact]
	public void Schedule_ComplexDag_GroupsStepsInCorrectLayers()
	{
		// Arrange
		var orchestration = TestOrchestrations.ComplexDag();

		// Act
		var schedule = _scheduler.Schedule(orchestration);

		// Assert
		schedule.Entries.Should().HaveCount(3);

		// Layer 1: entry1, entry2 (no dependencies)
		schedule.Entries[0].Steps.Should().HaveCount(2);
		var layer1Names = schedule.Entries[0].Steps.Select(s => s.Name).ToArray();
		layer1Names.Should().Contain("entry1");
		layer1Names.Should().Contain("entry2");

		// Layer 2: middle1, middle2, middle3
		schedule.Entries[1].Steps.Should().HaveCount(3);
		var layer2Names = schedule.Entries[1].Steps.Select(s => s.Name).ToArray();
		layer2Names.Should().Contain("middle1");
		layer2Names.Should().Contain("middle2");
		layer2Names.Should().Contain("middle3");

		// Layer 3: final
		schedule.Entries[2].Steps.Should().HaveCount(1);
		schedule.Entries[2].Steps[0].Name.Should().Be("final");
	}

	#endregion

	#region Error Detection Tests

	[Fact]
	public void Schedule_WithCycle_ThrowsInvalidOperationException()
	{
		// Arrange
		var orchestration = TestOrchestrations.WithCycle();

		// Act
		var act = () => _scheduler.Schedule(orchestration);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Circular dependency*");
	}

	[Fact]
	public void Schedule_WithSelfReference_ThrowsInvalidOperationException()
	{
		// Arrange
		var orchestration = TestOrchestrations.WithSelfReference();

		// Act
		var act = () => _scheduler.Schedule(orchestration);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Circular dependency*");
	}

	[Fact]
	public void Schedule_WithMissingDependency_ThrowsInvalidOperationException()
	{
		// Arrange
		var orchestration = TestOrchestrations.WithMissingDependency();

		// Act
		var act = () => _scheduler.Schedule(orchestration);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*'NonExistent'*does not exist*");
	}

	[Fact]
	public void Schedule_WithDuplicateNames_ThrowsInvalidOperationException()
	{
		// Arrange
		var orchestration = TestOrchestrations.WithDuplicateNames();

		// Act
		var act = () => _scheduler.Schedule(orchestration);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Duplicate step name*");
	}

	#endregion

	#region Topological Order Verification

	[Fact]
	public void Schedule_RespectsTopologicalOrder()
	{
		// Arrange - Create a DAG where order matters
		var orchestration = new Orchestration
		{
			Name = "order-test",
			Description = "Test topological ordering",
			Steps =
			[
				TestOrchestrations.CreatePromptStep("D", dependsOn: ["B", "C"]),
				TestOrchestrations.CreatePromptStep("A"),
				TestOrchestrations.CreatePromptStep("C", dependsOn: ["A"]),
				TestOrchestrations.CreatePromptStep("B", dependsOn: ["A"])
			]
		};

		// Act
		var schedule = _scheduler.Schedule(orchestration);

		// Assert - Flatten and verify dependencies come before dependents
		var flatOrder = schedule.Entries
			.SelectMany(e => e.Steps)
			.Select(s => s.Name)
			.ToList();

		flatOrder.IndexOf("A").Should().BeLessThan(flatOrder.IndexOf("B"));
		flatOrder.IndexOf("A").Should().BeLessThan(flatOrder.IndexOf("C"));
		flatOrder.IndexOf("B").Should().BeLessThan(flatOrder.IndexOf("D"));
		flatOrder.IndexOf("C").Should().BeLessThan(flatOrder.IndexOf("D"));
	}

	[Fact]
	public void Schedule_PreservesStepReferences()
	{
		// Arrange
		var step = TestOrchestrations.CreatePromptStep("original");
		var orchestration = new Orchestration
		{
			Name = "ref-test",
			Description = "Test reference preservation",
			Steps = [step]
		};

		// Act
		var schedule = _scheduler.Schedule(orchestration);

		// Assert - Same reference should be preserved
		schedule.Entries[0].Steps[0].Should().BeSameAs(step);
	}

	#endregion
}
