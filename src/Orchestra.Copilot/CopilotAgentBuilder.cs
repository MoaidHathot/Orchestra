using GitHub.Copilot.SDK;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
{
	private CopilotClient _client = new ();

	public override async Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(Model, nameof(Model));

		await _client.StartAsync(cancellationToken);

		return new CopilotAgent(
			client: _client,
			model: Model,
			systemPrompt: SystemPrompt,
			mcps: Mcps,
			reasoningLevel: ReasoningLevel,
			systemPromptMode: SystemPromptMode,
			reporter: Reporter
		);
	}

	public async ValueTask DisposeAsync()
	{
		await _client.StopAsync();
		await _client.DisposeAsync();

		GC.SuppressFinalize(this);
	}
}
