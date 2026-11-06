using BrimDeck.Core;

namespace BrimDeck.Core;

// Reads the calendars and the mailboxes and keeps what it finds, so the panel has one place to look.
//
// Two things are worth saying about how it works:
//
//  - The same meeting can arrive twice: once from a work calendar and once as an invitation in a mail. They are
//    one meeting, and the key that says so is the meeting's own identifier together with the moment it starts.
//  - The services are read on the pool and the results are handed over as plain data. A calendar server that has
//    gone away must not stop the panel from drawing.
public sealed class AgendaService(CalDavClient calendars, EwsCalendarClient exchange, MailReader mail, IProviderSecrets secrets)
{
    // The readers, for the settings page's test: a calendar that shows nothing is either a wrong account or an
    // unreachable host, and trying it is what tells the two apart.
    public CalDavClient Calendars => calendars;
    public EwsCalendarClient Exchange => exchange;
    // The mailbox reader, for the same test on a mailbox.
    public MailReader Reader => mail;
    // How often the calendars are read. Five minutes is often enough for a diary and rare enough not to be a
    // nuisance for the server.
    public static readonly TimeSpan CalendarInterval = TimeSpan.FromMinutes(5);
    // How often the mailboxes are read. Two minutes is the middle of what the mail clients do.
    public static readonly TimeSpan MailInterval = TimeSpan.FromMinutes(2);
    // How many of a mailbox's newest messages are read the first time it is seen. The page shows the recent ones, and
    // reading a whole mailbox to show six of them is not what this is for.
    public const int MailTake = 20;
    // How many messages the panel keeps at once, across every mailbox.
    public const int MailKeep = 60;
    // How far ahead the calendars are read. The page shows a month grid, which needs the days of the month it draws and
// the days either side of it that fill the grid's first and last weeks, so the window is a month and a half.
    public static readonly TimeSpan CalendarHorizon = TimeSpan.FromDays(45);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _calendarsAt, _mailAt;
    private bool _calendarsDone, _mailDone;

    // The events found, the mail found, and when each was last read.
    public IReadOnlyList<CalendarEvent> Events { get; private set; } = [];
    public IReadOnlyList<MailItem> Mail { get; private set; } = [];
    public DateTimeOffset? CalendarsRead { get; private set; }
    public DateTimeOffset? MailRead { get; private set; }
    public string CalendarError { get; private set; } = "";
    public string MailError { get; private set; } = "";

    // Reads whichever of the two is due, and returns true when something changed. The caller asks at its own pace
    // and this decides whether the work is needed at all.
    public async Task<bool> RefreshAsync(CalendarState state, DateTimeOffset now, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            bool changed = false;
            if (state.AutoSync && now - _calendarsAt >= CalendarInterval && !_calendarsDone)
            {
                await ReadCalendarsAsync(state, now, cancellation);
                _calendarsAt = now; _calendarsDone = true;
                changed = true;
            }
            if (now - _mailAt >= MailInterval && !_mailDone)
            {
                await ReadMailAsync(state, now, cancellation);
                _mailAt = now; _mailDone = true;
                changed = true;
            }
            return changed;
        }
        finally { _gate.Release(); }
    }

    // Reads the calendars and the local entries, and puts them into one list.
    private async Task ReadCalendarsAsync(CalendarState state, DateTimeOffset now, CancellationToken cancellation)
    {
        var events = new List<CalendarEvent>();
        // The entries the user added here are always there, whatever the servers say.
        events.AddRange(state.LocalEvents);
        string error = "";
        // The published calendars first: they need no account, so a machine with nothing set up still has the holidays.
        foreach (var subscription in state.Subscriptions.Where(subscription => subscription.Enabled && subscription.Url.Length > 0))
        {
            try
            {
                var from = now - CalendarHorizon;
                var to = now + CalendarHorizon;
                events.AddRange(await calendars.ReadSubscriptionAsync(subscription, from, to, cancellation));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A holiday calendar that cannot be read must not hide the work calendars.
                error = ex is CalDavException ? ex.Message : Loc.T("订阅读取失败", "The subscription could not be read");
            }
        }
        foreach (var account in state.Accounts.Where(account => account.Enabled && account.User.Length > 0))
        {
            try
            {
                var password = secrets.Read(account.InstanceId);
                if (password.Length == 0) continue;
                var from = now - CalendarHorizon;
                var to = now + CalendarHorizon;
                // WeCom does not offer its calendar over CalDAV; it speaks ActiveSync, which is a different protocol
                // with its own reader. The account says which of the two it is.
                if (account.Source == CalendarSource.WeCom)
                {
                    events.AddRange(await exchange.ReadAsync(account, password, account.Server, from, to, cancellation));
                    continue;
                }
                // Every calendar of the account is read, not only one: an account usually has several, and the one that holds
                // the user's meetings is not always the first the server lists.
                var hrefs = await calendars.DiscoverAllAsync(account, password, cancellation);
                foreach (var leaf in hrefs)
                    events.AddRange(await calendars.ReadAsync(account, password, leaf, from, to, cancellation));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One calendar that cannot be read must not hide the others.
                error = ex is CalDavException ? ex.Message : Loc.T("日历读取失败", "The calendar could not be read");
            }
        }
        // The same meeting from two sources is one meeting: the first one wins.
        Events = Dedupe(events);
        CalendarsRead = now;
        CalendarError = error;
    }

    // Reads a month the calendar page has stepped to. The subscriptions are read over that month so its days carry
    // their marks; the work calendars keep the window the panel reads, because a work calendar is read forward and
    // the page's own column shows only what is ahead.
    public async Task ReadMonthAsync(CalendarSubscription subscription, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            var extra = await calendars.ReadSubscriptionAsync(subscription, from, to, cancellation);
            if (extra.Count == 0) return;
            // What was read for this window replaces what the earlier read found there, so stepping back and forth
            // does not pile the same days up.
            Events = Dedupe(Events.Where(entry => entry.Source != "subscription" || entry.Start < from || entry.Start > to).Concat(extra));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { _gate.Release(); }
    }

    // Reads the mailboxes and puts what they hold into one list.
    private async Task ReadMailAsync(CalendarState state, DateTimeOffset now, CancellationToken cancellation)
    {
        string error = "";
        var accounts = state.MailAccounts.Where(account => account.Enabled && account.Email.Length > 0).ToList();
        foreach (var account in accounts)
        {
            try
            {
                // What this mailbox already shows, if anything. That is what decides where the read starts, and it has
                // to be this list rather than the highest UID on disk: after a restart the list is empty, and asking
                // only for what came after the newest UID on disk asks for nothing at all — the mailbox then shows
                // nothing for as long as the panel runs, which is exactly the blank page this is fixing.
                var held = _mailByAccount.TryGetValue(account.InstanceId, out var previous) ? previous : null;
                uint since = held is { Count: > 0 } ? held.Max(item => item.Uid) : 0u;
                var reader = new MailReader.Account(account.InstanceId, account.Email, account.Provider, account.Host, account.Port, account.User);
                var read = await mail.ReadAsync(reader, since, MailTake, cancellation);
                // The new messages go on top of what the mailbox already showed. Replacing the list with what this
                // poll found emptied the page on every poll that found nothing, which is the usual poll.
                _mailByAccount[account.InstanceId] = [.. read.Items, .. (held ?? []).Where(old => !read.Items.Any(newer => newer.Uid == old.Uid))];
                if (read.Highest > 0) state.SeenMailUids[account.InstanceId] = [read.Highest];
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A mailbox that cannot be read leaves a note in the log and the reason on the page: the two are for
                // different readers, and a silent failure is the one outcome that helps neither.
                Note($"mail: {account.Email} failed: {ex.GetType().Name}: {ex.Message}");
                error = ex is MailException ? ex.Message : Loc.T("邮件读取失败", "The mail could not be read");
            }
        }
        // The messages of every mailbox, newest first, and only the newest few are kept: the rest is history the
        // panel does not show.
        Mail = MergeMail(_mailByAccount.Values, MailKeep);
        MailRead = now;
        MailError = error;
    }

    // The messages last read from each mailbox, by the mailbox's id. Held separately because a poll that finds nothing
    // new must leave the messages it found last time where they are.
    private readonly Dictionary<Guid, List<MailItem>> _mailByAccount = [];

    // The messages of every mailbox as one list, newest first and no more than the page shows. Public because the rule
    // is the one that decides whether the page is empty, and a poll that finds nothing new is the ordinary case: it
    // must not empty the page.
    public static IReadOnlyList<MailItem> MergeMail(IEnumerable<IEnumerable<MailItem>> perAccount, int keep) =>
        perAccount.SelectMany(items => items).OrderByDescending(item => item.Received).Take(keep).ToList();

    // Forgets what was read from a mailbox that has been removed, so its mail does not outlive the account.
    public void ForgetMail(Guid account) => _mailByAccount.Remove(account);

    // A note about what a read did, in the same log the calendar reader writes: the only place a running panel can be
    // asked what it did without a debugger attached.
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

    // One list without the repeats: the same meeting seen twice is one meeting.
    public static List<CalendarEvent> Dedupe(IEnumerable<CalendarEvent> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CalendarEvent>();
        foreach (var entry in events.OrderBy(entry => entry.Start))
        {
            if (seen.Add(entry.DedupeKey)) result.Add(entry);
        }
        return result;
    }

    // The meetings that are coming, in the order they happen.
    public IReadOnlyList<CalendarEvent> Upcoming(DateTimeOffset now, TimeSpan within) =>
        Events.Where(entry => entry.Start >= now && entry.Start <= now + within).OrderBy(entry => entry.Start).ToList();

    // The mail that is not older than a given moment: what the panel shows.
    public IReadOnlyList<MailItem> RecentMail(DateTimeOffset now, TimeSpan within) =>
        Mail.Where(item => item.Received >= now - within).OrderByDescending(item => item.Received).ToList();

    // The meetings on one day, in the order they happen. Which day a meeting is on is decided by when it starts, in
    // the reader's own time zone: a meeting that begins at 23:30 belongs to the day it began on, not to the next one.
    //
    // A subscribed calendar is left out: the calendar page marks those on the dates themselves, and the column beside
    // the month is about meetings. The pick is the day the user asked about, so it decides what is shown rather than
    // what is already on the page.
    public static IReadOnlyList<CalendarEvent> OnDay(IEnumerable<CalendarEvent> events, DateOnly day) =>
        events.Where(entry => entry.Source != "subscription")
            .Where(entry => DateOnly.FromDateTime(entry.Start.LocalDateTime.Date) == day)
            .OrderBy(entry => entry.Start)
            .ToList();

    // The entries the calendar page's column holds: the pressed day's meetings, or the month's when no day is
    // pressed. `month` is the month the page is showing, so stepping to another month carries the column with it — a
    // column that kept showing this month's meetings under next month's grid answered a question nobody asked.
    //
    // In the month that holds today the column starts at today: a reader looking at the current month wants what is
    // still to come, not what has already happened. In any other month it is the whole month, because every day of it
    // is equally ahead or equally past.
    public static IReadOnlyList<CalendarEvent> CalendarColumn(IEnumerable<CalendarEvent> events, DateOnly? picked, DateTime month, DateTimeOffset now)
    {
        var meetings = events.Where(entry => entry.Source != "subscription");
        if (picked is { } day) return OnDay(meetings, day);
        var from = new DateTime(month.Year, month.Month, 1);
        var to = from.AddMonths(1);
        var today = now.LocalDateTime.Date;
        // Only the current month is trimmed to what is still ahead; a month already behind shows all of it, so a reader
        // stepping back still sees what happened.
        if (today >= from && today < to) from = today;
        return meetings.Where(entry => entry.Start.LocalDateTime >= from && entry.Start.LocalDateTime < to)
            .OrderBy(entry => entry.Start).ToList();
    }

    // The meetings that should be announced now: those that start within the lead time and have not been
    // announced. A meeting seen through two sources is announced once, because the key is the same.
//
// The test is on the events themselves, not on what this service happens to hold, so that the rule can be
// checked on its own.
    public static IReadOnlyList<CalendarEvent> DueForReminder(IEnumerable<CalendarEvent> events, CalendarState state, DateTimeOffset now, TimeSpan lead)
    {
        var due = new List<CalendarEvent>();
        // The same meeting through two sources is one meeting, and is announced once: the events are put together
        // first, so the second copy of a meeting is not a second reminder.
        foreach (var entry in Dedupe(events).Where(entry => entry.Start >= now && entry.Start <= now + lead))
        {
            if (state.IgnoredMailEvents.Contains(entry.DedupeKey)) continue;
            if (state.Announced.Contains(entry.DedupeKey)) continue;
            due.Add(entry);
        }
        return due;
    }

    // The same rule over the meetings this service holds.
    public IReadOnlyList<CalendarEvent> DueForReminder(CalendarState state, DateTimeOffset now, TimeSpan lead)
        => DueForReminder(Events, state, now, lead);
}
