using FluentAssertions;

namespace Orchestra.Engine.Tests.Domain;

public class OrchestrationTests
{
	#region Required Properties

	[Fact]
	public void Orchestration_RequiresName()
	{
		// Arrange & Act
		var orchestration = new Orchestration
		{
			Name = "test-orchestration",
			Description = "A test orchestration",
			Steps = []
		};

		// Assert
		orchestration.Name.Should().Be("test-orchestration");
	}

	[Fact]
	public void Orchestration_RequiresDescription()
	{
		// Arrange & Act
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "Test description",
			Steps = []
		};

		// Assert
		orchestration.Description.Should().Be("Test description");
	}

	[Fact]
	public void Orchestration_RequiresSteps()
	{
		// Arrange & Act
		var steps = new OrchestrationStep[]
		{
			new PromptOrchestrationStep
			{
				Name = "step1",
				Type = OrchestrationStepType.Prompt,
				DependsOn = [],
				SystemPrompt = "system",
				UserPrompt = "user",
				Model = "claude-opus-4.5"
			}
		};

		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps = steps
		};

		// Assert
		orchestration.Steps.Should().HaveCount(1);
		orchestration.Steps[0].Name.Should().Be("step1");
	}

	#endregion

	#region Default Values

	[Fact]
	public void Orchestration_Version_DefaultsTo1_0_0()
	{
		// Arrange & Act
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps = []
		};

		// Assert
		orchestration.Version.Should().Be("1.0.0");
	}

	[Fact]
	public void Orchestration_Trigger_DefaultsToNull()
	{
		// Arrange & Act
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps = []
		};

		// Assert
		orchestration.Trigger.Should().BeNull();
	}

	[Fact]
	public void Orchestration_Mcps_DefaultsToEmpty()
	{
		// Arrange & Act
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps = []
		};

		// Assert
		orchestration.Mcps.Should().BeEmpty();
	}

	[Fact]
	public void Orchestration_DefaultSystemPromptMode_DefaultsToNull()
	{
		// Arrange & Act
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps = []
		};

		// Assert
		orchestration.DefaultSystemPromptMode.Should().BeNull();
	}

	#endregion

	#region Custom Values

	[Fact]
	public void Orchestration_CanSetCustomVersion()
	{
		// Arrange & Act
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps = [],
			Version = "2.0.0"
		};

		// Assert
		orchestration.Version.Should().Be("2.0.0");
	}

	[Fact]
	public void Orchestration_CanSetDefaultSystemPromptMode()
	{
		// Arrange & Act
		var orchestration = new Orchestration
		{
			Name = "test",
			Description = "test",
			Steps = [],
			DefaultSystemPromptMode = SystemPromptMode.Replace
		};

		// Assert
		orchestration.DefaultSystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	#endregion
}
