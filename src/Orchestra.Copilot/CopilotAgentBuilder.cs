using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
{
	private CopilotClient _client = new ();
	private readonly ILoggerFactory _loggerFactory;
	private volatile bool _clientStarted;
	private readonly SemaphoreSlim _startLock = new(1, 1);

	/// <summary>
	/// Cached available model info from the last ListModelsAsync call.
	/// Avoids repeated network calls when multiple steps hit model mismatch in the same run.
	/// </summary>
	internal IReadOnlyList<AvailableModelInfo>? CachedAvailableModels { get; set; }

	public CopilotAgentBuilder(ILoggerFactory? loggerFactory = null)
	{
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
	}

	private async Task EnsureClientStartedAsync(CancellationToken cancellationToken)
	{
		if (_clientStarted) return;

		await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_clientStarted)
			{
				await _client.StartAsync(cancellationToken).ConfigureAwait(false);
				_clientStarted = true;
			}
		}
		finally
		{
			_startLock.Release();
		}
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

		await EnsureClientStartedAsync(cancellationToken).ConfigureAwait(false);

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
			cachedAvailableModels: CachedAvailableModels,
			onAvailableModelsListed: models => CachedAvailableModels = models,
			logger: _loggerFactory.CreateLogger<CopilotAgent>()
		);
	}

	public override async Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
	{
		await EnsureClientStartedAsync(cancellationToken).ConfigureAwait(false);

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
			cachedAvailableModels: CachedAvailableModels,
			onAvailableModelsListed: models => CachedAvailableModels = models,
			logger: _loggerFactory.CreateLogger<CopilotAgent>()
		);
	}

	public async ValueTask DisposeAsync()
	{
		await _client.StopAsync().ConfigureAwait(false);
		await _client.DisposeAsync().ConfigureAwait(false);
		_startLock.Dispose();

		GC.SuppressFinalize(this);
	}
}
