namespace BrimDeck.Core;

// Which chat application a message came from. The panel shows one banner per app, so this is also the
// thing a banner is grouped by, and the key a colour and an icon come from.
public enum ChatApp { Feishu, WeCom, WeChat }

// One incoming message, as much of it as the source could read. A source that only knows there is
// unread traffic (no sender, no text) still produces a message with what it has, and the panel shows
// what is there; nothing here is required except the app and the time it was noticed.
//
// Text is not logged, written to settings, or sent anywhere. It is held in memory for as long as the
// banner shows and then dropped.
public sealed record IncomingMessage(
    ChatApp App,
    string Sender,
    string Text,
    DateTimeOffset Received,
    string Conversation = "",
    bool Mention = false)
{
    // What identifies this message in the source that read it: the same unread state, seen twice, gives the same key,
    // and a changed state gives a new one. A source states it; the ledger only records and compares. Empty falls back
    // to the fields below, so a source that cannot name a message still gets usable de-duplication.
    public string Key { get; init; } = "";

    // A message with neither a sender nor any text carries no information beyond its app, which a banner
    // could not say anything about. Used to filter sources that read a tree but found nothing new.
    public bool IsEmpty => Sender.Length == 0 && Text.Length == 0;

    // What a banner puts after the app name when the source gave a sender but no body (an attachment, an
    // image, a message whose text the source could not read).
    public string Summary => Text.Length > 0 ? Text : Conversation.Length > 0 ? Conversation : Sender;

    public string Fingerprint => Key.Length > 0 ? Key : $"{App}:{Sender}:{Conversation}:{Text}";
}

// What a source found in one poll. Polls that read a tree also report whether the client was there at all, so a caller
// can tell "no new message" from "the app is not running".
public sealed record MessagePoll(IReadOnlyList<IncomingMessage> Messages, bool Reachable, string? Note = null)
{
    public static readonly MessagePoll Unreachable = new([], false);
}

// A source of incoming messages. Implementations read one chat application and must be safe to call from a background
// thread: the panel polls them on its refresh timer, never on the UI thread.
//
// A source reports the messages it can currently see, each with a stable Key, and does no de-duplication of its own.
// That is the panel's job (MessageLedger), so a source needs no memory between polls and a test can call one directly.
public interface IIncomingMessageSource
{
    ChatApp App { get; }

    // Whether this source can work on this machine at all: the app is installed, the API credentials are set, the
    // client is signed in. Checked when the settings page lists sources, so a source that cannot run is shown as
    // unavailable rather than as silent.
    bool Available { get; }

    // Reads what is on screen now. Returns Unreachable when the app is not running or its window is not readable,
    // which the panel treats as "nothing to show" rather than as an error.
    MessagePoll Poll();
}

// Fingerprints that survive a restart, so the same message is not announced again when the panel
// reopens. Bounded: only the newest entries are kept, because a source can only re-report what is still
// on screen.
public sealed class MessageLedger(int capacity = 500)
{
    private readonly List<string> _order = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    public int Capacity { get; } = Math.Max(2, capacity);

    public IReadOnlyCollection<string> Keys => _keys;
    public bool Contains(string key) => _keys.Contains(key);

    // Returns true when the key was new. The caller reports the message only in that case.
    public bool Add(string key)
    {
        if (!_keys.Add(key)) return false;
        _order.Add(key);
        while (_order.Count > Capacity)
        {
            _keys.Remove(_order[0]);
            _order.RemoveAt(0);
        }
        return true;
    }

    public void Seed(IEnumerable<string> keys)
    {
        foreach (var key in keys) Add(key);
    }

    // The newest keys, for persisting. Order is oldest first, as the list keeps them.
    public IEnumerable<string> Newest(int count) => _order.Skip(Math.Max(0, _order.Count - count));
}
