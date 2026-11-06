using BrimDeck.Core;

// The reset schedule is the one piece whose answer must stay right when nothing is running: the same
// anchor and interval have to give the same instants a month later. These tests pin that down, plus the
// window arithmetic that decides which resets the panel lists and when the list moves on.
internal static class ResetScheduleTests
{
    public static void Run(Action<string, bool> check)
    {
        var anchor = new DateTimeOffset(2026, 3, 2, 9, 30, 0, TimeSpan.FromHours(8));
        var week = TimeSpan.FromDays(7);
        var schedule = new ResetSchedule(anchor, week, 7, 5);

        // Resets are the anchor plus whole intervals, whoever asks and whenever.
        check("A reset schedule repeats from the anchor",
            schedule.Next(anchor) == anchor && schedule.Next(anchor.AddSeconds(1)) == anchor + week);
        check("The next reset skips past earlier ones",
            schedule.Next(anchor + week + TimeSpan.FromHours(1)) == anchor + 2 * week);
        check("A time before the anchor still points at it",
            schedule.Next(anchor - TimeSpan.FromDays(30)) == anchor);
        check("The last reset is the one just passed",
            schedule.Last(anchor + week + TimeSpan.FromHours(1)) == anchor + week);
        check("Before the anchor there is no last reset", schedule.Last(anchor - TimeSpan.FromDays(1)) is null);

        // The horizon covers a week from the window's start, and moves on every five days, so the windows
        // overlap by two days: no reset can fall between one window ending and the next starting.
        var start = schedule.WindowStart(anchor);
        check("The first window starts at the anchor", start == anchor);
        check("The window moves on the fifth day", schedule.WindowStart(anchor.AddDays(5)) == anchor.AddDays(5));
        check("The window holds still until then", schedule.WindowStart(anchor.AddDays(4).AddHours(23)) == anchor);
        var listed = schedule.Upcoming(anchor);
        check("A week-ahead window lists the next reset", listed.Count == 1 && listed[0] == anchor + week);
        // Two days into the next window, the reset still shows: this is the overlap that makes the scroll safe.
        check("Resets stay listed across a window change", schedule.Upcoming(anchor.AddDays(5).AddHours(1)).Count == 1);

        // A 3-day interval inside a 7-day horizon lists the resets that fall in it, soonest first.
        var threeDay = new ResetSchedule(anchor, TimeSpan.FromDays(3), 7, 5);
        var dense = threeDay.Upcoming(anchor);
        check("A dense schedule lists every reset in the window",
            dense.Count == 2 && dense[0] == anchor + TimeSpan.FromDays(3) && dense[1] == anchor + TimeSpan.FromDays(6));
        // A reset that has already passed is not something to wait for, so it leaves the list.
        check("A passed reset leaves the list", threeDay.Upcoming(anchor + TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1)).Count == 1);

        // An hour-by-hour key, the awkward case for the "whole days" shortcut: 48 resets in a two-day window.
        var hourly = new ResetSchedule(anchor, TimeSpan.FromHours(1), 2, 1);
        var hours = hourly.Upcoming(anchor);
        check("An hourly schedule lists its window", hours.Count == 48 && hours[0] == anchor.AddHours(1));

        // Elapsed share: a week-long key five days in is about 71% used up.
        double? elapsed = schedule.ElapsedPercent(anchor + TimeSpan.FromDays(5));
        check("Elapsed share follows the interval", elapsed is > 71.3 and < 71.5);

        // Normalize keeps the refresh inside the horizon, so a window can never be shorter than one refresh.
        var odd = new ResetSchedule(anchor, week, 7, 7).Normalize();
        check("A refresh that would reach the horizon is pulled back", odd.RefreshDays <= odd.HorizonDays - 1);
        var tiny = new ResetSchedule(anchor, TimeSpan.Zero, 7, 5).Normalize();
        check("A zero interval is not a schedule", tiny.IsNone && tiny.Next(anchor) is null && tiny.Upcoming(anchor).Count == 0);

        // The settings row the user edits carries the same arithmetic. The anchor it is given is put on the hour, so the
        // row's own anchor is the one to compare against rather than the one written here.
        var key = new KeyReset { Name = "  Claude   Pro ", Anchor = anchor, IntervalMinutes = 7 * 24 * 60 }.Normalized();
        check("A key row normalizes its name", key.DisplayName == "Claude Pro");
        check("A key row reports a readable interval", key.IntervalText == Loc.T("7 天", "7 d"));
        check("A key row answers through its settings", key.Schedule.Next(key.Anchor + TimeSpan.FromDays(1)) == key.Anchor + week);
        var renamed = new KeyReset { Name = "", IntervalMinutes = 90 }.Normalized();
        check("An unnamed key falls back to a label", renamed.DisplayName.Length > 0);
        check("Minutes are read in minutes", renamed.IntervalText == Loc.T("90 分钟", "90 min"));
        var clamped = new KeyReset { IntervalMinutes = 0, HorizonDays = 999, RefreshDays = 999 }.Normalized();
        check("A broken interval is clamped, not dropped", clamped.IntervalMinutes >= 1 && clamped.RefreshDays < clamped.HorizonDays);

        // Settings keep the rows across a copy, which is what saving and reloading goes through.
        var settings = new DeckSettings { KeyResets = [key] };
        settings.Normalize();
        var copy = settings.Copy();
        check("Key rows survive a settings copy", copy.KeyResets.Count == 1 && copy.KeyResets[0].Name == "Claude Pro");
        check("Key rows keep their identity across a copy", copy.KeyResets[0].InstanceId == settings.KeyResets[0].InstanceId);

        // Duplicate instance ids are repaired rather than merged, so two rows stay two rows.
        var duplicated = new DeckSettings { KeyResets = [new KeyReset { Name = "A" }, new KeyReset { Name = "B" }] };
        duplicated.KeyResets[1].InstanceId = duplicated.KeyResets[0].InstanceId;
        duplicated.Normalize();
        check("Rows with the same identity are told apart", duplicated.KeyResets[0].InstanceId != duplicated.KeyResets[1].InstanceId);

        // The message ledger is what stops a message being announced twice.
        var ledger = new MessageLedger(4);
        check("A first message is new", ledger.Add("feishu:1"));
        check("The same message is not new again", !ledger.Add("feishu:1"));
        foreach (var key2 in new[] { "feishu:2", "feishu:3", "feishu:4" }) ledger.Add(key2);
        check("The ledger holds its capacity", ledger.Keys.Count == 4 && ledger.Contains("feishu:1"));
        // One past the capacity pushes the oldest out, and only the oldest.
        ledger.Add("feishu:5");
        check("The oldest entry falls out first",
            !ledger.Contains("feishu:1") && ledger.Contains("feishu:2") && ledger.Contains("feishu:5"));
    }
}
