namespace BrimDeck.Core;

// A reset schedule for an AI key: an anchor instant plus a fixed repeat. The reset instants are
// anchor + k * Interval, so they are the same whether or not the panel was running: nothing here is
// stored state that a missed day would shift. Only the times the panel has already seen are needed to
// tell a fresh reset from an old one.
//
// The panel looks one Horizon ahead and moves that look-ahead every RefreshDays. With a 7-day horizon
// and a 5-day refresh the look-ahead always covers at least two days of resets before it moves on, so a
// reset is never dropped between the moment one window ends and the next begins.
public sealed record ResetSchedule(DateTimeOffset Anchor, TimeSpan Interval, int HorizonDays = 7, int RefreshDays = 5)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(366);

    // A schedule that never fires. Used for a key or interval the user has not filled in yet, so the rest
    // of the panel keeps one code path instead of checking for a missing schedule everywhere.
    public static readonly ResetSchedule None = new(DateTimeOffset.MinValue, TimeSpan.Zero, 0, 0);

    public bool IsNone => Interval <= TimeSpan.Zero || Anchor == DateTimeOffset.MinValue;

    public ResetSchedule Normalize()
    {
        if (IsNone) return None;
        var interval = Interval < MinInterval ? MinInterval : Interval > MaxInterval ? MaxInterval : Interval;
        // The refresh must leave something of the horizon behind it, or a window could end before the next
        // one covers the resets in between.
        int horizon = Math.Clamp(HorizonDays, 2, 60);
        int refresh = Math.Clamp(RefreshDays, 1, horizon - 1);
        return this with { Interval = interval, HorizonDays = horizon, RefreshDays = refresh };
    }

    // The start of the look-ahead window that contains now. It steps from the anchor in RefreshDays, so the
    // window moves on the fifth day after the previous one started rather than sliding with every tick,
    // which would change the shown list continuously.
    public DateTimeOffset WindowStart(DateTimeOffset now)
    {
        if (IsNone) return now;
        var since = now - Anchor;
        if (since <= TimeSpan.Zero) return Anchor;
        long steps = (long)Math.Floor(since.TotalSeconds / RefreshDays / 86400d);
        return Anchor.AddDays(steps * RefreshDays);
    }

    // Every reset still ahead of now within the current look-ahead window, soonest first. "Ahead" excludes the
    // anchor itself once it has arrived, which is what a countdown list wants: an instant that has passed is not
    // something to wait for. The window's close is included, so a reset landing exactly on the far edge shows.
    public IReadOnlyList<DateTimeOffset> Upcoming(DateTimeOffset now)
    {
        if (IsNone) return [];
        var close = WindowStart(now).AddDays(HorizonDays);
        var times = new List<DateTimeOffset>();
        if (close < Anchor) return times;
        // The first reset after now, then every interval on, while the window's close bounds the walk. Working
        // out last from the bound rather than looping until it overflows keeps a one-minute interval from
        // running for a very long time.
        long first = now < Anchor ? 1 : (now - Anchor).Ticks / Interval.Ticks + 1;
        long last = (close - Anchor).Ticks / Interval.Ticks;
        for (long k = first; k <= last; k++)
            times.Add(Anchor + TimeSpan.FromTicks(Interval.Ticks * k));
        return times;
    }

    // The next reset at or after now, which is what a countdown shows. Not limited to the window, so a
    // schedule whose window has not started yet still has an answer.
    public DateTimeOffset? Next(DateTimeOffset now)
    {
        if (IsNone) return null;
        if (now <= Anchor) return Anchor;
        long k = CeilDiv(now - Anchor, Interval);
        return Anchor + TimeSpan.FromTicks(Interval.Ticks * k);
    }

    // The most recent reset at or before now, for "last reset".
    public DateTimeOffset? Last(DateTimeOffset now)
    {
        if (IsNone) return null;
        if (now < Anchor) return null;
        long k = (now - Anchor).Ticks / Interval.Ticks;
        return Anchor + TimeSpan.FromTicks(Interval.Ticks * k);
    }

    // Where now falls between the last and the next reset, as a share of the interval. The bar that shows how
    // far into a reset period the key is.
    public double? ElapsedPercent(DateTimeOffset now)
    {
        if (IsNone || Interval <= TimeSpan.Zero) return null;
        var last = Last(now) ?? Anchor;
        var span = Interval.Ticks;
        if (span <= 0) return null;
        double share = (now - last).Ticks / (double)span;
        return double.IsFinite(share) ? Math.Clamp(share * 100, 0, 100) : null;
    }

    private static long CeilDiv(TimeSpan span, TimeSpan unit)
    {
        if (span <= TimeSpan.Zero) return 0;
        long ticks = unit.Ticks;
        if (ticks <= 0) return 0;
        long whole = span.Ticks / ticks;
        return span.Ticks % ticks == 0 ? whole : whole + 1;
    }
}
