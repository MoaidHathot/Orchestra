namespace Orchestra.Engine;

public abstract class OrchestrationStep
{
	public required string Name { get; init; }
	public required OrchestrationStep Type { get; init; }
	public required OrchestrationStep[] DependsOn { get; init; }
}
