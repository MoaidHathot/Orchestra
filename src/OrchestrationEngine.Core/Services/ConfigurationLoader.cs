using System.Text.Json;
using System.Text.Json.Serialization;
using OrchestrationEngine.Core.Models;

namespace OrchestrationEngine.Core.Services;

/// <summary>
/// Service for loading orchestration and MCP configurations from JSON files.
/// </summary>
public sealed class ConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<OrchestrationDefinition> LoadOrchestrationAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        var definition = await JsonSerializer.DeserializeAsync<OrchestrationDefinition>(
            stream, JsonOptions, cancellationToken);
        
        return definition ?? throw new InvalidOperationException(
            $"Failed to deserialize orchestration from {filePath}");
    }

    public async Task<McpConfiguration> LoadMcpConfigurationAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new McpConfiguration();
        }

        await using var stream = File.OpenRead(filePath);
        var config = await JsonSerializer.DeserializeAsync<McpConfiguration>(
            stream, JsonOptions, cancellationToken);
        
        return config ?? new McpConfiguration();
    }
}
