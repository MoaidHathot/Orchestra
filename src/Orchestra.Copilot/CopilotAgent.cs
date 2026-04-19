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
	private readonly Dictionary<string, SystemPromptSectionOverride>? _systemPromptSections;
	private readonly IOrchestrationReporter _reporter;
	private readonly IReadOnlyCollection<IEngineTool> _engineTools;
	private readonly EngineToolContext? _engineToolContext;
	private readonly string[] _skillDirectories;
	private readonly Engine.InfiniteSessionConfig? _infiniteSessionConfig;
	private readonly ImageAttachment[] _attachments;
	private readonly ILogger<CopilotAgent> _logger;
	private readonly IReadOnlyList<AvailableModelInfo>? _cachedAvailableModels;
	private readonly Action<IReadOnlyList<AvailableModelInfo>>? _onAvailableModelsListed;

	internal CopilotAgent(
			CopilotClient client,
			string model,
			string? systemPrompt,
			Mcp[] mcps,
			Subagent[] subagents,
			ReasoningLevel? reasoningLevel,
			SystemPromptMode? systemPromptMode,
			Dictionary<string, SystemPromptSectionOverride>? systemPromptSections,
			IOrchestrationReporter reporter,
			IReadOnlyCollection<IEngineTool> engineTools,
			EngineToolContext? engineToolContext,
			string[] skillDirectories,
			Engine.InfiniteSessionConfig? infiniteSessionConfig,
			ImageAttachment[] attachments,
			ILogger<CopilotAgent> logger,
			IReadOnlyList<AvailableModelInfo>? cachedAvailableModels = null,
			Action<IReadOnlyList<AvailableModelInfo>>? onAvailableModelsListed = null)
	{
		_client = client;
		_model = model;
		_systemPrompt = systemPrompt;
		_mcps = mcps;
		_subagents = subagents;
		_reasoningLevel = reasoningLevel;
		_systemPromptMode = systemPromptMode;
		_systemPromptSections = systemPromptSections;
		_reporter = reporter;
		_engineTools = engineTools;
		_engineToolContext = engineToolContext;
		_skillDirectories = skillDirectories;
		_infiniteSessionConfig = infiniteSessionConfig;
		_attachments = attachments;
		_logger = logger;
		_cachedAvailableModels = cachedAvailableModels;
		_onAvailableModelsListed = onAvailableModelsListed;
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

			LogSessionCreating(_client.GetHashCode(), _model, _mcps.Length, Environment.CurrentManagedThreadId);
			var sw = System.Diagnostics.Stopwatch.StartNew();
			CopilotSession session;
			try
			{
				session = await _client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				LogSessionCreateFailed(ex, _client.GetHashCode(), sw.ElapsedMilliseconds);
				throw;
			}
			LogSessionCreated(_client.GetHashCode(), sw.ElapsedMilliseconds);
			await using var _sessionDispose = session;

			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var handler = new CopilotSessionHandler(writer, _reporter, _model, done);

			session.On(handler.HandleEvent);

			// Build message options with optional attachments
			var messageOptions = new MessageOptions { Prompt = prompt };
			if (_attachments.Length > 0)
			{
				messageOptions.Attachments = BuildAttachments();
			}

			await session.SendAsync(messageOptions, cancellationToken).ConfigureAwait(false);

			using var registration = cancellationToken.Register(() =>
			{
				// Abort the in-flight message so the CLI stops processing
				_ = session.AbortAsync();
				done.TrySetCanceled(cancellationToken);
			});
			await done.Task.ConfigureAwait(false);

			// Handle model mismatch detection and reporting
			var availableModels = await CheckModelMismatchAsync(handler.ActualModel, cancellationToken).ConfigureAwait(false);

			return new AgentResult
			{
				Content = handler.FinalContent ?? string.Empty,
				SelectedModel = handler.SelectedModel,
				ActualModel = handler.ActualModel,
				Usage = handler.Usage,
				AvailableModels = availableModels,
			};
		}
		catch (OperationCanceledException)
		{
			throw;
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

		// Configure system message with Append, Replace, or Customize mode
		if (_systemPrompt is not null)
		{
			config.SystemMessage = new SystemMessageConfig
			{
				Content = _systemPrompt,
			};

			if (_systemPromptMode is not null)
			{
				config.SystemMessage.Mode = _systemPromptMode.Value switch
				{
					SystemPromptMode.Append => SystemMessageMode.Append,
					SystemPromptMode.Customize => SystemMessageMode.Customize,
					_ => SystemMessageMode.Replace,
				};

				// Apply section overrides for Customize mode
				if (_systemPromptMode.Value == SystemPromptMode.Customize && _systemPromptSections is { Count: > 0 })
				{
					config.SystemMessage.Sections = _systemPromptSections
						.ToDictionary(
							kvp => kvp.Key,
							kvp => new SectionOverride
							{
								Action = kvp.Value.Action switch
								{
									SystemPromptSectionAction.Replace => SectionOverrideAction.Replace,
									SystemPromptSectionAction.Remove => SectionOverrideAction.Remove,
									SystemPromptSectionAction.Append => SectionOverrideAction.Append,
									SystemPromptSectionAction.Prepend => SectionOverrideAction.Prepend,
									_ => SectionOverrideAction.Replace,
								},
								Content = kvp.Value.Content,
							});
				}
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

		// Configure infinite sessions
		if (_infiniteSessionConfig is not null)
		{
			config.InfiniteSessions = new GitHub.Copilot.SDK.InfiniteSessionConfig();

			if (_infiniteSessionConfig.Enabled.HasValue)
				config.InfiniteSessions.Enabled = _infiniteSessionConfig.Enabled.Value;

			if (_infiniteSessionConfig.BackgroundCompactionThreshold.HasValue)
				config.InfiniteSessions.BackgroundCompactionThreshold = _infiniteSessionConfig.BackgroundCompactionThreshold.Value;

			if (_infiniteSessionConfig.BufferExhaustionThreshold.HasValue)
				config.InfiniteSessions.BufferExhaustionThreshold = _infiniteSessionConfig.BufferExhaustionThreshold.Value;
		}

		// Configure session hooks for structured audit logging
		config.Hooks = BuildSessionHooks();

		return config;
	}

	/// <summary>
	/// Builds session hooks that capture structured audit log entries.
	/// Hooks fire at well-defined points in the session lifecycle and record
	/// tool calls, prompt submissions, errors, and lifecycle events.
	/// </summary>
	private SessionHooks BuildSessionHooks()
	{
		return new SessionHooks
		{
			OnSessionStart = (input, invocation) =>
			{
				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.SessionStart,
					SessionSource = input.Source,
					AdditionalContext = input.Cwd,
				});
				return Task.FromResult<SessionStartHookOutput?>(null);
			},

			OnUserPromptSubmitted = (input, invocation) =>
			{
				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.PromptSubmitted,
					Prompt = input.Prompt?.Length > 500 ? input.Prompt[..500] + "..." : input.Prompt,
				});
				return Task.FromResult<UserPromptSubmittedHookOutput?>(null);
			},

			OnPreToolUse = (input, invocation) =>
			{
				string? argsJson = null;
				if (input.ToolArgs is not null)
				{
					try { argsJson = System.Text.Json.JsonSerializer.Serialize(input.ToolArgs); }
					catch { /* ignore */ }
				}

				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.PreToolUse,
					ToolName = input.ToolName,
					ToolArguments = argsJson,
					PermissionDecision = "allow",
				});

				return Task.FromResult<PreToolUseHookOutput?>(
					new PreToolUseHookOutput { PermissionDecision = "allow" });
			},

			OnPostToolUse = (input, invocation) =>
			{
				string? resultStr = input.ToolResult?.ToString();

				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.PostToolUse,
					ToolName = input.ToolName,
					ToolResult = resultStr?.Length > 500 ? resultStr[..500] + "..." : resultStr,
				});
				return Task.FromResult<PostToolUseHookOutput?>(null);
			},

			OnErrorOccurred = (input, invocation) =>
			{
				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.Error,
					Error = input.Error,
					ErrorContext = input.ErrorContext,
				});
				return Task.FromResult<ErrorOccurredHookOutput?>(null);
			},

			OnSessionEnd = (input, invocation) =>
			{
				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.SessionEnd,
					SessionEndReason = input.Reason,
				});
				return Task.FromResult<SessionEndHookOutput?>(null);
			},
		};
	}

	/// <summary>
	/// The step name for audit log correlation. Set from the reporter context.
	/// Defaults to the model name if no step name is available.
	/// </summary>
	private string _stepName => _engineToolContext?.StepName ?? _model;

	/// <summary>
	/// Builds image attachments for the Copilot SDK message.
	/// </summary>
	private List<UserMessageDataAttachmentsItem> BuildAttachments()
	{
		var attachments = new List<UserMessageDataAttachmentsItem>();

		foreach (var attachment in _attachments)
		{
			switch (attachment)
			{
				case FileImageAttachment file:
					attachments.Add(new UserMessageDataAttachmentsItemFile
					{
						Path = file.Path,
						DisplayName = file.DisplayName ?? System.IO.Path.GetFileName(file.Path),
					});
					break;

				case BlobImageAttachment blob:
					attachments.Add(new UserMessageDataAttachmentsItemBlob
					{
						Data = blob.Data,
						MimeType = blob.MimeType,
					});
					break;
			}
		}

		return attachments;
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

		// Use cached models if available to avoid repeated network calls
		// across parallel steps in the same orchestration run.
		var availableModels = _cachedAvailableModels;

		if (availableModels is null)
		{
			try
			{
				var models = await _client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
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
						SupportsVision = m.Capabilities?.Supports?.Vision,
					})
					.ToList();

				// Cache for other agents in this run
				_onAvailableModelsListed?.Invoke(availableModels);
			}
			catch (Exception ex)
			{
				// Unable to list models - log and continue without them
				LogListModelsFailed(ex);
			}
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

	private Dictionary<string, object> BuildMcpServers() => BuildMcpServerDictionary(_mcps);

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

	private Dictionary<string, object> BuildMcpServersFor(Mcp[] mcps) => BuildMcpServerDictionary(mcps);

	private static Dictionary<string, object> BuildMcpServerDictionary(Mcp[] mcps)
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

	[LoggerMessage(
			EventId = 7,
			Level = LogLevel.Warning,
			Message = "Failed to list available models for model mismatch report")]
	private partial void LogListModelsFailed(Exception ex);

	[LoggerMessage(EventId = 8, Level = LogLevel.Information,
		Message = "Session: creating on client#{ClientHash} (model={Model}, mcps={McpCount}, thread={ThreadId})")]
	private partial void LogSessionCreating(int clientHash, string model, int mcpCount, int threadId);

	[LoggerMessage(EventId = 9, Level = LogLevel.Information,
		Message = "Session: created on client#{ClientHash} in {ElapsedMs}ms")]
	private partial void LogSessionCreated(int clientHash, long elapsedMs);

	[LoggerMessage(EventId = 10, Level = LogLevel.Error,
		Message = "Session: CreateSessionAsync FAILED on client#{ClientHash} after {ElapsedMs}ms")]
	private partial void LogSessionCreateFailed(Exception ex, int clientHash, long elapsedMs);

	#endregion
}
