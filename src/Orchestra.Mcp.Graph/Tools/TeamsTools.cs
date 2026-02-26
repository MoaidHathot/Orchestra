using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Orchestra.Mcp.Graph.Services;

namespace Orchestra.Mcp.Graph.Tools;

/// <summary>
/// MCP tools for Teams and Channels Graph API operations.
/// </summary>
[McpServerToolType]
public class TeamsTools
{
    private readonly GraphApiClient _graphClient;

    public TeamsTools(GraphApiClient graphClient)
    {
        _graphClient = graphClient;
    }

    [McpServerTool(Name = "get_joined_teams")]
    [Description("Get all Microsoft Teams the current user is a member of")]
    public async Task<string> GetJoinedTeams(CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            "/me/joinedTeams",
            useAzureCli: true,
            cancellationToken: cancellationToken);

        var teams = GraphApiClient.GetValues(result);
        return FormatTeams(teams);
    }

    [McpServerTool(Name = "get_team_channels")]
    [Description("Get all channels in a specific team")]
    public async Task<string> GetTeamChannels(
        [Description("The team ID")] string teamId,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            $"/teams/{teamId}/channels",
            useAzureCli: true,
            cancellationToken: cancellationToken);

        var channels = GraphApiClient.GetValues(result);
        return FormatChannels(channels);
    }

    [McpServerTool(Name = "get_channel_messages")]
    [Description("Get messages from a specific team channel")]
    public async Task<string> GetChannelMessages(
        [Description("The team ID")] string teamId,
        [Description("The channel ID")] string channelId,
        [Description("Maximum number of messages to return (default: 10)")] int top = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            $"/teams/{teamId}/channels/{channelId}/messages",
            new Dictionary<string, string>
            {
                ["$top"] = top.ToString()
            },
            cancellationToken: cancellationToken);

        var messages = GraphApiClient.GetValues(result);
        return FormatChannelMessages(messages);
    }

    [McpServerTool(Name = "get_thread_replies")]
    [Description("Get replies to a specific message in a team channel (thread)")]
    public async Task<string> GetThreadReplies(
        [Description("The team ID")] string teamId,
        [Description("The channel ID")] string channelId,
        [Description("The message ID (thread root)")] string messageId,
        [Description("Maximum number of replies to return (default: 10)")] int top = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            $"/teams/{teamId}/channels/{channelId}/messages/{messageId}/replies",
            new Dictionary<string, string>
            {
                ["$top"] = top.ToString()
            },
            cancellationToken: cancellationToken);

        var replies = GraphApiClient.GetValues(result);
        return FormatChannelMessages(replies);
    }

    private static string FormatTeams(List<JsonNode> teams)
    {
        var lines = new List<string>();

        foreach (var team in teams)
        {
            var displayName = team["displayName"]?.GetValue<string>() ?? "Unknown";
            var description = team["description"]?.GetValue<string>();
            var id = team["id"]?.GetValue<string>();

            lines.Add($"Team: {displayName}");
            lines.Add($"  ID: {id}");

            if (!string.IsNullOrEmpty(description))
            {
                lines.Add($"  Description: {GraphHelpers.Truncate(description, 100)}");
            }

            lines.Add("");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "No teams found.";
    }

    private static string FormatChannels(List<JsonNode> channels)
    {
        var lines = new List<string>();

        foreach (var channel in channels)
        {
            var displayName = channel["displayName"]?.GetValue<string>() ?? "Unknown";
            var description = channel["description"]?.GetValue<string>();
            var id = channel["id"]?.GetValue<string>();
            var membershipType = channel["membershipType"]?.GetValue<string>() ?? "standard";

            lines.Add($"Channel: {displayName} ({membershipType})");
            lines.Add($"  ID: {id}");

            if (!string.IsNullOrEmpty(description))
            {
                lines.Add($"  Description: {GraphHelpers.Truncate(description, 100)}");
            }

            lines.Add("");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "No channels found.";
    }

    private static string FormatChannelMessages(List<JsonNode> messages)
    {
        var lines = new List<string>();

        foreach (var msg in messages)
        {
            var user = msg["from"]?["user"];
            if (user == null) continue;

            var sender = user["displayName"]?.GetValue<string>() ?? "Unknown";
            var body = GraphHelpers.StripHtml(msg["body"]?["content"]?.GetValue<string>());
            var time = GraphHelpers.FormatTime(msg["createdDateTime"]?.GetValue<string>());
            var id = msg["id"]?.GetValue<string>();
            var replyCount = msg["replies"]?.AsArray()?.Count ?? 0;

            if (!string.IsNullOrEmpty(body))
            {
                var truncatedBody = GraphHelpers.Truncate(body, 200);
                lines.Add($"[{time}] {sender}: {truncatedBody}");
                lines.Add($"  Message ID: {id}");

                if (replyCount > 0)
                {
                    lines.Add($"  Replies: {replyCount}");
                }

                lines.Add("");
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "No messages found.";
    }
}
