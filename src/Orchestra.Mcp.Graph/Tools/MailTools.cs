using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Orchestra.Mcp.Graph.Services;

namespace Orchestra.Mcp.Graph.Tools;

/// <summary>
/// MCP tools for mail-related Graph API operations.
/// </summary>
[McpServerToolType]
public class MailTools
{
    private readonly GraphApiClient _graphClient;

    public MailTools(GraphApiClient graphClient)
    {
        _graphClient = graphClient;
    }

    [McpServerTool(Name = "get_mail")]
    [Description("Get recent mail messages with subject, sender, and preview")]
    public async Task<string> GetMail(
        [Description("Maximum number of messages to return (default: 10)")] int top = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            "/me/messages",
            new Dictionary<string, string>
            {
                ["$top"] = top.ToString(),
                ["$select"] = "subject,from,receivedDateTime,bodyPreview"
            },
            cancellationToken: cancellationToken);

        var messages = GraphApiClient.GetValues(result);
        return FormatMailMessages(messages);
    }

    [McpServerTool(Name = "get_mail_folder_messages")]
    [Description("Get messages from a specific mail folder (Inbox, Sent Items, etc.) from the last N days")]
    public async Task<string> GetMailFolderMessages(
        [Description("Folder name: Inbox, Sent Items, Drafts, Archive, Junk Email (default: Inbox)")] string folder = "Inbox",
        [Description("Number of days to look back (default: 7)")] int days = 7,
        [Description("Maximum number of messages to return (default: 50)")] int top = 50,
        [Description("Include full message body in results")] bool includeBody = false,
        CancellationToken cancellationToken = default)
    {
        if (days < 0)
        {
            return "Error: days must be >= 0";
        }

        if (top <= 0)
        {
            return "No messages requested.";
        }

        // Normalize folder name
        var normalizedFolder = folder.Replace(" ", "").ToLowerInvariant();
        var wellKnownFolders = new Dictionary<string, string>
        {
            ["inbox"] = "inbox",
            ["sent"] = "sentitems",
            ["sentitems"] = "sentitems",
            ["drafts"] = "drafts",
            ["archive"] = "archive",
            ["junkemail"] = "junkemail"
        };

        string endpoint;
        if (wellKnownFolders.TryGetValue(normalizedFolder, out var wellKnownName))
        {
            endpoint = $"/me/mailFolders/{wellKnownName}/messages";
        }
        else
        {
            // Try to find custom folder by name
            var escapedName = folder.Replace("'", "''");
            var foldersResult = await _graphClient.GetAsync(
                "/me/mailFolders",
                new Dictionary<string, string>
                {
                    ["$top"] = "100",
                    ["$filter"] = $"displayName eq '{escapedName}'",
                    ["$select"] = "id,displayName"
                },
                cancellationToken: cancellationToken);

            var folders = GraphApiClient.GetValues(foldersResult);
            if (folders.Count == 0)
            {
                return $"Mail folder not found: {folder}";
            }

            var folderId = folders[0]["id"]?.GetValue<string>();
            endpoint = $"/me/mailFolders/{folderId}/messages";
        }

        // Build time filter
        var timeField = normalizedFolder is "sent" or "sentitems" ? "sentDateTime" : "receivedDateTime";
        var startTime = DateTime.UtcNow.AddDays(-days);
        var startIso = startTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var selectFields = "id,subject,from,toRecipients,ccRecipients,receivedDateTime,sentDateTime,conversationId,webLink,bodyPreview";
        if (includeBody)
        {
            selectFields += ",body";
        }

        var messages = await _graphClient.GetAllPagesAsync(
            endpoint,
            new Dictionary<string, string>
            {
                ["$top"] = Math.Min(50, top).ToString(),
                ["$select"] = selectFields,
                ["$orderby"] = $"{timeField} desc",
                ["$filter"] = $"{timeField} ge {startIso}"
            },
            maxResults: top,
            cancellationToken: cancellationToken);

        return FormatMailMessages(messages, includeBody);
    }

    [McpServerTool(Name = "search_mail")]
    [Description("Search mail using Microsoft Search API. Supports KQL queries like subject:\"phrase\" or free text.")]
    public async Task<string> SearchMail(
        [Description("Search query (KQL supported, e.g., subject:\"project update\" or free text)")] string query,
        [Description("Maximum number of results (default: 5)")] int top = 5,
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.PostAsync(
            "/search/query",
            new
            {
                requests = new[]
                {
                    new
                    {
                        entityTypes = new[] { "message" },
                        query = new { queryString = query },
                        from = 0,
                        size = top
                    }
                }
            },
            cancellationToken: cancellationToken);

        var results = new List<JsonNode>();

        var containers = result?["value"]?.AsArray();
        if (containers != null)
        {
            foreach (var container in containers)
            {
                var hitsContainers = container?["hitsContainers"]?.AsArray();
                if (hitsContainers == null) continue;

                foreach (var hc in hitsContainers)
                {
                    var hits = hc?["hits"]?.AsArray();
                    if (hits == null) continue;

                    foreach (var hit in hits)
                    {
                        var resource = hit?["resource"];
                        if (resource != null)
                        {
                            results.Add(resource.DeepClone());
                        }
                    }
                }
            }
        }

        if (results.Count == 0)
        {
            return $"No mail found matching: {query}";
        }

        return FormatSearchResults(results);
    }

    private static string FormatMailMessages(List<JsonNode> messages, bool includeBody = false)
    {
        var lines = new List<string>();

        foreach (var msg in messages)
        {
            var subject = msg["subject"]?.GetValue<string>() ?? "(no subject)";
            var senderEmail = msg["from"]?["emailAddress"];
            var senderName = senderEmail?["name"]?.GetValue<string>() ?? "Unknown";
            var senderAddr = senderEmail?["address"]?.GetValue<string>() ?? "";
            var receivedTime = msg["receivedDateTime"]?.GetValue<string>();
            var preview = msg["bodyPreview"]?.GetValue<string>();

            var time = GraphHelpers.FormatTime(receivedTime);
            lines.Add($"[{time}] From: {senderName} <{senderAddr}>");
            lines.Add($"  Subject: {GraphHelpers.Truncate(subject, 100)}");

            if (!string.IsNullOrEmpty(preview) && !includeBody)
            {
                lines.Add($"  Preview: {GraphHelpers.Truncate(preview, 150)}");
            }

            if (includeBody)
            {
                var body = msg["body"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(body))
                {
                    var cleanBody = GraphHelpers.StripHtml(body);
                    lines.Add($"  Body: {GraphHelpers.Truncate(cleanBody, 500)}");
                }
            }

            var webLink = msg["webLink"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(webLink))
            {
                lines.Add($"  Link: {webLink}");
            }

            lines.Add("");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "No messages found.";
    }

    private static string FormatSearchResults(List<JsonNode> results)
    {
        var lines = new List<string>();

        foreach (var result in results)
        {
            var subject = result["subject"]?.GetValue<string>() ?? "(no subject)";
            var senderEmail = result["from"]?["emailAddress"];
            var senderName = senderEmail?["name"]?.GetValue<string>() ?? "Unknown";
            var receivedTime = result["receivedDateTime"]?.GetValue<string>();
            var preview = result["bodyPreview"]?.GetValue<string>();

            var time = GraphHelpers.FormatTime(receivedTime);
            lines.Add($"[{time}] From: {senderName}");
            lines.Add($"  Subject: {GraphHelpers.Truncate(subject, 100)}");

            if (!string.IsNullOrEmpty(preview))
            {
                lines.Add($"  Preview: {GraphHelpers.Truncate(preview, 150)}");
            }

            lines.Add("");
        }

        return string.Join("\n", lines);
    }
}
