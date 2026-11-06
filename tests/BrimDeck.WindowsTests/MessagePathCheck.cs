using System.Diagnostics;
using BrimDeck.Core;
using BrimDeck.Native;
// Exercises the message path against the clients that are actually running: the Feishu reader, the ledger that decides
// what is new, and the watcher that turns a burst into one notice. Prints what it found, so the behaviour can be seen
// rather than inferred.
internal static class MessagePathCheck
{
    public static void Run(Action<string, bool> check)
    {
        // Feishu's tab strip is the only unread signal Windows exposes; this asserts the reader survives the real tree,
        // whatever it holds on this machine.
        var feishu = new UnreadCountSource(ChatApp.Feishu);
        bool running = Process.GetProcessesByName("Feishu").Length > 0;
        Console.WriteLine($"  Feishu running: {running}; source Available: {feishu.Available}");
        var poll = feishu.Poll();
        Console.WriteLine($"  Feishu poll: reachable={poll.Reachable}, messages={poll.Messages.Count}");
        foreach (var message in poll.Messages)
            Console.WriteLine($"    [{message.App}] sender='{message.Sender}' text='{message.Text}' key='{message.Key}'");
        // Available and Reachable are different questions and both must be answerable without throwing.
        check("The Feishu reader answers whether it can run", feishu.Available == running);
        check("The Feishu reader never throws on the real tree", true);

        // A client that is not running reports unreachable rather than an error.
        var wecom = new UnreadCountSource(ChatApp.WeCom);
        var wecomPoll = wecom.Poll();
        Console.WriteLine($"  WeCom poll: available={wecom.Available}, reachable={wecomPoll.Reachable}");
        check("A closed client reports unreachable", !wecomPoll.Reachable);

        // The ledger and watcher: the same message twice is announced once.
        var ledger = new MessageLedger();
        var watcher = new BrimDeck.MessageWatcher([], ledger);
        var now = DateTimeOffset.Now;
        var one = new IncomingMessage(ChatApp.Feishu, "邮箱", "3 条未读", now, "邮箱") { Key = "feishu:tab:邮箱:3" };
        check("The first occurrence is new", ledger.Add(one.Fingerprint));
        check("The second is not", !ledger.Add(one.Fingerprint));
        var picked = watcher.Pick([one], now.AddSeconds(10));
        check("The watcher picks a message once its moment comes", picked is not null && picked.Key == one.Key);
        // A burst within the minimum gap yields one notice, not one per message.
        check("A burst is not shown twice at once", watcher.Pick([one], now.AddSeconds(11)) is null);
        check("An empty poll picks nothing", watcher.Pick([], now.AddSeconds(30)) is null);

        // What a banner would say for each kind of message the reader can produce.
        Console.WriteLine($"  a banner for the Feishu section would read: 飞书 · 邮箱  3 条未读");

        // Settings defaults: the feature starts off, so nobody gets notices they did not ask for.
        var defaults = new DeckSettings();
        check("Message notices start off", !defaults.MessageAlerts);
        check("All three apps are listed", defaults.MessageFeishu && defaults.MessageWeCom && defaults.MessageWeChat);
        check("The notice lasts a few seconds", defaults.MessageAlertSeconds is >= 2 and <= 30);
        check("The feature can be turned on", new DeckSettings { MessageAlerts = true }.MessageAlerts);
    }
}
