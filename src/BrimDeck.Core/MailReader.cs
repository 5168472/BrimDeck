using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
namespace BrimDeck.Core;

// One mail, as the panel shows it. The body is held in memory only and never written to disk.
public sealed record MailItem(
    string Account,
    string From,
    string Subject,
    DateTimeOffset Received,
    string Summary,
    string? Code = null,
    uint Uid = 0,
    IReadOnlyList<string>? Links = null,
    CalendarEvent? Meeting = null)
{
    // The links are only handed out when they are web addresses: a file: or javascript: link in a mail is not
    // something the panel should open.
    public static string? SafeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return uri.ToString();
    }
}

// Reads a mailbox over IMAP.
//
// The rules that matter:
//  - The mailbox is opened read-only and the parts are fetched with BODY.PEEK, so the panel never marks a mail as
//    read. Reading the user's mail is not the same as reading it in the client, and a mail that turned up as read
//    would be lost to the person who owns it.
//  - Only the text parts are fetched. An attachment is never downloaded, so a mailbox with a large file in it does
//    not cost a download every poll.
//  - The connection is over TLS from the first byte, on port 993. There is no plain-text path and no upgrade
//    afterwards.
public sealed class MailReader(IProviderSecrets secrets)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The mailboxes the panel knows, with the addresses it reaches them at.
    public enum Provider
    {
        Qq, Mail163, Mail126, Exmail, ICloud, Gmail, Custom
    }

    public static string Host(Provider provider) => provider switch
    {
        Provider.Qq => "imap.qq.com",
        Provider.Mail163 => "imap.163.com",
        Provider.Mail126 => "imap.126.com",
        Provider.Exmail => "imap.exmail.qq.com",
        Provider.ICloud => "imap.mail.me.com",
        Provider.Gmail => "imap.gmail.com",
        _ => ""
    };

    public const int Port = 993;

    public static string Hint(Provider provider) => provider switch
    {
        Provider.Qq => Loc.T("QQ 邮箱，需要授权码而非 QQ 密码", "QQ Mail: an authorization code, not the QQ password"),
        Provider.Mail163 => Loc.T("163 邮箱，需要客户端授权密码", "163 Mail: the client authorization password"),
        Provider.Mail126 => Loc.T("126 邮箱，需要客户端授权密码", "126 Mail: the client authorization password"),
        Provider.Exmail => Loc.T("腾讯企业邮箱，需要客户端专用密码", "Tencent Exmail: the client-specific password"),
        Provider.ICloud => Loc.T("iCloud，需要 App 专用密码", "iCloud: an app-specific password"),
        Provider.Gmail => Loc.T("Gmail，需要应用专用密码", "Gmail: an app-specific password"),
        _ => Loc.T("仅支持 IMAP over TLS（通常 993）", "IMAP over TLS only (usually 993)")
    };

    // One mailbox the user set up. The password is not here: it lives in the secrets store, under the account's id.
    public sealed record Account(Guid Id, string Email, Provider Provider, string Host = "", int Port = Port, string User = "")
    {
        public string UserName => User.Length > 0 ? User : Email;
        // A custom mailbox needs an address of its own; the named ones do not.
        public string EndpointHost => Host.Length > 0 ? Host : MailReader.Host(Provider);
        public int EndpointPort => Port > 0 ? Port : MailReader.Port;
    }

    // Reads the newest messages of a mailbox. `since` is the highest UID already in hand: zero reads the newest `take`
    // messages, and any other value reads only what came after it.
    //
    // The highest UID is not the same thing as "what the page shows". A mailbox that has been read once never has a
    // message above its highest UID again until a new one arrives, so asking only for what came after it found
    // nothing on every later poll — which is what left a configured mailbox blank. The caller keeps what it last read
    // and adds the new messages to it.
    public async Task<(IReadOnlyList<MailItem> Items, uint Highest)> ReadAsync(Account account, uint since, int take, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var password = secrets.Read(account.Id);
            if (password.Length == 0) throw new MailException(Loc.T("没有设置授权码", "No authorization code is set"));
            using var client = new ImapClient();
            await client.ConnectAsync(account.EndpointHost, account.EndpointPort, SecureSocketOptions.SslOnConnect, cancellation);
            await client.AuthenticateAsync(new SaslMechanismPlain(account.UserName, password), cancellation);
            // Opened read-only: the panel has no business changing what the user has read.
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellation);

            var items = new List<MailItem>();
            uint highest = since;
            var uids = await SearchAsync(client, since, take, cancellation);
            foreach (var uid in uids)
            {
                cancellation.ThrowIfCancellationRequested();
                highest = Math.Max(highest, uid.Id);
                var item = await FetchAsync(client, uid, account, cancellation);
                if (item is not null) items.Add(item);
            }
            return (items, highest);
        }
        finally { _gate.Release(); }
    }

    // The UIDs to read, newest first, at most as many as asked for. With no `since` the newest ones are read, because
    // that is what the page shows; with one, only what came after it is read, because the rest is already in hand.
    private static async Task<IList<UniqueId>> SearchAsync(ImapClient client, uint since, int take, CancellationToken cancellation)
    {
        var results = await client.Inbox.SearchAsync(SearchQuery.All, cancellation);
        var found = new List<UniqueId>();
        if (results is not null)
            foreach (var uid in results)
                if (uid.Id > since) found.Add(uid);
        found.Sort((left, right) => right.Id.CompareTo(left.Id));
        return found.Count > take ? found.Take(take).ToList() : found;
    }

    // Reads one message: its headings, its text, and the calendar part if it carries one.
    private static async Task<MailItem?> FetchAsync(ImapClient client, UniqueId uid, Account account, CancellationToken cancellation)
    {
        var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate);
        var summaries = await client.Inbox.FetchAsync([uid], request, cancellation);
        if (summaries is null || summaries.Count == 0) return null;
        var summary = summaries[0];
        var subject = summary.Envelope?.Subject ?? "";
        var from = summary.Envelope?.From?.Mailboxes?.FirstOrDefault()?.Address ?? "";

        // The parts are found by walking the message's own structure, and only the text ones are fetched: a mail with
        // a picture in it costs the same as one without.
        //
        // The structure has to be walked rather than read off IMessageSummary.BodyParts, because that list mixes
        // depths and says nothing about which part sits inside which. And a part with no specifier is not a broken
        // part: a message that is not multipart has exactly one part, it is the whole body, and it is answered for by
        // the empty specifier. Skipping those read a mailbox full of single-part mail as empty.
        var text = new StringBuilder();
        var html = new StringBuilder();
        string? calendar = null;
        foreach (var part in TextPartsOf(summary.Body))
        {
            var entity = await client.Inbox.GetBodyPartAsync(uid, part, cancellation);
            switch (entity)
            {
                case TextPart textPart when textPart.ContentType.MediaSubtype == "plain":
                    text.Append(textPart.Text);
                    break;
                case TextPart htmlPart when htmlPart.ContentType.MediaSubtype == "html":
                    html.Append(htmlPart.Text);
                    break;
                case MimePart mimePart when mimePart.ContentType.MediaSubtype is "calendar" or "ics":
                    calendar = ReadText(mimePart);
                    break;
            }
        }
        // The plain text is what the panel shows; a mail with only a markup body is read from that instead.
        string bodyText = text.Length > 0 ? text.ToString() : HtmlToText(html.ToString());
        if (bodyText.Length == 0 && calendar is null) return null;

        DateTimeOffset received = summary.InternalDate ?? summary.Envelope?.Date?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var meeting = calendar is null ? null : MeetingFromIcs(calendar, account.Email);
        return new MailItem(
            account.Email,
            from,
            subject,
            received,
            Summarize(bodyText),
            CodeFrom(bodyText),
            uid.Id,
            LinksOf(bodyText, html.ToString()),
            meeting);
    }

    // The text parts of a message, found by walking its structure. A part is skipped when it is an attachment, so a
    // picture or a document in a mail is never fetched; the whole message is one part when it is not multipart, and
    // that part is the body. Public because it is the rule that decides what a mail shows, and the rule is worth
    // checking without a server.
    public static IEnumerable<BodyPart> TextPartsOf(BodyPart? body) => TextParts(body);

    private static IEnumerable<BodyPart> TextParts(BodyPart? body)
    {
        switch (body)
        {
            case BodyPartText text when !IsAttachment(text):
                yield return text;
                break;
            case BodyPartMultipart multipart:
                foreach (var child in multipart.BodyParts)
                    foreach (var found in TextParts(child))
                        yield return found;
                break;
            // A forwarded mail carries a whole message inside a part; its text is the forwarded mail's text.
            case BodyPartMessage nested:
                foreach (var found in TextParts(nested.Body))
                    yield return found;
                break;
        }
    }

    private static bool IsAttachment(BodyPartBasic part) => part.IsAttachment || part.ContentDisposition?.IsAttachment == true;

    // A part's text, decoded from whatever encoding it came in.
    private static string ReadText(MimePart part)
    {
        if (part.Content is null) return "";
        try
        {
            using var stream = new MemoryStream();
            part.Content.DecodeTo(stream);
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ""; }
    }

    // The panel shows a short summary, not the whole mail: the first two hundred characters with the white space
    // flattened.
    private static string Summarize(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 200 ? flat[..200] + "…" : flat;
    }

    // The links in a mail, so the panel can offer them. Only web addresses are kept.
    public static IReadOnlyList<string> LinksOf(string text, string html)
    {
        var links = new List<string>();
        foreach (Match match in Regex.Matches(html, "href\\s*=\\s*(\"([^\"]+)\"|'([^']+)'|([^\\s>]+))", RegexOptions.IgnoreCase))
        {
            string href = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
            var safe = MailItem.SafeUrl(System.Net.WebUtility.HtmlDecode(href));
            if (safe is not null && !links.Contains(safe)) links.Add(safe);
        }
        // A bare address in the text is a link too.
        foreach (Match match in Regex.Matches(text, @"https?://[^\s\""'<>]+"))
        {
            var safe = MailItem.SafeUrl(match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '，', '。'));
            if (safe is not null && !links.Contains(safe)) links.Add(safe);
        }
        return links;
    }

    // A mail's markup body, read as plain text.
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<br\\s*/?>|</p>|</div>|</tr>|</li>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        var lines = text.Split('\n').Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return string.Join('\n', lines.Where(line => line.Length > 0));
    }

    // A meeting out of the calendar part of a mail. A mail that merely mentions a date is not a meeting: only a
    // calendar part is one.
    public static CalendarEvent? MeetingFromIcs(string ics, string account)
    {
        var events = CalendarParser.Parse(ics, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, "mail", account);
        return events.Count > 0 ? events[0] : null;
    }

    // A verification code in the body, if there is one. The shape of the text decides: a code next to the word
    // that names it, and nothing that looks like an order number or an amount.
    public static string? CodeFrom(string text) => VerificationCode.Find(text);
}

// A mail read that did not work, with the reason to show.
public sealed class MailException(string message) : Exception(message);
