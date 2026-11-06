using System.IO.Compression;
using System.Text;
using BrimDeck.Core;

// The two things that can be checked without a server: the WBXML codec the WeCom calendar speaks, and the addresses
// the holiday calendars are published at.
internal static class CalendarSourceTests
{
    public static void Run(Action<string, bool> check)
    {
        // The WBXML codec: what is written is read back as it was written.
        var request = new WbxmlNode(0, 5, "", [
            new WbxmlNode(0, 0x0F, "", [
                new WbxmlNode(0, 0x0B, "0", []),
                new WbxmlNode(0, 0x12, "folder-1", [])
            ])
        ]);
        var bytes = Wbxml.Encode(request);
        check("A WBXML document starts with the ActiveSync header", bytes.Length > 4 && bytes[0] == 3 && bytes[1] == 1 && bytes[2] == 106);
        var read = Wbxml.Decode(bytes);
        check("A WBXML document is read back", read is not null);
        check("The root tag is read back", read!.Tag == 5 && read.Page == 0);
        check("The nested collection is read back", read.Children.Count == 1 && read.Children[0].Tag == 0x0F);
        check("The sync key is read back", read.Children[0].Children[0].Value == "0");
        check("The folder id is read back", read.Children[0].Children[1].Value == "folder-1");

        // A page switch: the same tag number on another page is a different element.
        var paged = new WbxmlNode(7, 0x12, "", [new WbxmlNode(7, 0x0B, "0", [])]);
        var pagedRead = Wbxml.Decode(Wbxml.Encode(paged));
        check("A page switch is read back", pagedRead is not null && pagedRead.Page == 7 && pagedRead.Tag == 0x12);

        // Something that is not WBXML is refused rather than read as nonsense.
        check("A document with the wrong header is refused", Wbxml.Decode([0, 0, 0, 0]) is null);
        check("An empty document is refused", Wbxml.Decode([]) is null);

        // A compressed calendar is read as a calendar. Apple's holiday feeds are served gzip whether or not the
        // request asked for it, and the compressed bytes read as text are noise: the parse finds no events at all,
        // which is how a working subscription looked like a broken one.
        var holiday = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:lidong-2026
            DTSTAMP:20260101T000000Z
            DTSTART;VALUE=DATE:20261107
            SUMMARY:立冬
            END:VEVENT
            END:VCALENDAR
            """;
        var gzipped = Compress(Encoding.UTF8.GetBytes(holiday));
        check("A gzip body is recognised by its first bytes", gzipped.Length > 2 && gzipped[0] == 0x1F && gzipped[1] == 0x8B);
        var fromHoliday = CalendarParser.Parse(Decode(gzipped), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));
        check("A gzip calendar yields its events", fromHoliday.Count == 1 && fromHoliday[0].Title == "立冬");
        check("A gzip calendar keeps the day", fromHoliday.Count == 1 && fromHoliday[0].Start.Day == 7);
        // The same calendar uncompressed still reads.
        var plainCal = CalendarParser.Parse(holiday, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));
        check("An uncompressed calendar still reads", plainCal.Count == 1 && plainCal[0].Title == "立冬");
        // And a body that was never compressed is not mangled.
        check("An uncompressed body is read as it came", Decode(Encoding.UTF8.GetBytes(holiday)).Contains("立冬"));

        // Feishu sends a calendar with its line breaks as XML entities, so the whole file arrives as one long line.
        // Read as it comes, the parser finds no properties and the calendar looks empty; unescaped, it is a calendar.
        const string escaped =
            "BEGIN:VCALENDAR&#xD;&#xA;VERSION:2.0&#xD;&#xA;BEGIN:VEVENT&#xD;&#xA;UID:feishu-1&#xD;&#xA;SUMMARY:飞书阶段性汇报&#xD;&#xA;"
            + "DTSTART;TZID=Asia/Shanghai:20261106T140000&#xD;&#xA;DTEND;TZID=Asia/Shanghai:20261106T143000&#xD;&#xA;END:VEVENT&#xD;&#xA;END:VCALENDAR&#xD;&#xA;";
        check("The escaped breaks are recognised", escaped.Contains("&#xD;&#xA;"));
        var repaired = CalDavClient.Unfold(escaped);
        check("The escaped breaks become real breaks", repaired.Contains("\r\n") && !repaired.Contains("&#xD;"));
        var feishuLow = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        var feishuHigh = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var feishuEvents = CalendarParser.Parse(repaired, feishuLow, feishuHigh);
        check("A Feishu calendar yields its event once unescaped", feishuEvents.Count == 1);
        check("That event keeps its title", feishuEvents.Count == 1 && feishuEvents[0].Title == "飞书阶段性汇报");
        // Read as it came, the same text yields nothing: this is the failure that was seen.
        check("The same text unescaped yields nothing", CalendarParser.Parse(escaped, feishuLow, feishuHigh).Count == 0);
        // A calendar that already has real breaks is left exactly as it was.
        check("A calendar with real breaks is left alone", CalDavClient.Unfold(holiday) == holiday);

        // The holiday calendars: the address is the one iCloud publishes, and a subscription says what it is.
        check("The Chinese holidays are at the address Apple publishes",
            CalendarSubscription.AppleHolidayUrl("cn_zh") == "https://calendars.icloud.com/holidays/cn_zh.ics");
        var holidays = CalendarSubscription.Holidays("cn_zh");
        check("A holiday subscription names the region", holidays.Name.Contains("中国大陆") || holidays.Name.Contains("China"));
        check("A holiday subscription points at the calendar", holidays.Url.EndsWith("cn_zh.ics", StringComparison.Ordinal));
        check("A holiday subscription is on", holidays.Enabled);
        check("Every offered region has an address", CalendarSubscription.Regions.All(region => CalendarSubscription.AppleHolidayUrl(region.Code).StartsWith("https://", StringComparison.Ordinal)));

        // The calendar view of an event: a time of day for one that lasts, a date for a whole day. The event is built in
// local time, because that is how the panel shows it: the same instant reads differently in another zone.
        var now = DateTimeOffset.Now;
        var todayAt9 = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 0, 0, now.Offset);
        var meeting = new CalendarEvent("Standup", todayAt9, null, false);
        check("An event today shows its time", meeting.When(now) == "09:00");
        var later = new CalendarEvent("Review", todayAt9.AddDays(2), null, false);
        check("An event on another day shows its date and time", later.When(now) == $"{todayAt9.AddDays(2):MM-dd} 09:00");
        var day = new CalendarEvent("Holiday", new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset), null, true);
        check("A whole-day event shows as a whole day", day.When(now).Contains("全天") || day.When(now).Contains("All day"));
    }

    // A gzip stream of some bytes, as a server would send.
    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true)) gzip.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    // The decompression the subscription reader does, on bytes rather than over the network.
    private static string Decode(byte[] bytes) => CalDavClient.DecodeBody(bytes);
}
