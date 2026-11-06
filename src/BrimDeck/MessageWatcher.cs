using BrimDeck.Core;

namespace BrimDeck;

// Polls the chat sources and turns what they report into island notices.
//
// The poll runs on the pool and never on the UI thread: every source calls into another process, and one that has hung
// would otherwise freeze the panel. Only the main panel polls; the panels of other monitors show the banner they are told
// to (MainWindow.ShowMessage), the same way they show snapshot data they did not read.
//
// The ledger lives here and is shared by every source, so a message is announced once even when two sources look at the
// same app, and a restart does not replay what is still on screen (the newest keys are saved with the settings).
public sealed class MessageWatcher(IReadOnlyList<IIncomingMessageSource> sources, MessageLedger ledger)
{
    // Several unread sections can report at once; showing all of them back to back would hold the island for half a
    // minute. The newest is shown and the rest are dropped, which is what a phone does with a burst.
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(2);
    private DateTimeOffset _lastShown;
    private int _polling;
    public MessageLedger Ledger { get; } = ledger;

    // Sources that can run here, for the settings page to list them as available or not.
    public IReadOnlyList<IIncomingMessageSource> Sources { get; } = sources;

    public bool AnyEnabled(DeckSettings settings) => settings.MessageAlerts && EnabledSources(settings).Count > 0;

    public IReadOnlyList<IIncomingMessageSource> EnabledSources(DeckSettings settings)
    {
        var list = new List<IIncomingMessageSource>();
        foreach (var source in Sources)
        {
            bool on = source.App switch
            {
                ChatApp.Feishu => settings.MessageFeishu,
                ChatApp.WeCom => settings.MessageWeCom,
                _ => settings.MessageWeChat
            };
            if (on) list.Add(source);
        }
        return list;
    }

    // Reads every enabled source once and returns the messages that are new, newest last. Returns an empty list while
    // a previous poll is still running, so a slow client cannot stack polls.
    public IReadOnlyList<IncomingMessage> Poll(DeckSettings settings)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return [];
        try
        {
            var fresh = new List<IncomingMessage>();
            foreach (var source in EnabledSources(settings))
            {
                MessagePoll poll;
                try { poll = source.Poll(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
                foreach (var message in poll.Messages)
                {
                    if (message.IsEmpty) continue;
                    // The ledger is the only place that decides what is new, so a source needs no memory of its own.
                    if (Ledger.Add(message.Fingerprint)) fresh.Add(message);
                }
            }
            return fresh;
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    // Which message of the burst to show: the newest one, and only once the previous notice has had its moment.
    public IncomingMessage? Pick(IReadOnlyList<IncomingMessage> fresh, DateTimeOffset now)
    {
        if (fresh.Count == 0) return null;
        if (now - _lastShown < MinimumGap) return null;
        _lastShown = now;
        return fresh[^1];
    }
}
