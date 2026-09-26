using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orchestra.Cli.Commands;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// <see cref="LogLineFilter"/> - the <c>--level</c> filter of <c>orchestra logs</c> over the
/// file logger's <c>yyyy-MM-dd HH:mm:ss [Level] Category: message</c> format.
/// </summary>
public class LogLineFilterTests
{
	private const string Info = "2026-09-27 10:15:42 [Information] Orchestra.Host: started";
	private const string Warn = "2026-09-27 10:15:43 [Warning] Orchestra.Host: careful";
	private const string Error = "2026-09-27 10:15:44 [Error] Orchestra.Host: boom";
	private const string Stack = "   at Orchestra.Host.Something.Do() in Something.cs:line 42";

	[Theory]
	[InlineData(Info, LogLevel.Information)]
	[InlineData(Warn, LogLevel.Warning)]
	[InlineData(Error, LogLevel.Error)]
	[InlineData("2026-09-27 10:15:44 [Critical] X: y", LogLevel.Critical)]
	public void TryParseLevel_ReadsTheBracketedLevel(string line, LogLevel expected)
	{
		LogLineFilter.TryParseLevel(line).Should().Be(expected);
	}

	[Theory]
	[InlineData(Stack)]
	[InlineData("")]
	[InlineData("System.InvalidOperationException: boom")]
	[InlineData("2026-09-27 10:15:44 no bracket here")]
	public void TryParseLevel_ContinuationLines_AreNull(string line)
	{
		LogLineFilter.TryParseLevel(line).Should().BeNull();
	}

	[Fact]
	public void Trace_AcceptsEverything()
	{
		var filter = new LogLineFilter(LogLevel.Trace);

		new[] { Info, Stack, Warn }.Select(filter.Accept).Should().OnlyContain(accepted => accepted);
	}

	[Fact]
	public void Warning_DropsLowerLevels_AndKeepsContinuationsWithTheirEntry()
	{
		var filter = new LogLineFilter(LogLevel.Warning);

		filter.Accept(Info).Should().BeFalse();
		filter.Accept(Stack).Should().BeFalse("a stack line belongs to the dropped Information entry");
		filter.Accept(Error).Should().BeTrue();
		filter.Accept(Stack).Should().BeTrue("a stack line belongs to the kept Error entry");
		filter.Accept(Warn).Should().BeTrue();
	}
}
