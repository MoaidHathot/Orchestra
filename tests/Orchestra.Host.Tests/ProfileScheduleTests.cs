using FluentAssertions;
using Orchestra.Host.Profiles;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for ScheduleWindow and ProfileSchedule time-window evaluation.
/// </summary>
public class ProfileScheduleTests
{
	// ── ScheduleWindow.GetResolvedDays ──

	[Fact]
	public void GetResolvedDays_Weekdays_ExpandsToMonThroughFri()
	{
		var window = new ScheduleWindow { Days = ["weekdays"], StartTime = "09:00", EndTime = "17:00" };
		var days = window.GetResolvedDays();

		days.Should().BeEquivalentTo([
			DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
			DayOfWeek.Thursday, DayOfWeek.Friday
		]);
	}

	[Fact]
	public void GetResolvedDays_Weekends_ExpandsToSatSun()
	{
		var window = new ScheduleWindow { Days = ["weekends"], StartTime = "09:00", EndTime = "17:00" };
		var days = window.GetResolvedDays();

		days.Should().BeEquivalentTo([DayOfWeek.Saturday, DayOfWeek.Sunday]);
	}

	[Fact]
	public void GetResolvedDays_Everyday_ExpandsToAllDays()
	{
		var window = new ScheduleWindow { Days = ["everyday"], StartTime = "09:00", EndTime = "17:00" };
		var days = window.GetResolvedDays();

		days.Should().HaveCount(7);
	}

	[Fact]
	public void GetResolvedDays_Empty_DefaultsToEveryday()
	{
		var window = new ScheduleWindow { Days = [], StartTime = "09:00", EndTime = "17:00" };
		var days = window.GetResolvedDays();

		days.Should().HaveCount(7);
	}

	[Fact]
	public void GetResolvedDays_IndividualDays_ParsesCorrectly()
	{
		var window = new ScheduleWindow { Days = ["monday", "wednesday", "friday"], StartTime = "09:00", EndTime = "17:00" };
		var days = window.GetResolvedDays();

		days.Should().BeEquivalentTo([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);
	}

	[Fact]
	public void GetResolvedDays_MixedShorthandsAndDays_Deduplicates()
	{
		var window = new ScheduleWindow { Days = ["weekdays", "monday"], StartTime = "09:00", EndTime = "17:00" };
		var days = window.GetResolvedDays();

		// monday is in weekdays, so still 5 days
		days.Should().HaveCount(5);
		days.Should().Contain(DayOfWeek.Monday);
	}

	// ── ScheduleWindow.IsOvernight ──

	[Fact]
	public void IsOvernight_SameDayWindow_ReturnsFalse()
	{
		var window = new ScheduleWindow { Days = ["everyday"], StartTime = "09:00", EndTime = "17:00" };
		window.IsOvernight.Should().BeFalse();
	}

	[Fact]
	public void IsOvernight_OvernightWindow_ReturnsTrue()
	{
		var window = new ScheduleWindow { Days = ["everyday"], StartTime = "22:00", EndTime = "06:00" };
		window.IsOvernight.Should().BeTrue();
	}

	// ── ScheduleWindow.IsActiveAt ──

	[Fact]
	public void IsActiveAt_WithinWindow_ReturnsTrue()
	{
		var window = new ScheduleWindow { Days = ["monday"], StartTime = "09:00", EndTime = "17:00" };

		window.IsActiveAt(DayOfWeek.Monday, new TimeOnly(12, 0)).Should().BeTrue();
		window.IsActiveAt(DayOfWeek.Monday, new TimeOnly(9, 0)).Should().BeTrue(); // inclusive start
	}

	[Fact]
	public void IsActiveAt_OutsideWindow_ReturnsFalse()
	{
		var window = new ScheduleWindow { Days = ["monday"], StartTime = "09:00", EndTime = "17:00" };

		window.IsActiveAt(DayOfWeek.Monday, new TimeOnly(8, 59)).Should().BeFalse();
		window.IsActiveAt(DayOfWeek.Monday, new TimeOnly(17, 0)).Should().BeFalse(); // exclusive end
		window.IsActiveAt(DayOfWeek.Tuesday, new TimeOnly(12, 0)).Should().BeFalse(); // wrong day
	}

	[Fact]
	public void IsActiveAt_OvernightWindow_ActiveBeforeMidnight()
	{
		var window = new ScheduleWindow { Days = ["friday"], StartTime = "22:00", EndTime = "06:00" };

		// Friday at 23:00 - should be active (on start day, after start time)
		window.IsActiveAt(DayOfWeek.Friday, new TimeOnly(23, 0)).Should().BeTrue();
	}

	[Fact]
	public void IsActiveAt_OvernightWindow_ActiveAfterMidnight()
	{
		var window = new ScheduleWindow { Days = ["friday"], StartTime = "22:00", EndTime = "06:00" };

		// Saturday at 03:00 - should be active (previous day had overnight window)
		window.IsActiveAt(DayOfWeek.Saturday, new TimeOnly(3, 0)).Should().BeTrue();
	}

	[Fact]
	public void IsActiveAt_OvernightWindow_InactiveAfterEnd()
	{
		var window = new ScheduleWindow { Days = ["friday"], StartTime = "22:00", EndTime = "06:00" };

		// Saturday at 07:00 - should be inactive (past end time, no Saturday window)
		window.IsActiveAt(DayOfWeek.Saturday, new TimeOnly(7, 0)).Should().BeFalse();
	}

	// ── ProfileSchedule.IsActiveAt ──

	[Fact]
	public void ProfileSchedule_IsActiveAt_WithinAnyWindow_ReturnsTrue()
	{
		var schedule = new ProfileSchedule
		{
			Timezone = "UTC",
			Windows =
			[
				new ScheduleWindow { Days = ["monday"], StartTime = "09:00", EndTime = "12:00" },
				new ScheduleWindow { Days = ["monday"], StartTime = "13:00", EndTime = "17:00" },
			]
		};

		// Monday 10:00 UTC
		var monday10am = new DateTimeOffset(2026, 3, 30, 10, 0, 0, TimeSpan.Zero); // Monday
		schedule.IsActiveAt(monday10am).Should().BeTrue();

		// Monday 14:00 UTC
		var monday2pm = new DateTimeOffset(2026, 3, 30, 14, 0, 0, TimeSpan.Zero);
		schedule.IsActiveAt(monday2pm).Should().BeTrue();
	}

	[Fact]
	public void ProfileSchedule_IsActiveAt_OutsideAllWindows_ReturnsFalse()
	{
		var schedule = new ProfileSchedule
		{
			Timezone = "UTC",
			Windows =
			[
				new ScheduleWindow { Days = ["monday"], StartTime = "09:00", EndTime = "17:00" },
			]
		};

		// Monday 20:00 UTC
		var monday8pm = new DateTimeOffset(2026, 3, 30, 20, 0, 0, TimeSpan.Zero);
		schedule.IsActiveAt(monday8pm).Should().BeFalse();

		// Tuesday 12:00 UTC
		var tuesday12pm = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
		schedule.IsActiveAt(tuesday12pm).Should().BeFalse();
	}

	[Fact]
	public void ProfileSchedule_NoWindows_AlwaysInactive()
	{
		var schedule = new ProfileSchedule
		{
			Timezone = "UTC",
			Windows = [],
		};

		var now = new DateTimeOffset(2026, 3, 30, 12, 0, 0, TimeSpan.Zero);
		schedule.IsActiveAt(now).Should().BeFalse();
	}

	// ── ProfileSchedule.GetNextTransitionTime ──

	[Fact]
	public void GetNextTransitionTime_NoWindows_ReturnsNull()
	{
		var schedule = new ProfileSchedule { Windows = [] };
		schedule.GetNextTransitionTime(DateTimeOffset.UtcNow).Should().BeNull();
	}

	[Fact]
	public void GetNextTransitionTime_InsideWindow_ReturnsEndTime()
	{
		var schedule = new ProfileSchedule
		{
			Timezone = "UTC",
			Windows =
			[
				new ScheduleWindow { Days = ["everyday"], StartTime = "09:00", EndTime = "17:00" },
			]
		};

		// At 12:00 UTC on a Monday, next transition should be 17:00 (end of window)
		var now = new DateTimeOffset(2026, 3, 30, 12, 0, 0, TimeSpan.Zero);
		var next = schedule.GetNextTransitionTime(now);

		next.Should().NotBeNull();
		next!.Value.Hour.Should().Be(17);
	}

	[Fact]
	public void GetNextTransitionTime_OutsideWindow_ReturnsNextStartTime()
	{
		var schedule = new ProfileSchedule
		{
			Timezone = "UTC",
			Windows =
			[
				new ScheduleWindow { Days = ["everyday"], StartTime = "09:00", EndTime = "17:00" },
			]
		};

		// At 20:00 UTC, next transition should be 09:00 the next day
		var now = new DateTimeOffset(2026, 3, 30, 20, 0, 0, TimeSpan.Zero);
		var next = schedule.GetNextTransitionTime(now);

		next.Should().NotBeNull();
		next!.Value.Should().BeAfter(now);
	}
}
