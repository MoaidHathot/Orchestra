using System.Text.Json.Serialization;

namespace OrchestrationEngine.Core.Models;

/// <summary>
/// Root configuration for MCP servers loaded from mcp.json.
/// </summary>
public sealed record McpConfiguration
{
    /// <summary>
    /// Dictionary of MCP server configurations keyed by server name.
    /// </summary>
    public IReadOnlyDictionary<string, McpServerConfig> McpServers { get; init; } 
        = new Dictionary<string, McpServerConfig>();
}

/// <summary>
/// Configuration for a single MCP server. Supports both local (command-based) 
/// and remote (URL-based) servers.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>
    /// The type of MCP server: "local" for command-based, "remote" for URL-based.
    /// Defaults to "local" for backwards compatibility.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "local";
    
    /// <summary>
    /// For local servers: The command to execute (e.g., "npx", "dotnet", "pwsh").
    /// Can be a single string or use Command array for complex invocations.
    /// </summary>
    [JsonPropertyName("command")]
    public string? CommandString { get; init; }
    
    /// <summary>
    /// For local servers: Command as an array for complex invocations.
    /// First element is the executable, rest are arguments.
    /// </summary>
    [JsonPropertyName("commandArray")]
    public IReadOnlyList<string>? CommandArray { get; init; }
    
    /// <summary>
    /// For local servers: Arguments to pass to the command.
    /// Used when CommandString is provided.
    /// </summary>
    [JsonPropertyName("args")]
    public IReadOnlyList<string> Args { get; init; } = [];
    
    /// <summary>
    /// For local servers: Environment variables to set when running the command.
    /// </summary>
    [JsonPropertyName("env")]
    public IReadOnlyDictionary<string, string> Env { get; init; } 
        = new Dictionary<string, string>();
    
    /// <summary>
    /// For remote servers: The URL endpoint of the MCP server.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
    
    /// <summary>
    /// For remote servers: Optional headers to include in requests (e.g., for authentication).
    /// </summary>
    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();
    
    /// <summary>
    /// Returns true if this is a remote (URL-based) MCP server.
    /// </summary>
    [JsonIgnore]
    public bool IsRemote => Type.Equals("remote", StringComparison.OrdinalIgnoreCase);
    
    /// <summary>
    /// Returns true if this is a local (command-based) MCP server.
    /// </summary>
    [JsonIgnore]
    public bool IsLocal => Type.Equals("local", StringComparison.OrdinalIgnoreCase);
    
    /// <summary>
    /// Gets the effective command for local servers.
    /// Prefers CommandArray if set, otherwise uses CommandString.
    /// </summary>
    [JsonIgnore]
    public string? Command => CommandArray?.FirstOrDefault() ?? CommandString;
    
    /// <summary>
    /// Gets the effective arguments for local servers.
    /// If CommandArray is used, returns elements after the first. Otherwise returns Args.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveArgs => 
        CommandArray is { Count: > 1 } 
            ? CommandArray.Skip(1).ToList() 
            : Args;
}
