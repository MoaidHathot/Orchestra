using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Orchestra.Mcp.Graph.Services;

namespace Orchestra.Mcp.Graph.Tools;

/// <summary>
/// MCP tools for calendar and meeting-related Graph API operations.
/// </summary>
[McpServerToolType]
public class CalendarTools
{
    private readonly GraphApiClient _graphClient;

    public CalendarTools(GraphApiClient graphClient)
    {
        _graphClient = graphClient;
    }

    [McpServerTool(Name = "get_upcoming_meetings")]
    [Description("Get upcoming calendar events/meetings within the next N hours")]
    public async Task<string> GetUpcomingMeetings(
        [Description("Number of hours to look ahead (default: 24)")] int hours = 24,
        [Description("Maximum number of events to return (default: 20)")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        if (hours <= 0)
        {
            return "Error: hours must be > 0";
        }

        var now = DateTime.UtcNow;
        var endTime = now.AddHours(hours);

        var result = await _graphClient.GetAsync(
            "/me/calendarView",
            new Dictionary<string, string>
            {
                ["startDateTime"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["endDateTime"] = endTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["$top"] = top.ToString(),
                ["$orderby"] = "start/dateTime",
                ["$select"] = "id,subject,start,end,location,organizer,attendees,isOnlineMeeting,onlineMeetingUrl,bodyPreview,importance"
            },
            cancellationToken: cancellationToken);

        var events = GraphApiClient.GetValues(result);
        return FormatCalendarEvents(events, "upcoming");
    }

    [McpServerTool(Name = "get_recent_meetings")]
    [Description("Get recent past calendar events/meetings from the last N hours")]
    public async Task<string> GetRecentMeetings(
        [Description("Number of hours to look back (default: 24)")] int hours = 24,
        [Description("Maximum number of events to return (default: 20)")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        if (hours <= 0)
        {
            return "Error: hours must be > 0";
        }

        var now = DateTime.UtcNow;
        var startTime = now.AddHours(-hours);

        var result = await _graphClient.GetAsync(
            "/me/calendarView",
            new Dictionary<string, string>
            {
                ["startDateTime"] = startTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["endDateTime"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["$top"] = top.ToString(),
                ["$orderby"] = "start/dateTime desc",
                ["$select"] = "id,subject,start,end,location,organizer,attendees,isOnlineMeeting,onlineMeetingUrl,bodyPreview,importance"
            },
            cancellationToken: cancellationToken);

        var events = GraphApiClient.GetValues(result);
        return FormatCalendarEvents(events, "recent");
    }

    [McpServerTool(Name = "get_todays_meetings")]
    [Description("Get all calendar events/meetings for today")]
    public async Task<string> GetTodaysMeetings(
        [Description("Maximum number of events to return (default: 50)")] int top = 50,
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);

        var result = await _graphClient.GetAsync(
            "/me/calendarView",
            new Dictionary<string, string>
            {
                ["startDateTime"] = today.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["endDateTime"] = tomorrow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["$top"] = top.ToString(),
                ["$orderby"] = "start/dateTime",
                ["$select"] = "id,subject,start,end,location,organizer,attendees,isOnlineMeeting,onlineMeetingUrl,bodyPreview,importance"
            },
            cancellationToken: cancellationToken);

        var events = GraphApiClient.GetValues(result);
        return FormatCalendarEvents(events, "today");
    }

    [McpServerTool(Name = "search_calendar")]
    [Description("Search calendar events by subject text within a date range")]
    public async Task<string> SearchCalendar(
        [Description("Text to search for in event subjects")] string query,
        [Description("Number of days to look back (default: 7)")] int daysBack = 7,
        [Description("Number of days to look ahead (default: 7)")] int daysAhead = 7,
        [Description("Maximum number of events to return (default: 20)")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var startTime = now.AddDays(-daysBack);
        var endTime = now.AddDays(daysAhead);

        var result = await _graphClient.GetAsync(
            "/me/calendarView",
            new Dictionary<string, string>
            {
                ["startDateTime"] = startTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["endDateTime"] = endTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["$top"] = "100",
                ["$orderby"] = "start/dateTime desc",
                ["$select"] = "id,subject,start,end,location,organizer,attendees,isOnlineMeeting,onlineMeetingUrl,bodyPreview,importance"
            },
            cancellationToken: cancellationToken);

        var events = GraphApiClient.GetValues(result);

        // Filter by query (case-insensitive contains)
        var filtered = events
            .Where(e =>
            {
                var subject = e["subject"]?.GetValue<string>();
                return !string.IsNullOrEmpty(subject) &&
                       subject.Contains(query, StringComparison.OrdinalIgnoreCase);
            })
            .Take(top)
            .ToList();

        if (filtered.Count == 0)
        {
            return $"No calendar events found matching: {query}";
        }

        return FormatCalendarEvents(filtered, "search");
    }

    private static string FormatCalendarEvents(List<JsonNode> events, string context)
    {
        if (events.Count == 0)
        {
            return context switch
            {
                "upcoming" => "No upcoming meetings found.",
                "recent" => "No recent meetings found.",
                "today" => "No meetings scheduled for today.",
                _ => "No calendar events found."
            };
        }

        var lines = new List<string>();

        foreach (var evt in events)
        {
            var subject = evt["subject"]?.GetValue<string>() ?? "(no subject)";
            var importance = evt["importance"]?.GetValue<string>();

            // Parse start/end times
            var startStr = evt["start"]?["dateTime"]?.GetValue<string>();
            var endStr = evt["end"]?["dateTime"]?.GetValue<string>();
            var startTz = evt["start"]?["timeZone"]?.GetValue<string>() ?? "UTC";

            var timeRange = FormatTimeRange(startStr, endStr, startTz);

            // Organizer
            var organizer = evt["organizer"]?["emailAddress"];
            var organizerName = organizer?["name"]?.GetValue<string>() ?? "Unknown";

            // Location
            var location = evt["location"]?["displayName"]?.GetValue<string>();
            var isOnline = evt["isOnlineMeeting"]?.GetValue<bool>() ?? false;
            var meetingUrl = evt["onlineMeetingUrl"]?.GetValue<string>();

            // Attendees count
            var attendees = evt["attendees"]?.AsArray();
            var attendeeCount = attendees?.Count ?? 0;

            // Body preview
            var bodyPreview = evt["bodyPreview"]?.GetValue<string>();

            // Format output
            var importanceMarker = importance == "high" ? " [HIGH]" : "";
            lines.Add($"{timeRange}{importanceMarker}");
            lines.Add($"  Subject: {GraphHelpers.Truncate(subject, 100)}");
            lines.Add($"  Organizer: {organizerName}");

            if (!string.IsNullOrEmpty(location))
            {
                lines.Add($"  Location: {location}");
            }

            if (isOnline)
            {
                lines.Add($"  Online Meeting: Yes");
                if (!string.IsNullOrEmpty(meetingUrl))
                {
                    lines.Add($"  Join URL: {meetingUrl}");
                }
            }

            if (attendeeCount > 0)
            {
                lines.Add($"  Attendees: {attendeeCount}");
            }

            if (!string.IsNullOrEmpty(bodyPreview))
            {
                lines.Add($"  Preview: {GraphHelpers.Truncate(bodyPreview, 150)}");
            }

            lines.Add("");
        }

        return string.Join("\n", lines);
    }

    private static string FormatTimeRange(string? startStr, string? endStr, string timeZone)
    {
        if (string.IsNullOrEmpty(startStr))
        {
            return "[Unknown time]";
        }

        try
        {
            var start = DateTime.Parse(startStr);
            var end = !string.IsNullOrEmpty(endStr) ? DateTime.Parse(endStr) : (DateTime?)null;

            var startFormatted = start.ToString("ddd MMM d, h:mm tt");

            if (end.HasValue)
            {
                // Same day - just show end time
                if (start.Date == end.Value.Date)
                {
                    return $"[{startFormatted} - {end.Value:h:mm tt}]";
                }
                else
                {
                    return $"[{startFormatted} - {end.Value:ddd MMM d, h:mm tt}]";
                }
            }

            return $"[{startFormatted}]";
        }
        catch
        {
            return $"[{startStr}]";
        }
    }
}
