namespace Orchestra.Engine.Tests.TestHelpers;

/// <summary>
/// Factory methods for creating test orchestrations with various DAG configurations.
/// </summary>
public static class TestOrchestrations
{
	/// <summary>
	/// Creates a simple single-step orchestration.
	/// </summary>
	public static Orchestration SingleStep(string name = "single-step") => new()
	{
		Name = name,
		Description = "Single step orchestration",
		Steps =
		[
			CreatePromptStep("step1")
		]
	};

	/// <summary>
	/// Creates a linear chain: A -> B -> C
	/// </summary>
	public static Orchestration LinearChain(string name = "linear-chain") => new()
	{
		Name = name,
		Description = "Linear chain: A -> B -> C",
		Steps =
		[
			CreatePromptStep("A"),
			CreatePromptStep("B", dependsOn: ["A"]),
			CreatePromptStep("C", dependsOn: ["B"])
		]
	};

	/// <summary>
	/// Creates parallel steps with no dependencies: A, B, C (all run concurrently)
	/// </summary>
	public static Orchestration ParallelSteps(string name = "parallel") => new()
	{
		Name = name,
		Description = "Parallel steps: A, B, C",
		Steps =
		[
			CreatePromptStep("A"),
			CreatePromptStep("B"),
			CreatePromptStep("C")
		]
	};

	/// <summary>
	/// Creates a diamond DAG: A -> B, A -> C, B -> D, C -> D
	/// </summary>
	public static Orchestration DiamondDag(string name = "diamond") => new()
	{
		Name = name,
		Description = "Diamond DAG: A -> B,C -> D",
		Steps =
		[
			CreatePromptStep("A"),
			CreatePromptStep("B", dependsOn: ["A"]),
			CreatePromptStep("C", dependsOn: ["A"]),
			CreatePromptStep("D", dependsOn: ["B", "C"])
		]
	};

	/// <summary>
	/// Creates a complex DAG with multiple entry points and merge points.
	/// </summary>
	public static Orchestration ComplexDag(string name = "complex") => new()
	{
		Name = name,
		Description = "Complex DAG with multiple paths",
		Steps =
		[
			CreatePromptStep("entry1"),
			CreatePromptStep("entry2"),
			CreatePromptStep("middle1", dependsOn: ["entry1"]),
			CreatePromptStep("middle2", dependsOn: ["entry1", "entry2"]),
			CreatePromptStep("middle3", dependsOn: ["entry2"]),
			CreatePromptStep("final", dependsOn: ["middle1", "middle2", "middle3"])
		]
	};

	/// <summary>
	/// Creates an orchestration with a cycle: A -> B -> C -> A
	/// </summary>
	public static Orchestration WithCycle(string name = "cyclic") => new()
	{
		Name = name,
		Description = "Cyclic: A -> B -> C -> A",
		Steps =
		[
			CreatePromptStep("A", dependsOn: ["C"]),
			CreatePromptStep("B", dependsOn: ["A"]),
			CreatePromptStep("C", dependsOn: ["B"])
		]
	};

	/// <summary>
	/// Creates an orchestration with a self-referencing step.
	/// </summary>
	public static Orchestration WithSelfReference(string name = "self-ref") => new()
	{
		Name = name,
		Description = "Self-reference: A -> A",
		Steps =
		[
			CreatePromptStep("A", dependsOn: ["A"])
		]
	};

	/// <summary>
	/// Creates an orchestration with a missing dependency.
	/// </summary>
	public static Orchestration WithMissingDependency(string name = "missing-dep") => new()
	{
		Name = name,
		Description = "Missing dependency: A -> NonExistent",
		Steps =
		[
			CreatePromptStep("A", dependsOn: ["NonExistent"])
		]
	};

	/// <summary>
	/// Creates an orchestration with duplicate step names.
	/// </summary>
	public static Orchestration WithDuplicateNames(string name = "duplicate") => new()
	{
		Name = name,
		Description = "Duplicate step names",
		Steps =
		[
			CreatePromptStep("A"),
			CreatePromptStep("A") // Duplicate
		]
	};

	/// <summary>
	/// Creates an empty orchestration with no steps.
	/// </summary>
	public static Orchestration Empty(string name = "empty") => new()
	{
		Name = name,
		Description = "Empty orchestration",
		Steps = []
	};

	/// <summary>
	/// Creates an orchestration with parameters.
	/// </summary>
	public static Orchestration WithParameters(string name = "with-params") => new()
	{
		Name = name,
		Description = "Orchestration with parameters",
		Steps =
		[
			CreatePromptStep("A", parameters: ["param1", "param2"]),
			CreatePromptStep("B", dependsOn: ["A"], parameters: ["param2", "param3"])
		]
	};

	/// <summary>
	/// Helper to create a minimal PromptOrchestrationStep for testing.
	/// </summary>
	public static PromptOrchestrationStep CreatePromptStep(
		string name,
		string[]? dependsOn = null,
		string[]? parameters = null,
		string systemPrompt = "You are a test assistant.",
		string userPrompt = "Test prompt",
		string model = "claude-opus-4.5")
	{
		return new PromptOrchestrationStep
		{
			Name = name,
			Type = OrchestrationStepType.Prompt,
			DependsOn = dependsOn ?? [],
			Parameters = parameters ?? [],
			SystemPrompt = systemPrompt,
			UserPrompt = userPrompt,
			Model = model
		};
	}

	/// <summary>
	/// Creates a step with parameter placeholders in the prompt.
	/// </summary>
	public static PromptOrchestrationStep CreateStepWithParameterizedPrompt(
		string name,
		string userPrompt,
		string[] parameters,
		string[]? dependsOn = null)
	{
		return new PromptOrchestrationStep
		{
			Name = name,
			Type = OrchestrationStepType.Prompt,
			DependsOn = dependsOn ?? [],
			Parameters = parameters,
			SystemPrompt = "You are a test assistant.",
			UserPrompt = userPrompt,
			Model = "claude-opus-4.5"
		};
	}

	/// <summary>
	/// Creates a step with a specific SystemPromptMode.
	/// </summary>
	public static PromptOrchestrationStep CreatePromptStepWithSystemPromptMode(
		string name,
		SystemPromptMode systemPromptMode,
		string[]? dependsOn = null,
		string model = "claude-opus-4.5")
	{
		return new PromptOrchestrationStep
		{
			Name = name,
			Type = OrchestrationStepType.Prompt,
			DependsOn = dependsOn ?? [],
			Parameters = [],
			SystemPrompt = "You are a test assistant.",
			UserPrompt = "Test prompt",
			Model = model,
			SystemPromptMode = systemPromptMode
		};
	}
}
