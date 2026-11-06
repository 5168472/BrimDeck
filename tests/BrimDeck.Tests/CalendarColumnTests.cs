using BrimDeck.Core;

// What the calendar page shows in the column beside the month, and what a locked size means. Both are rules about
// data rather than about drawing, so they are checked here without a window.
internal static class CalendarColumnTests
{
    public static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset At(int day, int hour) => new(2026, 11, day, hour, 0, 0, TimeSpan.FromHours(8));

        var events = new List<CalendarEvent>
        {
            new("Standup", At(5, 10), At(5, 10).AddMinutes(15), false, "a", "Feishu", ""),
            new("Review", At(5, 15), At(5, 16), false, "b", "Feishu", ""),
            new("Planning", At(7, 9), At(7, 10), false, "c", "Feishu", ""),
            // A meeting that starts late at night belongs to the day it began on.
            new("Late call", At(7, 23), At(8, 1), false, "d", "Feishu", ""),
            new("Holiday", At(5, 0), null, true, "e", "subscription", ""),
            // One that has already begun is not "coming up".
            new("Passed", At(1, 9), At(1, 10), false, "f", "Feishu", "")
        };

        // A picked day shows that day's meetings, in the order they happen.
        var fifth = AgendaService.OnDay(events, new DateOnly(2026, 11, 5));
        check("A picked day shows its meetings", fifth.Count == 2);
        check("A picked day's meetings are in order", fifth[0].Title == "Standup" && fifth[1].Title == "Review");
        check("A subscribed holiday is not a meeting on the day", fifth.All(entry => entry.Source != "subscription"));

        // A meeting that starts at 23:00 is on the day it started, and the day after does not claim it.
        var seventh = AgendaService.OnDay(events, new DateOnly(2026, 11, 7));
        var eighth = AgendaService.OnDay(events, new DateOnly(2026, 11, 8));
        check("A late meeting is on the day it started", seventh.Any(entry => entry.Title == "Late call"));
        check("A late meeting is not on the next day", eighth.Count == 0);

        // A day with nothing on it is empty, and that is not the same as an error.
        check("A day with no meetings is empty", AgendaService.OnDay(events, new DateOnly(2026, 11, 20)).Count == 0);

        // With no day picked the column shows the month the page is on, from today onward in the current month. This
        // is what makes stepping the month mean something: a column that kept showing this month's meetings under next
        // month's grid answered a question nobody asked.
        var thisMonth = new DateTime(2026, 11, 1);
        var upcoming = AgendaService.CalendarColumn(events, null, thisMonth, now);
        check("With no day picked the column shows this month's meetings", upcoming.Any(entry => entry.Title == "Standup"));
        check("A meeting that has already begun is not shown as upcoming", upcoming.All(entry => entry.Title != "Passed"));
        check("A subscription is not in the column", upcoming.All(entry => entry.Source != "subscription"));
        check("The column is in the order the meetings happen", upcoming.Select(entry => entry.Start).SequenceEqual(upcoming.Select(entry => entry.Start).OrderBy(start => start)));

        // A month that does not hold today shows all of it, past days included: every day of a month already gone is
        // equally past, and a reader who stepped back is asking what happened.
        var october = new DateTime(2026, 10, 1);
        var past = new CalendarEvent("Earlier", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8)), null, false, "h", "Feishu", "");
        var back = AgendaService.CalendarColumn([.. events, past], null, october, now);
        check("A month already gone shows its meetings", back.Any(entry => entry.Title == "Earlier"));
        check("A month already gone does not show this month's", back.All(entry => entry.Title != "Standup"));

        // Stepping to another month carries the column with it.
        var december = new DateTime(2026, 12, 1);
        var later = new CalendarEvent("Next month", new DateTimeOffset(2026, 12, 3, 9, 0, 0, TimeSpan.FromHours(8)), null, false, "i", "Feishu", "");
        var forward = AgendaService.CalendarColumn([.. events, later], null, december, now);
        check("The next month's column shows the next month's meetings", forward.Count == 1 && forward[0].Title == "Next month");

        // A picked day ignores the month: asking about a day is asking about that day, whatever the grid shows.
        var far = new CalendarEvent("Far away", new DateTimeOffset(2027, 3, 1, 10, 0, 0, TimeSpan.FromHours(8)), null, false, "g", "Feishu", "");
        var pickedFar = AgendaService.CalendarColumn([.. events, far], new DateOnly(2027, 3, 1), december, now);
        check("A picked day is answered whatever the month shown", pickedFar.Count == 1 && pickedFar[0].Title == "Far away");

        // How many entries the column holds, from the settings: five by default, and a hand-edited file cannot ask for
        // none of them or for a thousand.
        var settings = new DeckSettings();
        check("The column holds five entries by default", settings.Agenda.ListRows == 5);
        settings.Agenda.ListRows = 0;
        settings.Normalize();
        check("A column of no entries is raised to one", settings.Agenda.ListRows == 1);
        settings.Agenda.ListRows = 999;
        settings.Normalize();
        check("A column of a thousand entries is lowered to twelve", settings.Agenda.ListRows == 12);
        settings.Agenda.ListRows = 7;
        settings.Normalize();
        check("A sensible number of entries is left alone", settings.Agenda.ListRows == 7);
        check("The number of entries survives a copy", settings.Copy().Agenda.ListRows == 7);

        // The lock is off by default, survives a copy and a save, and is read from the settings so a switch takes
        // effect at once.
        check("The size is not locked by default", !new DeckSettings().SizeLocked);
        var locked = new DeckSettings { SizeLocked = true };
        check("A locked size survives a copy", locked.Copy().SizeLocked);

        // Stepping the month. A month already gone still answers: the view used to clamp itself to the month today is
        // in, so pressing "previous" on the current month did nothing at all, which is why the button looked dead.
        var decemberDays = AgendaService.CalendarColumn([.. events, later], null, new DateTime(2026, 12, 1), now);
        check("A month ahead is drawn from its own days", decemberDays.Count == 1 && decemberDays[0].Title == "Next month");
        var septemberDays = AgendaService.CalendarColumn(events, null, new DateTime(2026, 9, 1), now);
        check("A month behind is drawn as empty rather than clamped forward", septemberDays.Count == 0);
    }
}
