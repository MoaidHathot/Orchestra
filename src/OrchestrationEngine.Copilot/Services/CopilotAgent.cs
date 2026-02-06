using GitHub.Copilot.SDK;
using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Events;

namespace OrchestrationEngine.Copilot.Services;

/// <summary>
/// Agent implementation using GitHub Copilot SDK.
/// </summary>
internal sealed class CopilotAgent : IAgent
{
    private readonly CopilotSession _session;
    private bool _disposed;

    public CopilotAgent(CopilotSession session)
    {
        _session = session;
    }

    public IAITask SendAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var task = new CopilotAITask();

        // Subscribe to session events
        _session.On(evt =>
        {
            switch (evt)
            {
                case AssistantReasoningDeltaEvent reasoning:
                    task.Writer.TryWrite(new ReasoningDeltaEvent(reasoning.Data.DeltaContent ?? string.Empty));
                    break;

                case AssistantReasoningEvent reasoningComplete:
                    task.Writer.TryWrite(new ReasoningCompleteEvent(reasoningComplete.Data.Content ?? string.Empty));
                    break;

                case AssistantMessageDeltaEvent delta:
                    var content = delta.Data.DeltaContent ?? string.Empty;
                    task.AppendResponse(content);
                    task.Writer.TryWrite(new ResponseDeltaEvent(content));
                    break;

                case AssistantMessageEvent message:
                    task.Writer.TryWrite(new ResponseCompleteEvent(message.Data.Content ?? string.Empty));
                    break;

                case ToolExecutionStartEvent toolStart:
                    task.Writer.TryWrite(new ToolCallStartEvent(
                        toolStart.Data.ToolName ?? "unknown",
                        "{}"));
                    break;

                case ToolExecutionCompleteEvent toolComplete:
                    task.Writer.TryWrite(new ToolCallEndEvent(
                        "tool",
                        toolComplete.Data.Result?.ToString() ?? string.Empty));
                    break;

                case SessionIdleEvent:
                    task.Writer.TryWrite(new CompletedEvent());
                    task.Complete();
                    break;

                case SessionErrorEvent error:
                    var ex = new Exception(error.Data.Message ?? "Unknown error");
                    task.Writer.TryWrite(new ErrorEvent(error.Data.Message ?? "Unknown error", ex));
                    task.Fail(ex);
                    break;
            }
        });

        // Send the prompt asynchronously (use SendAsync, not SendAndWaitAsync, to avoid timeout issues)
        // Completion is tracked via SessionIdleEvent in the event handler above
        _ = Task.Run(async () =>
        {
            try
            {
                await _session.SendAsync(new MessageOptions { Prompt = prompt });
            }
            catch (Exception ex)
            {
                task.Writer.TryWrite(new ErrorEvent(ex.Message, ex));
                task.Fail(ex);
            }
        }, cancellationToken);

        return task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _session.DisposeAsync();
    }
}
