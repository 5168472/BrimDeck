using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The settings page for message notices and the AI key reset schedule. Kept in its own file, as the monitor's and the
// sensor's pages are, and registered with SettingsWindow's page list.
public partial class SettingsWindow
{
    internal const int MessagesPage = 11;
    internal const int CalendarPage = 12;
    internal const int MailPage = 13;

    private void Messages()
    {
        SectionTitle(Loc.T("消息提示", "Message notices"));
        Group(
            Toggle(Loc.T("在岛内提示新消息", "Show new messages in the island"), S.MessageAlerts,
                on => Change(s => s.MessageAlerts = on),
                help: Loc.T("有未读消息时，收起状态的岛会短暂显示是哪个应用、有多少条。它和配额预警共用同一行，谁后到显示谁。",
                    "When messages are waiting, the collapsed island briefly shows which app and how many. It shares the row with quota warnings; whichever arrives last is shown.")),
            Toggle(Loc.T("飞书", "Feishu"), S.MessageFeishu, on => Change(s => s.MessageFeishu = on),
                help: Loc.T("读取飞书标签页上的未读计数。飞书把消息画在网页视图里，Windows 的接口读不到正文和发送人，所以只显示应用名和条数。",
                    "Reads the unread count from Feishu's tabs. Feishu draws its messages in a web view, so Windows cannot read the text or the sender; the app name and the count are what shows.")),
            Toggle(Loc.T("企业微信", "WeCom"), S.MessageWeCom, on => Change(s => s.MessageWeCom = on)),
            Toggle(Loc.T("微信", "WeChat"), S.MessageWeChat, on => Change(s => s.MessageWeChat = on),
                help: Loc.T("微信 4 用 Qt 自己绘制界面，不向系统暴露任何控件，也从不向 Windows 通知中心投递消息，所以目前读不到。这一项先占位，等它提供可读的接口。",
                    "WeChat 4 draws its own interface with Qt, exposes no controls and never posts to the Windows notification centre, so nothing can be read yet. This reserves the place for when it can.")));

        SectionTitle(Loc.T("AI 密钥重置时间", "AI key reset times"),
            help: () => Loc.T("每小时整点用你填写的密钥向服务商查询一次真实的重置时间，并从两次重置之间自动学习间隔。中间不需要任何别的任务：岛按已知的重置时间自己倒数。",
                "Once an hour, on the hour, the panel asks the provider for the real reset time with the key you enter, and learns the interval from two resets. Nothing else needs to run: the island counts down from the last reset it learned."));
        if (S.KeyResets.Count == 0)
            Target.Children.Add(new Border
            {
                Child = TextLine(Loc.T("还没有密钥。添加一个、填上密钥，岛就会每小时自动查询并维护重置时间。",
                    "No keys yet. Add one, paste its key, and the island asks every hour and keeps the reset time itself."), 12, TextSecondary),
                Padding = new Thickness(0, 0, 0, 16)
            });
        foreach (var key in S.KeyResets.ToList()) KeyEditor(key);
        var add = ActionButton(Loc.T("添加密钥", "Add a key"), AddKey, primary: S.KeyResets.Count == 0);
        add.IsEnabled = S.KeyResets.Count < AppPresets.MaximumKeys;
        add.Margin = new Thickness(0, 8, 0, 28);
        Target.Children.Add(add);
    }

    // The calendar page: the work calendars and the published holidays. It is its own setting page rather than part
    // of the messages page, because setting up an account is not looking for the notice switches.
    private void CalendarSettingsPage()
    {
        SectionTitle(Loc.T("显示", "Shown"));
        Group(Toggle(Loc.T("在岛里显示日程", "Show the calendar in the island"), S.CalendarPage,
            on => Change(s => s.CalendarPage = on),
            help: Loc.T("关掉后「日程」页签不再出现，日历也不再读取。",
                "Off, the Calendar tab disappears and the calendars are no longer read.")),
            Row(Loc.T("提前提醒", "Remind before"),
                Field(S.ReminderLead.ToString(), 70, value =>
                {
                    if (int.TryParse(value, out int minutes)) Change(s => s.ReminderLead = Math.Clamp(minutes, 0, 1440));
                }, 5),
                TextLine(Loc.T("会议开始前多少分钟在岛里提示。填 0 表示不提醒。",
                    "How many minutes before a meeting the island mentions it. 0 turns the reminder off."), 12, TextSecondary)),
            Row(Loc.T("列表条数", "List length"),
                Field(S.Agenda.ListRows.ToString(), 70, value =>
                {
                    if (int.TryParse(value, out int rows)) Change(s => s.Agenda.ListRows = Math.Clamp(rows, 1, 12));
                }, 5),
                TextLine(Loc.T("日程页右侧显示多少条。点月历里的某一天会换成那天的日程，再点一次或按「全部」回到这个列表。",
                    "How many entries the right-hand column of the calendar page holds. Pressing a day in the month shows that day's meetings; press it again, or press All, to come back to this list."), 12, TextSecondary)));
        CalendarSettings();
    }

    // The mail page: the mailboxes and what is read from them.
    private void MailSettingsPage()
    {
        SectionTitle(Loc.T("显示", "Shown"));
        Group(Toggle(Loc.T("在岛里显示邮件", "Show mail in the island"), S.MailPage,
            on => Change(s => s.MailPage = on),
            help: Loc.T("关掉后「邮件」页签不再出现，邮箱也不再读取。",
                "Off, the Mail tab disappears and the mailboxes are no longer read.")));
        MailSettings();
    }

    // The work calendars. The password is the one the platform issued for the calendar; it is kept in the secrets
    // store, not in the settings file.
    private void CalendarSettings()
    {
        SectionTitle(Loc.T("工作日历", "Work calendars"),
            help: () => Loc.T("飞书和钉钉的日历用 CalDAV，企业微信用 ActiveSync。账号和密码是平台在日历同步设置页里生成的那一对，服务器地址那里也会一并给你。",
                "Feishu and DingTalk calendars use CalDAV; WeCom uses ActiveSync. The account and password are the pair the platform generates on its calendar-sync settings page, which also states the server address."));
        if (S.Agenda.Accounts.Count == 0)
            Target.Children.Add(new Border
            {
                Child = TextLine(Loc.T("还没有日历账号。添加一个后，日程会出现在「日程」页。",
                    "No calendar yet. Add one and its meetings appear on the Calendar page."), 12, TextSecondary),
                Padding = new Thickness(0, 0, 0, 16)
            });
        foreach (var account in S.Agenda.Accounts.ToList()) CalendarEditor(account);
        var add = ActionButton(Loc.T("添加日历", "Add a calendar"), AddCalendar);
        add.Margin = new Thickness(0, 8, 0, 28);
        Target.Children.Add(add);

        // The published calendars: an address, not a list of regions. Any iCalendar feed works, which is what a holiday
// subscription usually is, and the address is the one thing a person already has when they want to add one.
        SectionTitle(Loc.T("订阅日历", "Subscribed calendars"),
            help: () => Loc.T("填一个 iCalendar 地址即可，不需要账号。苹果的节假日日历是这样一串地址，例如中国大陆是 https://calendars.icloud.com/holidays/cn_zh.ics 。订阅的内容会显示在「日程」页的右栏。",
                "Any iCalendar address works and none needs an account. Apple publishes the holiday calendars as such addresses — mainland China is https://calendars.icloud.com/holidays/cn_zh.ics — and what they hold appears in the right-hand column of the Calendar page."));
        foreach (var subscription in S.Agenda.Subscriptions.ToList()) SubscriptionEditor(subscription);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        var addSubscription = ActionButton(Loc.T("添加订阅", "Add a subscription"), () =>
        {
            Change(s => s.Agenda.Subscriptions.Add(CalendarSubscription.Holidays("cn_zh")));
            ShowPage(_page);
        });
        actions.Children.Add(addSubscription);
        // A shortcut to the address most people want, so adding the mainland holidays is one press.
        var quick = ActionButton(Loc.T("加中国大陆节假日", "Add mainland China holidays"), () =>
        {
            Change(s =>
            {
                var url = CalendarSubscription.AppleHolidayUrl("cn_zh");
                if (!s.Agenda.Subscriptions.Any(subscription => subscription.Url == url))
                    s.Agenda.Subscriptions.Add(CalendarSubscription.Holidays("cn_zh"));
            });
            ShowPage(_page);
        });
        quick.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(quick);
        Target.Children.Add(actions);
    }

    // One subscription: what it is called and where it is. The test reads it now, so a wrong address is told apart
    // from a calendar with nothing in the next month.
    private void SubscriptionEditor(CalendarSubscription subscription)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var remove = ActionButton(Loc.T("移除", "Remove"), () => { Change(s => s.Agenda.Subscriptions.RemoveAll(a => a.InstanceId == subscription.InstanceId)); ShowPage(_page); });
        remove.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(remove, Dock.Right); head.Children.Add(remove);
        var title = TextLine(subscription.DisplayName, 13, TextPrimary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title);
        panel.Children.Add(head);

        panel.Children.Add(Row(Loc.T("名称", "Name"),
            Field(subscription.Name, 200, value => EditSubscription(subscription, a => a.Name = value), 60),
            TextLine(Loc.T("这一栏在日程页右侧的标题。", "What this calendar is called in the calendar page."), 12, TextSecondary)));
        panel.Children.Add(Row(Loc.T("地址", "Address"),
            Field(subscription.Url, 340, value => EditSubscription(subscription, a => a.Url = value), 300),
            TextLine(Loc.T("iCalendar 地址，通常以 .ics 结尾。", "An iCalendar address, usually ending in .ics."), 12, TextSecondary)));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        var test = ActionButton(Loc.T("测试连接", "Test connection"), () => { });
        test.Style = (Style)FindResource("SettingsOutlineButton");
        test.Click += async (_, _) => await TestSubscriptionAsync(subscription, result, test);
        buttons.Children.Add(test);
        var save = ActionButton(Loc.T("保存", "Save"), () => { _app.FlushSettings(); result.Foreground = UI.Brush(TextSecondary); result.Text = Loc.T("已保存。", "Saved."); }, primary: true);
        save.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(save);
        buttons.Children.Add(result);
        panel.Children.Add(buttons);

        Target.Children.Add(panel);
        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
    }

    private void EditSubscription(CalendarSubscription subscription, Action<CalendarSubscription> edit) => Change(s =>
    {
        var own = s.Agenda.Subscriptions.FirstOrDefault(a => a.InstanceId == subscription.InstanceId);
        if (own is not null) edit(own);
    });

    // Reads the subscription now and says what it holds, so a wrong address is told apart from a calendar whose next
    // month happens to be empty.
    private async Task TestSubscriptionAsync(CalendarSubscription subscription, TextBlock result, Button button)
    {
        var own = S.Agenda.Subscriptions.FirstOrDefault(a => a.InstanceId == subscription.InstanceId);
        if (own is null) return;
        if (own.Url.Length == 0)
        {
            result.Foreground = UI.Brush(TextPrimary); result.Text = Loc.T("请先填写地址。", "Enter the address first."); return;
        }
        button.IsEnabled = false;
        result.Foreground = UI.Brush(TextSecondary); result.Text = Loc.T("正在读取…", "Reading…");
        var started = Environment.TickCount64;
        try
        {
            // A full year, so the test says what the feed contains rather than what the panel happens to show.
            var events = await _app.Agenda.Calendars.ReadSubscriptionAsync(own, DateTimeOffset.Now.AddYears(-1), DateTimeOffset.Now.AddYears(1), _app.Lifetime.Token);
            double seconds = (Environment.TickCount64 - started) / 1000d;
            result.Foreground = UI.Brush(TextPrimary);
            result.Text = events.Count > 0
                ? Loc.T($"读取成功：一年内有 {events.Count} 个日程（{seconds:0.0} 秒）", $"Read: {events.Count} events within a year ({seconds:0.0} s)")
                : Loc.T($"读取成功，但这份日历是空的（{seconds:0.0} 秒）", $"Read, but the calendar is empty ({seconds:0.0} s)");
        }
        catch (OperationCanceledException) { result.Text = ""; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Foreground = UI.Brush(TextPrimary);
            result.Text = Loc.T($"读取失败：{ex.Message}", $"Failed: {ex.Message}");
        }
        finally { button.IsEnabled = true; }
    }

    private void CalendarEditor(CalendarAccount account)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var remove = ActionButton(Loc.T("移除", "Remove"), () => { Change(s => s.Agenda.Accounts.RemoveAll(a => a.InstanceId == account.InstanceId)); ShowPage(_page); });
        remove.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(remove, Dock.Right); head.Children.Add(remove);
        var title = TextLine(CalendarName(account.Source), 13, TextPrimary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title);
        panel.Children.Add(head);

        panel.Children.Add(Row(Loc.T("账号", "Account"),
            Field(account.User, 220, value => EditCalendar(account, a => a.User = value), 120),
            TextLine(Loc.T("平台在日历同步设置里给出的用户名。", "The user name the platform's calendar-sync settings give."), 12, TextSecondary)));
        panel.Children.Add(Row(Loc.T("密码", "Password"),
            CalendarSecret(account),
            TextLine(Loc.T("同一页给出的密码，只保存在本机。", "The password from the same page, kept on this machine only."), 12, TextSecondary)));
        // The server is a field for every kind, not only for WeCom: the platform states it next to the account and
        // password ("服务器地址: caldav.feishu.cn"), and a reader who changes it here should not have to guess that
        // only one kind of account takes one.
        panel.Children.Add(Row(Loc.T("服务器地址", "Server address"),
            Field(account.Server.Length > 0 ? account.Server : DefaultServer(account.Source), 260, value => EditCalendar(account, a => a.Server = value), 200),
            TextLine(Loc.T($"留空用 {DefaultServer(account.Source)}", $"Empty uses {DefaultServer(account.Source)}"), 12, TextSecondary)));

        // The two buttons: one asks the server now, the other keeps what is on the page. Both are here because a
        // calendar that shows nothing is either a wrong account or a page that was never saved, and those are told
        // apart by trying it.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        var test = ActionButton(Loc.T("测试连接", "Test connection"), () => { });
        test.Style = (Style)FindResource("SettingsOutlineButton");
        test.Click += async (_, _) => await TestCalendarAsync(account, result, test);
        actions.Children.Add(test);
        var save = ActionButton(Loc.T("保存", "Save"), () => { _app.FlushSettings(); result.Foreground = UI.Brush(TextSecondary); result.Text = Loc.T("已保存。", "Saved."); }, primary: true);
        save.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(save);
        actions.Children.Add(result);
        panel.Children.Add(actions);

        Target.Children.Add(panel);
        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
    }

    // The address a kind of calendar is reached at by default, which is what the platform publishes.
    private static string DefaultServer(CalendarSource source) => source switch
    {
        CalendarSource.Feishu => "caldav.feishu.cn",
        CalendarSource.DingTalk => "calendar.dingtalk.com",
        CalendarSource.WeCom => "https://wecom.work",
        _ => ""
    };

    // Asks the calendar's server now, so a wrong account or an unreachable host is told apart from an empty
    // calendar. The answer is the number of meetings found in the next two days, or the server's own reason.
    private async Task TestCalendarAsync(CalendarAccount account, TextBlock result, Button button)
    {
        var own = S.Agenda.Accounts.FirstOrDefault(a => a.InstanceId == account.InstanceId);
        if (own is null) return;
        var password = _app.Secrets.Read(own.InstanceId);
        if (password.Length == 0)
        {
            result.Foreground = UI.Brush(TextPrimary); result.Text = Loc.T("请先填写密码。", "Enter the password first."); return;
        }
        button.IsEnabled = false;
        result.Foreground = UI.Brush(TextSecondary); result.Text = Loc.T("正在连接…", "Connecting…");
        var started = Environment.TickCount64;
        try
        {
            var from = DateTimeOffset.Now.AddDays(-1);
            var to = DateTimeOffset.Now.AddDays(2);
            IReadOnlyList<CalendarEvent> events;
            if (own.Source == CalendarSource.WeCom)
                events = await _app.Agenda.Exchange.ReadAsync(own, password, own.Server, from, to, _app.Lifetime.Token);
            else
            {
                var href = await _app.Agenda.Calendars.DiscoverAsync(own, password, _app.Lifetime.Token);
                events = await _app.Agenda.Calendars.ReadAsync(own, password, href, from, to, _app.Lifetime.Token);
                // The collection that was found is kept, so the next read does not have to search again.
                EditCalendar(account, a => a.Href = href);
            }
            double seconds = (Environment.TickCount64 - started) / 1000d;
            result.Foreground = UI.Brush(TextPrimary);
            result.Text = events.Count > 0
                ? Loc.T($"连接成功：最近三天有 {events.Count} 个日程（{seconds:0.0} 秒）", $"Connected: {events.Count} meetings in the next three days ({seconds:0.0} s)")
                : Loc.T($"连接成功，但最近三天没有日程（{seconds:0.0} 秒）", $"Connected, but no meetings in the next three days ({seconds:0.0} s)");
        }
        catch (OperationCanceledException) { result.Text = ""; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Foreground = UI.Brush(TextPrimary);
            result.Text = Loc.T($"连接失败：{ex.Message}", $"Failed: {ex.Message}");
        }
        finally { button.IsEnabled = true; }
    }

    // The password of a calendar account, in a frame with a hint, as the key field has.
    private FrameworkElement CalendarSecret(CalendarAccount account)
    {
        bool has = _app.Secrets.Read(account.InstanceId).Length > 0;
        var box = new PasswordBox { Style = (Style)FindResource("SettingsSecret"), Width = 220, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, Loc.T("日历密码", "Calendar password"));
        var hint = TextLine(Loc.T("粘贴密码", "Paste the password"), 12, TextTertiary);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false;
        var stack = new Grid { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(hint); stack.Children.Add(box);
        var frame = new Border
        {
            Child = stack, Height = 35, CornerRadius = new CornerRadius(6), Background = UI.Brush(_palette.Input),
            BorderBrush = UI.Brush(_palette.InputBorder), BorderThickness = new Thickness(1), Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (has) { box.Password = "********"; hint.Visibility = Visibility.Collapsed; }
        box.PasswordChanged += (_, _) => hint.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FieldFocus(frame, box, () => UI.Brush(_palette.InputBorder));
        void Commit()
        {
            if (box.Password == "********") return;
            try { _app.Secrets.Write(account.InstanceId, box.Password.Trim()); SaveStatus(""); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            { SaveStatus(Loc.T("密码文件无法写入。", "The password file could not be written."), true); }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter) { Commit(); System.Windows.Input.Keyboard.ClearFocus(); } };
        return frame;
    }

    private void EditCalendar(CalendarAccount account, Action<CalendarAccount> edit) => Change(s =>
    {
        var own = s.Agenda.Accounts.FirstOrDefault(a => a.InstanceId == account.InstanceId);
        if (own is not null) edit(own);
    });

    private void AddCalendar()
    {
        Change(s => s.Agenda.Accounts.Add(new CalendarAccount { Source = CalendarSource.Feishu }));
        ShowPage(_page);
    }

    private static string CalendarName(CalendarSource source) => source switch
    {
        CalendarSource.Feishu => Loc.T("飞书日历", "Feishu calendar"),
        CalendarSource.DingTalk => Loc.T("钉钉日历", "DingTalk calendar"),
        CalendarSource.WeCom => Loc.T("企业微信日历", "WeCom calendar"),
        _ => Loc.T("日历", "Calendar")
    };

    // The mailboxes. The password is the authorization code the mailbox issued, not the account's own password.
    private void MailSettings()
    {
        SectionTitle(Loc.T("邮箱", "Mailboxes"),
            help: () => Loc.T("通过 IMAP 读取，用平台给出的授权码而不是账号密码。面板只读收件箱，不会把邮件标记为已读，也不会下载附件。",
                "Read over IMAP with the authorization code the service issued, not the account's own password. The panel reads only the inbox, never marks a mail as read, and never downloads an attachment."));
        if (S.Agenda.MailAccounts.Count == 0)
            Target.Children.Add(new Border
            {
                Child = TextLine(Loc.T("还没有邮箱。添加一个后，新邮件会出现在「邮件」页。",
                    "No mailbox yet. Add one and new mail appears on the Mail page."), 12, TextSecondary),
                Padding = new Thickness(0, 0, 0, 16)
            });
        foreach (var account in S.Agenda.MailAccounts.ToList()) MailboxEditor(account);
        var add = ActionButton(Loc.T("添加邮箱", "Add a mailbox"), AddMailbox);
        add.Margin = new Thickness(0, 8, 0, 28);
        Target.Children.Add(add);
    }

    private void MailboxEditor(MailboxAccount account)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var remove = ActionButton(Loc.T("移除", "Remove"), () => { Change(s => s.Agenda.MailAccounts.RemoveAll(a => a.InstanceId == account.InstanceId)); ShowPage(_page); });
        remove.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(remove, Dock.Right); head.Children.Add(remove);
        var title = TextLine(account.DisplayName.Length > 0 ? account.DisplayName : account.Provider.ToString(), 13, TextPrimary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title);
        panel.Children.Add(head);

        panel.Children.Add(Row(Loc.T("邮箱地址", "Address"),
            Field(account.Email, 220, value => EditMailbox(account, a => a.Email = value), 120)));
        panel.Children.Add(Row(Loc.T("服务商", "Service"),
            MailboxProvider(account),
            TextLine(MailReader.Hint(account.Provider), 12, TextSecondary)));
        panel.Children.Add(Row(Loc.T("授权码", "Authorization code"),
            MailboxSecret(account),
            TextLine(Loc.T("保存在本机，只有你的 Windows 账户能解开。", "Kept on this machine, readable only by your Windows account."), 12, TextSecondary)));

        // The same pair of buttons as a calendar's: one asks the mailbox now, the other keeps what is on the page.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        var test = ActionButton(Loc.T("测试连接", "Test connection"), () => { });
        test.Style = (Style)FindResource("SettingsOutlineButton");
        test.Click += async (_, _) => await TestMailboxAsync(account, result, test);
        actions.Children.Add(test);
        var save = ActionButton(Loc.T("保存", "Save"), () => { _app.FlushSettings(); result.Foreground = UI.Brush(TextSecondary); result.Text = Loc.T("已保存。", "Saved."); }, primary: true);
        save.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(save);
        actions.Children.Add(result);
        panel.Children.Add(actions);

        Target.Children.Add(panel);
        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
    }

    // Signs in to the mailbox and reads its newest message, which is what tells a wrong authorization code from an
    // empty inbox. Nothing is marked as read.
    private async Task TestMailboxAsync(MailboxAccount account, TextBlock result, Button button)
    {
        var own = S.Agenda.MailAccounts.FirstOrDefault(a => a.InstanceId == account.InstanceId);
        if (own is null) return;
        if (own.Email.Length == 0)
        {
            result.Foreground = UI.Brush(TextPrimary); result.Text = Loc.T("请先填写邮箱地址。", "Enter the address first."); return;
        }
        var code = _app.Secrets.Read(own.InstanceId);
        if (code.Length == 0)
        {
            result.Foreground = UI.Brush(TextPrimary); result.Text = Loc.T("请先填写授权码。", "Enter the authorization code first."); return;
        }
        button.IsEnabled = false;
        result.Foreground = UI.Brush(TextSecondary); result.Text = Loc.T("正在连接…", "Connecting…");
        var started = Environment.TickCount64;
        try
        {
            var reader = new MailReader.Account(own.InstanceId, own.Email, own.Provider, own.Host, own.Port, own.User);
            // Zero reads the newest messages, which is what a test wants to see: whether the connection and the
            // message read work, not whether anything is new since the last poll.
            var read = await _app.Agenda.Reader.ReadAsync(reader, 0u, 5, _app.Lifetime.Token);
            double seconds = (Environment.TickCount64 - started) / 1000d;
            result.Foreground = UI.Brush(TextPrimary);
            result.Text = read.Items.Count > 0
                ? Loc.T($"连接成功：读到 {read.Items.Count} 封邮件（{seconds:0.0} 秒）", $"Connected: {read.Items.Count} messages read ({seconds:0.0} s)")
                : Loc.T($"连接成功，但收件箱是空的（{seconds:0.0} 秒）", $"Connected, but the inbox is empty ({seconds:0.0} s)");
        }
        catch (OperationCanceledException) { result.Text = ""; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Foreground = UI.Brush(TextPrimary);
            result.Text = Loc.T($"连接失败：{ex.Message}", $"Failed: {ex.Message}");
        }
        finally { button.IsEnabled = true; }
    }

    private FrameworkElement MailboxProvider(MailboxAccount account)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        var names = new[] { "QQ", "163", "126", Loc.T("企业邮", "Exmail"), "iCloud", "Gmail", Loc.T("自定义", "Custom") };
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var choice = new RadioButton
            {
                Content = names[i], GroupName = $"mailbox-{account.InstanceId}", IsChecked = (int)account.Provider == index,
                Style = (Style)FindResource("SettingsSegment"), VerticalAlignment = VerticalAlignment.Center
            };
            choice.Checked += (_, _) => EditMailbox(account, a => a.Provider = (MailReader.Provider)index);
            strip.Children.Add(choice);
        }
        group.Children.Add(new Border
        {
            Child = strip, Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.InputBorder),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(2)
        });
        return group;
    }

    private FrameworkElement MailboxSecret(MailboxAccount account)
    {
        bool has = _app.Secrets.Read(account.InstanceId).Length > 0;
        var box = new PasswordBox { Style = (Style)FindResource("SettingsSecret"), Width = 220, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, Loc.T("授权码", "Authorization code"));
        var hint = TextLine(Loc.T("粘贴授权码", "Paste the code"), 12, TextTertiary);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false;
        var stack = new Grid { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(hint); stack.Children.Add(box);
        var frame = new Border
        {
            Child = stack, Height = 35, CornerRadius = new CornerRadius(6), Background = UI.Brush(_palette.Input),
            BorderBrush = UI.Brush(_palette.InputBorder), BorderThickness = new Thickness(1), Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (has) { box.Password = "********"; hint.Visibility = Visibility.Collapsed; }
        box.PasswordChanged += (_, _) => hint.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FieldFocus(frame, box, () => UI.Brush(_palette.InputBorder));
        void Commit()
        {
            if (box.Password == "********") return;
            try { _app.Secrets.Write(account.InstanceId, box.Password.Trim()); SaveStatus(""); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            { SaveStatus(Loc.T("密码文件无法写入。", "The password file could not be written."), true); }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter) { Commit(); System.Windows.Input.Keyboard.ClearFocus(); } };
        return frame;
    }

    private void EditMailbox(MailboxAccount account, Action<MailboxAccount> edit) => Change(s =>
    {
        var own = s.Agenda.MailAccounts.FirstOrDefault(a => a.InstanceId == account.InstanceId);
        if (own is not null) edit(own);
    });

    private void AddMailbox()
    {
        Change(s => s.Agenda.MailAccounts.Add(new MailboxAccount { Provider = MailReader.Provider.Qq }));
        ShowPage(_page);
    }

    private void AddKey()
    {
        Change(s => s.KeyResets.Add(new KeyReset { Name = Loc.T("新密钥", "New key"), Anchor = DateTimeOffset.Now }));
        ShowPage(_page);
    }

    // One key's editor: what it is called, where to probe, and the countdown that is the point of the page.
    private void KeyEditor(KeyReset key)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var remove = ActionButton(Loc.T("移除", "Remove"), () => { Change(s => s.KeyResets.RemoveAll(k => k.InstanceId == key.InstanceId)); ShowPage(_page); });
        remove.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(remove, Dock.Right); head.Children.Add(remove);
        var title = TextLine(key.DisplayName, 13, TextPrimary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title);
        panel.Children.Add(head);

        panel.Children.Add(Row(Loc.T("名称", "Name"),
            Field(key.Name, 180, value => EditKey(key, k => k.Name = value), 64),
            TextLine(Loc.T("这一行在岛里显示的名字。", "What this row is called in the island."), 12, TextSecondary)));

        panel.Children.Add(Row(Loc.T("密钥", "Key"),
            SecretField(key),
            TextLine(Loc.T("保存在本机，只有你的 Windows 账户能解开；不会写进设置文件，也不会随设置一起同步。",
                "Kept on this PC, readable only by your Windows account, and never written into the settings file or carried along with it."), 12, TextSecondary)));

        panel.Children.Add(Row(Loc.T("服务", "Service"),
            ServiceChoice(key),
            TextLine(Loc.T("决定怎么读重置时间：Anthropic 在响应头里给绝对时间，OpenAI 系给时长。",
                "Decides how the reset is read: Anthropic states an instant in its headers, the OpenAI family a duration."), 12, TextSecondary)));

        // An OpenAI-compatible gateway has no address the panel could guess, and refuses a model it no longer serves, so
        // both are asked for. They are hidden for the two services whose address is known; the page states that instead of
        // showing two boxes that do nothing.
        if (key.Probe == KeyProbeKind.OpenAICompatible)
        {
            panel.Children.Add(Row(Loc.T("接口地址", "Endpoint"),
                Field(key.Endpoint, 300, value => EditKey(key, k => k.Endpoint = value), 300),
                TextLine(Loc.T("完整地址，例如 https://api.minimaxi.com/v1/chat/completions",
                    "The full address, e.g. https://api.minimaxi.com/v1/chat/completions"), 12, TextSecondary)));
            panel.Children.Add(Row(Loc.T("模型", "Model"),
                Field(key.Model, 200, value => EditKey(key, k => k.Model = value), 120),
                TextLine(Loc.T("这个服务当前可用的模型名。名称被下线时会在这里报错，换一个即可。",
                    "A model name this service currently serves. When one is retired the test says so here; use another."), 12, TextSecondary)));
        }

        // The interval is shown in hours, because that is the unit this feature works in: the probe runs hourly and the
        // service's own window is hours. It stays editable: a probe that measures the gap overwrites it, but until one has
        // measured it (or when probing is off) this is the number the countdown uses, so it has to be settable.
        panel.Children.Add(Row(Loc.T("重置间隔", "Reset interval"),
            IntervalField(key),
            TextLine(key.ProbedAt is not null && key.PreviousResetAt is not null
                ? Loc.T("已由查询实测得出。修改后会在下一次实测前生效；实测到新的间隔会再次覆盖。",
                    "Measured by a probe. A change here holds until the next measurement overwrites it.")
                : Loc.T("第一次查询之前用这个值，之后由每小时的查询实测覆盖。",
                    "Used until the first probe measures the real gap, after which the hourly probe overwrites it."), 12, TextSecondary)));

        // What the key knows right now: the countdown, and when it was last asked. This is what the page exists for.
        // The block is rebuilt after a test, so it always states the values that were just saved.
        var status = KeyStatus(key);
        panel.Children.Add(Row(Loc.T("测试连接", "Test connection"),
            TestButton(key, status),
            TextLine(Loc.T("立即用这个密钥查询一次。成功后会立刻保存结果，并刷新上面的重置时间，不必等到下一个整点。",
                "Asks the service once, now, with this key. On success the answer is saved and the reset time above is refreshed, without waiting for the next hour."), 12, TextSecondary)));
        panel.Children.Add(status);

        panel.Children.Add(Row(Loc.T("每小时查询", "Probe every hour"),
            Toggle("", key.ProbeEnabled, on => EditKey(key, k => k.ProbeEnabled = on), keepPreview: true),
            TextLine(Loc.T("关掉后不再发请求，只按上一次学到的间隔继续倒数。",
                "Off, no request is sent and the countdown continues from the interval last learned."), 12, TextSecondary)));

        Target.Children.Add(panel);
        Target.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
    }

    // Edits one key in place and saves, so a probe that finishes at the same moment cannot overwrite the edit.
    private void EditKey(KeyReset key, Action<KeyReset> edit) => Change(s =>
    {
        var own = s.KeyResets.FirstOrDefault(k => k.InstanceId == key.InstanceId);
        if (own is not null) edit(own);
    });

    // A single-line field in the framed style the settings window already uses for text input. SettingsField draws its own
    // frame and corner radius, so the input is visible without a wrapper; SettingsNumber, which draws none, would have made
    // the field read as empty space.
    private FrameworkElement Field(string text, double width, Action<string> commit, int maxLength)
    {
        var input = new TextBox { Style = (Style)FindResource("SettingsField"), Text = text, Width = width, MaxLength = maxLength, VerticalAlignment = VerticalAlignment.Center };
        input.LostFocus += (_, _) => commit(input.Text);
        input.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter) { commit(input.Text); System.Windows.Input.Keyboard.ClearFocus(); } };
        return input;
    }

    // The reset interval in hours, with steppers for the common adjustment and a field for anything else.
    private FrameworkElement IntervalField(KeyReset key)
    {
        string Hours(int minutes) => (minutes / 60d).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        var input = new TextBox
        {
            Style = (Style)FindResource("SettingsField"), Width = 58, VerticalAlignment = VerticalAlignment.Center,
            Text = Hours(key.IntervalMinutes), TextAlignment = TextAlignment.Right
        };
        AutomationProperties.SetName(input, Loc.T("重置间隔小时数", "Reset interval in hours"));
        void Commit()
        {
            // A value that is not a positive number keeps what was there rather than becoming zero.
            if (!double.TryParse(input.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double value) || value <= 0)
            {
                input.Text = Hours(key.IntervalMinutes);
                return;
            }
            int minutes = (int)Math.Clamp(Math.Round(value * 60), 1, 527040);
            EditKey(key, k => k.IntervalMinutes = minutes);
            input.Text = Hours(minutes);
        }
        input.LostFocus += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter) { Commit(); System.Windows.Input.Keyboard.ClearFocus(); } };
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(input);
        var unit = TextLine(Loc.T("小时", "h"), 11, TextTertiary);
        unit.Margin = new Thickness(6, 0, 8, 0); unit.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(unit);
        var minus = new RepeatButton { Style = (Style)FindResource("SettingsStepper"), Content = "−" };
        var plus = new RepeatButton { Style = (Style)FindResource("SettingsStepper"), Content = "+" };
        AutomationProperties.SetName(minus, Loc.T("减少一小时", "One hour less"));
        AutomationProperties.SetName(plus, Loc.T("增加一小时", "One hour more"));
        // A step of an hour, clamped at the schedule's own limits, and put straight back into the field.
        void Step(int hours)
        {
            int minutes = (int)Math.Clamp(key.IntervalMinutes + hours * 60L, 1, 527040);
            EditKey(key, k => k.IntervalMinutes = minutes);
            input.Text = Hours(minutes);
        }
        minus.Click += (_, _) => Step(-1);
        plus.Click += (_, _) => Step(1);
        line.Children.Add(minus); line.Children.Add(plus);
        return line;
    }

    // The key itself. A PasswordBox, so it is not left on screen; a stored key shows as a placeholder rather than being
    // read back into the field, because there is no reason for it to be displayed again.
    //
    // PasswordBox has no framed style of its own to borrow (SettingsField is a TextBox style, and WPF refuses a style whose
    // target type differs), so the frame is a Border drawn here. That frame is what makes the field visible at all.
    private FrameworkElement SecretField(KeyReset key)
    {
        bool has = _app.Secrets.Read(key.InstanceId).Length > 0;
        var box = new PasswordBox { Style = (Style)FindResource("SettingsSecret"), Width = 220, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, Loc.T("密钥", "Key"));
        var frame = new Border
        {
            Height = 35, CornerRadius = new CornerRadius(6), Background = UI.Brush(_palette.Input),
            BorderBrush = UI.Brush(_palette.InputBorder), BorderThickness = new Thickness(1), Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        // Empty: the frame holds the field and a hint behind it, so the box reads as a place to type and says what
        // belongs there. The hint goes as soon as the field has anything in it.
        var hint = TextLine(Loc.T("粘贴 API key", "Paste the API key"), 12, TextTertiary);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false;
        var stack = new Grid { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(hint);
        stack.Children.Add(box);
        frame.Child = stack;
        if (has) { box.Password = "********"; hint.Visibility = Visibility.Collapsed; }
        box.PasswordChanged += (_, _) => hint.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FieldFocus(frame, box, () => UI.Brush(_palette.InputBorder));
        // Changing it writes through the same DPAPI store the provider keys use, under this row's own id.
        void Commit()
        {
            var text = box.Password;
            if (text == "********") return;
            try
            {
                _app.Secrets.Write(key.InstanceId, text.Trim());
                // A new key is worth asking about at once rather than at the next hour.
                EditKey(key, k => k.ProbedAt = null);
                SaveStatus("");
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                SaveStatus(Loc.T("密钥文件无法写入。", "The key file could not be written."), true);
            }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter) { Commit(); System.Windows.Input.Keyboard.ClearFocus(); } };
        return frame;
    }

    // The countdown and the probe's last word. Rebuilt in place after a test, so it always states what was just saved.
    private FrameworkElement KeyStatus(KeyReset key)
    {
        var next = key.NextReset(DateTimeOffset.Now);
        var panel = new StackPanel
        {
            Children =
            {
                TextLine(next is { } at
                    ? Loc.T($"下次重置 {at.LocalDateTime:MM-dd HH:mm}（{UI.ShortReset(at)}）", $"Next reset {at.LocalDateTime:MM-dd HH:mm} ({UI.ShortReset(at)})")
                    : Loc.T("还没有可用的重置时间", "No reset time yet"), 12, TextPrimary, FontWeights.SemiBold),
                TextLine(key.ProbedAt is { } probed
                    ? Loc.T($"上次查询 {probed.LocalDateTime:MM-dd HH:mm}", $"Last probe {probed.LocalDateTime:MM-dd HH:mm}")
                      + (key.ProbeNote.Length > 0 ? " · " + key.ProbeNote : "")
                    : Loc.T("还没有查询过：启动时会立刻查一次，之后每小时一次。", "Not probed yet: it runs once at start and then hourly."), 12, TextSecondary),
                // The interval in words, so a value the probe measured can be told from the default at a glance.
                TextLine(Loc.T($"间隔 {key.IntervalText}", $"Interval {key.IntervalText}"), 11.5, TextTertiary)
            }
        };
        panel.Margin = new Thickness(0, 6, 0, 0);
        return panel;
    }

    // Tests the key now, and on success saves the answer and refreshes the status block the user is looking at.
    private FrameworkElement TestButton(KeyReset key, FrameworkElement status)
    {
        var test = ActionButton(Loc.T("测试连接", "Test connection"), () => { });
        test.Style = (Style)FindResource("SettingsOutlineButton");
        test.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(test, "test-key-" + key.InstanceId.ToString("N"));
        // The result of the last test, under the button; it survives until the page is rebuilt or another test runs.
        var outcome = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var result = new StackPanel { Children = { outcome }, Visibility = Visibility.Collapsed };

        test.Click += async (_, _) =>
        {
            // The row as it is saved now: a test must ask about the key the user just typed, not the one from before.
            if (S.KeyResets.FirstOrDefault(k => k.InstanceId == key.InstanceId) is not { } current) return;
            if (_app.Secrets.Read(current.InstanceId).Length == 0)
            {
                result.Visibility = Visibility.Visible; outcome.Children.Clear();
                outcome.Children.Add(TextLine(Loc.T("请先填写密钥。", "Enter a key first."), 12, TextPrimary, FontWeights.SemiBold));
                return;
            }
            test.IsEnabled = false; test.Content = Loc.T("正在测试…", "Testing…");
            outcome.Children.Clear(); result.Visibility = Visibility.Collapsed;
            var started = Environment.TickCount64;
            KeyProbeResult answer;
            try { answer = await _app.Prober.TestAsync(current, _app.Lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { answer = KeyProbeResult.Failed(ex.GetType().Name); }
            finally { test.Content = Loc.T("测试连接", "Test connection"); test.IsEnabled = true; }
            double seconds = (Environment.TickCount64 - started) / 1000d;

            // Saving is the part that makes the test useful: the answer becomes the row's reset time, so the countdown
            // and the panel strip change at once and the next hourly probe starts from what was just learned.
            var now = DateTimeOffset.Now;
            EditKey(current, k => { var updated = k.WithProbe(answer, now); k.Anchor = updated.Anchor; k.IntervalMinutes = updated.IntervalMinutes; k.ProbedResetAt = updated.ProbedResetAt; k.ProbedAt = updated.ProbedAt; k.ProbedWindow = updated.ProbedWindow; k.ProbeNote = updated.ProbeNote; k.PreviousResetAt = updated.PreviousResetAt; });

            result.Visibility = Visibility.Visible;
            outcome.Children.Clear();
            // The headline states the verdict; the lines under it say what the service answered and what was learned.
            // A key that reaches the service but learns no reset time is a success: the credential is right, and the
            // countdown falls back to the interval. Saying "failed" there would send the user looking for a key problem
            // that does not exist.
            string verdict = answer.Ok
                ? answer.ResetAt is { } at
                    ? Loc.T($"连接成功 · 下次重置 {at.LocalDateTime:MM-dd HH:mm}（{UI.ShortReset(at)}）", $"Connected · next reset {at.LocalDateTime:MM-dd HH:mm} ({UI.ShortReset(at)})")
                    : Loc.T("连接成功（该服务不提供重置时间）", "Connected (this service reports no reset time)")
                : Loc.T("连接失败", "Failed");
            outcome.Children.Add(TextLine(verdict + Loc.T($" · {seconds:0.0} 秒", $" · {seconds:0.0} s"), 12, TextPrimary, FontWeights.SemiBold));
            var lines = new List<string>();
            if (answer.StatusText.Length > 0) lines.Add(answer.StatusText + (answer.Window.Length > 0 ? Loc.T($" · {answer.Window}", $" · {answer.Window}") : ""));
            if (answer.Note.Length > 0) lines.Add(answer.Note);
            // What was saved, in the terms the page uses: the reset time when the service gave one, and always the
            // interval the countdown will follow.
            if (answer.Ok && S.KeyResets.FirstOrDefault(k => k.InstanceId == key.InstanceId) is { } saved)
            {
                lines.Add(answer.ResetAt is null
                    ? Loc.T($"已保存：按学到的间隔 {saved.IntervalText} 倒数", $"Saved: counting by the learned interval {saved.IntervalText}")
                    : Loc.T($"已保存：间隔 {saved.IntervalText}", $"Saved: interval {saved.IntervalText}"));
            }
            if (lines.Count > 0)
            {
                var detail = TextLine(string.Join("\n", lines), 11.5, TextSecondary);
                detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 3, 0, 0);
                outcome.Children.Add(detail);
            }
            // The status block above is replaced rather than edited, so it shows the numbers just saved.
            if (status.Parent is Panel host)
            {
                int index = host.Children.IndexOf(status);
                if (index >= 0 && S.KeyResets.FirstOrDefault(k => k.InstanceId == key.InstanceId) is { } refreshed)
                {
                    var replacement = KeyStatus(refreshed);
                    host.Children.RemoveAt(index);
                    host.Children.Insert(index, replacement);
                    status = replacement;
                }
            }
        };
        return new StackPanel { Children = { test, result } };
    }

    // The size controls for the keys page, built into the page rows by SettingsWindow's Pages(). Kept beside the other
    // key settings rather than in the expanded-appearance file, because this page belongs to the keys.
    private void KeysSizeControls()
    {
        SizeControls(S.KeysWidth, S.KeysHeight, (w, h) => Resize(s => { s.KeysWidth = w; s.KeysHeight = h; }, DeckPage.Keys));
    }

    // The size controls for the calendar and the mail pages.
    private void CalendarSizeControls()
    {
        SizeControls(S.CalendarWidth, S.CalendarHeight, (w, h) => Resize(s => { s.CalendarWidth = w; s.CalendarHeight = h; }, DeckPage.Calendar));
    }

    private void MailSizeControls()
    {
        SizeControls(S.MailWidth, S.MailHeight, (w, h) => Resize(s => { s.MailWidth = w; s.MailHeight = h; }, DeckPage.Mail));
    }

    // A page's width and height, in the same shape the other pages use.
    private void SizeControls(double width, double height, Action<double, double> apply)
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        dimensions.ColumnDefinitions.Add(new ColumnDefinition()); dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        var w = Dimension(Loc.T("宽度", "Width"), width, DeckPages.MinWidth, DeckPages.MaxWidth, value => apply(value, height));
        w.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(w);
        var h = Dimension(Loc.T("高度", "Height"), height, DeckPages.MinHeight, DeckPages.MaxHeight, value => apply(width, value));
        h.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(h, 1); dimensions.Children.Add(h);
        Target.Children.Add(dimensions);
    }

    // Anthropic, OpenAI, or anything OpenAI-compatible. SettingsSegment is the segmented control used elsewhere in this
    // window: a flat radio that fills its selected option. SettingsChoice, which was tried first, is a *card* style and
    // adds its own check badge under the content, which on a one-line option floats as a stray tick.
    private FrameworkElement ServiceChoice(KeyReset key)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var names = new[] { "Anthropic", "OpenAI", Loc.T("OpenAI 兼容", "OpenAI compatible") };
        int current = key.Probe switch { KeyProbeKind.Anthropic => 0, KeyProbeKind.OpenAI => 1, _ => 2 };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var choice = new RadioButton
            {
                Content = names[i], GroupName = $"keyprobe-{key.InstanceId}", IsChecked = current == index,
                Style = (Style)FindResource("SettingsSegment"), VerticalAlignment = VerticalAlignment.Center
            };
            choice.Checked += (_, _) => EditKey(key, k => k.Probe = index switch { 0 => KeyProbeKind.Anthropic, 1 => KeyProbeKind.OpenAI, _ => KeyProbeKind.OpenAICompatible });
            strip.Children.Add(choice);
        }
        group.Children.Add(new Border
        {
            Child = strip, Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.InputBorder),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(2)
        });
        return group;
    }
}
