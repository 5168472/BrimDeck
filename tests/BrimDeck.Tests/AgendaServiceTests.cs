using BrimDeck.Core;

internal static class AgendaServiceTests
{
    public static void Run(Action<string, bool> check)
    {
        var start = new DateTimeOffset(2026, 3, 3, 9, 0, 0, TimeSpan.Zero);

        // The same meeting seen through a work calendar and through the mail that carried its invitation is one
        // meeting, because the key is the meeting's own identifier and the moment it starts.
        var fromCalendar = new CalendarEvent("Standup", start, start.AddHours(1), false, "meeting-1", "Feishu", "a@b.c");
        var fromMail = new CalendarEvent("Standup", start, start.AddHours(1), false, "meeting-1", "mail", "a@b.c");
        var merged = AgendaService.Dedupe([fromCalendar, fromMail]);
        check("The same meeting from two sources is one meeting", merged.Count == 1);
        check("The calendar's copy is the one that is kept", merged.Count == 1 && merged[0].Source == "Feishu");

        // Two meetings that differ in when they start are two meetings.
        var later = new CalendarEvent("Standup", start.AddHours(2), start.AddHours(3), false, "meeting-1", "mail", "a@b.c");
        check("Meetings at different times are two meetings", AgendaService.Dedupe([fromCalendar, later]).Count == 2);

        // Two meetings with different identifiers are two meetings.
        var other = new CalendarEvent("Review", start, start.AddHours(1), false, "meeting-2", "mail", "a@b.c");
        check("Meetings with different identifiers are two meetings", AgendaService.Dedupe([fromCalendar, other]).Count == 2);

        // The list comes out in the order the meetings happen.
        var ordered = AgendaService.Dedupe([later, fromCalendar]);
        check("The meetings come out in the order they happen", ordered.Count == 2 && ordered[0].Start == start);

        // A reminder is due once, and only for a meeting that has not been announced.
        var state = new CalendarState { Announced = [], IgnoredMailEvents = [] };
        // The meeting starts at the moment the panel looks, so it is inside the lead time.
        var meetings = new[] { fromCalendar };
        var due = AgendaService.DueForReminder(meetings, state, start, TimeSpan.FromMinutes(15));
        check("A meeting inside the lead time is due", due.Count == 1);
        // Once it has been announced it is not due again.
        state.Announced.Add(fromCalendar.DedupeKey);
        check("An announced meeting is not due again", AgendaService.DueForReminder(meetings, state, start, TimeSpan.FromMinutes(15)).Count == 0);
        // A meeting that was ignored is not due either.
        state.Announced.Clear(); state.IgnoredMailEvents.Add(fromCalendar.DedupeKey);
        check("An ignored meeting is not due", AgendaService.DueForReminder(meetings, state, start, TimeSpan.FromMinutes(15)).Count == 0);
        state.IgnoredMailEvents.Clear();
        // A meeting that starts long after the moment the panel looks is not due yet.
        var distant = new[] { fromCalendar with { Start = start.AddHours(3), End = start.AddHours(4) } };
        check("A meeting beyond the lead time is not due", AgendaService.DueForReminder(distant, state, start, TimeSpan.FromMinutes(15)).Count == 0);
        // A meeting that has already begun is not due either: it is not ahead of the panel.
        var passed = new[] { fromCalendar with { Start = start.AddMinutes(-5), End = start.AddMinutes(55) } };
        check("A meeting that has begun is not due", AgendaService.DueForReminder(passed, state, start, TimeSpan.FromMinutes(15)).Count == 0);
        // The same meeting through two sources is announced once, not twice.
        var both = new[] { fromCalendar, fromMail };
        check("The same meeting through two sources is announced once", AgendaService.DueForReminder(both, state, start, TimeSpan.FromMinutes(15)).Count == 1);

        // The settings keep the calendar state across a copy, so a restart does not lose the accounts.
        var settings = new DeckSettings
        {
            Agenda = new CalendarState
            {
                Accounts = [new CalendarAccount { User = "a@b.c" }],
                MailAccounts = [new MailboxAccount { Email = "x@y.z" }],
                Announced = ["k1"], IgnoredMailEvents = ["k2"]
            }
        };
        settings.Normalize();
        var copy = settings.Copy();
        check("The calendar accounts survive a copy", copy.Agenda.Accounts.Count == 1 && copy.Agenda.Accounts[0].User == "a@b.c");
        check("The mailboxes survive a copy", copy.Agenda.MailAccounts.Count == 1 && copy.Agenda.MailAccounts[0].Email == "x@y.z");
        check("The announced meetings survive a copy", copy.Agenda.Announced.Contains("k1"));
        check("The ignored meetings survive a copy", copy.Agenda.IgnoredMailEvents.Contains("k2"));
        check("The accounts keep their identity across a copy", copy.Agenda.Accounts[0].InstanceId == settings.Agenda.Accounts[0].InstanceId);
    }

    // A secrets store that holds nothing, for the parts of the service that are not about reading.
    private sealed class StubSecrets : IProviderSecrets
    {
        public string Read(Guid instanceId) => "";
        public void Write(Guid instanceId, string secret) { }
        public void Delete(Guid instanceId) { }
    }
}
