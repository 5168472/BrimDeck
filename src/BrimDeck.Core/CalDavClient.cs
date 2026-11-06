using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace BrimDeck.Core;

// Reads a work calendar over CalDAV: the protocol the Chinese platforms (Feishu, DingTalk) speak for the calendar
// that belongs to a work account.
//
// Three rules are kept because breaking any of them sends the account's password somewhere it should not go, or
// loses the events:
//
//  - The password is only ever sent to the origin the account was configured with. A reply that points elsewhere
//    is followed by hand, at most three times, and each hop is checked again; the method is kept on the way, so a
//    report is not turned into a get.
//  - The request must be https. Plain http is allowed only to a loopback address, which is how a local server is
//    reached in a test.
//  - The event query asks for the entity tag as well as the data. Some servers refuse the query without it.
public sealed class CalDavClient(HttpClient http)
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    // The address a calendar is reached at: the one the account names, or the platform's published one. A bare host is
// taken as https on the default port, because that is how the platforms write it ("caldav.feishu.cn").
public static string Server(CalendarAccount account)
{
    string named = (account.Server ?? "").Trim();
    if (named.Length == 0) return DefaultServer(account.Source);    if (named.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || named.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return named.TrimEnd('/');
    return "https://" + named.Trim('/');
}

// The address each platform publishes for its calendar, used when the account names none.
public static string DefaultServer(CalendarSource source) => source switch
{
    CalendarSource.Feishu => "https://caldav.feishu.cn/calendars/",
    CalendarSource.DingTalk => "https://calendar.dingtalk.com",
    CalendarSource.WeCom => "https://wecom.work",
    _ => ""
};

    // The first part of a body, for the trace: a server's complaint is usually a short sentence.
    private static string Shorten(string text) => text.Length <= 300 ? text.Replace('\n', ' ').Replace('\r', ' ') : text[..300].Replace('\n', ' ').Replace('\r', ' ');

    // A note about what the discovery did, for the trace the panel keeps. Writing it here rather than in the panel keeps
    // the reason for a failed discovery next to the code that decided it.
    private static void Note(string line)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "agenda.log"), $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    // Finds the calendar collection to read, by the first address that names one. Kept for the settings page's test,
    // which wants one calendar to try; the reader itself takes every calendar through DiscoverAllAsync.
    public async Task<string> DiscoverAsync(CalendarAccount account, string password, CancellationToken cancellation)
    {
        var calendars = await DiscoverAllAsync(account, password, cancellation);
        return calendars[0];
    }

    // The places a calendar may live, in the order they are tried. The base the account names is tried first as it is,
// which is what a platform that states a full collection address means; the well-known path and the calendars
// collection are what the standard says to look at when it is only a host.
    private static IEnumerable<string> Candidates(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        yield return trimmed + "/";
        yield return trimmed + "/.well-known/caldav";
        // The calendars collection, unless the base already is it: "https://host/calendars/" would otherwise be
        // tried again as "https://host/calendars/calendars/".
        if (!trimmed.EndsWith("/calendars", StringComparison.OrdinalIgnoreCase)) yield return trimmed + "/calendars/";
        // A bare host also looks one level up, which is where the principal of a CalDAV server usually is.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.AbsolutePath.Trim('/').Length > 0)
            yield return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    // The WebDAV method the request is sent with. PROPFIND asks for a resource's properties and REPORT asks for the
    // events of a collection; sending either as a plain POST is not the protocol, and a server is entitled to answer
    // 403 — which is what Feishu does, so every calendar looked like an account without permission.
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Report = new("REPORT");

    // Every calendar a listing names, not only the first. A work account usually has more than one — Feishu lists the
    // user's own calendar and a bot calendar — and taking the first found only the bot, whose calendar is empty, so a
    // correctly configured account showed no meetings at all.
    public static List<string> AllCalendars(string xml, string listing)
    {
        var found = new List<string>();
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return found; }
        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Descendants(Dav + "href").FirstOrDefault()?.Value;
            if (href is null) continue;
            if (string.Equals(href.TrimEnd('/'), listing.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;
            var resourceType = response.Descendants(Dav + "resourcetype").FirstOrDefault();
            bool marked = resourceType?.Elements(CalDav + "calendar").Any() == true;
            if (marked || LooksLikeCalendar(href, listing, response))
            {
                var url = Absolute(href, listing);
                if (!found.Contains(url)) found.Add(url);
            }
        }
        return found;
    }

    // The calendar collection a listing names, or null when it names none. Two ways of naming one are accepted, because
    // the platforms differ:
    //
    //  - A proper CalDAV answer marks it with a resource type of calendar, which is what the standard says and what
    //    most servers send.
    //  - Feishu does not mark it at all: its calendar list is a set of paths under /calendars/ with a display name each
    //    ("魏树声", "BOT"). Reading only the marked collections found nothing there, which is why a correctly configured
    //    Feishu account showed no meetings.
    //
    // A listing answers for its own address as well as for its children, and that answer is not one of the children:
    // taking it for a calendar is what made Feishu's listing itself the calendar, so every query against it was empty.
    public static string? ChooseCalendar(string xml, string listing)
    {
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Descendants(Dav + "href").FirstOrDefault()?.Value;
            if (href is null) continue;
            // A resource type of calendar, checked as the element it is. Searching the text for "calendar" would also
            // match the listing itself, whose resource type carries the CalDAV namespace and whose name is "calendars".
            var resourceType = response.Descendants(Dav + "resourcetype").FirstOrDefault();
            bool marked = resourceType?.Elements(CalDav + "calendar").Any() == true;
            if (string.Equals(href.TrimEnd('/'), listing.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;
            if (marked) return Absolute(href, listing);
            if (LooksLikeCalendar(href, listing, response)) return Absolute(href, listing);
        }
        // The principal and its calendar home, which is where a server that lists nothing useful says to look.
        var home = document.Descendants(CalDav + "calendar-home-set").Descendants(Dav + "href").FirstOrDefault()?.Value;
        if (home is not null) return Absolute(home, listing);
        var principal = document.Descendants(Dav + "current-user-principal").Descendants(Dav + "href").FirstOrDefault()?.Value;
        return principal is null ? null : Absolute(principal, listing);
    }

    // The full address of a path, for a test to check on the shape a server sends.
    public static string AbsoluteUrl(string href, string listing) => Absolute(href, listing);

    // Every calendar the account has, by discovery. A work account usually has more than one — Feishu lists the user's
    // own calendar next to a bot's — and reading only the first found the bot's, which is empty, so a correctly
    // configured account showed no meetings at all.
    public async Task<List<string>> DiscoverAllAsync(CalendarAccount account, string password, CancellationToken cancellation)
    {
        var baseUrl = Server(account);
        if (baseUrl.Length == 0) throw new CalDavException(Loc.T("该平台没有提供日历接口", "This platform offers no calendar"));
        var found = new List<string>();
        foreach (var candidate in Candidates(baseUrl))
        {
            try
            {
                var calendars = await DiscoverListAtAsync(candidate, account, password, cancellation);
                foreach (var calendar in calendars) if (!found.Contains(calendar)) found.Add(calendar);
                // The first place that names calendars is the right one: the others are what to look at when this one
                // has none, and trying them anyway would read the same calendars twice.
                if (found.Count > 0) break;
            }
            catch (CalDavException) { }
            catch (HttpRequestException) { }
        }
        if (found.Count == 0)
            throw new CalDavException(Loc.T("没有找到日历，请确认这个账号能在客户端里同步日程", "No calendar found; check that this account can sync its schedule"));
        Note($"discover: {found.Count} calendars");
        return found;
    }

    // The calendars one address names.
    private async Task<List<string>> DiscoverListAtAsync(string url, CalendarAccount account, string password, CancellationToken cancellation)
    {
        var body = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:resourcetype/><d:displayname/><d:current-user-principal/><c:calendar-home-set/></d:prop>
            </d:propfind>
            """;
        var (status, xml) = await SendAsync(account, password, Propfind, url, body, "1", cancellation);
        if (status == HttpStatusCode.NotFound) return [];
        if (status >= HttpStatusCode.BadRequest) throw new CalDavException(Describe(status));
        if (xml is null) return [];
        var calendars = AllCalendars(xml, url);
        // Nothing here names a calendar; the principal or its home might, and ChooseCalendar knows where to look.
        if (calendars.Count == 0 && ChooseCalendar(xml, url) is { } next && !string.Equals(next.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return await DiscoverListAtAsync(next, account, password, cancellation);
        return calendars;
    }

    // Whether an entry of a listing is a calendar that named no resource type: a path below a calendars collection,
    // with a display name of its own. Feishu answers its calendar list this way.
    private static bool LooksLikeCalendar(string href, string listing, XElement response)
    {
        var trimmed = href.TrimEnd('/');
        if (trimmed.Length == 0) return false;
        // The listing answers for its own address as well as for its children; it is not one of its children.
        if (string.Equals(trimmed, listing.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
        // A calendar in this form sits one level below the collection it is listed in: "/calendars/<id>/". The
        // collection itself is "/calendars" or "/calendars/", and its parent is "/".
        if (!trimmed.Contains("/calendars/", StringComparison.OrdinalIgnoreCase)) return false;
        string tail = trimmed[(trimmed.LastIndexOf("/calendars/", StringComparison.OrdinalIgnoreCase) + "/calendars/".Length)..].Trim('/');
        if (tail.Length == 0 || tail.Contains('/')) return false;
        // A calendar in this form always names itself; the principal's path does not.
        var display = response.Descendants(Dav + "displayname").FirstOrDefault()?.Value?.Trim() ?? "";
        return display.Length > 0;
    }

    // Reads the events that fall in a window.
    public async Task<List<CalendarEvent>> ReadAsync(CalendarAccount account, string password, string href, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellation)
    {
        var start = from.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var end = to.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/><c:calendar-data><c:expand start="{start}" end="{end}"/></c:calendar-data></d:prop>
              <c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT">
                <c:time-range start="{start}" end="{end}"/>
              </c:comp-filter></c:comp-filter></c:filter>
            </c:calendar-query>
            """;
        var (status, xml) = await SendAsync(account, password, Report, href, body, "1", cancellation);
        if (status >= HttpStatusCode.BadRequest) throw new CalDavException(Describe(status));

        var events = new List<CalendarEvent>();
        if (xml is null) return events;
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return events; }

        // The calendars that answer with the data inline, which is what the standard says and what most servers do.
        var hrefs = new List<string>();
        foreach (var response in document.Descendants(Dav + "response"))
        {
            // A reply may carry an empty calendar-data element and no body: the element's presence is not the data's.
            // What counts is whether it holds anything, so an empty one leaves the resource for the multiget below.
            bool described = false;
            foreach (var data in response.Descendants(CalDav + "calendar-data"))
            {
                var ics = data.Value;
                if (ics.Length == 0) continue;
                described = true;
                events.AddRange(CalendarParser.Parse(Unfold(ics), from, to, account.Source.ToString(), account.User));
            }
            if (described) continue;
            // A resource the query named but did not describe: Feishu lists the events of the window by href and
            // sends no data at all, so their bodies are asked for in a second request.
            var resource = response.Descendants(Dav + "href").FirstOrDefault()?.Value;
            if (resource is not null && resource.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) hrefs.Add(resource);
        }
        if (hrefs.Count > 0)
        {
            var more = await MultigetAsync(account, password, href, hrefs, from, to, cancellation);
            Note($"  {href}: {hrefs.Count} resources, {more.Count} events from the multiget");
            events.AddRange(more);
        }
        else Note($"  {href}: {events.Count} events inline");
        return events;
    }

    // Asks for the bodies of the resources a query named, in one request. A server that answers its queries this
    // way sends the events' hrefs and entity tags only; the data comes from here.
    private async Task<List<CalendarEvent>> MultigetAsync(CalendarAccount account, string password, string href, List<string> resources, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellation)
    {
        var events = new List<CalendarEvent>();
        var start = from.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var end = to.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        // A window of a few days holds a handful of events; the cap keeps a month view from asking for thousands.
        var named = string.Join("", resources.Take(200).Select(resource => $"<d:href>{System.Security.SecurityElement.Escape(resource)}</d:href>"));
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <c:calendar-multiget xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/><c:calendar-data><c:expand start="{start}" end="{end}"/></c:calendar-data></d:prop>
              {named}
            </c:calendar-multiget>
            """;
        var (status, xml) = await SendAsync(account, password, Report, href, body, "1", cancellation);
        if (status >= HttpStatusCode.BadRequest || xml is null) return events;
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return events; }
        foreach (var data in document.Descendants(CalDav + "calendar-data"))
        {
            if (data.Value.Length == 0) continue;
            events.AddRange(CalendarParser.Parse(Unfold(data.Value), from, to, account.Source.ToString(), account.User));
        }
        return events;
    }

    // Repairs a calendar whose line breaks arrived as XML entities. Feishu sends its data as
    // "BEGIN:VCALENDAR&#xD;&#xA;VERSION:2.0&#xD;&#xA;…", so the text is one long line: the parser sees no properties
    // at all, and the calendar reads as empty. Unescaping the entities that stand for a break turns it back into the
    // text the parser expects.
    public static string Unfold(string ics)
    {
        if (ics.Length == 0) return ics;
        if (ics.Contains("&#xD;", StringComparison.Ordinal) || ics.Contains("&#xA;", StringComparison.Ordinal)
            || ics.Contains("&amp;#xD", StringComparison.Ordinal) || ics.Contains("&amp;#xA", StringComparison.Ordinal))
        {
            // One pass may leave a second layer behind (a server that escaped the escaping), so it is repeated.
            for (int pass = 0; pass < 3; pass++)
            {
                var repaired = ics.Replace("&#xD;", "\r").Replace("&#xA;", "\n")
                                  .Replace("&#x0D;", "\r").Replace("&#x0A;", "\n")
                                  .Replace("&amp;#xD;", "&#xD;").Replace("&amp;#xA;", "&#xA;")
                                  .Replace("&#13;", "\r").Replace("&#10;", "\n");
                if (repaired == ics) break;
                ics = repaired;
            }
        }
        // A calendar with no line break at all cannot be read; the properties are separated by nothing to split on.
        return ics;
    }

    // Reads a calendar that is published at an address, without an account. The public holiday calendars are offered
// this way: one plain GET returns an iCalendar file.
//
// Only https is accepted, and only an address that names a host: a subscription is a public feed, and there is no
// reason for the panel to fetch anything else.
//
// The answer may be compressed. Apple's holiday calendars are served as gzip whether or not the request asked for
// it, and the bytes of a compressed calendar are not an iCalendar file: read as text they are noise, and the parse
// finds nothing. The body is therefore decompressed by its own first bytes rather than trusting the header.
public async Task<List<CalendarEvent>> ReadSubscriptionAsync(CalendarSubscription subscription, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellation)
{
    if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host))
        throw new CalDavException(Loc.T("订阅地址不是一个可用的网址", "The subscription address is not a usable URL"));
    using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
    if (!response.IsSuccessStatusCode)
        throw new CalDavException(response.StatusCode == HttpStatusCode.NotFound
            ? Loc.T("订阅地址不存在", "The subscription address does not exist")
            : Loc.T($"订阅读取失败（{(int)response.StatusCode}）", $"The subscription could not be read ({(int)response.StatusCode})"));
    await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
    var bytes = await ReadAllAsync(stream, cancellation);
    if (bytes.Length == 0) throw new CalDavException(Loc.T("订阅没有返回内容", "The subscription returned nothing"));
    var text = Decode(bytes);
    var events = CalendarParser.Parse(text, from, to, "subscription", subscription.DisplayName);
    if (events.Count == 0 && !text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        throw new CalDavException(Loc.T("订阅地址返回的不是日历", "The subscription address did not return a calendar"));
    return events;
}

// The body's bytes, with a limit so a broken address cannot fill memory.
private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellation)
{
    using var buffer = new MemoryStream();
    var chunk = new byte[16 * 1024];
    int read;
    while ((read = await stream.ReadAsync(chunk, cancellation)) > 0)
    {
        buffer.Write(chunk, 0, read);
        if (buffer.Length > 16 * 1024 * 1024) throw new CalDavException(Loc.T("订阅内容过大", "The subscription is too large"));
    }
    return buffer.ToArray();
}

// The text of a calendar body, decompressed if it was compressed. The first bytes say which: gzip starts 1f 8b,
// zlib 78, and a plain calendar starts with the text itself. Exposed so a test can check it on real bytes.
public static string DecodeBody(byte[] bytes) => Decode(bytes);

private static string Decode(byte[] bytes)
{
    Stream source = new MemoryStream(bytes);
    try
    {
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b) source = new System.IO.Compression.GZipStream(source, System.IO.Compression.CompressionMode.Decompress);
        else if (bytes.Length > 2 && bytes[0] == 0x78) source = new System.IO.Compression.ZLibStream(source, System.IO.Compression.CompressionMode.Decompress);
        else if (bytes.Length > 3 && bytes[0] == 0x42 && bytes[1] == 0x5A) source = new System.IO.Compression.BrotliStream(source, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    catch (InvalidDataException)
    {
        // A body that claimed to be compressed and was not: read it as it came.
        return Encoding.UTF8.GetString(bytes);
    }
}

// Sends one request and returns the answer's code and body. The password goes only to the account's own origin.
    private async Task<(HttpStatusCode Status, string? Xml)> SendAsync(CalendarAccount account, string password, HttpMethod method, string url, string body, string depth, CancellationToken cancellation)
    {
        var baseUrl = Server(account);
        var target = url;
        // At most three hops, each checked: a reply that points at another origin is not followed.
        for (int hop = 0; hop < 3; hop++)
        {
            if (!SameOrigin(target, baseUrl)) throw new CalDavException(Loc.T("日历服务器地址不安全", "The calendar server address is not safe"));
            using var request = new HttpRequestMessage(method, target);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.User}:{password}")));
            request.Headers.TryAddWithoutValidation("Depth", depth);
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
            using var response = await http.SendAsync(request, cancellation);
            var text = await response.Content.ReadAsStringAsync(cancellation);
            // The code and the server's own words, in the trace: a 403 from a working account is otherwise a puzzle
            // with nothing to go on.
            if (response.StatusCode is not HttpStatusCode.MultiStatus and not HttpStatusCode.OK)
                Note($"request {method} {target} -> {(int)response.StatusCode} {response.StatusCode}: {Shorten(text)}");
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect)
            {
                var location = response.Headers.Location;
                if (location is null) return (response.StatusCode, null);
                target = new Uri(new Uri(target), location).ToString();
                continue;
            }
            return (response.StatusCode, text);
        }
        throw new CalDavException(Loc.T("日历服务器跳转过多", "The calendar server redirected too often"));
    }

    // Whether a URL belongs to the same origin as the account's server.
    private static bool SameOrigin(string url, string baseUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var base_)) return false;
        if (uri.Scheme != "https") return false;
        if (!string.Equals(uri.Host, base_.Host, StringComparison.OrdinalIgnoreCase)) return false;
        return uri.Port == base_.Port || (uri.Port == 443 && base_.Port == 443);
    }

    // Turns a path from the server into a full address on the same origin. A path that starts with a slash is absolute
// to the host, which is what CalDAV hrefs are: resolving it against the collection's own address would put the
// collection's path in front of it and look for "/calendars/calendars/<id>/", which no server has.
    private static string Absolute(string href, string origin)
    {
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var base_)) return href;
        if (href.StartsWith('/'))
            return base_.GetLeftPart(UriPartial.Authority) + href;
        return new Uri(base_, href).ToString();
    }

    // The server's own words about what went wrong, so the settings page can say what to fix.
    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => Loc.T("账号或密码不对", "The account or password is wrong"),
        HttpStatusCode.Forbidden => Loc.T("这个账号没有日历权限", "This account has no calendar permission"),
        HttpStatusCode.NotFound => Loc.T("找不到日历地址", "The calendar address was not found"),
        HttpStatusCode.MethodNotAllowed => Loc.T("这个服务器不接受日历查询", "This server does not accept calendar queries"),
        HttpStatusCode.ProxyAuthenticationRequired => Loc.T("需要代理认证", "Proxy authentication is needed"),
        (HttpStatusCode)429 => Loc.T("请求太频繁，请稍后再试", "Too many requests; try again later"),
        _ => status >= HttpStatusCode.InternalServerError
            ? Loc.T("日历服务器出错了", "The calendar server failed")
            : Loc.T($"日历服务器返回 {(int)status}", $"The calendar server answered {(int)status}")
    };
}

// A calendar read that did not work, with the reason to show.
public sealed class CalDavException(string message) : Exception(message);


