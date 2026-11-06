using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The calendar and the mail, as pages of the open panel.
//
// The calendar is a month with the next few meetings beside it: the month is the shape a person pictures a month in,
// and the subscriptions — the public holidays — are marked on the days they fall, because a holiday is a fact about a
// date rather than a line in a list. The work calendar is the other way round: its meetings are read forward, one
// after another, so the column beside the month holds the next few and nothing else.
//
// The mail is a list, because it is something the user reads down: a mail has a sender and a subject, and the eye
// goes down the column.
public partial class MainWindow
{
    // The two columns of the calendar page: the month is seven day cells across, and the column beside it is wide
    // enough for a time and a title. The widths are constants, not content sizes, so pressing a day changes what the
    // column holds and never where the page is.
    //
    // The sum is what the page is wide by default (520), so the month and the column fit without being cut off. The
    // month takes the day grid plus a little room for the arrows beside the title; the rest is the column.
    private const double MonthWidth = 7 * 35 + 12;
    private const double ListWidth = 520 - MonthWidth;
    // The space between the month and the column, so the two do not touch.
    private const double ListGap = 14;

    private void RenderCalendar()
    {
        if (CalendarContent is null) return;
        ShowOnly(CalendarContent);
        CalendarContent.Children.Clear();

        var now = DateTimeOffset.Now;
        bool configured = Settings.Agenda.Accounts.Count > 0 || Settings.Agenda.Subscriptions.Count > 0;
        if (!configured)
        {
            CalendarContent.Children.Add(SetupNote(Loc.T("还没有添加日历", "No calendar yet"),
                Loc.T("在设置里添加一个工作日历或订阅节假日，日程会显示在这里。", "Add a work calendar or a holiday subscription in the settings; its meetings show here.")));
            return;
        }

        // The two columns are fixed widths, and the page's own width is their sum. A right-hand column sized by its
        // content moved the whole page sideways whenever a day's meetings were a different length, so the month
        // visibly jumped between two places as days were pressed. The widths are constants now and only what the
        // column holds changes.
        var columns = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Width = MonthWidth + ListWidth };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MonthWidth) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ListWidth) });

        // The month, with the subscriptions marked on their days. It reads its own days from everything the panel
        // holds, not only from what is upcoming: a holiday earlier in the month is still on this month's page.
        var month = MonthView(ShownMonth(), _app.Agenda.Events);
        Grid.SetColumn(month, 0); columns.Children.Add(month);

        // The column beside it shows the day that was picked, and the upcoming meetings when none was. A picked day is
        // the one thing the month cannot answer on its own: a dot says a day has meetings, not which.
        var picked = _pickedDay is { } day ? day : (DateOnly?)null;
        var shown = ShownMonth();
        var rows = AgendaService.CalendarColumn(_app.Agenda.Events, picked, shown, now);
        int keep = Math.Clamp(Settings.Agenda.ListRows, 1, 12);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Width = ListWidth - ListGap, Margin = new Thickness(ListGap, 4, 0, 0) };
        // The heading names the day when one is picked, and can be pressed to go back to the upcoming list: a page
        // that has been sent to a day is a page that has to be able to come back.
        right.Children.Add(picked is { } shownDay
            ? PickedHeading(shownDay)
            : AgendaHeading(Loc.T($"接下来 · {shown.Month}月", $"Coming up · {shown:MMM}")));
        if (rows.Count == 0)
        {
            var empty = UI.Fixed(_app.Agenda.CalendarError.Length > 0
                ? _app.Agenda.CalendarError
                : picked is not null
                    ? Loc.T("这一天没有日程", "Nothing on this day")
                    : Loc.T($"未来 {AgendaService.CalendarHorizon.Days} 天没有工作日程", $"No meetings in the next {AgendaService.CalendarHorizon.Days} days"), 11, 15, UI.Tertiary);
            empty.TextWrapping = TextWrapping.Wrap;
            right.Children.Add(empty);
        }
        else
        {
            foreach (var meeting in rows.Take(keep)) right.Children.Add(MeetingRow(meeting, now));
            if (rows.Count > keep)
            {
                var more = UI.Fixed(Loc.T($"还有 {rows.Count - keep} 个", $"{rows.Count - keep} more"), 10, 14, UI.Tertiary);
                more.Margin = new Thickness(0, 5, 0, 0);
                right.Children.Add(more);
            }
        }
        // The blank space around the column is a way back to the whole list: a reader who has been sent to a day and
        // then pressed the empty part beside its meetings is asking for everything again. The background sits behind
        // the rows, so a row's own press still wins, and it only answers while a day is picked — with nothing picked
        // there is nothing to come back from.
        var column = new Grid();
        if (picked is not null)
        {
            var slack = new Border
            {
                Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
            };
            slack.MouseLeftButtonUp += (_, _) => { _pickedDay = null; RenderUsage(); };
            AutomationProperties.SetName(slack, Loc.T("显示全部日程", "Show every meeting"));
            column.Children.Add(slack);
        }
        column.Children.Add(right);
        Grid.SetColumn(column, 1); columns.Children.Add(column);

        CalendarContent.Children.Add(columns);
    }

    // The day that was picked on the month, kept while the panel is open. Null is the ordinary state: the column
    // beside the month shows what is coming up.
    private DateOnly? _pickedDay;

    // The heading of the picked day: the date, and a way back to the upcoming list.
    private FrameworkElement PickedHeading(DateOnly day)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var text = UI.Fixed($"{day:MM月dd日}", 10, 14, UI.Secondary, FontWeights.SemiBold);
        text.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(text);
        var clear = new Button
        {
            Content = UI.Centered(Loc.T("全部", "All"), 9.5, UI.Tertiary), Height = 18, Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(8, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Focusable = false, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
        };
        clear.Click += (_, _) => { _pickedDay = null; RenderUsage(); };
        AutomationProperties.SetName(clear, Loc.T("显示接下来", "Show what is coming up"));
        line.Children.Add(clear);
        panel.Children.Add(line);
        return panel;
    }

    private static FrameworkElement AgendaHeading(string text)
    {
        var heading = UI.Fixed(text, 10, 14, UI.Tertiary);
        heading.Margin = new Thickness(0, 0, 0, 6);
        return heading;
    }

    // A month as a calendar: the weekday headings, then six rows of days, each showing the day's number and, in small
    // type under it, what the subscribed calendars say about that date.
    private FrameworkElement MonthView(DateTime month, IReadOnlyList<CalendarEvent> events)
    {
        // The month's name in the middle, with a step back on its left and a step forward on its right, the way a
        // calendar puts them. The title is centred on the grid of days below it rather than on the page, so it sits
        // over the month it names; the arrows take the space either side of it and keep the title's place fixed.
        //
        // The month shown is the page's month, not today's after a step, so a reader who looks ahead keeps it while the
        // panel is open.
        var body = new Grid { Width = 7 * 35 };
        for (int i = 0; i < 7; i++) body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 6; i++) body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });

        // The weekday headings, Monday first, which is how a Chinese calendar is read. The weekend's two carry the same
        // colour their days do, so the columns read as what they are before a date is looked at.
        string[] weekdays = [Loc.T("一", "M"), Loc.T("二", "T"), Loc.T("三", "W"), Loc.T("四", "T"), Loc.T("五", "F"), Loc.T("六", "S"), Loc.T("日", "S")];
        for (int i = 0; i < 7; i++)
        {
            bool weekend = i >= 5;
            var label = UI.Centered(weekdays[i], 9.5, weekend ? WeekendColour : UI.Tertiary);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 0, 0, 5);
            Grid.SetRow(label, 0); Grid.SetColumn(label, i);
            body.Children.Add(label);
        }

        // What a date holds, by its day, for the marks under the numbers.
        var byDay = new Dictionary<DateOnly, List<CalendarEvent>>();
        foreach (var entry in events)
        {
            var day = DateOnly.FromDateTime(entry.Start.LocalDateTime.Date);
            if (!byDay.TryGetValue(day, out var list)) byDay[day] = list = [];
            list.Add(entry);
        }

        // The grid starts on the Monday of the week that holds the first: a month does not begin on a Monday.
        int offset = ((int)month.DayOfWeek + 6) % 7;
        var first = month.AddDays(-offset);
        for (int cell = 0; cell < 42; cell++)
        {
            var date = first.AddDays(cell);
            var day = DateOnly.FromDateTime(date);
            var cellView = DayCell(date, day, month.Month == date.Month, byDay.TryGetValue(day, out var onDay) ? onDay : []);
            Grid.SetRow(cellView, cell / 7 + 1); Grid.SetColumn(cellView, cell % 7);
            body.Children.Add(cellView);
        }

        // The title row: a step back, the month's name, a step forward. The name is centred on the day grid it names,
        // and the two arrows sit either side of it at the grid's own edges, so the row is exactly as wide as the grid
        // below and nothing of it is cut off by the page.
        var head = new Grid { Width = 7 * 35, Height = 24, Margin = new Thickness(0, 2, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        var previous = MonthArrow(false, () => StepMonth(-1));
        var next = MonthArrow(true, () => StepMonth(1));
        previous.HorizontalAlignment = HorizontalAlignment.Left;
        next.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(previous, 0); head.Children.Add(previous);
        Grid.SetColumn(next, 2); head.Children.Add(next);
        var title = UI.Centered($"{month:yyyy}年{month.Month}月", 12, UI.Primary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1); head.Children.Add(title);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        return stack;
    }

    // One day: its number, and under it what the calendars say about that date — a holiday, or a meeting.
    //
    // The colours say what kind of day it is before anything is read: a subscribed holiday and a weekend day are the
    // days a person plans around, so they are the ones given a colour the eye catches. Today is a filled disc behind
    // the number, which is how the Windows calendar marks it.
    private FrameworkElement DayCell(DateTime date, DateOnly day, bool inMonth, IReadOnlyList<CalendarEvent> entries)
    {
        // A day is a button, not a grid with a click handler: pressing it is the one thing it does, and a button is
        // what both the pointer and the keyboard find. It is drawn as a plain cell — no chrome until the pointer is
        // over it — so the month still reads as a month.
        var cell = new Button
        {
            Width = 35, Height = 35, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Focusable = false, Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(cell, $"{day:yyyy-MM-dd}");
        var inner = new Grid { Width = 35, Height = 35 };
        cell.Content = inner;
        var subscribed = entries.FirstOrDefault(entry => entry.Source == "subscription");
        bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        bool today = date.Date == DateTime.Today;

        // The colours, chosen for a dark panel: a holiday is the loudest of the three, a weekend a shade quieter, an
        // ordinary day the usual text colour. A day outside the month keeps the month's own dim grey whatever it is,
        // because it belongs to the month before or after and is only there to fill the week.
        string numberColour = !inMonth ? UI.Tertiary
            : subscribed is not null ? HolidayColour
            : weekend ? WeekendColour
            : UI.Primary;

        // A holiday's day gets a tinted pill behind the number, so it reads as a marked day even before the word under
        // it is read. It is faint: the text over it stays the loudest thing in the cell.
        if (inMonth && subscribed is not null)
        {
            var pill = new Border
            {
                Width = 24, Height = 18, CornerRadius = new CornerRadius(9), Background = UI.Brush(HolidayColour),
                Opacity = 0.16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0), IsHitTestVisible = false
            };
            inner.Children.Add(pill);
        }

        var number = UI.Centered($"{date.Day}", 11, numberColour, subscribed is not null || today ? FontWeights.SemiBold : null);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.Margin = new Thickness(0, 2, 0, 0);
        number.VerticalAlignment = VerticalAlignment.Top;
        inner.Children.Add(number);

        // A subscribed calendar's entry goes under the number, in the holiday's own colour, because it is a fact about
        // the date; the day a work meeting falls on is marked with a dot instead, since its title belongs in the column
        // beside the month.
        if (subscribed is not null && inMonth)
        {
            var label = UI.Centered(subscribed.Title, 8, HolidayColour);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Bottom;
            label.Margin = new Thickness(0, 0, 0, 3);
            label.MaxWidth = 34;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            inner.Children.Add(label);
            cell.ToolTip = subscribed.Title;
        }
        else if (entries.Any(entry => entry.Source != "subscription"))
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 3.5, Height = 3.5, Fill = UI.Brush(inMonth ? MeetingColour : UI.Tertiary),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5)
            };
            inner.Children.Add(dot);
        }

        // Today is a filled disc behind the number, drawn before the text so the number stays crisp on top of it.
        if (today)
        {
            var disc = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = UI.Brush(TodayColour),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
                IsHitTestVisible = false
            };
            inner.Children.Insert(0, disc);
            // The number on the disc is white, whatever the day is: a coloured disc with coloured text over it reads
            // as a smudge.
            number.Foreground = UI.Brush("#FFFFFF");
            number.FontWeight = FontWeights.SemiBold;
        }

        // The day a reader picked carries a ring, so the column beside it can be read knowing which day it answers for.
        if (_pickedDay == day)
        {
            inner.Children.Insert(0, new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.4),
                BorderBrush = UI.Brush(UI.Secondary), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0), IsHitTestVisible = false
            });
        }

        // A day can be picked, whether it falls in this month or not: the first and last weeks of the grid carry the
        // days either side, and a reader stepping through them is asking about those days. Picking one shows that day's
        // meetings in the column beside the month.
        cell.Click += (_, _) =>
        {
            // A second press on the same day puts the column back to what is coming up, which is how a reader undoes a
            // pick without hunting for the way back.
            _pickedDay = _pickedDay == day ? null : day;
            RenderUsage();
        };
        return cell;
    }

    // The colours of the month: a holiday is the loudest, a weekend a shade quieter, a meeting a soft blue that does
    // not compete with either. They are chosen against the dark surface the panel draws, and each is legible on it.
    private static string HolidayColour => "#FF6B6B";
    private static string WeekendColour => "#8FB8FF";
    private static string MeetingColour => "#7FD1C4";
    private static string TodayColour => "#0A84FF";

    // A month step: the arrows beside the month's name.
    private FrameworkElement MonthArrow(bool forward, Action action)
    {
        // A chevron on a disc of its own. The panel's surface is very dark and a stroked line on it disappears however
        // bright the stroke is when the line is thin; a filled disc gives the arrow something to stand on, so it is
        // found before it is aimed at. The fill is the panel's own text colour at low opacity — a light grey disc on
        // black — and the chevron is drawn in the surface colour on top, which is the strongest contrast available
        // without introducing a colour the page does not use.
        var disc = new System.Windows.Shapes.Ellipse
        {
            Width = 18, Height = 18, Fill = UI.Brush(UI.Primary), Opacity = 0.22, IsHitTestVisible = false
        };
        var shape = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(forward ? "M 1,1 L 6,6 L 1,11" : "M 6,1 L 1,6 L 6,11"),
            StrokeThickness = 2.0, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 8, Height = 13, Stroke = UI.Brush(UI.Primary),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var face = new Grid { Width = 20, Height = 20 };
        face.Children.Add(disc);
        face.Children.Add(shape);
        var button = new Button
        {
            Content = face, Width = 20, Height = 20, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Focusable = false, Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        // Hovering lights the disc up, so the arrow answers the pointer before it is pressed.
        button.MouseEnter += (_, _) => disc.Opacity = 0.38;
        button.MouseLeave += (_, _) => disc.Opacity = 0.22;
        button.Click += (_, _) => action();
        button.ToolTip = forward ? Loc.T("下个月", "Next month") : Loc.T("上个月", "Previous month");
        AutomationProperties.SetName(button, forward ? Loc.T("下个月", "Next month") : Loc.T("上个月", "Previous month"));
        return button;
    }

    // The month the page shows, kept while the panel is open so a reader looking ahead is not sent back.
    private DateTime _agendaMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private void StepMonth(int delta)
    {
        _agendaMonth = _agendaMonth.AddMonths(delta);
        RenderUsage();
        // The month stepped to may reach past what the calendars were read for; the subscriptions are read over it so
        // its days carry their marks. The read redraws the page when it comes back.
        var month = ShownMonth();
        var state = Settings.Agenda;
        _ = Task.Run(async () =>
        {
            foreach (var subscription in state.Subscriptions.Where(subscription => subscription.Enabled && subscription.Url.Length > 0))
            {
                try { await _app.Agenda.ReadMonthAsync(subscription, month.AddDays(-7), month.AddMonths(1).AddDays(7), _app.Lifetime.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
            }
            Dispatcher.BeginInvoke(new Action(() => { if (IsCalendarPage) RenderUsage(); }));
        });
    }

    // The month the view draws. It is whatever was stepped to: stepping back is how a reader looks at the days that
    // have already gone, and a step that was quietly undone by a clamp was a button that did nothing.
    private DateTime ShownMonth() => _agendaMonth;

    // One meeting: when it is, then what it is. The grid is given a width, because a star column inside a StackPanel
// that does not stretch measures as nothing and the row collapses to its left edge — which is how the time column
// came out as a narrow strip with the title pinned against it.
    private FrameworkElement MeetingRow(CalendarEvent meeting, DateTimeOffset now)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var when = UI.Fixed(meeting.When(now), 11, 16, UI.Secondary);
        when.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(when, 0); grid.Children.Add(when);

        var title = UI.Fixed(meeting.Title, 12, 17, UI.Primary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(title, 1); grid.Children.Add(title);

        grid.ToolTip = $"{meeting.Title}\n{meeting.When(now)}" + (meeting.Source.Length > 0 ? $"\n{meeting.Source}" : "");
        return grid;
    }

    // The mail that came in, newest first. A mail with a verification code shows it, because that is the one thing
    // the user opens the page for.
    private void RenderMail()
    {
        if (MailContent is null) return;
        ShowOnly(MailContent);
        MailContent.Children.Clear();

var now = DateTimeOffset.Now;
        var items = _app.Agenda.RecentMail(now, TimeSpan.FromDays(2));
        if (items.Count == 0)
        {
            MailContent.Children.Add(_app.Agenda.MailError.Length > 0
                ? PageNote(_app.Agenda.MailError)
                : Settings.Agenda.MailAccounts.Count == 0
                    ? SetupNote(Loc.T("还没有添加邮箱", "No mailbox yet"),
                        Loc.T("在设置里添加一个邮箱账号，新邮件会显示在这里。", "Add a mailbox in the settings; new mail shows here."))
                    : PageNote(Loc.T("最近没有新邮件", "No new mail")));
            return;
        }
        // Top-aligned rather than centred: a list taller than the page is a list with a scrolling end, and centring it
        // pushed its first rows up under the toolbar where they could not be read at all.
        var rows = new StackPanel();
        rows.Children.Add(RowHead(Loc.T("时间", "Time"), Loc.T("邮件", "Mail")));
        foreach (var item in items) rows.Children.Add(MailRow(item));
        MailContent.Children.Add(new ScrollViewer
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            MaxWidth = 620,
            Content = rows
        });
    }

    private FrameworkElement MailRow(MailItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        // The sender's column is a fixed width and its address is cut to fit it. Left to its own size, one long
        // address — and a verification mail's address is very long — took the width the subject needed, which left the
        // subject squeezed into an ellipsis that cut it off at both ends and read as a fragment of somebody else's mail.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SenderWidth) });

        var when = UI.Fixed(item.Received.LocalDateTime.ToString("MM-dd HH:mm"), 12, 17, UI.Secondary);
        when.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(when, 0); grid.Children.Add(when);

        // The subject, cut from its end when it does not fit: the beginning of a subject says which mail it is, and the
        // end does not.
        string subject = item.Subject.Length > 0 ? item.Subject : item.Summary;
        var title = UI.Fixed(subject, 13, 18, UI.Primary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(title, 1); grid.Children.Add(title);

        // The code, when there is one: it is the thing the user came for, so it is the only thing in the row's
        // colour. A mail without one shows its sender instead, cut to the column.
        string? code = item.Code;
        if (code is null)
        {
            var from = UI.Fixed(item.From, 10, 14, UI.Tertiary);
            from.VerticalAlignment = VerticalAlignment.Center; from.Margin = new Thickness(10, 0, 0, 0);
            from.TextTrimming = TextTrimming.CharacterEllipsis; from.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(from, 2); grid.Children.Add(from);
        }
        else
        {
            var chip = new Border
            {
                Child = UI.Fixed(code, 12, 16, "#0A84FF", FontWeights.SemiBold),
                Background = UI.Brush(UI.FillSelected), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(chip, 2); grid.Children.Add(chip);
        }
        grid.ToolTip = item.Subject.Length > 0 ? item.Subject : item.Summary;
        return grid;
    }

    // How wide the sender's column is. Wide enough for a normal address, and cut when it is not: the subject is what
    // the row is read for.
    private const double SenderWidth = 168;

    // The headings of a list: the two columns the rows below share their widths with.
    private static FrameworkElement RowHead(string first, string second)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SenderWidth) });
        var left = UI.Fixed(first, 9.5, 13, UI.Tertiary);
        Grid.SetColumn(left, 0); grid.Children.Add(left);
        var right = UI.Fixed(second, 9.5, 13, UI.Tertiary);
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        return grid;
    }

    // A page with nothing on it: either an empty page or one that could not be read.
    private static FrameworkElement PageNote(string text)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(UI.Fixed(text, 13, 18, UI.Secondary));
        return panel;
    }

    // A page that has nothing to show because nothing is set up: it says what is missing and opens the settings
    // page where the account is added, because that is the one thing the reader can do about it.
    private FrameworkElement SetupNote(string title, string hint)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(UI.Fixed(title, 14, 20, UI.Secondary));
        var line = UI.Fixed(hint, 11.5, 16, UI.Tertiary);
        line.Margin = new Thickness(0, 4, 0, 0); line.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(line);
        var openText = Loc.T("打开设置", "Open settings");
        var open = Segmented([new(openText, openText, false, _app.OpenSettings)], 12, 22, 12, centerText: true);
        open.Margin = new Thickness(0, 12, 0, 0); open.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(open);
        return panel;
    }
}
