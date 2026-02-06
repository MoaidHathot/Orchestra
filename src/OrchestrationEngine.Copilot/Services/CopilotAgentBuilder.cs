using GitHub.Copilot.SDK;
using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Models;

namespace OrchestrationEngine.Copilot.Services;

/// <summary>
/// Fluent builder for creating Copilot agents.
/// </summary>
internal sealed class CopilotAgentBuilder : IAgentBuilder
{
    private readonly CopilotClient _client;
    private readonly McpConfiguration _mcpConfig;
    
    private string _systemPrompt = string.Empty;
    private string _model = "claude-opus-4.6";
    private readonly List<string> _mcpServerNames = [];
    private bool _streaming = true;

    public CopilotAgentBuilder(CopilotClient client, McpConfiguration mcpConfig)
    {
        _client = client;
        _mcpConfig = mcpConfig;
    }

    public IAgentBuilder WithSystemPrompt(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
        return this;
    }

    public IAgentBuilder WithModel(string model)
    {
        _model = model;
        return this;
    }

    public IAgentBuilder WithMcpServers(params string[] mcpServerNames)
    {
        _mcpServerNames.AddRange(mcpServerNames);
        return this;
    }

    public IAgentBuilder WithStreaming(bool enabled = true)
    {
        _streaming = enabled;
        return this;
    }

    public async Task<IAgent> BuildAsync(CancellationToken cancellationToken = default)
    {
        var config = new SessionConfig
        {
            Model = _model,
            Streaming = _streaming,
        };

        // Add system message if provided
        if (!string.IsNullOrWhiteSpace(_systemPrompt))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = _systemPrompt
            };
        }

        // Configure MCP servers if requested
        if (_mcpServerNames.Count > 0)
        {
            var mcpServers = new Dictionary<string, object>();
            
            foreach (var serverName in _mcpServerNames)
            {
                if (_mcpConfig.McpServers.TryGetValue(serverName, out var serverConfig))
                {
                    if (serverConfig.IsRemote)
                    {
                        // Remote MCP server (URL-based)
                        mcpServers[serverName] = new McpRemoteServerConfig
                        {
                            Type = "remote",
                            Url = serverConfig.Url ?? throw new InvalidOperationException(
                                $"Remote MCP server '{serverName}' requires a URL"),
                            Headers = serverConfig.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                            Tools = ["*"]
                        };
                    }
                    else
                    {
                        // Local MCP server (command-based)
                        var command = serverConfig.Command ?? throw new InvalidOperationException(
                            $"Local MCP server '{serverName}' requires a command");
                        
                        mcpServers[serverName] = new McpLocalServerConfig
                        {
                            Type = "local",
                            Command = command,
                            Args = serverConfig.EffectiveArgs.ToList(),
                            Tools = ["*"],
                            Env = serverConfig.Env.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                        };
                    }
                }
            }

            if (mcpServers.Count > 0)
            {
                config.McpServers = mcpServers;
            }
        }

        var session = await _client.CreateSessionAsync(config);
        return new CopilotAgent(session);
    }
}

/// <summary>
/// Factory for creating Copilot agent builders.
/// </summary>
internal sealed class CopilotAgentBuilderFactory : IAgentBuilderFactory
{
    private readonly CopilotClient _client;
    private readonly McpConfiguration _mcpConfig;

    public CopilotAgentBuilderFactory(CopilotClient client, McpConfiguration mcpConfig)
    {
        _client = client;
        _mcpConfig = mcpConfig;
    }

    public IAgentBuilder Create() => new CopilotAgentBuilder(_client, _mcpConfig);
}
