using System.Threading.Channels;

namespace Orchestra.Engine;

public class AgentTask : IAsyncEnumerable<AgentEvent>
{
	private readonly ChannelReader<AgentEvent> _reader;
	private readonly Task<AgentResult> _resultTask;

	public AgentTask(ChannelReader<AgentEvent> reader, Task<AgentResult> resultTask)
	{
		_reader = reader;
		_resultTask = resultTask;
	}

	public Task<AgentResult> GetResultAsync() => _resultTask;

	public IAsyncEnumerator<AgentEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
	{
		return _reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
	}
}
