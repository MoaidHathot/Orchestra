using FluentAssertions;
using Orchestra.Mcp.Graph.Services;
using Xunit;

namespace Orchestra.Mcp.Graph.Tests.Services;

public class GraphHelpersTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("<p>paragraph</p>", "paragraph")]
    [InlineData("<div><span>nested</span></div>", "nested")]
    [InlineData("<b>bold</b> and <i>italic</i>", "bold and italic")]
    [InlineData("text&nbsp;with&nbsp;spaces", "text with spaces")]
    [InlineData("text&#160;with&#160;spaces", "text with spaces")]
    [InlineData("special&amp;chars&lt;here&gt;", "special&chars<here>")]
    [InlineData("  multiple   spaces  ", "multiple spaces")]
    public void StripHtml_VariousCases_ReturnsExpected(string? input, string expected)
    {
        var result = GraphHelpers.StripHtml(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("2024-01-15T10:30:00Z", "01/15 10:30")]
    [InlineData("2024-01-15T10:30:00+00:00", "01/15 10:30")]
    [InlineData("2024-12-25T23:59:59.123Z", "12/25 23:59")]
    [InlineData("2024-06-01T12:00:00.1234567Z", "06/01 12:00")]
    public void FormatTime_VariousCases_ReturnsExpected(string? input, string expected)
    {
        var result = GraphHelpers.FormatTime(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void FormatTime_InvalidFormat_ReturnsTruncated()
    {
        var result = GraphHelpers.FormatTime("this is not a date at all");

        result.Should().Be("this is not a da"); // First 16 chars
    }

    [Theory]
    [InlineData(null, 120, "")]
    [InlineData("", 120, "")]
    [InlineData("short text", 120, "short text")]
    [InlineData("this is a long text that needs truncation", 20, "this is a long te...")]
    [InlineData("exactly20characters!", 20, "exactly20characters!")]
    public void Truncate_VariousCases_ReturnsExpected(string? input, int maxLength, string expected)
    {
        var result = GraphHelpers.Truncate(input, maxLength);

        result.Should().Be(expected);
    }

    [Fact]
    public void StripHtml_ComplexHtml_StripsCorrectly()
    {
        var html = @"
            <html>
            <body>
                <div class=""message"">
                    <p>Hello, <b>World</b>!</p>
                    <p>This is a <a href=""https://example.com"">link</a>.</p>
                </div>
            </body>
            </html>";

        var result = GraphHelpers.StripHtml(html);

        result.Should().Be("Hello, World! This is a link.");
    }
}
