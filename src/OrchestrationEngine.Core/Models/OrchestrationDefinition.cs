namespace OrchestrationEngine.Core.Models;

/// <summary>
/// Root definition of an orchestration loaded from JSON.
/// </summary>
public sealed record OrchestrationDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<OrchestrationStep> Steps { get; init; }
}

/// <summary>
/// A single step in the orchestration pipeline.
/// </summary>
public sealed record OrchestrationStep
{
    public required string Name { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public IReadOnlyList<string> ToolList { get; init; } = [];
    public IReadOnlyList<string> DependentOn { get; init; } = [];
    public string? HandleInputPrompt { get; init; }
    public string? HandleOutputPrompt { get; init; }
    public string? Model { get; init; }
}
