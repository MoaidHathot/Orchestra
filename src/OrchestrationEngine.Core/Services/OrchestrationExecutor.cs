using System.Text.RegularExpressions;
using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Models;

namespace OrchestrationEngine.Core.Services;

/// <summary>
/// Default implementation of the orchestration engine.
/// </summary>
public sealed partial class OrchestrationExecutor : IOrchestrationEngine
{
    private readonly IAgentRepository _agentRepository;
    private readonly IProgressReporter _progressReporter;
    private const string DefaultModel = "claude-opus-4.6";

    public OrchestrationExecutor(
        IAgentRepository agentRepository,
        IProgressReporter progressReporter)
    {
        _agentRepository = agentRepository;
        _progressReporter = progressReporter;
    }

    public async Task<string> ExecuteAsync(
        OrchestrationDefinition orchestration,
        CancellationToken cancellationToken = default)
    {
        _progressReporter.ReportOrchestrationName(orchestration.Name);

        var steps = orchestration.Steps
            .Select(s => new StepInfo(s.Name, StepStatus.Pending))
            .ToList();
        _progressReporter.ReportSteps(steps);

        string previousOutput = string.Empty;

        foreach (var step in orchestration.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _progressReporter.ReportStepStarted(step.Name);
                previousOutput = await ExecuteStepAsync(step, previousOutput, cancellationToken);
                _progressReporter.ReportStepCompleted(step.Name);
            }
            catch (Exception ex)
            {
                _progressReporter.ReportStepFailed(step.Name, ex.Message);
                _progressReporter.ReportOrchestrationCompleted(false);
                throw;
            }
        }

        _progressReporter.ReportOrchestrationCompleted(true, previousOutput);
        return previousOutput;
    }

    private async Task<string> ExecuteStepAsync(
        OrchestrationStep step,
        string previousOutput,
        CancellationToken cancellationToken)
    {
        var model = step.Model ?? DefaultModel;
        string inputForStep = previousOutput;

        // Handle input transformation if specified
        if (!string.IsNullOrWhiteSpace(step.HandleInputPrompt) && !string.IsNullOrWhiteSpace(previousOutput))
        {
            inputForStep = await TransformInputAsync(
                step.HandleInputPrompt,
                previousOutput,
                model,
                cancellationToken);
        }

        // Resolve placeholders in user prompt
        var resolvedPrompt = await ResolvePromptPlaceholdersAsync(
            step.UserPrompt,
            inputForStep,
            model,
            cancellationToken);

        // Execute the main step agent
        _progressReporter.ReportActiveAgent(step.Name, AgentType.Step);
        _progressReporter.ReportAgentStatus(AgentStatus.Thinking);
        
        await using var agent = await _agentRepository.CreateOrchestrationAgentAsync(step, cancellationToken);
        var task = agent.SendAsync(resolvedPrompt, cancellationToken);
        
        await foreach (var evt in task.WithCancellation(cancellationToken))
        {
            _progressReporter.ReportAgentEvent(evt);
        }

        var stepOutput = await task.GetResultAsync(cancellationToken);

        // Handle output transformation if specified
        if (!string.IsNullOrWhiteSpace(step.HandleOutputPrompt))
        {
            stepOutput = await TransformOutputAsync(
                step.HandleOutputPrompt,
                stepOutput,
                model,
                cancellationToken);
        }

        return stepOutput;
    }

    private async Task<string> TransformInputAsync(
        string handleInputPrompt,
        string previousOutput,
        string model,
        CancellationToken cancellationToken)
    {
        _progressReporter.ReportActiveAgent("Input Handler", AgentType.InputHandler);
        _progressReporter.ReportAgentStatus(AgentStatus.Thinking);
        
        await using var handler = await _agentRepository.CreateInputHandlerAgentAsync(
            handleInputPrompt, model, cancellationToken);

        var prompt = $"""
            Previous step output:
            ---
            {previousOutput}
            ---
            
            Apply the following transformation:
            {handleInputPrompt}
            """;

        var task = handler.SendAsync(prompt, cancellationToken);
        
        await foreach (var evt in task.WithCancellation(cancellationToken))
        {
            _progressReporter.ReportAgentEvent(evt);
        }

        return await task.GetResultAsync(cancellationToken);
    }

    private async Task<string> ResolvePromptPlaceholdersAsync(
        string userPrompt,
        string inputData,
        string model,
        CancellationToken cancellationToken)
    {
        var placeholders = PlaceholderRegex().Matches(userPrompt);
        if (placeholders.Count == 0)
        {
            // No placeholders, just prepend input data if available
            return string.IsNullOrWhiteSpace(inputData) 
                ? userPrompt 
                : $"{inputData}\n\n{userPrompt}";
        }

        _progressReporter.ReportActiveAgent("Placeholder Resolver", AgentType.PlaceholderResolver);
        _progressReporter.ReportAgentStatus(AgentStatus.Thinking);
        
        await using var resolver = await _agentRepository.CreatePlaceholderAgentAsync(model, cancellationToken);

        var placeholderNames = placeholders.Select(m => m.Groups[1].Value).Distinct();
        var prompt = $"""
            Given the following input data:
            ---
            {inputData}
            ---
            
            And the following template with placeholders:
            ---
            {userPrompt}
            ---
            
            The placeholders are: {string.Join(", ", placeholderNames)}
            
            Extract the relevant information from the input data and produce the final prompt
            with all placeholders replaced by the appropriate values.
            Return ONLY the final resolved prompt, nothing else.
            """;

        var task = resolver.SendAsync(prompt, cancellationToken);
        
        await foreach (var evt in task.WithCancellation(cancellationToken))
        {
            _progressReporter.ReportAgentEvent(evt);
        }

        return await task.GetResultAsync(cancellationToken);
    }

    private async Task<string> TransformOutputAsync(
        string handleOutputPrompt,
        string stepOutput,
        string model,
        CancellationToken cancellationToken)
    {
        _progressReporter.ReportActiveAgent("Output Handler", AgentType.OutputHandler);
        _progressReporter.ReportAgentStatus(AgentStatus.Thinking);
        
        await using var handler = await _agentRepository.CreateOutputHandlerAgentAsync(
            handleOutputPrompt, model, cancellationToken);

        var prompt = $"""
            Step output:
            ---
            {stepOutput}
            ---
            
            Apply the following transformation:
            {handleOutputPrompt}
            """;

        var task = handler.SendAsync(prompt, cancellationToken);
        
        await foreach (var evt in task.WithCancellation(cancellationToken))
        {
            _progressReporter.ReportAgentEvent(evt);
        }

        return await task.GetResultAsync(cancellationToken);
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderRegex();
}
