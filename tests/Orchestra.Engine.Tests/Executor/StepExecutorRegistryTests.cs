using FluentAssertions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

public class StepExecutorRegistryTests
{
	private StepExecutorRegistry CreateRegistry() => new();

	private static IStepExecutor CreateMockExecutor(OrchestrationStepType stepType)
	{
		var executor = Substitute.For<IStepExecutor>();
		executor.StepType.Returns(stepType);
		return executor;
	}

	[Fact]
	public void Register_SingleExecutor_CanBeResolved()
	{
		// Arrange
		var registry = CreateRegistry();
		var executor = CreateMockExecutor(OrchestrationStepType.Prompt);

		// Act
		registry.Register(executor);
		var resolved = registry.Resolve(OrchestrationStepType.Prompt);

		// Assert
		resolved.Should().BeSameAs(executor);
	}

	[Fact]
	public void Register_MultipleExecutors_AllCanBeResolved()
	{
		// Arrange
		var registry = CreateRegistry();
		var promptExecutor = CreateMockExecutor(OrchestrationStepType.Prompt);
		var httpExecutor = CreateMockExecutor(OrchestrationStepType.Http);
		var transformExecutor = CreateMockExecutor(OrchestrationStepType.Transform);

		// Act
		registry.Register(promptExecutor);
		registry.Register(httpExecutor);
		registry.Register(transformExecutor);

		// Assert
		registry.Resolve(OrchestrationStepType.Prompt).Should().BeSameAs(promptExecutor);
		registry.Resolve(OrchestrationStepType.Http).Should().BeSameAs(httpExecutor);
		registry.Resolve(OrchestrationStepType.Transform).Should().BeSameAs(transformExecutor);
	}

	[Fact]
	public void Register_SameType_OverwritesPrevious()
	{
		// Arrange
		var registry = CreateRegistry();
		var first = CreateMockExecutor(OrchestrationStepType.Prompt);
		var second = CreateMockExecutor(OrchestrationStepType.Prompt);

		// Act
		registry.Register(first);
		registry.Register(second);
		var resolved = registry.Resolve(OrchestrationStepType.Prompt);

		// Assert
		resolved.Should().BeSameAs(second);
		resolved.Should().NotBeSameAs(first);
	}

	[Fact]
	public void Register_FluentChaining_ReturnsRegistry()
	{
		// Arrange
		var registry = CreateRegistry();
		var promptExecutor = CreateMockExecutor(OrchestrationStepType.Prompt);
		var httpExecutor = CreateMockExecutor(OrchestrationStepType.Http);

		// Act
		var result = registry
			.Register(promptExecutor)
			.Register(httpExecutor);

		// Assert
		result.Should().BeSameAs(registry);
		registry.Resolve(OrchestrationStepType.Prompt).Should().BeSameAs(promptExecutor);
		registry.Resolve(OrchestrationStepType.Http).Should().BeSameAs(httpExecutor);
	}

	[Fact]
	public void Resolve_UnregisteredType_ThrowsInvalidOperationException()
	{
		// Arrange
		var registry = CreateRegistry();

		// Act
		var act = () => registry.Resolve(OrchestrationStepType.Prompt);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*no step executor registered*Prompt*");
	}

	[Fact]
	public void TryResolve_RegisteredType_ReturnsTrueAndExecutor()
	{
		// Arrange
		var registry = CreateRegistry();
		var executor = CreateMockExecutor(OrchestrationStepType.Http);
		registry.Register(executor);

		// Act
		var found = registry.TryResolve(OrchestrationStepType.Http, out var resolved);

		// Assert
		found.Should().BeTrue();
		resolved.Should().BeSameAs(executor);
	}

	[Fact]
	public void TryResolve_UnregisteredType_ReturnsFalseAndNull()
	{
		// Arrange
		var registry = CreateRegistry();

		// Act
		var found = registry.TryResolve(OrchestrationStepType.Transform, out var resolved);

		// Assert
		found.Should().BeFalse();
		resolved.Should().BeNull();
	}

	[Fact]
	public void RegisteredTypes_ReturnsAllRegisteredTypes()
	{
		// Arrange
		var registry = CreateRegistry();
		registry.Register(CreateMockExecutor(OrchestrationStepType.Prompt));
		registry.Register(CreateMockExecutor(OrchestrationStepType.Http));
		registry.Register(CreateMockExecutor(OrchestrationStepType.Transform));

		// Act
		var types = registry.RegisteredTypes;

		// Assert
		types.Should().HaveCount(3);
		types.Should().Contain(OrchestrationStepType.Prompt);
		types.Should().Contain(OrchestrationStepType.Http);
		types.Should().Contain(OrchestrationStepType.Transform);
	}

	[Fact]
	public void RegisteredTypes_EmptyRegistry_ReturnsEmpty()
	{
		// Arrange
		var registry = CreateRegistry();

		// Act
		var types = registry.RegisteredTypes;

		// Assert
		types.Should().BeEmpty();
	}
}
