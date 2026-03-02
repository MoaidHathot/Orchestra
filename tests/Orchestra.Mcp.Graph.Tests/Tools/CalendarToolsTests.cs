using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Mcp.Graph.Authentication;
using Orchestra.Mcp.Graph.Configuration;
using Orchestra.Mcp.Graph.Services;
using Orchestra.Mcp.Graph.Tools;
using Xunit;

namespace Orchestra.Mcp.Graph.Tests.Tools;

public class CalendarToolsTests
{
    private static CalendarTools CreateCalendarTools(Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
    {
        handler ??= _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { value = Array.Empty<object>() }))
        };

        var messageHandler = new MockHttpMessageHandler(handler);
        var httpClient = new HttpClient(messageHandler);

        var tokenProvider = Substitute.For<ITokenProvider>();
        tokenProvider.GetTokenAsync(Arg.Any<TokenType>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("mock-token"));

        var options = new GraphOptions();
        var logger = Substitute.For<ILogger<GraphApiClient>>();

        var graphClient = new GraphApiClient(httpClient, tokenProvider, options, logger);
        return new CalendarTools(graphClient);
    }

    [Fact]
    public async Task GetUpcomingMeetings_WithInvalidHours_ReturnsError()
    {
        var calendarTools = CreateCalendarTools();
        var result = await calendarTools.GetUpcomingMeetings(hours: 0);

        result.Should().Be("Error: hours must be > 0");
    }

    [Fact]
    public async Task GetUpcomingMeetings_WithNegativeHours_ReturnsError()
    {
        var calendarTools = CreateCalendarTools();
        var result = await calendarTools.GetUpcomingMeetings(hours: -5);

        result.Should().Be("Error: hours must be > 0");
    }

    [Fact]
    public async Task GetRecentMeetings_WithInvalidHours_ReturnsError()
    {
        var calendarTools = CreateCalendarTools();
        var result = await calendarTools.GetRecentMeetings(hours: 0);

        result.Should().Be("Error: hours must be > 0");
    }

    [Fact]
    public async Task GetRecentMeetings_WithNegativeHours_ReturnsError()
    {
        var calendarTools = CreateCalendarTools();
        var result = await calendarTools.GetRecentMeetings(hours: -5);

        result.Should().Be("Error: hours must be > 0");
    }

    [Fact]
    public async Task GetUpcomingMeetings_NoEvents_ReturnsNoMeetingsMessage()
    {
        var calendarTools = CreateCalendarTools();
        var result = await calendarTools.GetUpcomingMeetings();

        result.Should().Be("No upcoming meetings found.");
    }

    [Fact]
    public async Task GetRecentMeetings_NoEvents_ReturnsNoMeetingsMessage()
    {
        var calendarTools = CreateCalendarTools();
        var result = await calendarTools.GetRecentMeetings();

        result.Should().Be("No recent meetings found.");
    }

    [Fact]
    public async Task GetTodaysMeetings_NoEvents_ReturnsNoMeetingsMessage()
    {
        var calendarTools = CreateCalendarTools();
        var result = await calendarTools.GetTodaysMeetings();

        result.Should().Be("No meetings scheduled for today.");
    }

    [Fact]
    public async Task SearchCalendar_NoMatches_ReturnsNotFoundMessage()
    {
        var calendarTools = CreateCalendarTools();
        var result = await calendarTools.SearchCalendar("nonexistent meeting");

        result.Should().Be("No calendar events found matching: nonexistent meeting");
    }

    [Fact]
    public async Task GetUpcomingMeetings_WithEvents_FormatsCorrectly()
    {
        var events = new
        {
            value = new[]
            {
                new
                {
                    subject = "Team Standup",
                    start = new { dateTime = "2024-01-15T09:00:00", timeZone = "UTC" },
                    end = new { dateTime = "2024-01-15T09:30:00", timeZone = "UTC" },
                    organizer = new { emailAddress = new { name = "John Doe", address = "john@example.com" } },
                    location = new { displayName = "Conference Room A" },
                    isOnlineMeeting = true,
                    onlineMeetingUrl = "https://teams.microsoft.com/meet/123",
                    attendees = new object[] { new { }, new { } },
                    importance = "normal",
                    bodyPreview = "Daily standup meeting"
                }
            }
        };

        var calendarTools = CreateCalendarTools(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(events))
            });

        var result = await calendarTools.GetUpcomingMeetings();

        result.Should().Contain("Team Standup");
        result.Should().Contain("John Doe");
        result.Should().Contain("Conference Room A");
        result.Should().Contain("Online Meeting: Yes");
        result.Should().Contain("Attendees: 2");
        result.Should().Contain("Daily standup meeting");
    }

    [Fact]
    public async Task GetUpcomingMeetings_HighImportance_IncludesMarker()
    {
        var events = new
        {
            value = new[]
            {
                new
                {
                    subject = "Urgent Meeting",
                    start = new { dateTime = "2024-01-15T09:00:00", timeZone = "UTC" },
                    end = new { dateTime = "2024-01-15T09:30:00", timeZone = "UTC" },
                    organizer = new { emailAddress = new { name = "Boss", address = "boss@example.com" } },
                    importance = "high",
                    isOnlineMeeting = false
                }
            }
        };

        var calendarTools = CreateCalendarTools(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(events))
            });

        var result = await calendarTools.GetUpcomingMeetings();

        result.Should().Contain("[HIGH]");
    }

    [Fact]
    public async Task SearchCalendar_WithMatches_FiltersCorrectly()
    {
        var events = new
        {
            value = new[]
            {
                new
                {
                    subject = "Project Alpha Review",
                    start = new { dateTime = "2024-01-15T09:00:00", timeZone = "UTC" },
                    end = new { dateTime = "2024-01-15T10:00:00", timeZone = "UTC" },
                    organizer = new { emailAddress = new { name = "PM", address = "pm@example.com" } },
                    importance = "normal",
                    isOnlineMeeting = false
                },
                new
                {
                    subject = "Team Standup",
                    start = new { dateTime = "2024-01-15T10:00:00", timeZone = "UTC" },
                    end = new { dateTime = "2024-01-15T10:30:00", timeZone = "UTC" },
                    organizer = new { emailAddress = new { name = "Lead", address = "lead@example.com" } },
                    importance = "normal",
                    isOnlineMeeting = false
                }
            }
        };

        var calendarTools = CreateCalendarTools(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(events))
            });

        var result = await calendarTools.SearchCalendar("alpha");

        result.Should().Contain("Project Alpha Review");
        result.Should().NotContain("Team Standup");
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
