using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Models;
using OrchestrationEngine.Core.Services;

namespace OrchestrationEngine.Copilot.Services;

/// <summary>
/// Repository for creating preconfigured and dynamic agents using Copilot SDK.
/// </summary>
internal sealed class CopilotAgentRepository : IAgentRepository
{
    private readonly IAgentBuilderFactory _builderFactory;
    private readonly PromptLoader _promptLoader;
    private const string DefaultModel = "claude-opus-4.6";

    // Prompt file names (without .md extension)
    private const string InputHandlerPromptName = "input-handler";
    private const string OutputHandlerPromptName = "output-handler";
    private const string PlaceholderPromptName = "placeholder-resolver";

    public CopilotAgentRepository(IAgentBuilderFactory builderFactory, PromptLoader promptLoader)
    {
        _builderFactory = builderFactory;
        _promptLoader = promptLoader;
    }

    public async Task<IAgent> CreateInputHandlerAgentAsync(
        string handleInputPrompt,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _promptLoader.LoadPromptAsync(InputHandlerPromptName, cancellationToken);
        
        return await _builderFactory.Create()
            .WithSystemPrompt(systemPrompt)
            .WithModel(model ?? DefaultModel)
            .WithStreaming()
            .BuildAsync(cancellationToken);
    }

    public async Task<IAgent> CreateOutputHandlerAgentAsync(
        string handleOutputPrompt,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _promptLoader.LoadPromptAsync(OutputHandlerPromptName, cancellationToken);
        
        return await _builderFactory.Create()
            .WithSystemPrompt(systemPrompt)
            .WithModel(model ?? DefaultModel)
            .WithStreaming()
            .BuildAsync(cancellationToken);
    }

    public async Task<IAgent> CreatePlaceholderAgentAsync(
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = await _promptLoader.LoadPromptAsync(PlaceholderPromptName, cancellationToken);
        
        return await _builderFactory.Create()
            .WithSystemPrompt(systemPrompt)
            .WithModel(model ?? DefaultModel)
            .WithStreaming()
            .BuildAsync(cancellationToken);
    }

    public Task<IAgent> CreateOrchestrationAgentAsync(
        OrchestrationStep step,
        CancellationToken cancellationToken = default)
    {
        var builder = _builderFactory.Create()
            .WithSystemPrompt(step.SystemPrompt)
            .WithModel(step.Model ?? DefaultModel)
            .WithStreaming();

        if (step.ToolList.Count > 0)
        {
            builder.WithMcpServers([.. step.ToolList]);
        }

        return builder.BuildAsync(cancellationToken);
    }
}
