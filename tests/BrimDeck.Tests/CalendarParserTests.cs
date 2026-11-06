using BrimDeck.Core;

internal static class CalendarParserTests
{
    public static void Run(Action<string, bool> check)
    {
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);

        // A plain entry.
        var plain = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:plain-1
            DTSTAMP:20260201T000000Z
            DTSTART:20260303T090000Z
            DTEND:20260303T100000Z
            SUMMARY:Standup
            END:VEVENT
            END:VCALENDAR
            """;
        var events = CalendarParser.Parse(plain, from, to);
        check("A plain entry is read", events.Count == 1);
        check("Its title is read", events.Count == 1 && events[0].Title == "Standup");
        check("Its start is read", events.Count == 1 && events[0].Start == new DateTimeOffset(2026, 3, 3, 9, 0, 0, TimeSpan.Zero));
        check("Its end is read", events.Count == 1 && events[0].End == new DateTimeOffset(2026, 3, 3, 10, 0, 0, TimeSpan.Zero));
        check("Its uid is read", events.Count == 1 && events[0].Uid == "plain-1");
        check("It is not a whole-day entry", events.Count == 1 && !events[0].AllDay);

        // A whole-day entry: no time of day, and its end is the day after.
        var allDay = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:day-1
            DTSTAMP:20260201T000000Z
            DTSTART;VALUE=DATE:20260304
            DTEND;VALUE=DATE:20260305
            SUMMARY:Holiday
            END:VEVENT
            END:VCALENDAR
            """;
        var days = CalendarParser.Parse(allDay, from, to);
        check("A whole-day entry is read", days.Count == 1 && days[0].AllDay);
        check("A whole-day entry keeps its date", days.Count == 1 && days[0].Start.Day == 4 && days[0].Start.Month == 3);

        // A cancelled entry is not an event that happens.
        var cancelled = plain.Replace("SUMMARY:Standup", "SUMMARY:Standup\r\nSTATUS:CANCELLED");
        check("A cancelled entry is left out", CalendarParser.Parse(cancelled, from, to).Count == 0);

        // An entry outside the window is not shown.
        check("An entry outside the window is left out", CalendarParser.Parse(plain, to, to.AddDays(7)).Count == 0);

        // An entry that lasts into the window is shown: it overlaps, it does not start inside.
        var spanning = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:span-1
            DTSTAMP:20260201T000000Z
            DTSTART:20260227T090000Z
            DTEND:20260302T090000Z
            SUMMARY:Long
            END:VEVENT
            END:VCALENDAR
            """;
        check("An entry that lasts into the window is shown", CalendarParser.Parse(spanning, from, to).Count == 1);

        // A recurring entry: the rule expands into the occurrences that fall in the window.
        var recurring = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:rep-1
            DTSTAMP:20260201T000000Z
            DTSTART:20260302T090000Z
            DTEND:20260302T100000Z
            RRULE:FREQ=DAILY;COUNT=10
            SUMMARY:Daily
            END:VEVENT
            END:VCALENDAR
            """;
        var repeats = CalendarParser.Parse(recurring, from, to);
        check("A recurring entry is expanded into the window", repeats.Count == 6);
        check("The first occurrence is the first day", repeats.Count > 0 && repeats[0].Start.Day == 2);
        check("Each occurrence keeps the length", repeats.Count > 0 && repeats.All(e => e.End - e.Start == TimeSpan.FromHours(1)));

        // The same meeting seen twice is one meeting: the key is its uid and its start.
        var one = CalendarParser.Parse(plain, from, to);
        check("The dedupe key names the uid and the start",
            one.Count == 1 && one[0].DedupeKey == $"plain-1|{one[0].Start.ToUnixTimeSeconds()}");
        // An entry whose uid was not written gets one from the library, so it is still identified: the key names
// whatever the entry is called, and the same entry keeps the same key between reads.
        var noUid = plain.Replace("UID:plain-1\n", "");
        var keyless = CalendarParser.Parse(noUid, from, to);
        check("An entry without a uid still gets an identity",
            keyless.Count == 1 && keyless[0].Uid.Length > 0 && keyless[0].DedupeKey.Contains('|'));
        // The same entry read twice keeps the same key, which is what makes two sources of one meeting one meeting.
        var twice = CalendarParser.Parse(noUid, from, to);
        check("The same entry read twice keeps the same key",
            twice.Count == 1 && keyless.Count == 1 && keyless[0].DedupeKey == twice[0].DedupeKey);

        // Something that is not a calendar at all is not an error: it reads as nothing.
        check("Text that is not a calendar reads as nothing", CalendarParser.Parse("not a calendar", from, to).Count == 0);
        check("Empty text reads as nothing", CalendarParser.Parse("", from, to).Count == 0);
    }
}
