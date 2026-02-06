using System.Reflection;

namespace OrchestrationEngine.Core.Services;

/// <summary>
/// Service for loading prompt templates from markdown files.
/// </summary>
public sealed class PromptLoader
{
    private readonly string _promptsDirectory;
    private readonly Dictionary<string, string> _cache = new();

    public PromptLoader(string promptsDirectory = "prompts")
    {
        // Resolve prompts directory relative to the assembly location (not CWD)
        var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        _promptsDirectory = Path.Combine(assemblyLocation ?? ".", promptsDirectory);
    }

    /// <summary>
    /// Loads a prompt from a markdown file.
    /// </summary>
    /// <param name="promptName">The name of the prompt file (without .md extension)</param>
    /// <returns>The prompt content</returns>
    public async Task<string> LoadPromptAsync(string promptName, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(promptName, out var cached))
        {
            return cached;
        }

        var filePath = Path.Combine(_promptsDirectory, $"{promptName}.md");
        
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Prompt file not found: {filePath}", filePath);
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        _cache[promptName] = content;
        
        return content;
    }

    /// <summary>
    /// Loads a prompt synchronously (for use in constructors).
    /// </summary>
    public string LoadPrompt(string promptName)
    {
        if (_cache.TryGetValue(promptName, out var cached))
        {
            return cached;
        }

        var filePath = Path.Combine(_promptsDirectory, $"{promptName}.md");
        
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Prompt file not found: {filePath}", filePath);
        }

        var content = File.ReadAllText(filePath);
        _cache[promptName] = content;
        
        return content;
    }
}
