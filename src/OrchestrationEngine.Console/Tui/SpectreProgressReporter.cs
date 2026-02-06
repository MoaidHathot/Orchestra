using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Events;
using Spectre.Console;
using System.Diagnostics;

namespace OrchestrationEngine.Console.Tui;

/// <summary>
/// TUI that shows detailed progress with agent type and status.
/// </summary>
public sealed class SpectreProgressReporter : IProgressReporter
{
    private readonly Stopwatch _orchestrationTimer = new();
    private readonly Stopwatch _stepTimer = new();
    private readonly Stopwatch _agentTimer = new();
    
    private string _currentReasoning = string.Empty;
    private string _currentResponse = string.Empty;
    private string _currentAgentName = string.Empty;
    private AgentType _currentAgentType;
    private AgentStatus _currentStatus = AgentStatus.Initializing;
    private int _toolCallCount;

    public void ReportOrchestrationName(string name)
    {
        _orchestrationTimer.Restart();
        
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText(name).Color(Color.Cyan1));
        AnsiConsole.WriteLine();
    }

    public void ReportSteps(IReadOnlyList<StepInfo> steps)
    {
        AnsiConsole.MarkupLine("[bold]Pipeline Steps:[/]");
        foreach (var step in steps)
        {
            AnsiConsole.MarkupLine($"  [grey]{step.Name}[/]");
        }
        AnsiConsole.WriteLine();
    }

    public void ReportStepStarted(string stepName)
    {
        _currentReasoning = string.Empty;
        _currentResponse = string.Empty;
        _toolCallCount = 0;
        _stepTimer.Restart();
        
        AnsiConsole.Write(new Rule($"[yellow bold]{Markup.Escape(stepName)}[/]").LeftJustified());
    }

    public void ReportStepCompleted(string stepName)
    {
        var elapsed = _stepTimer.Elapsed;
        
        // Show reasoning if captured
        if (!string.IsNullOrWhiteSpace(_currentReasoning))
        {
            AnsiConsole.Write(new Panel(Markup.Escape(TruncateText(_currentReasoning, 600)))
            {
                Header = new PanelHeader("[blue]Reasoning[/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("blue"),
                Expand = false
            });
        }
        
        // Show response if captured
        if (!string.IsNullOrWhiteSpace(_currentResponse))
        {
            AnsiConsole.Write(new Panel(Markup.Escape(TruncateText(_currentResponse, 1000)))
            {
                Header = new PanelHeader("[green]Response[/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse("green"),
                Expand = false
            });
        }
        
        AnsiConsole.MarkupLine($"[green]Done[/] [grey]({elapsed:mm\\:ss\\.f})[/]");
        AnsiConsole.WriteLine();
    }

    public void ReportStepFailed(string stepName, string error)
    {
        AnsiConsole.MarkupLine($"[red]Failed: {Markup.Escape(error)}[/]");
        AnsiConsole.WriteLine();
    }

    public void ReportActiveAgent(string agentName, AgentType agentType)
    {
        _currentAgentName = agentName;
        _currentAgentType = agentType;
        _currentStatus = AgentStatus.Initializing;
        _agentTimer.Restart();

        var typeLabel = agentType switch
        {
            AgentType.Step => "[cyan]Step Agent[/]",
            AgentType.InputHandler => "[magenta]Input Handler[/]",
            AgentType.OutputHandler => "[magenta]Output Handler[/]",
            AgentType.PlaceholderResolver => "[yellow]Placeholder Resolver[/]",
            _ => "[grey]Agent[/]"
        };

        var description = agentType switch
        {
            AgentType.Step => "Executing main step logic",
            AgentType.InputHandler => "Transforming input from previous step",
            AgentType.OutputHandler => "Transforming step output",
            AgentType.PlaceholderResolver => "Resolving template placeholders",
            _ => ""
        };

        AnsiConsole.MarkupLine($"  {typeLabel}: [white]{Markup.Escape(agentName)}[/]");
        if (!string.IsNullOrEmpty(description))
        {
            AnsiConsole.MarkupLine($"    [grey]{description}[/]");
        }
    }

    public void ReportAgentStatus(AgentStatus status, string? detail = null)
    {
        _currentStatus = status;
        var elapsed = _agentTimer.Elapsed;

        var statusText = status switch
        {
            AgentStatus.Initializing => "[grey]Initializing...[/]",
            AgentStatus.Thinking => "[yellow]Thinking...[/]",
            AgentStatus.Reasoning => "[blue]Reasoning...[/]",
            AgentStatus.Streaming => "[green]Streaming response...[/]",
            AgentStatus.CallingTool => $"[purple]Calling tool: {Markup.Escape(detail ?? "unknown")}[/]",
            AgentStatus.Completed => "[green]Completed[/]",
            _ => "[grey]Working...[/]"
        };

        AnsiConsole.MarkupLine($"    {statusText} [grey]({elapsed:mm\\:ss})[/]");
    }

    public void ReportAgentEvent(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case ReasoningDeltaEvent reasoning:
                if (_currentStatus != AgentStatus.Reasoning)
                {
                    _currentStatus = AgentStatus.Reasoning;
                    AnsiConsole.MarkupLine($"    [blue]Reasoning...[/] [grey]({_agentTimer.Elapsed:mm\\:ss})[/]");
                }
                _currentReasoning += reasoning.Delta;
                break;
                
            case ResponseDeltaEvent response:
                if (_currentStatus != AgentStatus.Streaming)
                {
                    _currentStatus = AgentStatus.Streaming;
                    AnsiConsole.MarkupLine($"    [green]Streaming response...[/] [grey]({_agentTimer.Elapsed:mm\\:ss})[/]");
                }
                _currentResponse += response.Delta;
                break;
                
            case ToolCallStartEvent tool:
                _toolCallCount++;
                _currentStatus = AgentStatus.CallingTool;
                AnsiConsole.MarkupLine($"    [purple]Tool #{_toolCallCount}:[/] {Markup.Escape(tool.ToolName)} [grey]({_agentTimer.Elapsed:mm\\:ss})[/]");
                if (!string.IsNullOrWhiteSpace(tool.Arguments) && tool.Arguments != "{}")
                {
                    var args = TruncateText(tool.Arguments, 100);
                    AnsiConsole.MarkupLine($"      [grey]Args: {Markup.Escape(args)}[/]");
                }
                break;
                
            case ToolCallEndEvent tool:
                if (!string.IsNullOrWhiteSpace(tool.Result))
                {
                    var result = TruncateText(tool.Result, 120);
                    AnsiConsole.MarkupLine($"      [grey]-> {Markup.Escape(result)}[/]");
                }
                break;
                
            case CompletedEvent:
                AnsiConsole.MarkupLine($"    [green]Agent completed[/] [grey]({_agentTimer.Elapsed:mm\\:ss\\.f})[/]");
                break;
                
            case ErrorEvent error:
                AnsiConsole.MarkupLine($"    [red]Error: {Markup.Escape(error.Message)}[/]");
                break;
        }
    }

    public void ReportOrchestrationCompleted(bool success, string? finalOutput = null)
    {
        _orchestrationTimer.Stop();
        var elapsed = _orchestrationTimer.Elapsed;
        
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        
        if (success)
        {
            AnsiConsole.MarkupLine($"[green bold]Completed successfully[/] [grey]Total time: {elapsed:mm\\:ss\\.fff}[/]");
            
            if (!string.IsNullOrWhiteSpace(finalOutput))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Panel(Markup.Escape(finalOutput))
                {
                    Header = new PanelHeader("[green bold]Final Output[/]"),
                    Border = BoxBorder.Double,
                    BorderStyle = Style.Parse("green")
                });
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red bold]Failed[/] [grey]Total time: {elapsed:mm\\:ss\\.fff}[/]");
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}
