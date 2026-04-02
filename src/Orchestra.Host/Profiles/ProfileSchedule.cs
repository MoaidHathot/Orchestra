namespace Orchestra.Host.Profiles;

/// <summary>
/// Defines time-window-based automatic activation schedule for a profile.
/// The profile is automatically activated when the current time falls within
/// any of the defined windows, and deactivated when outside all windows.
/// </summary>
public class ProfileSchedule
{
	/// <summary>
	/// The timezone in which the windows are evaluated (IANA timezone ID).
	/// Defaults to the system's local timezone.
	/// </summary>
	public string? Timezone { get; set; }

	/// <summary>
	/// Time windows during which the profile should be active.
	/// The profile is active when the current time falls within any window.
	/// </summary>
	public ScheduleWindow[] Windows { get; set; } = [];

	/// <summary>
	/// Resolves the timezone to a <see cref="TimeZoneInfo"/>.
	/// Falls back to local timezone if not set or not found.
	/// </summary>
	public TimeZoneInfo GetTimeZoneInfo()
	{
		if (string.IsNullOrWhiteSpace(Timezone))
			return TimeZoneInfo.Local;

		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById(Timezone);
		}
		catch (TimeZoneNotFoundException)
		{
			return TimeZoneInfo.Local;
		}
	}

	/// <summary>
	/// Determines whether the profile should be active at the given UTC time.
	/// </summary>
	public bool IsActiveAt(DateTimeOffset utcNow)
	{
		var tz = GetTimeZoneInfo();
		var localTime = TimeZoneInfo.ConvertTime(utcNow, tz);
		var dayOfWeek = localTime.DayOfWeek;
		var timeOfDay = TimeOnly.FromTimeSpan(localTime.TimeOfDay);

		foreach (var window in Windows)
		{
			if (window.IsActiveAt(dayOfWeek, timeOfDay))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Computes the next transition time (activate or deactivate) from the given UTC time.
	/// Returns null if no transitions exist (e.g., no windows defined).
	/// </summary>
	public DateTimeOffset? GetNextTransitionTime(DateTimeOffset utcNow)
	{
		if (Windows.Length == 0)
			return null;

		var tz = GetTimeZoneInfo();
		var localNow = TimeZoneInfo.ConvertTime(utcNow, tz);
		var currentlyActive = IsActiveAt(utcNow);

		// Search up to 8 days ahead (enough for weekly schedules)
		DateTimeOffset? earliest = null;

		for (var dayOffset = 0; dayOffset <= 8; dayOffset++)
		{
			var checkDate = localNow.Date.AddDays(dayOffset);
			var checkDayOfWeek = checkDate.DayOfWeek;

			foreach (var window in Windows)
			{
				if (!window.AppliesToDay(checkDayOfWeek))
					continue;

				var startTime = window.GetStartTimeOnly();
				var endTime = window.GetEndTimeOnly();
				var isOvernight = startTime > endTime;

				// Check start transition
				var startDateTime = new DateTimeOffset(checkDate.Year, checkDate.Month, checkDate.Day,
					startTime.Hour, startTime.Minute, 0, localNow.Offset);

				if (startDateTime > localNow && (!currentlyActive || isOvernight))
				{
					var utcStart = TimeZoneInfo.ConvertTimeToUtc(startDateTime.DateTime, tz);
					var candidate = new DateTimeOffset(utcStart, TimeSpan.Zero);
					if (earliest is null || candidate < earliest)
						earliest = candidate;
				}

				// Check end transition
				var endDate = isOvernight ? checkDate.AddDays(1) : checkDate;
				var endDateTime = new DateTimeOffset(endDate.Year, endDate.Month, endDate.Day,
					endTime.Hour, endTime.Minute, 0, localNow.Offset);

				if (endDateTime > localNow && (currentlyActive || isOvernight))
				{
					var utcEnd = TimeZoneInfo.ConvertTimeToUtc(endDateTime.DateTime, tz);
					var candidate = new DateTimeOffset(utcEnd, TimeSpan.Zero);
					if (earliest is null || candidate < earliest)
						earliest = candidate;
				}
			}

			// If we found a transition, no need to check further days
			if (earliest is not null)
				break;
		}

		return earliest;
	}
}

/// <summary>
/// A single time window defining when a profile should be active.
/// Supports day-of-week filtering and time ranges, including overnight windows.
/// </summary>
public class ScheduleWindow
{
	/// <summary>
	/// Days of the week this window applies to.
	/// Supports individual days ("monday", "tuesday", etc.) and shorthands
	/// ("weekdays" for Mon-Fri, "weekends" for Sat-Sun, "everyday" for all days).
	/// </summary>
	public string[] Days { get; set; } = [];

	/// <summary>
	/// Start time in HH:mm format (24-hour). The window is active from this time.
	/// </summary>
	public required string StartTime { get; set; }

	/// <summary>
	/// End time in HH:mm format (24-hour). The window is active until this time.
	/// If EndTime is less than StartTime, the window spans midnight.
	/// </summary>
	public required string EndTime { get; set; }

	private static readonly Dictionary<string, DayOfWeek[]> s_dayShorthands = new(StringComparer.OrdinalIgnoreCase)
	{
		["weekdays"] = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
		["weekends"] = [DayOfWeek.Saturday, DayOfWeek.Sunday],
		["everyday"] = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday],
	};

	private static readonly Dictionary<string, DayOfWeek> s_dayNames = new(StringComparer.OrdinalIgnoreCase)
	{
		["sunday"] = DayOfWeek.Sunday,
		["monday"] = DayOfWeek.Monday,
		["tuesday"] = DayOfWeek.Tuesday,
		["wednesday"] = DayOfWeek.Wednesday,
		["thursday"] = DayOfWeek.Thursday,
		["friday"] = DayOfWeek.Friday,
		["saturday"] = DayOfWeek.Saturday,
	};

	/// <summary>
	/// Resolves the configured days into concrete <see cref="DayOfWeek"/> values,
	/// expanding shorthands like "weekdays" and "weekends".
	/// If no days are specified, defaults to every day.
	/// </summary>
	public DayOfWeek[] GetResolvedDays()
	{
		if (Days.Length == 0)
			return s_dayShorthands["everyday"];

		var result = new HashSet<DayOfWeek>();
		foreach (var day in Days)
		{
			if (s_dayShorthands.TryGetValue(day, out var expanded))
			{
				foreach (var d in expanded)
					result.Add(d);
			}
			else if (s_dayNames.TryGetValue(day, out var dow))
			{
				result.Add(dow);
			}
		}
		return [.. result];
	}

	/// <summary>
	/// Parses the StartTime string into a <see cref="TimeOnly"/>.
	/// </summary>
	public TimeOnly GetStartTimeOnly() => TimeOnly.Parse(StartTime);

	/// <summary>
	/// Parses the EndTime string into a <see cref="TimeOnly"/>.
	/// </summary>
	public TimeOnly GetEndTimeOnly() => TimeOnly.Parse(EndTime);

	/// <summary>
	/// Whether this window's start time is after its end time, indicating it spans midnight.
	/// </summary>
	public bool IsOvernight => GetStartTimeOnly() > GetEndTimeOnly();

	/// <summary>
	/// Determines whether this window applies to the given day of the week.
	/// </summary>
	public bool AppliesToDay(DayOfWeek dayOfWeek)
	{
		return GetResolvedDays().Contains(dayOfWeek);
	}

	/// <summary>
	/// Determines whether this window is active at the given day and time.
	/// Handles overnight windows that span midnight.
	/// </summary>
	public bool IsActiveAt(DayOfWeek dayOfWeek, TimeOnly timeOfDay)
	{
		var start = GetStartTimeOnly();
		var end = GetEndTimeOnly();

		if (start <= end)
		{
			// Normal window: same day
			return AppliesToDay(dayOfWeek) && timeOfDay >= start && timeOfDay < end;
		}
		else
		{
			// Overnight window: spans midnight
			// Active if: (on the start day AND time >= start) OR (on the next day AND time < end)
			if (AppliesToDay(dayOfWeek) && timeOfDay >= start)
				return true;

			// Check if previous day had a window that spans into today
			var previousDay = dayOfWeek == DayOfWeek.Sunday ? DayOfWeek.Saturday : dayOfWeek - 1;
			if (AppliesToDay(previousDay) && timeOfDay < end)
				return true;

			return false;
		}
	}
}
