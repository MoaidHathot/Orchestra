using GitHub.Copilot.SDK;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
{
	private CopilotClient _client = new ();

	public override async Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(Model, nameof(Model));

		// Capture state immediately to avoid race conditions with concurrent builder usage
		var model = Model;
		var systemPrompt = SystemPrompt;
		var mcps = Mcps;
		var reasoningLevel = ReasoningLevel;
		var systemPromptMode = SystemPromptMode;
		var reporter = Reporter;

		await _client.StartAsync(cancellationToken);

		return new CopilotAgent(
			client: _client,
			model: model,
			systemPrompt: systemPrompt,
			mcps: mcps,
			reasoningLevel: reasoningLevel,
			systemPromptMode: systemPromptMode,
			reporter: reporter
		);
	}

	public async ValueTask DisposeAsync()
	{
		await _client.StopAsync();
		await _client.DisposeAsync();

		GC.SuppressFinalize(this);
	}
}
