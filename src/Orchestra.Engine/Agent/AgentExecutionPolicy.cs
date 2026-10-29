namespace Orchestra.Engine;

/// <summary>Opt-in, step-local automatic continuation. Tool iterations do not consume this budget.</summary>
public sealed record AgentExecutionPolicy
{
	public AgentExecutionMode Mode { get; init; } = AgentExecutionMode.Standard;
	public int MaxContinues { get; init; } = 5;

	public void Validate()
	{
		if (!Enum.IsDefined(Mode))
			throw new ArgumentException("executionPolicy.mode must be standard or autopilot.");
		if (MaxContinues < 0)
			throw new ArgumentException("executionPolicy.maxContinues must be non-negative.");
	}
}

public enum AgentExecutionMode { Standard, Autopilot }
