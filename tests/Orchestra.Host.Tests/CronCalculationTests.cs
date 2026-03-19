using FluentAssertions;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for Cronos-based cron calculation in TriggerManager.
/// Tests the static CalculateNextCronTime behavior indirectly via CalculateNextFireTime,
/// which is private. We test by verifying the Cronos expression produces valid next times.
/// </summary>
public class CronCalculationTests
{
	[Fact]
	public void CronExpression_EveryMinute_ReturnsNextMinute()
	{
		// Arrange
		var cron = "* * * * *";

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var next = expression.GetNextOccurrence(DateTime.UtcNow);

		// Assert
		next.Should().NotBeNull();
		next!.Value.Should().BeAfter(DateTime.UtcNow);
		(next.Value - DateTime.UtcNow).TotalMinutes.Should().BeLessThanOrEqualTo(1);
	}

	[Fact]
	public void CronExpression_EveryHour_ReturnsNextHour()
	{
		// Arrange
		var cron = "0 * * * *";

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var next = expression.GetNextOccurrence(DateTime.UtcNow);

		// Assert
		next.Should().NotBeNull();
		next!.Value.Should().BeAfter(DateTime.UtcNow);
		(next.Value - DateTime.UtcNow).TotalHours.Should().BeLessThanOrEqualTo(1);
	}

	[Fact]
	public void CronExpression_EveryFiveMinutes_ReturnsCorrectInterval()
	{
		// Arrange
		var cron = "*/5 * * * *";

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var now = DateTime.UtcNow;
		var next = expression.GetNextOccurrence(now);

		// Assert
		next.Should().NotBeNull();
		next!.Value.Should().BeAfter(now);
		(next.Value - now).TotalMinutes.Should().BeLessThanOrEqualTo(5);
	}

	[Fact]
	public void CronExpression_DailyAtMidnight_ReturnsNextMidnight()
	{
		// Arrange
		var cron = "0 0 * * *"; // Every day at midnight

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var next = expression.GetNextOccurrence(DateTime.UtcNow);

		// Assert
		next.Should().NotBeNull();
		next!.Value.Hour.Should().Be(0);
		next.Value.Minute.Should().Be(0);
		next.Value.Should().BeAfter(DateTime.UtcNow);
	}

	[Fact]
	public void CronExpression_WeekdaysOnly_SkipsWeekends()
	{
		// Arrange
		var cron = "0 9 * * 1-5"; // 9 AM on weekdays

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var next = expression.GetNextOccurrence(DateTime.UtcNow);

		// Assert
		next.Should().NotBeNull();
		next!.Value.DayOfWeek.Should().NotBe(DayOfWeek.Saturday);
		next.Value.DayOfWeek.Should().NotBe(DayOfWeek.Sunday);
		next.Value.Hour.Should().Be(9);
	}

	[Fact]
	public void CronExpression_SpecificDayOfMonth_ParsesCorrectly()
	{
		// Arrange
		var cron = "0 12 15 * *"; // Noon on the 15th of every month

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var next = expression.GetNextOccurrence(DateTime.UtcNow);

		// Assert
		next.Should().NotBeNull();
		next!.Value.Day.Should().Be(15);
		next.Value.Hour.Should().Be(12);
		next.Value.Minute.Should().Be(0);
	}

	[Fact]
	public void CronExpression_InvalidExpression_ThrowsCronFormatException()
	{
		// Arrange
		var invalidCron = "invalid cron";

		// Act
		var act = () => Cronos.CronExpression.Parse(invalidCron);

		// Assert
		act.Should().Throw<Cronos.CronFormatException>();
	}

	[Fact]
	public void CronExpression_ConsecutiveOccurrences_AreOrdered()
	{
		// Arrange
		var cron = "*/10 * * * *"; // Every 10 minutes

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var first = expression.GetNextOccurrence(DateTime.UtcNow);
		var second = expression.GetNextOccurrence(first!.Value);

		// Assert
		first.Should().NotBeNull();
		second.Should().NotBeNull();
		second!.Value.Should().BeAfter(first.Value);
		(second.Value - first.Value).TotalMinutes.Should().Be(10);
	}

	[Fact]
	public void CronExpression_FromSpecificTime_ReturnsCorrectNext()
	{
		// Arrange
		var cron = "0 * * * *"; // Every hour at :00
		var fromTime = new DateTime(2025, 6, 15, 14, 30, 0, DateTimeKind.Utc);

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var next = expression.GetNextOccurrence(fromTime);

		// Assert
		next.Should().NotBeNull();
		next!.Value.Should().Be(new DateTime(2025, 6, 15, 15, 0, 0, DateTimeKind.Utc));
	}

	[Fact]
	public void CronExpression_MonthlyCron_ReturnsCorrectMonth()
	{
		// Arrange
		var cron = "0 0 1 * *"; // First day of every month at midnight
		var fromTime = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc);

		// Act
		var expression = Cronos.CronExpression.Parse(cron);
		var next = expression.GetNextOccurrence(fromTime);

		// Assert
		next.Should().NotBeNull();
		next!.Value.Should().Be(new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc));
	}
}
