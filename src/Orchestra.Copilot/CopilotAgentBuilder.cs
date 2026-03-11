using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
{
	private CopilotClient _client = new ();
	private readonly ILoggerFactory _loggerFactory;

	public CopilotAgentBuilder(ILoggerFactory? loggerFactory = null)
	{
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
	}

	public override async Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(Model, nameof(Model));

		// Capture state immediately to avoid race conditions with concurrent builder usage
		var model = Model;
		var systemPrompt = SystemPrompt;
		var mcps = Mcps;
		var subagents = Subagents;
		var reasoningLevel = ReasoningLevel;
		var systemPromptMode = SystemPromptMode;
		var reporter = Reporter;

		await _client.StartAsync(cancellationToken);

		return new CopilotAgent(
			client: _client,
			model: model,
			systemPrompt: systemPrompt,
			mcps: mcps,
			subagents: subagents,
			reasoningLevel: reasoningLevel,
			systemPromptMode: systemPromptMode,
			reporter: reporter,
			logger: _loggerFactory.CreateLogger<CopilotAgent>()
		);
	}

	public async ValueTask DisposeAsync()
	{
		await _client.StopAsync();
		await _client.DisposeAsync();

		GC.SuppressFinalize(this);
	}
}
