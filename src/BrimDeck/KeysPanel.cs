using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// 「额度环」: every allowance the panel knows about, one ring at a time, in the manner of a fitness ring.
//
// A ring holds the one figure that matters, how much of the allowance is left, with the name of the service in the
// middle and the moment it comes back underneath. When there are several, arrows on either side step between them
// and the dots below say which of how many is showing — the same idea as the quotas on the collapsed island, so the
// two read alike.
//
// Two kinds of allowance appear here and they are treated alike: the applications the panel already reads (their
// reset times come from the service itself) and the keys the user added by hand (asked about on the hour).
public partial class MainWindow
{
    // Which ring is showing. Kept on the window so a redraw does not send the reader back to the first one.
    private int _keyPage;

    private void RenderKeys()
    {
        if (KeysContent is null) return;
        ShowOnly(KeysContent);
        KeysContent.Children.Clear();

        var rings = KeyRings();
        if (rings.Count == 0)
        {
            KeysContent.Children.Add(EmptyKeys());
            return;
        }
        _keyPage = Math.Clamp(_keyPage, 0, rings.Count - 1);
        KeysContent.Children.Add(RingStage(rings, _keyPage));
    }

    // One allowance: what it is worth, what it is called, and when it comes back. HasShare says whether the figure is a
// measured share of an allowance, which is what a ring can honestly draw.
    private sealed record KeyRingModel(double Remaining, bool HasShare, string Name, string Source, DateTimeOffset? Reset, DateTimeOffset? Asked, string Note, string Color)
    {
        // The service said its allowance is spent. The ring is full and red, and the figure under it says so in words.
        public bool Exhausted { get; init; }
    }

    // The rings the page can show, in the order the applications are listed and then the keys the user added.
    private List<KeyRingModel> KeyRings()
    {
        var rings = new List<KeyRingModel>();
        foreach (var entry in Settings.EnabledApps)
        {
            var snapshot = DashboardUsage.Quota(entry, Snapshots);
            // The ring with the least left is the one worth showing for an application with several windows: it is
            // the one that will run out first. A window without a share (a balance, a count) has no ring to draw.
            var metric = snapshot.Metrics
                .Where(metric => metric.UsedPercent is not null)
                .OrderByDescending(metric => metric.UsedPercent)
                .FirstOrDefault();
            if (metric is null) continue;
            double remaining = Math.Clamp(100 - (metric.UsedPercent ?? 100), 0, 100);
            // A window the service stated a reset for is the one whose ring says something; one without keeps the
            // ring but says the time is unknown, rather than inventing one.
            rings.Add(new KeyRingModel(
                remaining,
                true,
                entry.Name,
                metric.Label,
                metric.ResetAt,
                snapshot.QuotaTime,
                "",
                PanelTheme.QuotaTheme(entry)));
        }
        var now = DateTimeOffset.Now;
        foreach (var key in Settings.KeyResets.Where(key => key.Enabled))
        {
            // What the last probe heard. A service that answered "the allowance is used up" has told us the one figure
            // the panel could not otherwise know: the ring is full and red. A service that said nothing about a limit
            // leaves the ring empty, because a share worked out from the clock would be a figure the user could
            // mistake for their own usage.
            bool exhausted = Exhausted(key.ProbeNote);
            rings.Add(new KeyRingModel(
                0,
                false,
                key.DisplayName,
                exhausted ? Loc.T("已用尽", "Used up") : key.IntervalText,
                key.NextReset(now),
                key.ProbedAt,
                key.ProbeNote,
                key.Color.Length > 0 ? key.Color : UI.Secondary)
            { Exhausted = exhausted });
        }
        return rings;
    }

    // Whether a service's answer says its allowance is spent. The words come from the services themselves — MiniMax
    // says "已达到 Token Plan 用量上限", the OpenAI family "insufficient_quota" and "exceeded your current quota" — so
    // they are matched as the phrases they are rather than through a quota percentage the panel never receives.
    internal static bool Exhausted(string note)
    {
        if (note.Length == 0) return false;
        foreach (var phrase in new[]
        {
            "上限", "已用尽", "用量已达", "额度不足", "余额不足",
            "insufficient_quota", "exceeded your current quota", "quota exceeded", "rate limit reached",
            "out of credit", "no remaining quota"
        })
            if (note.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The stage: the ring in the middle, an arrow on each side when there is more than one, and a dot for each.
    private FrameworkElement RingStage(IReadOnlyList<KeyRingModel> rings, int index)
    {
        var model = rings[index];
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The arrows: only where there is somewhere to go, and dimmed at the ends so the two ends read as the ends.
        if (rings.Count > 1)
        {
            var left = Arrow(false, index > 0, () => Step(-1, rings.Count));
            left.HorizontalAlignment = HorizontalAlignment.Right;
            left.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(left, 0); grid.Children.Add(left);
            var right = Arrow(true, index < rings.Count - 1, () => Step(1, rings.Count));
            right.HorizontalAlignment = HorizontalAlignment.Left;
            right.Margin = new Thickness(14, 0, 0, 0);
            Grid.SetColumn(right, 2); grid.Children.Add(right);
        }

        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        column.Children.Add(RingBody(model));
        if (rings.Count > 1) column.Children.Add(Dots(rings.Count, index));
        Grid.SetColumn(column, 1); grid.Children.Add(column);
        return grid;
    }

    // The ring itself: the arc with the name in the middle and the reset time under it. Everything fits the height the
    // page gives it — the ring, one line of time and one line of what the window is — because a taller column is
    // centred and the ring's own top would otherwise be cut off by the page's edge.
    private FrameworkElement RingBody(KeyRingModel model)
    {
        const double size = 92, thickness = 7;
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var host = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        // The ring is the control: pressing it reads the figures again. A transparent background gives the whole
        // circle its presses, including the empty middle, which is where a cursor naturally lands.
        host.Background = Brushes.Transparent;
        host.Cursor = Cursors.Hand;
        // The arc is coloured by how much is left, from green through amber to red, so the state is readable before
        // the number is: a ring that has just reset is green and one at its limit is red. A service that said its
        // allowance is spent has the arc drawn all the way round in red, which is the one case where the panel knows
        // the figure without being told a percentage.
        string colour = model.Exhausted ? LoadColour(0) : model.HasShare ? LoadColour(model.Remaining) : model.Color;
        double arc = model.Exhausted ? 100 : model.HasShare ? model.Remaining : 0;
        host.Children.Add(UI.Ring(arc, colour, size, thickness));
        // The middle holds the name alone: it is what the ring is about. The figures are under the ring, where a
        // person reads them, and the middle stays quiet enough to recognise the service at a glance.
        var name = UI.Centered(model.Name, 12, UI.Primary, FontWeights.SemiBold);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.MaxWidth = size - thickness * 2 - 12;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        host.Children.Add(name);
        column.Children.Add(host);

        // The figure the ring is for: the share left when there is one, otherwise the moment the window reopens.
        var figure = model.HasShare
            ? UI.Centered($"{model.Remaining:0}%", 16, UI.Primary, FontWeights.SemiBold)
            : model.Reset is { } at
                ? UI.Centered($"{at.LocalDateTime:MM-dd HH:mm}", 15, UI.Primary, FontWeights.SemiBold)
                : UI.Centered(Loc.T("重置时间未知", "Reset time unknown"), 12, UI.Secondary);
        figure.HorizontalAlignment = HorizontalAlignment.Center;
        figure.Margin = new Thickness(0, 9, 0, 0);
        column.Children.Add(figure);

        // What the ring measures: the window's length for an application, the interval for a key.
        string caption = model.Source.Length > 0 ? model.Source : Loc.T("额度", "Allowance");
        var under = UI.Centered(caption, 10, UI.Tertiary);
        under.HorizontalAlignment = HorizontalAlignment.Center;
        under.Margin = new Thickness(0, 2, 0, 0);
        under.MaxWidth = 220; under.TextTrimming = TextTrimming.CharacterEllipsis;
        column.Children.Add(under);

        // The tooltip names the allowance and, when the service said something, repeats what it said — which is where a
        // long note belongs: read on demand rather than always taking room.
        var lines = new List<string> { model.Name };
        if (model.Source.Length > 0) lines.Add(model.Source);
        if (model.Reset is { } when) lines.Add(Loc.T($"重置 {when.LocalDateTime:yyyy-MM-dd HH:mm}", $"Resets {when.LocalDateTime:yyyy-MM-dd HH:mm}"));
        if (model.Asked is { } asked) lines.Add(Loc.T($"上次读取 {asked.LocalDateTime:MM-dd HH:mm}", $"Last read {asked.LocalDateTime:MM-dd HH:mm}"));
        if (model.Note.Length > 0) lines.Add(model.Note);
        lines.Add(Loc.T("点击圆环立即检测", "Click the ring to check now"));
        host.ToolTip = string.Join("\n", lines);
        // A press on the ring reads the figures again. The tooltip above says so, because a ring that can be pressed
        // looks no different from one that cannot.
        host.MouseLeftButtonUp += async (_, _) => await CheckNowAsync(host);
        AutomationProperties.SetName(host, Loc.T($"{model.Name}，点击立即检测", $"{model.Name}, click to check now"));
        return column;
    }

    // A way to ask now rather than wait for the hour: pressing the ring reads the applications' quotas and probes the
    // keys the user added, which is exactly what the scheduled reads do.
    //
    // The press is guarded, because the read takes seconds and the panel is a small target under a cursor that is
    // already moving: a second press while the first is running would start a second read of the same services and, for
    // a key, spend a second request against a rate limit. While a check runs the ring keeps its look and ignores presses.
    private bool _checkingNow;

    // Reads the quotas and the keys now, and holds every press until it has finished. The ring itself shows that a
    // check is running by keeping the figure it had; the tooltip says why nothing changes under the press.
    private async Task CheckNowAsync(UIElement source)
    {
        if (_checkingNow) return;
        _checkingNow = true;
        source.IsHitTestVisible = false;
        try
        {
            // The quotas of the applications, read the way the timer reads them.
            await RefreshAsync(queueIfBusy: false);
            // And the keys the user added, probed at once rather than at the next hour.
            foreach (var key in Settings.KeyResets.Where(key => key.Enabled).ToList())
            {
                var answer = await _app.Prober.TestAsync(key, _app.Lifetime.Token);
                // The row is replaced in place, so a key probed while the list is being edited is not lost.
                Settings.UpdateKey(key.InstanceId, existing => existing.WithProbe(answer, DateTimeOffset.Now));
            }
            _app.UpdateSettings(Settings);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        finally
        {
            _checkingNow = false;
            source.IsHitTestVisible = true;
            RenderUsage();
        }
    }

    // The colour of a ring by how much of the allowance is left: green with plenty, amber as it runs down, red at the
    // limit — the same three bands the island's warnings use, spread into a continuous shade so the state is readable
    // at a glance without reading the number.
    internal static string LoadColour(double remaining)
    {
        double share = Math.Clamp(remaining, 0, 100) / 100;
        // Below the warning level the hue runs 0 (red) to 145 (green) as the figure rises; there is no blue in it, so
        // the whole scale reads as a traffic light rather than as a rainbow.
        double hue = share <= 0 ? 0 : share >= 1 ? 145 : share * 145;
        // The saturation is held high so a small arc is still visible against the dark surface; the lightness rises a
        // little at the top so green does not turn muddy.
        double saturation = 0.72, lightness = 0.46 + share * 0.10;
        return Hsl(hue, saturation, lightness);
    }

    // A colour from hue, saturation and lightness, in the hex form the drawing code takes.
    private static string Hsl(double hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = lightness - c / 2;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        int R = (int)Math.Round(Math.Clamp(r + m, 0, 1) * 255), G = (int)Math.Round(Math.Clamp(g + m, 0, 1) * 255), B = (int)Math.Round(Math.Clamp(b + m, 0, 1) * 255);
        return $"#{R:X2}{G:X2}{B:X2}";
    }

    // One step to the next or the previous allowance.
    private void Step(int delta, int count)
    {
        if (count <= 1) return;
        _keyPage = ((_keyPage + delta) % count + count) % count;
        RenderUsage();
    }

    // A dot for each allowance, the one showing filled.
    private FrameworkElement Dots(int count, int index)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        for (int i = 0; i < count && i < 12; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = i == index ? 7 : 5, Height = i == index ? 7 : 5,
                Fill = UI.Brush(i == index ? UI.Primary : UI.Tertiary),
                Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(dot);
        }
        return row;
    }

    // A chevron. It is a button so it can be pressed, and it is drawn rather than typed so it stays crisp at any scale.
    private FrameworkElement Arrow(bool right, bool enabled, Action action)
    {
        var shape = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(right ? "M 2,1 L 7,6 L 2,11" : "M 7,1 L 2,6 L 7,11"),
            StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 9, Height = 12,
            Stroke = UI.Brush(enabled ? UI.Secondary : UI.Tertiary), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Button
        {
            Content = shape, Width = 26, Height = 30, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Focusable = false, IsEnabled = enabled, Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            VerticalAlignment = VerticalAlignment.Center
        };
        // The chevron takes the colour of the state it is in, so a disabled arrow reads as unavailable rather than missing.
        button.Opacity = enabled ? 1 : 0.35;
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, right ? Loc.T("下一个", "Next") : Loc.T("上一个", "Previous"));
        return button;
    }

    private FrameworkElement EmptyKeys()
    {
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        empty.Children.Add(UI.Fixed(Loc.T("还没有可显示的额度", "No allowance to show"), 14, 20, UI.Secondary));
        var hint = UI.Fixed(Loc.T("在设置里登录一个 AI 应用，或添加一个 API key，这里会显示它们的重置时间。",
            "Sign in to an AI application in the settings, or add an API key; their reset times show here."), 11.5, 16, UI.Tertiary);
        hint.Margin = new Thickness(0, 4, 0, 0); hint.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(hint);
        var openText = Loc.T("打开设置", "Open settings");
        var open = Segmented([new(openText, openText, false, _app.OpenSettings)], 12, 22, 12, centerText: true);
        open.Margin = new Thickness(0, 12, 0, 0); open.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(open);
        return empty;
    }
}

