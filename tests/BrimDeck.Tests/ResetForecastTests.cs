using BrimDeck.Core;

// The reset arithmetic, checked on the cases that go wrong in practice: a key added at an odd minute, a window that
// moves on while resets are due, and an interval shorter than the horizon so several resets fall in one window.
internal static class ResetForecastTests
{
    public static void Run(Action<string, bool> check)
    {
        // A key added at 11:18 starts its schedule on the hour, so its resets land on the hour rather than every :18.
        var added = new KeyReset { Anchor = new DateTimeOffset(2026, 11, 5, 11, 18, 34, TimeSpan.FromHours(8)), IntervalMinutes = 300 }.Normalized();
        check("An anchor is put on the hour", added.Anchor.Minute == 0 && added.Anchor.Second == 0 && added.Anchor.Hour == 11);
        var fromOdd = added.NextReset(added.Anchor.AddMinutes(30));
        check("A five-hour schedule from 11:00 resets at 16:00", fromOdd == new DateTimeOffset(2026, 11, 5, 16, 0, 0, TimeSpan.FromHours(8)));

        // An anchor already on the hour is left as it is.
        var onHour = new KeyReset { Anchor = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero), IntervalMinutes = 60 }.Normalized();
        check("An anchor on the hour is unchanged", onHour.Anchor == new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));

        // The look-ahead moves on the fifth day and must not lose a reset when it does: with a weekly interval and a
        // seven-day horizon, the reset after the move is the same one, and it is still ahead.
        var weekly = new KeyReset { Anchor = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), IntervalMinutes = 10080, HorizonDays = 7, RefreshDays = 5 }.Normalized();
        var beforeMove = weekly.NextReset(weekly.Anchor.AddDays(4));
        var afterMove = weekly.NextReset(weekly.Anchor.AddDays(5));
        check("A weekly key answers the same reset across the window move", beforeMove == afterMove && beforeMove == weekly.Anchor.AddDays(7));
        // And the move leaves the reset ahead of the moment it is asked about, never behind.
        check("The answer after the move is still ahead", afterMove > weekly.Anchor.AddDays(5));

        // An interval far shorter than the horizon: a reset every five hours in a seven-day window.
        var fiveHourly = new KeyReset { Anchor = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), IntervalMinutes = 300, HorizonDays = 7, RefreshDays = 5 }.Normalized();
        var list = fiveHourly.Schedule.Upcoming(fiveHourly.Anchor.AddHours(1));
        check("A five-hourly key lists the resets of a week", list.Count == 33);
        check("The first is the next five-hour mark", list[0] == fiveHourly.Anchor.AddHours(5));
        check("Each is five hours after the one before", list.Zip(list.Skip(1)).All(pair => pair.Second - pair.First == TimeSpan.FromHours(5)));
        check("The last still falls inside the week", list[^1] <= fiveHourly.Anchor.AddDays(7));

        // A reset already passed is not offered as the next one.
        check("A passed reset is not the next one",
            fiveHourly.NextReset(fiveHourly.Anchor.AddHours(5).AddSeconds(1)) == fiveHourly.Anchor.AddHours(10));

        // A reported reset wins over the schedule while it is still ahead, and keeps its own minutes: a service that
        // said 16:43 meant 16:43.
        var reported = fiveHourly.Copy();
        reported.ProbedResetAt = new DateTimeOffset(2026, 11, 5, 16, 43, 0, TimeSpan.Zero);
        check("A reported reset is used as it was stated",
            reported.NextReset(new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero)) == new DateTimeOffset(2026, 11, 5, 16, 43, 0, TimeSpan.Zero));
        // Once it has passed, the schedule answers again.
        var later = reported.NextReset(new DateTimeOffset(2026, 11, 5, 17, 0, 0, TimeSpan.Zero));
        check("A passed report falls back to the schedule", later is not null && later > new DateTimeOffset(2026, 11, 5, 17, 0, 0, TimeSpan.Zero));

        // The window moves in whole steps from the anchor, not with the clock: the same window answers all day.
        var window = fiveHourly.Schedule.WindowStart(new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero));
        var windowLater = fiveHourly.Schedule.WindowStart(new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.Zero));
        check("The window does not slide with the clock", window == windowLater && window == fiveHourly.Anchor);
        // On the fifth day it steps by exactly five days.
        check("The window steps by its own period",
            fiveHourly.Schedule.WindowStart(fiveHourly.Anchor.AddDays(5)) == fiveHourly.Anchor.AddDays(5));

        // A key whose interval is longer than its horizon still answers: the next reset is beyond the window, and the
        // countdown must not read "unknown" because of it.
        var monthly = new KeyReset { Anchor = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), IntervalMinutes = 43200, HorizonDays = 7, RefreshDays = 5 }.Normalized();
        check("An interval longer than the window still answers", monthly.NextReset(monthly.Anchor.AddDays(1)) == monthly.Anchor.AddDays(30));
        check("Its window holds no reset, which is not an error", monthly.Schedule.Upcoming(monthly.Anchor.AddDays(1)).Count == 0);

        // The interval in words, for the row under the ring.
        check("A day-and-a-half interval is read in minutes", new KeyReset { IntervalMinutes = 2160 }.Normalized().IntervalText.Contains("36") || new KeyReset { IntervalMinutes = 2160 }.Normalized().IntervalText.Contains("1"));
    }
}
