using Cronos;
using Orchestra.Watchers.Models;

namespace Orchestra.Watchers.Runtime;

/// <summary>
/// Turns a <see cref="WatcherSchedule"/> plus the failure back-off rule into concrete tick times.
/// Cron expressions go through Cronos - the same library the scheduler trigger uses - evaluated in
/// UTC; a 6-field expression (leading seconds) is detected by token count.
/// </summary>
public static class WatcherScheduleCalculator
{
	/// <summary>Base delay for the exponential back-off applied after a failed tick.</summary>
	public static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(2);

	/// <summary>Upper bound on the back-off delay.</summary>
	public static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(300);

	/// <summary>Parses a 5- or 6-field cron expression; throws <see cref="CronFormatException"/> when invalid.</summary>
	public static CronExpression ParseCron(string cron)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cron);

		var fieldCount = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
		return fieldCount >= 6
			? CronExpression.Parse(cron, CronFormat.IncludeSeconds)
			: CronExpression.Parse(cron);
	}

	/// <summary>
	/// The next tick after <paramref name="now"/> for the schedule alone (no back-off).
	/// <paramref name="lastTickStart"/> anchors interval schedules so a slow tick does not drift the
	/// cadence; when it is <see langword="null"/> (first tick) an interval schedule fires immediately.
	/// </summary>
	public static DateTimeOffset NextTick(WatcherSchedule schedule, DateTimeOffset now, DateTimeOffset? lastTickStart)
	{
		ArgumentNullException.ThrowIfNull(schedule);

		if (!string.IsNullOrWhiteSpace(schedule.Cron))
		{
			var next = ParseCron(schedule.Cron).GetNextOccurrence(now.UtcDateTime, inclusive: false);
			// A cron expression that never fires again (or is exhausted) falls back to one hour, matching the scheduler trigger.
			return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : now.AddHours(1);
		}

		var interval = TimeSpan.FromSeconds(schedule.IntervalSeconds ?? 60);
		if (lastTickStart is null)
			return now;

		var due = lastTickStart.Value + interval;
		return due <= now ? now : due;
	}

	/// <summary>
	/// Extra delay after <paramref name="consecutiveFailures"/> failed ticks:
	/// <c>min(2s * 2^(failures-1), 300s)</c>, zero when there are no failures.
	/// </summary>
	public static TimeSpan Backoff(int consecutiveFailures)
	{
		if (consecutiveFailures <= 0)
			return TimeSpan.Zero;

		// 2^(n-1) overflows long past the cap; clamp the exponent first.
		var exponent = Math.Min(consecutiveFailures - 1, 20);
		var ticks = BackoffBase.Ticks * (1L << exponent);
		return ticks >= BackoffCap.Ticks ? BackoffCap : TimeSpan.FromTicks(ticks);
	}

	/// <summary>
	/// The moment the next tick should start: the scheduled time pushed out by the back-off when the
	/// previous ticks failed.
	/// </summary>
	public static DateTimeOffset NextTickWithBackoff(WatcherSchedule schedule, DateTimeOffset now, DateTimeOffset? lastTickStart, int consecutiveFailures)
	{
		var scheduled = NextTick(schedule, now, lastTickStart);
		var backoff = Backoff(consecutiveFailures);
		if (backoff == TimeSpan.Zero)
			return scheduled;

		var earliest = now + backoff;
		return scheduled > earliest ? scheduled : earliest;
	}
}
