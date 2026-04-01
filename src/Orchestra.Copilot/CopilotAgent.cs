using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public partial class CopilotAgent : IAgent
{
	private readonly CopilotClient _client;
	private readonly string _model;
	private readonly string? _systemPrompt;
	private readonly Mcp[] _mcps;
	private readonly Subagent[] _subagents;
	private readonly ReasoningLevel? _reasoningLevel;
	private readonly SystemPromptMode? _systemPromptMode;
	private readonly IOrchestrationReporter _reporter;
	private readonly IReadOnlyCollection<IEngineTool> _engineTools;
	private readonly EngineToolContext? _engineToolContext;
	private readonly string[] _skillDirectories;
	private readonly ILogger<CopilotAgent> _logger;

	internal CopilotAgent(
			CopilotClient client,
			string model,
			string? systemPrompt,
			Mcp[] mcps,
			Subagent[] subagents,
			ReasoningLevel? reasoningLevel,
			SystemPromptMode? systemPromptMode,
			IOrchestrationReporter reporter,
			IReadOnlyCollection<IEngineTool> engineTools,
			EngineToolContext? engineToolContext,
			string[] skillDirectories,
			ILogger<CopilotAgent> logger)
	{
		_client = client;
		_model = model;
		_systemPrompt = systemPrompt;
		_mcps = mcps;
		_subagents = subagents;
		_reasoningLevel = reasoningLevel;
		_systemPromptMode = systemPromptMode;
		_reporter = reporter;
		_engineTools = engineTools;
		_engineToolContext = engineToolContext;
		_skillDirectories = skillDirectories;
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

	internal SessionConfig BuildSessionConfig()
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

		if (_subagents.Length > 0)
		{
			config.CustomAgents = BuildCustomAgents();
		}

		// Register engine tools as custom AIFunction instances
		if (_engineTools.Count > 0 && _engineToolContext is not null)
		{
			config.Tools = BuildEngineTools();
		}

		if (_skillDirectories.Length > 0)
		{
			config.SkillDirectories = [.. _skillDirectories];
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
						Tools = ["*"],
					};
					break;

				case RemoteMcp remote:
					servers[mcp.Name] = new McpRemoteServerConfig
					{
						Url = remote.Endpoint,
						Headers = remote.Headers,
						Tools = ["*"],
					};
					break;
			}
		}

		return servers;
	}

	private List<CustomAgentConfig> BuildCustomAgents()
	{
		var customAgents = new List<CustomAgentConfig>();

		foreach (var subagent in _subagents)
		{
			var config = new CustomAgentConfig
			{
				Name = subagent.Name,
				Prompt = subagent.Prompt,
			};

			if (subagent.DisplayName is not null)
				config.DisplayName = subagent.DisplayName;

			if (subagent.Description is not null)
				config.Description = subagent.Description;

			if (subagent.Tools is { Length: > 0 })
				config.Tools = [.. subagent.Tools];

			if (!subagent.Infer)
				config.Infer = false;

			// Add MCP servers specific to this subagent
			if (subagent.Mcps.Length > 0)
			{
				config.McpServers = BuildMcpServersFor(subagent.Mcps);
			}

			customAgents.Add(config);
		}

		LogSubagentConfiguration();
		return customAgents;
	}

	private Dictionary<string, object> BuildMcpServersFor(Mcp[] mcps)
	{
		var servers = new Dictionary<string, object>();

		foreach (var mcp in mcps)
		{
			switch (mcp)
			{
				case LocalMcp local:
					servers[mcp.Name] = new McpLocalServerConfig
					{
						Command = local.Command,
						Args = [.. local.Arguments],
						Cwd = local.WorkingDirectory,
						Tools = ["*"],
					};
					break;

				case RemoteMcp remote:
					servers[mcp.Name] = new McpRemoteServerConfig
					{
						Url = remote.Endpoint,
						Headers = remote.Headers,
						Tools = ["*"],
					};
					break;
			}
		}

		return servers;
	}

	private void LogSubagentConfiguration()
	{
		LogSubagentCount(_subagents.Length);

		foreach (var subagent in _subagents)
		{
			LogSubagentDetails(
				subagent.Name,
				subagent.DisplayName ?? "(none)",
				subagent.Tools is { Length: > 0 } ? string.Join(", ", subagent.Tools) : "all",
				subagent.Mcps.Length,
				subagent.Infer);
		}
	}

	/// <summary>
	/// Converts engine tools to AIFunction instances that the Copilot SDK can register.
	/// Each engine tool is wrapped in an <see cref="EngineToolAIFunction"/> that delegates
	/// to <see cref="IEngineTool.Execute"/> with the shared <see cref="EngineToolContext"/>.
	/// </summary>
	private List<AIFunction> BuildEngineTools()
	{
		var functions = new List<AIFunction>();

		foreach (var tool in _engineTools)
		{
			functions.Add(new EngineToolAIFunction(tool, _engineToolContext!));
		}

		return functions;
	}

	#region Source-Generated Logging

	[LoggerMessage(
			EventId = 1,
			Level = LogLevel.Information,
			Message = "Agent has {McpCount} MCPs configured")]
	private partial void LogMcpCount(int mcpCount);

	[LoggerMessage(
			EventId = 2,
			Level = LogLevel.Information,
			Message = "Configuring local MCP server '{Name}': Command={Command}, Args=[{Args}], Cwd={WorkingDirectory}")]
	private partial void LogLocalMcpServer(string name, string command, string args, string? workingDirectory);

	[LoggerMessage(
			EventId = 3,
			Level = LogLevel.Information,
			Message = "Configuring remote MCP server '{Name}': Url={Url}")]
	private partial void LogRemoteMcpServer(string name, string url);

	[LoggerMessage(
			EventId = 4,
			Level = LogLevel.Debug,
			Message = "No MCPs configured for this agent")]
	private partial void LogNoMcpsConfigured();

	[LoggerMessage(
			EventId = 5,
			Level = LogLevel.Debug,
			Message = "Agent has {SubagentCount} subagents configured")]
	private partial void LogSubagentCount(int subagentCount);

	[LoggerMessage(
			EventId = 6,
			Level = LogLevel.Debug,
			Message = "Configuring subagent '{Name}': DisplayName={DisplayName}, Tools=[{Tools}], McpCount={McpCount}, Infer={Infer}")]
	private partial void LogSubagentDetails(string name, string displayName, string tools, int mcpCount, bool infer);

	#endregion
}
