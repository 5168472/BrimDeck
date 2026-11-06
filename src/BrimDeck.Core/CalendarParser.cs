using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
// The library's own event class has the same name as this project's one, so it is named here rather than imported.
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace BrimDeck.Core;

// Reads iCalendar data into the panel's own event model, and expands the recurring entries into the ones that
// fall in a window.
//
// Three things are decided here rather than left to the library, because each of them is a place where a wrong
// answer would be shown to the user as fact:
//
//  - An entry whose end is missing or before its start keeps no end. A duration is never invented; the panel shows
//    it as a point in time rather than as an event that runs to the end of the day.
//  - An entry in a time zone this machine does not know is left out rather than read as local time. A meeting at
//    nine in the morning in a zone we cannot resolve is not the same as nine in the morning here.
//  - A recurring entry is expanded and then filtered by the window, because the library's expansion takes only a
//    starting point; the end of the window is this code's business.
public static class CalendarParser
{
    // A recurring entry that repeats many times over in a small window is not shown: the list would be useless and
    // the panel would spend its time on it. The limit is high enough for any real diary.
    private const int MaxOccurrences = 2000;

    public static List<CalendarEvent> Parse(string ics, DateTimeOffset from, DateTimeOffset to, string source = "", string account = "")
    {
        var events = new List<CalendarEvent>();
        if (string.IsNullOrWhiteSpace(ics)) return events;
        Calendar calendar;
        try { calendar = Calendar.Load(ics); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return events; }
        if (calendar is null) return events;

        // The window, as the library wants it: a starting point, and the end is applied here.
        var windowStart = new CalDateTime(from.UtcDateTime, "UTC");
        var windowEnd = to.UtcDateTime;

        foreach (var component in calendar.Events)
        {
            if (component.Status is not null && component.Status.Equals(EventStatus.Cancelled, StringComparison.OrdinalIgnoreCase)) continue;
            var single = ParseOne(component, source, account);
            if (single is null) continue;

            // A plain entry: no rule, so it either falls in the window or it does not.
            if (component.RecurrenceRule is null && !component.RecurrenceDates.GetAllPeriods().Any())            {
                if (Overlaps(single, from, to)) events.Add(single);
                continue;
            }

            // A recurring entry: the library expands it from the window's start, and the occurrences that fall
            // past the window's end are dropped here, because the library has no end of its own.
            int count = 0;
            foreach (var occurrence in component.GetOccurrences(windowStart, new EvaluationOptions()))
            {
                if (++count > MaxOccurrences) break;
                var start = occurrence.Period.StartTime.AsUtc;
                // The end of an occurrence is in EffectiveEndTime; EndTime is empty for them.
                var end = occurrence.Period.EffectiveEndTime?.AsUtc;
                var expanded = single with
                {
                    Start = new DateTimeOffset(start, TimeSpan.Zero),
                    End = end is null ? null : new DateTimeOffset(end.Value, TimeSpan.Zero)
                };
                if (Overlaps(expanded, from, to)) events.Add(expanded);
            }
        }
        return events;
    }

    // One entry, without its recurrences. Returns null when the entry cannot be read: a missing start, or a time
    // zone this machine cannot resolve.
    private static CalendarEvent? ParseOne(IcalEvent component, string source, string account)
    {
        if (component.DtStart is null) return null;
        DateTimeOffset start;
        try { start = new DateTimeOffset(component.DtStart.AsUtc, TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException) { return null; }
        // A whole-day entry has no time of day; it is shown as a date.
        bool allDay = component.IsAllDay || !component.DtStart.HasTime;
        DateTimeOffset? end = null;
        if (component.DtEnd is not null)
        {
            try
            {
                var candidate = new DateTimeOffset(component.DtEnd.AsUtc, TimeSpan.Zero);
                // An end at or before the start is not a duration; it is a mistake in the source, and it is left out
                // rather than turned into a length of zero or a negative one.
                if (candidate > start) end = candidate;
            }
            catch (ArgumentOutOfRangeException) { }
        }
        string title = component.Summary?.Trim() ?? "";
        if (title.Length == 0) title = Loc.T("日程", "Event");
        // The library gives an entry without a written identifier a fresh one on every read, so the same meeting read
        // twice would look like two. The identifier used here is therefore worked out from the entry itself, so that
        // the same meeting keeps the same key however often it is read.
        string uid = StableId(component, title);
        return new CalendarEvent(title, start, end, allDay, uid, source, account);
    }

    // An entry's identity, worked out so that the same meeting keeps it between reads.
    //
    // An identifier the source wrote is used as it is. One it did not write is derived from what the entry says:
    // its start, its end and its title. That is stable across reads, unlike the one the library would invent, and
    // two entries that differ in any of those are two meetings.
    private static string StableId(IcalEvent component, string title)
    {
        string written = component.Uid ?? "";
        // The library gives an entry that named no identifier a fresh one in the shape of a GUID. Such an entry is
        // not the same meeting each time it is read, so it is given an identity worked out from what it says.
        if (written.Length > 0 && !LooksGenerated(written)) return written;
        long start = component.DtStart is null ? 0 : new DateTimeOffset(component.DtStart.AsUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        long end = component.DtEnd is null ? start : new DateTimeOffset(component.DtEnd.AsUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        return $"{start}|{end}|{title}";
    }

    // Whether an identifier is the one the library made up: a GUID in the shape it uses.
    private static bool LooksGenerated(string uid)
    {
        if (uid.Length != 36) return false;
        if (uid[8] != '-' || uid[13] != '-' || uid[18] != '-' || uid[23] != '-') return false;
        return Guid.TryParse(uid, out _);
    }

    // An entry is in the window when it overlaps it, not only when it starts inside it: a meeting that began
    // yesterday and ends this morning is still on today.
    public static bool Overlaps(CalendarEvent entry, DateTimeOffset from, DateTimeOffset to)
        => entry.Start < to && (entry.EndOrStart > from);
}
