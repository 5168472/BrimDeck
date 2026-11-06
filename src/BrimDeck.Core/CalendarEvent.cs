using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// One calendar entry, wherever it came from: a work calendar over CalDAV, a local entry the user added, or an
// invitation that arrived in a mail. The three share this model on purpose, so the same meeting seen through two
// of them is one meeting.
//
// The times are instants, not wall-clock readings: a meeting at nine in the morning in Shanghai and the same
// meeting seen on a machine in another zone are the same instant, and the panel shows each in the reader's own
// local time.
public sealed record CalendarEvent(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool AllDay,
    string Uid = "",
    string Source = "",
    string Account = "")
{
    // An event with no end is a point in time, not an event that lasts for ever: the panel shows it without one.
    public DateTimeOffset? EndOrStart => End ?? Start;

    // Two entries are the same meeting when they carry the same UID and start at the same instant. An entry without
    // a UID (a local one, or a mail that named no identifier) falls back to its start and title, which is as close
    // as two such entries can be called the same.
    public string DedupeKey => Uid.Length > 0 ? $"{Uid}|{Start.ToUnixTimeSeconds()}" : $"t{Start.ToUnixTimeSeconds()}|{Title.Trim()}";

    // The text the panel shows for the time: a time of day for an event that lasts, a date for a whole day.
    public string When(DateTimeOffset now)
    {
        if (AllDay) return $"{Start.LocalDateTime:MM-dd} 全天";
        var sameDay = Start.Date == now.Date;
        return sameDay ? $"{Start.LocalDateTime:HH:mm}" : $"{Start.LocalDateTime:MM-dd HH:mm}";
    }
}

// Where a calendar comes from. The work calendars of the Chinese platforms are CalDAV; a local calendar is the
// user's own entries, kept on this machine; a subscription is a published calendar read over plain HTTP, which is
// how the public holiday calendars are offered.
public enum CalendarSource { Feishu, DingTalk, WeCom, Local, Subscription }

// A calendar published as an address, which needs no account: the public holiday calendars are the ones people use.
// The address is kept with the subscription so it can be changed, and the name is what the panel calls it.
public sealed class CalendarSubscription
{
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    // What the panel calls this calendar, e.g. "中国大陆节假日".
    public string Name { get; set; } = "";
    // The address the calendar is published at.
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public CalendarSubscription Copy() => (CalendarSubscription)MemberwiseClone();
    [JsonIgnore] public string DisplayName => Name.Length > 0 ? Name : Loc.T("订阅日历", "Subscribed calendar");

    // The holiday calendars Apple publishes, which is what this is mostly for. The address pattern is the one
    // iCloud uses for them.
    public const string AppleHolidays = "https://calendars.icloud.com/holidays/";

    // The holidays of a region, by the code Apple uses: cn_zh for mainland China in Chinese, and so on.
    public static string AppleHolidayUrl(string region) => AppleHolidays + region + ".ics";

    // The regions offered in the settings, each with the name the panel shows.
    public static IReadOnlyList<(string Code, string Name)> Regions { get; } =
    [
        ("cn_zh", Loc.T("中国大陆", "Mainland China")),
        ("hk_zh", Loc.T("中国香港", "Hong Kong")),
        ("tw_zh", Loc.T("中国台湾", "Taiwan")),
        ("us_en", Loc.T("美国", "United States")),
        ("gb_en", Loc.T("英国", "United Kingdom")),
        ("jp_ja", Loc.T("日本", "Japan")),
        ("kr_ko", Loc.T("韩国", "Korea"))
    ];

    // A subscription to the holidays of a region, ready to add.
    public static CalendarSubscription Holidays(string region)
    {
        var name = Regions.FirstOrDefault(entry => entry.Code == region).Name ?? region;
        return new CalendarSubscription { Name = name + Loc.T("节假日", " holidays"), Url = AppleHolidayUrl(region) };
    }
}

// One calendar the user has set up: which platform, the account name it was given, and the password, which lives
// in the secrets store rather than here.
public sealed class CalendarAccount
{
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public CalendarSource Source { get; set; } = CalendarSource.Feishu;
    // The account name the platform gave, e.g. a Feishu address. It is also the Basic-auth user name.
    public string User { get; set; } = "";
    // The calendar collection to read, learned by discovery and kept so the next read does not have to search again.
    public string Href { get; set; } = "";
    // The server to reach, when it is not the one the platform publishes. WeCom's ActiveSync endpoint is reached at
    // the host the account was given, which is not always wecom.work.
    public string Server { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public string DisplayName => User.Length > 0 ? User : Source.ToString();
    public CalendarAccount Copy() => (CalendarAccount)MemberwiseClone();
}

// One mailbox the user set up. The password is not here: it lives in the secrets store, under the account's id.
public sealed class MailboxAccount
{
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public MailReader.Provider Provider { get; set; } = MailReader.Provider.Qq;
    // For a mailbox the panel has no name for: the server it is reached at.
    public string Host { get; set; } = "";
    public int Port { get; set; } = MailReader.Port;
    // The name to log in with, when it is not the address itself.
    public string User { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public string DisplayName => Email.Length > 0 ? Email : Provider.ToString();
    public MailboxAccount Copy() => (MailboxAccount)MemberwiseClone();
}

// The state of the calendar service: the accounts, when they were last read, what failed and how often.
public sealed class CalendarState
{
    public List<CalendarAccount> Accounts { get; set; } = [];
    // The calendars read without an account, such as the public holiday calendars. A first start has the mainland
// China holidays on, because a calendar with the public holidays in it is what most people want without setting
// anything up; the settings page switches it, or picks another region.
    public List<CalendarSubscription> Subscriptions { get; set; } = [CalendarSubscription.Holidays("cn_zh")];
    // The mailboxes, read on their own cadence.
    public List<MailboxAccount> MailAccounts { get; set; } = [];
    // Whether the panel keeps reading the calendars on its own.
    public bool AutoSync { get; set; } = true;
    // The entries the user added here, kept in the settings so they survive a restart.
    public List<CalendarEvent> LocalEvents { get; set; } = [];
    // The ignored mail invitations, by their dedupe key, so a hidden one stays hidden.
    public List<string> IgnoredMailEvents { get; set; } = [];
    // The moments a meeting was already announced for, so the same one does not remind twice.
    public List<string> Announced { get; set; } = [];
    // The mail already read, by mailbox, so a message is not shown twice.
    public Dictionary<Guid, List<uint>> SeenMailUids { get; set; } = [];
    // How many entries the calendar page's right-hand column holds when no day is picked. Five fits a panel without
    // scrolling and is what a person plans a day or two around.
    public int ListRows { get; set; } = 5;
    public DateTimeOffset? LastRead { get; set; }
    public string LastError { get; set; } = "";
    // How many reads in a row failed. The error is only shown from the second, so a single hiccup is not alarming.
    public int Failures { get; set; }
    public CalendarState Copy() => new()
    {
        Accounts = [.. Accounts.Select(account => account.Copy())],
        Subscriptions = [.. Subscriptions.Select(subscription => subscription.Copy())],
        MailAccounts = [.. MailAccounts.Select(account => account.Copy())],
        AutoSync = AutoSync,
        LocalEvents = [.. LocalEvents],
        IgnoredMailEvents = [.. IgnoredMailEvents],
        Announced = [.. Announced],
        SeenMailUids = SeenMailUids.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
        ListRows = ListRows,
        LastRead = LastRead,
        LastError = LastError,
        Failures = Failures
    };
}
