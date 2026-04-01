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
		var engineTools = EngineTools;
		var engineToolCtx = EngineToolCtx;
		var skillDirectories = SkillDirectories;

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
			engineTools: engineTools,
			engineToolContext: engineToolCtx,
			skillDirectories: skillDirectories,
			logger: _loggerFactory.CreateLogger<CopilotAgent>()
		);
	}

	public override async Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
	{
		await _client.StartAsync(cancellationToken);

		return new CopilotAgent(
			client: _client,
			model: config.Model,
			systemPrompt: config.SystemPrompt,
			mcps: config.Mcps,
			subagents: config.Subagents,
			reasoningLevel: config.ReasoningLevel,
			systemPromptMode: config.SystemPromptMode,
			reporter: config.Reporter,
			engineTools: config.EngineTools,
			engineToolContext: config.EngineToolCtx,
			skillDirectories: config.SkillDirectories,
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
