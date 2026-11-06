using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BrimDeck.Core;

// Reads a WeCom (企业微信) calendar over Exchange ActiveSync.
//
// WeCom does not offer its calendar over CalDAV, so the protocol is ActiveSync: a POST to one endpoint with a WBXML
// body, naming the command in the query. Only reading is done here; nothing about the account or the events is ever
// written back, and the device name is a fixed one so the server does not grow a device entry per run.
//
// The three commands, in order:
//  1. FolderSync, to find the folder the calendar lives in.
//  2. Sync with a sync key of 0, which asks for the whole collection.
//  3. Paging: a reply that says more is available carries the next sync key and is asked for again.
//
// WBXML is a binary encoding of XML: a tag is one byte (its number in the current page), a string is the byte 0x03
// followed by the text and a NUL, and a page switch is the byte 0x00 followed by the page number. The three pages used
// here are AirSync, Calendar and FolderHierarchy, each with the tag numbers Microsoft documents.
public sealed class EwsCalendarClient(HttpClient http)
{
    private const byte AirPage = 0, CalendarPage = 4, FolderPage = 7;

    // The tags, by the page they belong to.
    private static class Air { public const byte Sync = 5, SyncKey = 0x0B, CollectionId = 0x12, Status = 0x0E, MoreAvailable = 0x14, Commands = 0x16, Collection = 0x0F, Options = 0x13, FilterType = 0x15, SyncWindow = 0x17, WindowSize = 0x18, ServerId = 0x0D, ApplicationData = 0x1D, Add = 0x07, Change = 0x08, Delete = 0x09, SoftDelete = 0x21; }
    private static class Cal { public const byte TimeZone = 0x05, AllDay = 0x06, Start = 0x27, End = 0x12, Subject = 0x26, Uid = 0x28, Recurrence = 0x1B, Exception = 0x13, ExceptionStartTime = 0x16, Deleted = 0x15; }
    private static class Folder { public const byte SyncKey = 0x0B, Status = 0x0C, Changes = 0x0E, Change = 0x0F, Type = 0x0A, ServerId = 0x08, SyncFolderHierarchy = 0x12; }

    // The address WeCom publishes for the protocol, on the host the account was configured with.
    public static string Endpoint(string server) => server.TrimEnd('/') + "/Microsoft-Server-ActiveSync";

    // Everything the calendar holds that falls in the window, read as one snapshot.
    public async Task<List<CalendarEvent>> ReadAsync(CalendarAccount account, string password, string server, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellation)
    {
        var endpoint = Endpoint(server.Length > 0 ? server : "https://wecom.work");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new CalDavException(Loc.T("日历服务器地址不安全", "The calendar server address is not safe"));

        // The folder the calendar lives in.
        var folders = await CommandAsync(uri, account.User, password, "FolderSync", FolderRequest(), cancellation);
        var calendarFolder = CalendarFolderId(folders)
            ?? throw new CalDavException(Loc.T("企业微信没有返回日历文件夹", "WeCom returned no calendar folder"));

        // The whole collection, page by page.
        var events = new List<CalendarEvent>();
        string key = "0";
        for (int page = 0; page < 100; page++)
        {
            var reply = await CommandAsync(uri, account.User, password, "Sync", SyncRequest(calendarFolder, key), cancellation);
            var collection = reply.Child(AirPage, Air.Collection) ?? throw new CalDavException(Loc.T("企业微信没有返回日历数据", "WeCom returned no calendar data"));
            string status = collection.Text(AirPage, Air.Status);
            if (status.Length > 0 && status != "1")
                throw new CalDavException(Loc.T($"企业微信返回状态 {status}", $"WeCom answered with status {status}"));
            foreach (var item in collection.Child(AirPage, Air.Commands)?.Children ?? [])
            {
                // A deletion carries no data; the snapshot is read whole, so there is nothing to remove.
                if (item.Tag is Air.Delete or Air.SoftDelete) continue;
                var data = item.Child(AirPage, Air.ApplicationData);
                if (data is null) continue;
                if (EventFrom(data, account.User) is { } entry && CalendarParser.Overlaps(entry, from, to)) events.Add(entry);
            }
            string next = collection.Text(AirPage, Air.SyncKey);
            bool more = collection.Text(AirPage, Air.MoreAvailable) == "1";
            if (!more || next.Length == 0 || next == key) break;
            key = next;
        }
        return events;
    }

    // The id of the folder whose type says it holds a calendar. Type 8 is the default calendar folder, 13 a user's
    // calendar folder; either is a calendar.
    private static string? CalendarFolderId(WbxmlNode reply)
    {
        var changes = reply.Child(FolderPage, Folder.Changes);
        if (changes is null) return null;
        foreach (var change in changes.Children)
        {
            string type = change.Text(FolderPage, Folder.Type);
            if (type is not ("8" or "13")) continue;
            string id = change.Text(FolderPage, Folder.ServerId);
            if (id.Length > 0) return id;
        }
        return null;
    }

    // One event out of the calendar application data. The times are UTC in the protocol; the time zone the item names
    // is not needed for that, because the start and end already carry the zone the server resolved them into.
    private static CalendarEvent? EventFrom(WbxmlNode data, string account)
    {
        string start = data.Text(CalendarPage, Cal.Start);
        if (!TryTime(start, out var begins)) return null;
        DateTimeOffset? ends = TryTime(data.Text(CalendarPage, Cal.End), out var end) && end > begins ? end : null;
        // A cancelled occurrence of a series is not something that happens.
        if (data.Text(CalendarPage, Cal.Exception) .Length > 0 && data.Child(CalendarPage, Cal.Deleted) is { } deleted && deleted.Value == "1") return null;
        string title = data.Text(CalendarPage, Cal.Subject).Trim();
        if (title.Length == 0) title = Loc.T("日程", "Event");
        bool allDay = data.Text(CalendarPage, Cal.AllDay) == "1";
        // A series is expanded by the parser from the rule the item carries; the rule is not encoded here, so each
        // occurrence comes back as its own item from the server's snapshot.
        string uid = data.Text(CalendarPage, Cal.Uid);
        if (uid.Length == 0) uid = $"{start}|{title}";
        return new CalendarEvent(title, begins, ends, allDay, uid, "WeCom", account);
    }

    // A protocol time: 20260303T090000Z.
    private static bool TryTime(string text, out DateTimeOffset value)
    {
        value = default;
        if (text.Length < 16) return false;
        var trimmed = text.TrimEnd('Z');
        return DateTimeOffset.TryParseExact(trimmed, "yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out value);
    }

    // One command: the query names it, the body is WBXML, and the reply is decoded.
    private async Task<WbxmlNode> CommandAsync(Uri endpoint, string user, string password, string command, WbxmlNode body, CancellationToken cancellation)
    {
        var target = new UriBuilder(endpoint)
        {
            Query = $"Cmd={command}&User={Uri.EscapeDataString(user)}&DeviceId=BrimDeckCalendar01&DeviceType=BrimDeck"
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, target.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        request.Headers.TryAddWithoutValidation("MS-ASProtocolVersion", "14.0");
        request.Content = new ByteArrayContent(Wbxml.Encode(body));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.ms-sync.wbxml");
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CalDavException(Loc.T("账号或密码不对", "The account or password is wrong"));
        if (!response.IsSuccessStatusCode)
            throw new CalDavException(Loc.T($"企业微信返回 {(int)response.StatusCode}", $"WeCom answered {(int)response.StatusCode}"));
        // The reply is small; a limit keeps a broken server from filling memory.
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
        if (bytes.Length > 16 * 1024 * 1024) throw new CalDavException(Loc.T("企业微信返回的数据过大", "WeCom returned too much data"));
        return Wbxml.Decode(bytes) ?? throw new CalDavException(Loc.T("企业微信返回的数据无法解析", "WeCom's answer could not be read"));
    }

    // FolderSync: the whole hierarchy, from the beginning.
    private static WbxmlNode FolderRequest() =>
        new(FolderPage, Folder.SyncFolderHierarchy, "", [new WbxmlNode(FolderPage, Folder.SyncKey, "0", [])]);

    // Sync: the collection to read, and the key that says where to start. The first request uses "0", which means the
    // whole collection; a filter of 0 asks for everything within the window rather than only what changed.
    private static WbxmlNode SyncRequest(string folder, string key)
    {
        var fields = new List<WbxmlNode> { new(AirPage, Air.SyncKey, key, []), new(AirPage, Air.CollectionId, folder, []) };
        if (key != "0")
        {
            fields.Add(new WbxmlNode(AirPage, Air.Options, "", []));
            fields.Add(new WbxmlNode(AirPage, Air.FilterType, "1", []));
            fields.Add(new WbxmlNode(AirPage, Air.SyncWindow, "", [new WbxmlNode(AirPage, Air.WindowSize, "100", [])]));
        }
        return new WbxmlNode(AirPage, Air.Sync, "", [new WbxmlNode(AirPage, Air.Collection, "", fields)]);
    }
}

// One element of a WBXML document: its page, its tag number, its text and its children.
public sealed record WbxmlNode(byte Page, byte Tag, string Value, List<WbxmlNode> Children)
{
    public WbxmlNode Child(byte page, byte tag) => Children.FirstOrDefault(node => node.Page == page && node.Tag == tag)!;
    public string Text(byte page, byte tag) => Child(page, tag)?.Value ?? "";
}

// The WBXML codec: the part of it the calendar protocol needs, which is a tree of tags and strings.
public static class Wbxml
{
    // The header every document starts with: WBXML 1.3, public identifier 106 (ActiveSync), no charset, no string table.
    private static readonly byte[] Header = [3, 1, 106, 0];

    public static byte[] Encode(WbxmlNode root)
    {
        var bytes = new List<byte>(Header);
        byte page = 0;
        Write(root, ref page, bytes, 0);
        return [.. bytes];
    }

    private static void Write(WbxmlNode node, ref byte page, List<byte> output, int depth)
    {
        if (depth > 64) throw new InvalidOperationException("WBXML nested too deeply");
        if (page != node.Page) { output.Add(0); output.Add(node.Page); page = node.Page; }
        bool hasContent = node.Value.Length > 0 || node.Children.Count > 0;
        output.Add((byte)(node.Tag | (hasContent ? 0x40 : 0)));
        if (node.Value.Length > 0) { output.Add(3); output.AddRange(Encoding.UTF8.GetBytes(node.Value)); output.Add(0); }
        foreach (var child in node.Children) Write(child, ref page, output, depth + 1);
        if (hasContent) output.Add(1);
    }

    // Reads a document into its tree. A document that does not follow the shape is refused rather than guessed at.
    public static WbxmlNode? Decode(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 3) return null;
        int at = 1;
        if (Integer(bytes, ref at) != 1 || Integer(bytes, ref at) != 106 || Integer(bytes, ref at) != 0) return null;
        byte page = 0;
        var stack = new List<WbxmlNode> { new(0, 0, "", []) };
        while (at < bytes.Length)
        {
            byte token = bytes[at++];
            switch (token)
            {
                case 0:
                    if (at >= bytes.Length) return null;
                    page = bytes[at++];
                    break;
                case 1:
                    if (stack.Count <= 1) return null;
                    var finished = stack[^1]; stack.RemoveAt(stack.Count - 1);
                    var parent = stack[^1];
                    parent.Children.Add(finished);
                    break;
                case 3:
                case 0xC3:
                    if (stack.Count <= 1) return null;
                    int length;
                    if (token == 3)
                    {
                        int end = Array.IndexOf(bytes, (byte)0, at);
                        if (end < 0) return null;
                        length = end - at;
                    }
                    else length = Integer(bytes, ref at);
                    if (length < 0 || at + length > bytes.Length) return null;
                    stack[^1] = stack[^1] with { Value = stack[^1].Value + Encoding.UTF8.GetString(bytes, at, length) };
                    at += length + (token == 3 ? 1 : 0);
                    break;
                default:
                    // A tag: bit 0x40 says it has content, the low six bits are its number.
                    if ((token & 0x80) != 0 || (token & 0x3F) < 5) return null;
                    var node = new WbxmlNode(page, (byte)(token & 0x3F), "", new List<WbxmlNode>());
                    if ((token & 0x40) != 0)
                    {
                        if (stack.Count >= 64) return null;
                        stack.Add(node);
                    }
                    else stack[^1].Children.Add(node);
                    break;
            }
        }
        return stack.Count == 1 && stack[0].Children.Count == 1 ? stack[0].Children[0] : null;
    }

    // WBXML's multi-byte integer: seven bits per byte, the high bit set while more follow.
    private static int Integer(byte[] bytes, ref int at)
    {
        int value = 0;
        for (int i = 0; i < 5; i++)
        {
            if (at >= bytes.Length) return -1;
            byte b = bytes[at++];
            value = value * 128 + (b & 127);
            if ((b & 0x80) == 0) return value;
        }
        return -1;
    }
}
