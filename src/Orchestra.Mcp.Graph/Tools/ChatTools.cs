using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Orchestra.Mcp.Graph.Services;

namespace Orchestra.Mcp.Graph.Tools;

/// <summary>
/// MCP tools for chat-related Graph API operations.
/// </summary>
[McpServerToolType]
public class ChatTools
{
    private readonly GraphApiClient _graphClient;

    public ChatTools(GraphApiClient graphClient)
    {
        _graphClient = graphClient;
    }

    [McpServerTool(Name = "get_chats")]
    [Description("Get user's chats including 1:1 conversations, group chats, and meeting chats")]
    public async Task<string> GetChats(
        [Description("Maximum number of chats to return (default: 20)")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            "/me/chats",
            new Dictionary<string, string>
            {
                ["$top"] = top.ToString(),
                ["$expand"] = "members"
            },
            cancellationToken: cancellationToken);

        var chats = GraphApiClient.GetValues(result);
        return FormatChats(chats);
    }

    [McpServerTool(Name = "find_chat_by_member")]
    [Description("Find chats that include a specific member by their email address")]
    public async Task<string> FindChatByMember(
        [Description("Email address of the member to find")] string email,
        [Description("Chat type filter: oneOnOne, group, or meeting (optional)")] string? chatType = null,
        CancellationToken cancellationToken = default)
    {
        // For 1:1 chats, we can construct the chat ID directly
        if (chatType == "oneOnOne")
        {
            var chat = await GetOneOnOneChatByEmailInternal(email, cancellationToken);
            if (chat != null)
            {
                return FormatChats([chat]);
            }
            return "No 1:1 chat found with this user.";
        }

        // For other types, page through chats
        var parameters = new Dictionary<string, string>
        {
            ["$top"] = "50",
            ["$expand"] = "members,lastMessagePreview"
        };

        if (!string.IsNullOrEmpty(chatType))
        {
            parameters["$filter"] = $"chatType eq '{chatType}'";
        }

        var allChats = await _graphClient.GetAllPagesAsync(
            "/me/chats",
            parameters,
            cancellationToken: cancellationToken);

        var matchingChats = allChats
            .Where(c => ChatContainsMember(c, email))
            .ToList();

        if (matchingChats.Count == 0)
        {
            return $"No chats found with member: {email}";
        }

        return FormatChats(matchingChats);
    }

    [McpServerTool(Name = "find_chat_by_topic")]
    [Description("Find chats whose topic/name contains the given text")]
    public async Task<string> FindChatByTopic(
        [Description("Text to search for in chat topic")] string topic,
        CancellationToken cancellationToken = default)
    {
        var escapedTopic = topic.Replace("'", "''");
        var parameters = new Dictionary<string, string>
        {
            ["$top"] = "50",
            ["$filter"] = $"contains(topic, '{escapedTopic}')",
            ["$expand"] = "lastMessagePreview"
        };

        var chats = await _graphClient.GetAllPagesAsync(
            "/me/chats",
            parameters,
            cancellationToken: cancellationToken);

        if (chats.Count == 0)
        {
            return $"No chats found with topic containing: {topic}";
        }

        return FormatChats(chats);
    }

    [McpServerTool(Name = "get_oneononone_chat")]
    [Description("Get a 1:1 chat with a specific user by their email address")]
    public async Task<string> GetOneOnOneChat(
        [Description("Email address of the other person")] string email,
        CancellationToken cancellationToken = default)
    {
        var chat = await GetOneOnOneChatByEmailInternal(email, cancellationToken);

        if (chat == null)
        {
            return $"No 1:1 chat found with: {email}";
        }

        return FormatChats([chat]);
    }

    [McpServerTool(Name = "get_chat_with_last_activity")]
    [Description("Get a single chat by ID with last message preview and members")]
    public async Task<string> GetChatWithLastActivity(
        [Description("The chat ID")] string chatId,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            $"/me/chats/{chatId}",
            new Dictionary<string, string>
            {
                ["$expand"] = "lastMessagePreview,members"
            },
            cancellationToken: cancellationToken);

        if (result == null)
        {
            return $"Chat not found: {chatId}";
        }

        return FormatChats([result]);
    }

    [McpServerTool(Name = "get_chat_messages")]
    [Description("Get messages from a specific chat")]
    public async Task<string> GetChatMessages(
        [Description("The chat ID")] string chatId,
        [Description("Maximum number of messages to return (default: 10)")] int top = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            $"/me/chats/{chatId}/messages",
            new Dictionary<string, string>
            {
                ["$top"] = top.ToString()
            },
            cancellationToken: cancellationToken);

        var messages = GraphApiClient.GetValues(result);
        return FormatMessages(messages);
    }

    private async Task<JsonNode?> GetOneOnOneChatByEmailInternal(string email, CancellationToken cancellationToken)
    {
        try
        {
            // Get the other user's ID
            var user = await _graphClient.GetAsync(
                $"/users/{email}",
                new Dictionary<string, string> { ["$select"] = "id,displayName,mail" },
                useAzureCli: true,
                cancellationToken: cancellationToken);

            var theirId = user?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(theirId))
            {
                return null;
            }

            // Get my ID
            var me = await _graphClient.GetAsync(
                "/me",
                new Dictionary<string, string> { ["$select"] = "id" },
                useAzureCli: true,
                cancellationToken: cancellationToken);

            var myId = me?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(myId))
            {
                return null;
            }

            // Construct 1:1 chat ID (sorted user IDs)
            var ids = new[] { myId, theirId }.OrderBy(x => x).ToArray();
            var chatId = $"19:{ids[0]}_{ids[1]}@unq.gbl.spaces";

            // Fetch the chat
            return await _graphClient.GetAsync(
                $"/me/chats/{chatId}",
                new Dictionary<string, string> { ["$expand"] = "lastMessagePreview,members" },
                cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static bool ChatContainsMember(JsonNode chat, string email)
    {
        var members = chat["members"]?.AsArray();
        if (members == null) return false;

        return members.Any(m =>
            m?["email"]?.GetValue<string>()?.Equals(email, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string FormatChats(List<JsonNode> chats)
    {
        var lines = new List<string>();

        foreach (var chat in chats)
        {
            var chatType = chat["chatType"]?.GetValue<string>() ?? "unknown";
            var topic = chat["topic"]?.GetValue<string>();
            var id = chat["id"]?.GetValue<string>();

            var title = chatType switch
            {
                "oneOnOne" => GetOneOnOneChatTitle(chat),
                _ => topic ?? $"{chatType} chat"
            };

            lines.Add($"[{chatType}] {title}");
            lines.Add($"  ID: {id}");

            // Last message preview
            var preview = chat["lastMessagePreview"];
            if (preview != null)
            {
                var previewBody = preview["body"]?["content"]?.GetValue<string>();
                var previewTime = preview["createdDateTime"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(previewBody))
                {
                    var text = GraphHelpers.Truncate(GraphHelpers.StripHtml(previewBody), 80);
                    var time = GraphHelpers.FormatTime(previewTime);
                    lines.Add($"  Last: [{time}] {text}");
                }
            }

            lines.Add("");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "No chats found.";
    }

    private static string GetOneOnOneChatTitle(JsonNode chat)
    {
        var members = chat["members"]?.AsArray();
        if (members == null) return "Unknown";

        foreach (var member in members)
        {
            var displayName = member?["displayName"]?.GetValue<string>();
            var email = member?["email"]?.GetValue<string>()?.ToLowerInvariant();

            // Return the other person's name (not the current user)
            if (!string.IsNullOrEmpty(displayName) && !string.IsNullOrEmpty(email))
            {
                // We don't know the current user's email, so return first member we find
                return displayName;
            }
        }

        return "Unknown";
    }

    private static string FormatMessages(List<JsonNode> messages)
    {
        var lines = new List<string>();

        foreach (var msg in messages)
        {
            var messageType = msg["messageType"]?.GetValue<string>();
            if (messageType != "message") continue;

            var sender = msg["from"]?["user"]?["displayName"]?.GetValue<string>() ?? "Unknown";
            var body = GraphHelpers.StripHtml(msg["body"]?["content"]?.GetValue<string>());
            var time = GraphHelpers.FormatTime(msg["createdDateTime"]?.GetValue<string>());

            if (!string.IsNullOrEmpty(body))
            {
                var truncatedBody = GraphHelpers.Truncate(body, 200);
                lines.Add($"[{time}] {sender}: {truncatedBody}");
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "No messages found.";
    }
}
