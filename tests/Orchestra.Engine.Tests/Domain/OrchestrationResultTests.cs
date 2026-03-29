using FluentAssertions;

namespace Orchestra.Engine.Tests.Domain;

public class OrchestrationResultTests
{
	#region From Factory Method

	[Fact]
	public void From_SingleTerminalStep_ReturnsSucceeded()
	{
		// Arrange
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "sys",
					UserPrompt = "user",
					Model = "model"
				}
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["step1"] = ExecutionResult.Succeeded("Output")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Results.Should().ContainKey("step1");
		result.StepResults.Should().ContainKey("step1");
	}

	[Fact]
	public void From_TerminalStepFailed_ReturnsFailedStatus()
	{
		// Arrange
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "sys",
					UserPrompt = "user",
					Model = "model"
				}
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["step1"] = ExecutionResult.Failed("Error occurred")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public void From_TerminalStepSkipped_ReturnsCancelledStatus()
	{
		// Arrange
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "sys",
					UserPrompt = "user",
					Model = "model"
				}
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["step1"] = ExecutionResult.Skipped("Dependency failed")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert — Skipped without any Failed step yields Cancelled at orchestration level
		result.Status.Should().Be(ExecutionStatus.Cancelled);
	}

	[Fact]
	public void From_LinearChain_OnlyTerminalStepInResults()
	{
		// Arrange - step1 -> step2 (terminal)
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					SystemPrompt = "sys",
					UserPrompt = "user",
					Model = "model"
				},
				new PromptOrchestrationStep
				{
					Name = "step2",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["step1"],
					SystemPrompt = "sys",
					UserPrompt = "user",
					Model = "model"
				}
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["step1"] = ExecutionResult.Succeeded("Output1"),
			["step2"] = ExecutionResult.Succeeded("Output2")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert - Only step2 is terminal (step1 is depended on)
		result.Results.Should().HaveCount(1);
		result.Results.Should().ContainKey("step2");
		result.Results.Should().NotContainKey("step1");

		// StepResults contains all
		result.StepResults.Should().HaveCount(2);
		result.StepResults.Should().ContainKey("step1");
		result.StepResults.Should().ContainKey("step2");
	}

	[Fact]
	public void From_DiamondDag_OnlyFinalStepIsTerminal()
	{
		// Arrange - Diamond: A -> B, A -> C, B -> D, C -> D
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "C", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "D", Type = OrchestrationStepType.Prompt, DependsOn = ["B", "C"], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["A"] = ExecutionResult.Succeeded("A output"),
			["B"] = ExecutionResult.Succeeded("B output"),
			["C"] = ExecutionResult.Succeeded("C output"),
			["D"] = ExecutionResult.Succeeded("D output")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert - Only D is terminal
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Results.Should().HaveCount(1);
		result.Results.Should().ContainKey("D");
		result.StepResults.Should().HaveCount(4);
	}

	[Fact]
	public void From_ParallelTerminalSteps_AllTerminalStepsInResults()
	{
		// Arrange - A -> B, A -> C (both B and C are terminal)
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "C", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["A"] = ExecutionResult.Succeeded("A output"),
			["B"] = ExecutionResult.Succeeded("B output"),
			["C"] = ExecutionResult.Succeeded("C output")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert - Both B and C are terminal
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Results.Should().HaveCount(2);
		result.Results.Should().ContainKey("B");
		result.Results.Should().ContainKey("C");
		result.Results.Should().NotContainKey("A");
	}

	[Fact]
	public void From_OneTerminalFails_WholeOrchestrationFails()
	{
		// Arrange - A -> B, A -> C (B succeeds, C fails)
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "C", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["A"] = ExecutionResult.Succeeded("A output"),
			["B"] = ExecutionResult.Succeeded("B output"),
			["C"] = ExecutionResult.Failed("C failed")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert - Orchestration fails because one terminal step failed
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public void From_IntermediateStepFails_TerminalStepSkipped_OrchestrationFails()
	{
		// Arrange - A -> B (A fails, B is skipped)
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["A"] = ExecutionResult.Failed("A failed"),
			["B"] = ExecutionResult.Skipped("Dependency A failed")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.Results.Should().ContainKey("B");
		result.Results["B"].Status.Should().Be(ExecutionStatus.Skipped);
	}

	#endregion

	#region Cancelled Status

	[Fact]
	public void From_TerminalStepCancelled_ReturnsCancelledStatus()
	{
		// Arrange
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "step1", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["step1"] = ExecutionResult.Cancelled()
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
	}

	[Fact]
	public void From_CancelledAndSucceeded_ReturnsCancelledStatus()
	{
		// Arrange — two parallel terminal steps, one succeeds and one is cancelled
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["A"] = ExecutionResult.Succeeded("done"),
			["B"] = ExecutionResult.Cancelled()
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
	}

	[Fact]
	public void From_CancelledAndFailed_ReturnsFailedStatus()
	{
		// Arrange — Failed takes priority over Cancelled
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["A"] = ExecutionResult.Failed("error"),
			["B"] = ExecutionResult.Cancelled()
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert — Failed takes priority
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public void From_NonTerminalCancelled_TerminalSkipped_ReturnsCancelledStatus()
	{
		// Arrange — A is cancelled, B depends on A and is skipped.
		// No step actually Failed, so orchestration should be Cancelled.
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["A"] = ExecutionResult.Cancelled(),
			["B"] = ExecutionResult.Skipped("Dependency cancelled")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.Results.Should().ContainKey("B");
		result.Results["B"].Status.Should().Be(ExecutionStatus.Skipped);
	}

	[Fact]
	public void From_NonTerminalFailed_TerminalSkipped_ReturnsFailedStatus()
	{
		// Arrange — A fails (non-terminal), B depends on A and is skipped (terminal).
		// hasAnyFailed is true because A failed, so orchestration should be Failed.
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "A", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "B", Type = OrchestrationStepType.Prompt, DependsOn = ["A"], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["A"] = ExecutionResult.Failed("error"),
			["B"] = ExecutionResult.Skipped("Dependency failed")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	#endregion

	#region Case Insensitivity

	[Fact]
	public void From_DependsOn_IsCaseInsensitive()
	{
		// Arrange - step1 depends on "STEP0" but step is named "step0"
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "step0", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "step1", Type = OrchestrationStepType.Prompt, DependsOn = ["STEP0"], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["step0"] = ExecutionResult.Succeeded("Output0"),
			["step1"] = ExecutionResult.Succeeded("Output1")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert - step0 should NOT be in Results since step1 depends on it (case insensitive)
		result.Results.Should().HaveCount(1);
		result.Results.Should().ContainKey("step1");
	}

	#endregion

	#region CompletedByStep

	[Fact]
	public void From_WithOrchestrationCompleteStepName_SetsCompletedByStep()
	{
		// Arrange
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "step1", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" },
				new PromptOrchestrationStep { Name = "step2", Type = OrchestrationStepType.Prompt, DependsOn = ["step1"], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["step1"] = ExecutionResult.Succeeded("Output"),
			["step2"] = ExecutionResult.Cancelled()
		};

		// Act
		var result = OrchestrationResult.From(
			orchestration, stepResults,
			orchestrationCompleteStatus: ExecutionStatus.Succeeded,
			orchestrationCompleteReason: "Nothing to do",
			orchestrationCompleteStepName: "step1");

		// Assert
		result.CompletedByStep.Should().Be("step1");
		result.CompletionReason.Should().Be("Nothing to do");
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public void From_WithoutOrchestrationCompleteStepName_CompletedByStepIsNull()
	{
		// Arrange
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps =
			[
				new PromptOrchestrationStep { Name = "step1", Type = OrchestrationStepType.Prompt, DependsOn = [], SystemPrompt = "s", UserPrompt = "u", Model = "m" }
			]
		};

		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["step1"] = ExecutionResult.Succeeded("Output")
		};

		// Act
		var result = OrchestrationResult.From(orchestration, stepResults);

		// Assert
		result.CompletedByStep.Should().BeNull();
		result.CompletionReason.Should().BeNull();
	}

	#endregion
}
