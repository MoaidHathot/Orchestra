using System.Threading.Channels;
using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Events;

namespace OrchestrationEngine.Copilot.Services;

/// <summary>
/// Default implementation of IAITask that collects events and provides streaming.
/// </summary>
internal sealed class CopilotAITask : IAITask
{
    private readonly Channel<AgentEvent> _eventChannel;
    private readonly TaskCompletionSource<string> _resultTcs = new();
    private string _accumulatedResponse = string.Empty;

    public CopilotAITask()
    {
        _eventChannel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    }

    public ChannelWriter<AgentEvent> Writer => _eventChannel.Writer;

    public void AppendResponse(string delta)
    {
        _accumulatedResponse += delta;
    }

    public void Complete()
    {
        _eventChannel.Writer.TryComplete();
        _resultTcs.TrySetResult(_accumulatedResponse);
    }

    public void Fail(Exception exception)
    {
        _eventChannel.Writer.TryComplete(exception);
        _resultTcs.TrySetException(exception);
    }

    public async IAsyncEnumerator<AgentEvent> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }

    public Task<string> GetResultAsync(CancellationToken cancellationToken = default)
    {
        return _resultTcs.Task.WaitAsync(cancellationToken);
    }
}
