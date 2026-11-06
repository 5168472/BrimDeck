using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Reads chat unread counts from the running clients' own windows through UI Automation.
//
// What each client exposes was measured on this machine, and it is little:
//
//  - Feishu (CEF): the main window's control tree is only its chrome, about 70 elements: the tab strip, the caption
//    buttons and a watermark view. The conversation list and the messages are drawn inside a web view and are not in
//    the tree at all. The one readable unread signal is the tab strip, whose items name their badge ("邮箱, unread: 49").
//    So Feishu reports *that* something is unread, with the count, and never who sent it.
//  - WeChat 4 (Qt): the window's tree is two empty panes. Qt draws its own widgets and exposes no accessibility tree,
//    and MSAA finds no object on the window either. Nothing can be read, so this source reports Feishu-style
//    availability and then never a message.
//  - WeCom: not read here; it is handled by the API source's configuration only.
//
// This class therefore only ever says "app X has N unread". That is enough for a banner that shows the app and a
// count, and it is honest about the rest: the message text and the sender are left empty rather than guessed.
//
// UI Automation calls into another process and can block, so every read runs on the pool, guarded so only one runs
// at a time, and the window is looked up at most once every few seconds.
internal sealed class UnreadCountSource(ChatApp app) : IIncomingMessageSource
{
    // A read takes tens of milliseconds when the window is there and the process is responsive, and can hang for
    // seconds when it is not, so it is bounded and the result of a timed-out read is simply "nothing new".
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(4);
    private int _busy;

    public ChatApp App { get; } = app;

    // Process names and the window class the client uses for its main window, measured from the running clients.
    private (string[] Processes, string Class) Target => App switch
    {
        ChatApp.Feishu => (["Feishu", "Lark"], "Chrome_WidgetWin_1"),
        ChatApp.WeChat => (["Weixin", "WeChat", "WeChatAppEx"], "Qt51514QWindowIcon"),
        ChatApp.WeCom => (["WXWork", "WeCom"], "Chrome_WidgetWin_1"),
        _ => ([], "")
    };

    public bool Available
    {
        get
        {
            var (processes, _) = Target;
            foreach (var name in processes)
            {
                try { if (Process.GetProcessesByName(name).Length > 0) return true; }
                catch (InvalidOperationException) { }
            }
            return false;
        }
    }

    public MessagePoll Poll()
    {
        var (processes, windowClass) = Target;
        if (windowClass.Length == 0 || processes.Length == 0) return MessagePoll.Unreachable;
        var process = processes.Select(Find).FirstOrDefault(p => p is not null);
        if (process is null) return MessagePoll.Unreachable;
        // The walk is cheap for Feishu's 70 elements and instant for WeChat's two; no throttling beyond the poll
        // interval itself is needed, but a read still behind its timeout is not started again.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return MessagePoll.Unreachable;
        try
        {
            var read = Task.Run(() => Read(process, windowClass));
            if (!read.Wait(ReadTimeout)) return MessagePoll.Unreachable;
            return read.Result ?? MessagePoll.Unreachable;
        }
        catch (AggregateException) { return MessagePoll.Unreachable; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static Process? Find(string name)
    {
        try
        {
            var candidates = Process.GetProcessesByName(name);
            // The process with a window is the one worth reading; a helper without one has no tree.
            foreach (var process in candidates)
                if (process.MainWindowHandle != IntPtr.Zero) return process;
            return null;
        }
        catch (InvalidOperationException) { return null; }
    }

    private MessagePoll? Read(Process process, string windowClass)
    {
        try
        {
            var window = Window(process, windowClass);
            if (window is null) return MessagePoll.Unreachable;
            var messages = App == ChatApp.Feishu ? Feishu(window) : [];
            return new MessagePoll(messages, true);
        }
        catch (ElementNotAvailableException) { return MessagePoll.Unreachable; }
        catch (InvalidOperationException) { return MessagePoll.Unreachable; }
        catch (System.Runtime.InteropServices.COMException) { return MessagePoll.Unreachable; }
    }

    private static AutomationElement? Window(Process process, string windowClass)
    {
        var byProcess = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);
        var byClass = new PropertyCondition(AutomationElement.ClassNameProperty, windowClass);
        var both = new AndCondition(byProcess, byClass);
        var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, both);
        return window;
    }

    // Feishu's tab strip names a badge as "邮箱, unread: 49" (English) or "…，N 条未读" (Chinese). Both forms are read,
    // because the client's language is the user's and not the panel's.
    private static readonly Regex UnreadPattern = new(@"(?:unread|未读)\s*[:：]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<IncomingMessage> Feishu(AutomationElement window)
    {
        var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
        var tabs = window.FindAll(TreeScope.Descendants, tabCondition);
        var messages = new List<IncomingMessage>();
        var now = DateTimeOffset.Now;
        foreach (AutomationElement tab in tabs)
        {
            var name = tab.Current.Name ?? "";
            var match = UnreadPattern.Match(name);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int count) || count <= 0) continue;
            var section = name.Split(',')[0].Split('，')[0].Trim();
            // The key names the section and its count, so the same state read twice matches and a changed count does
            // not. Reading the messages clears the badge, so the next unread of the same size announces again.
            messages.Add(new IncomingMessage(ChatApp.Feishu, section,
                Loc.T($"{count} 条未读", Loc.Count(count, "unread message", "unread messages")), now, section)
            {
                Key = $"feishu:tab:{section}:{count}"
            });
        }
        return messages;
    }
}
