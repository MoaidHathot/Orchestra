using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public partial class CopilotAgent : IAgent
{
	private readonly CopilotClient _client;
	private readonly string _model;
	private readonly string? _systemPrompt;
	private readonly Mcp[] _mcps;
	private readonly ReasoningLevel? _reasoningLevel;
	private readonly SystemPromptMode? _systemPromptMode;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<CopilotAgent> _logger;

	internal CopilotAgent(
			CopilotClient client,
			string model,
			string? systemPrompt,
			Mcp[] mcps,
			ReasoningLevel? reasoningLevel,
			SystemPromptMode? systemPromptMode,
			IOrchestrationReporter reporter,
			ILogger<CopilotAgent> logger)
	{
		_client = client;
		_model = model;
		_systemPrompt = systemPrompt;
		_mcps = mcps;
		_reasoningLevel = reasoningLevel;
		_systemPromptMode = systemPromptMode;
		_reporter = reporter;
		_logger = logger;
	}

	public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var resultTask = RunSessionAsync(prompt, channel.Writer, cancellationToken);
		return new AgentTask(channel.Reader, resultTask);
	}

	private async Task<AgentResult> RunSessionAsync(
			string prompt,
			ChannelWriter<AgentEvent> writer,
			CancellationToken cancellationToken)
	{
		try
		{
			var config = BuildSessionConfig();
			LogMcpConfiguration();

			await using var session = await _client.CreateSessionAsync(config, cancellationToken);

			var done = new TaskCompletionSource();
			var handler = new CopilotSessionHandler(writer, _reporter, _model, done);

			session.On(handler.HandleEvent);

			await session.SendAsync(new MessageOptions { Prompt = prompt }, cancellationToken);

			using var registration = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
			await done.Task;

			// Handle model mismatch detection and reporting
			var availableModels = await CheckModelMismatchAsync(handler.ActualModel, cancellationToken);

			return new AgentResult
			{
				Content = handler.FinalContent ?? string.Empty,
				SelectedModel = handler.SelectedModel,
				ActualModel = handler.ActualModel,
				Usage = handler.Usage,
				AvailableModels = availableModels,
			};
		}
		finally
		{
			writer.TryComplete();
		}
	}

	private SessionConfig BuildSessionConfig()
	{
		var config = new SessionConfig
		{
			Model = _model,
			Streaming = true,
			OnPermissionRequest = PermissionHandler.ApproveAll,
		};

		if (_reasoningLevel is not null)
		{
			config.ReasoningEffort = _reasoningLevel.Value.ToString().ToLowerInvariant();
		}

		if (_systemPrompt is not null)
		{
			config.SystemMessage = new SystemMessageConfig
			{
				Content = _systemPrompt,
			};

			if (_systemPromptMode is not null)
			{
				config.SystemMessage.Mode = _systemPromptMode.Value == SystemPromptMode.Append
					? SystemMessageMode.Append
					: SystemMessageMode.Replace;
			}
		}

		if (_mcps.Length > 0)
		{
			config.McpServers = BuildMcpServers();
		}

		return config;
	}

	private void LogMcpConfiguration()
	{
		LogMcpCount(_mcps.Length);

		if (_mcps.Length > 0)
		{
			foreach (var mcp in _mcps)
			{
				switch (mcp)
				{
					case LocalMcp local:
						LogLocalMcpServer(mcp.Name, local.Command, string.Join(", ", local.Arguments), local.WorkingDirectory);
						break;
					case RemoteMcp remote:
						LogRemoteMcpServer(mcp.Name, remote.Endpoint);
						break;
				}
			}
		}
		else
		{
			LogNoMcpsConfigured();
		}
	}

	private async Task<IReadOnlyList<AvailableModelInfo>?> CheckModelMismatchAsync(
		string? actualModel,
		CancellationToken cancellationToken)
	{
		if (actualModel is null || string.Equals(actualModel, _model, StringComparison.OrdinalIgnoreCase))
			return null;

		IReadOnlyList<AvailableModelInfo>? availableModels = null;

		try
		{
			var models = await _client.ListModelsAsync(cancellationToken);
			availableModels = models
				.OrderBy(m => m.Id)
				.Select(m => new AvailableModelInfo
				{
					Id = m.Id,
					Name = m.Name,
					BillingMultiplier = m.Billing?.Multiplier,
					ReasoningEfforts = m.SupportedReasoningEfforts is { Count: > 0 }
						? [.. m.SupportedReasoningEfforts]
						: null,
				})
				.ToList();
		}
		catch
		{
			// Unable to list models - continue without them
		}

		_reporter.ReportModelMismatch(new ModelMismatchInfo
		{
			ConfiguredModel = _model,
			ActualModel = actualModel,
			SystemPromptMode = _systemPromptMode?.ToString() ?? "(SDK default)",
			ReasoningLevel = _reasoningLevel?.ToString() ?? "(none)",
			SystemPromptPreview = _systemPrompt is not null
				? $"{_systemPrompt[..Math.Min(_systemPrompt.Length, 80)]}..."
				: "(none)",
			McpServers = _mcps.Length > 0
				? _mcps.Select(m => m.Name).ToArray()
				: null,
			AvailableModels = availableModels,
		});

		return availableModels;
	}

	private Dictionary<string, object> BuildMcpServers()
	{
		var servers = new Dictionary<string, object>();

		foreach (var mcp in _mcps)
		{
			switch (mcp)
			{
				case LocalMcp local:
					servers[mcp.Name] = new McpLocalServerConfig
					{
						Command = local.Command,
						Args = [.. local.Arguments],
						Cwd = local.WorkingDirectory,
					};
					break;

				case RemoteMcp remote:
					servers[mcp.Name] = new McpRemoteServerConfig
					{
						Url = remote.Endpoint,
						Headers = remote.Headers,
					};
					break;
			}
		}

		return servers;
	}

	#region Source-Generated Logging

	[LoggerMessage(
			EventId = 1,
			Level = LogLevel.Debug,
			Message = "Agent has {McpCount} MCPs configured")]
	private partial void LogMcpCount(int mcpCount);

	[LoggerMessage(
			EventId = 2,
			Level = LogLevel.Debug,
			Message = "Configuring local MCP server '{Name}': Command={Command}, Args=[{Args}], Cwd={WorkingDirectory}")]
	private partial void LogLocalMcpServer(string name, string command, string args, string? workingDirectory);

	[LoggerMessage(
			EventId = 3,
			Level = LogLevel.Debug,
			Message = "Configuring remote MCP server '{Name}': Url={Url}")]
	private partial void LogRemoteMcpServer(string name, string url);

	[LoggerMessage(
			EventId = 4,
			Level = LogLevel.Debug,
			Message = "No MCPs configured for this agent")]
	private partial void LogNoMcpsConfigured();

	#endregion
}
