using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Orchestra.Mcp.Graph.Services;

namespace Orchestra.Mcp.Graph.Tools;

/// <summary>
/// MCP tools for Microsoft 365 Copilot integration.
/// </summary>
[McpServerToolType]
public class CopilotTools
{
    private readonly GraphApiClient _graphClient;

    public CopilotTools(GraphApiClient graphClient)
    {
        _graphClient = graphClient;
    }

    [McpServerTool(Name = "ask_copilot")]
    [Description("Ask Microsoft 365 Copilot a question. Copilot can access your M365 data including emails, documents, and chats.")]
    public async Task<string> AskCopilot(
        [Description("The question to ask Copilot")] string question,
        [Description("Your timezone (default: Australia/Sydney)")] string timezone = "Australia/Sydney",
        CancellationToken cancellationToken = default)
    {
        // Step 1: Create conversation
        var conversationResult = await _graphClient.PostAsync(
            "/copilot/conversations",
            new { },
            useBeta: true,
            cancellationToken: cancellationToken);

        var conversationId = conversationResult?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(conversationId))
        {
            return "Error: Failed to create Copilot conversation.";
        }

        // Step 2: Send message
        var chatResult = await _graphClient.PostAsync(
            $"/copilot/conversations/{conversationId}/chat",
            new
            {
                message = new { text = question },
                locationHint = new { timeZone = timezone }
            },
            useBeta: true,
            cancellationToken: cancellationToken);

        // Extract response
        var messages = chatResult?["messages"]?.AsArray();
        if (messages == null || messages.Count < 2)
        {
            return "No response received from Copilot.";
        }

        // Find the response message (skip the echo of user's question)
        var firstMessageId = messages[0]?["id"]?.GetValue<string>();

        foreach (var msg in messages)
        {
            var msgId = msg?["id"]?.GetValue<string>();
            if (msgId != firstMessageId)
            {
                var responseText = msg?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(responseText))
                {
                    return FormatCopilotResponse(responseText, chatResult);
                }
            }
        }

        return "No response text found in Copilot response.";
    }

    private static string FormatCopilotResponse(string responseText, JsonNode? fullResponse)
    {
        var lines = new List<string>
        {
            "Copilot Response:",
            "─────────────────",
            responseText
        };

        // Include attributions if present
        var attributions = fullResponse?["attributions"]?.AsArray();
        if (attributions != null && attributions.Count > 0)
        {
            lines.Add("");
            lines.Add("Sources:");

            foreach (var attr in attributions)
            {
                var title = attr?["title"]?.GetValue<string>();
                var url = attr?["url"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(title))
                {
                    if (!string.IsNullOrEmpty(url))
                    {
                        lines.Add($"  - {title}: {url}");
                    }
                    else
                    {
                        lines.Add($"  - {title}");
                    }
                }
            }
        }

        return string.Join("\n", lines);
    }
}
