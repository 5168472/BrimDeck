using System.Net;
using BrimDeck.Core;

// A probe is only useful if it reads the services' headers exactly as they send them, so these tests use the header
// shapes the services are documented to send, including the ones that mean "no window is running".
internal static class KeyProbeTests
{
    private static Dictionary<string, string> H(params (string Name, string Value)[] pairs)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in pairs) headers[name] = value;
        return headers;
    }

    public static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);

        // Anthropic's unified reset: whole seconds since the epoch, and the account-wide window the row should show.
        var inThreeHours = now.AddHours(3);
        var anthropic = KeyProbeParser.Parse(KeyProbeKind.Anthropic, HttpStatusCode.OK,
            H(("anthropic-ratelimit-unified-reset", inThreeHours.ToUnixTimeSeconds().ToString())), now);
        check("Anthropic's unified reset is read as an instant", anthropic.Ok && anthropic.ResetAt == inThreeHours);

        // A per-window reset is an RFC 3339 instant; the weekly one must win over the hourly one.
        var weekly = now.AddDays(4);
        var windows = KeyProbeParser.Parse(KeyProbeKind.Anthropic, HttpStatusCode.OK,
            H(("anthropic-ratelimit-5h-reset", now.AddHours(2).ToString("O")),
              ("anthropic-ratelimit-7d-reset", weekly.ToString("O"))), now);
        check("The weekly window wins over the hourly one", windows.ResetAt == weekly && windows.Window == "7d");

        // A reset already behind us means no window of that length is running, so the next shorter one is taken.
        var hourlyOnly = KeyProbeParser.Parse(KeyProbeKind.Anthropic, HttpStatusCode.OK,
            H(("anthropic-ratelimit-7d-reset", now.AddDays(-1).ToString("O")),
              ("anthropic-ratelimit-5h-reset", now.AddHours(2).ToString("O"))), now);
        check("A stale window is skipped", hourlyOnly.ResetAt == now.AddHours(2) && hourlyOnly.Window == "5h");

        // A 429 states when the window reopens through retry-after, which is the reset the panel should show.
        var throttled = KeyProbeParser.Parse(KeyProbeKind.Anthropic, HttpStatusCode.TooManyRequests, H(("retry-after", "3600")), now);
        check("A 429's retry-after is the reopened window", throttled.Ok && throttled.ResetAt == now.AddHours(1));

        // Retry-After may also be an HTTP date.
        var dated = KeyProbeParser.Parse(KeyProbeKind.Anthropic, HttpStatusCode.TooManyRequests,
            H(("retry-after", now.AddMinutes(30).ToString("R"))), now);
        check("Retry-After as a date is understood", dated.ResetAt == now.AddMinutes(30));

        // OpenAI-compatible durations, in the three spellings those services use.
        var openai = KeyProbeParser.Parse(KeyProbeKind.OpenAI, HttpStatusCode.OK, H(("x-ratelimit-reset-requests", "6m0s")), now);
        check("A duration is added to the response time", openai.ResetAt == now.AddMinutes(6));
        var hours = KeyProbeParser.Parse(KeyProbeKind.OpenAICompatible, HttpStatusCode.OK, H(("x-ratelimit-reset-tokens", "1h2m3s")), now);
        check("Hours, minutes and seconds all count", hours.ResetAt == now.AddSeconds(3723));
        var millis = KeyProbeParser.Parse(KeyProbeKind.OpenAI, HttpStatusCode.OK, H(("x-ratelimit-reset-requests", "500ms")), now);
        check("Milliseconds are understood", millis.ResetAt == now.AddMilliseconds(500));

        // A rejected key is a real answer, and is reported as a failure so the page can say so.
        var rejected = KeyProbeParser.Parse(KeyProbeKind.Anthropic, HttpStatusCode.Unauthorized, H(), now);
        check("A rejected key fails loudly", !rejected.Ok && rejected.Note.Length > 0);
        var forbidden = KeyProbeParser.Parse(KeyProbeKind.OpenAI, HttpStatusCode.Forbidden, H(), now);
        check("A forbidden key fails too", !forbidden.Ok);

        // A service that sends nothing is not an error; it simply has no reset to report.
        var silent = KeyProbeParser.Parse(KeyProbeKind.OpenAI, HttpStatusCode.OK, H(), now);
        check("A silent service reports no reset rather than a wrong one", silent.Ok && silent.ResetAt is null);

        // Durations are strict: what cannot be read is refused, never guessed.
        check("A bare number of seconds is a duration", KeyProbeParser.Duration("90") == TimeSpan.FromSeconds(90));
        check("An unreadable duration is refused", KeyProbeParser.Duration("soon") is null && KeyProbeParser.Duration("1w") is null && KeyProbeParser.Duration("") is null);
        check("A duration past a year is refused", KeyProbeParser.Duration("400d") is null);

        // A key row keeps what a probe told it, and learns the period from the gap between two resets.
        var anchor = now;
        var key = new KeyReset { Anchor = anchor, IntervalMinutes = 7 * 24 * 60 }.Normalized();
        // Before any probe the schedule answers from the anchor: the first reset is one interval on.
        check("Before any probe the schedule answers", key.NextReset(now.AddHours(1)) == anchor.AddDays(7));
        var first = key.WithProbe(new KeyProbeResult(true, now.AddDays(3), "7d", ""), now);
        check("A probe's reset is what the row counts down to", first.NextReset(now) == now.AddDays(3));
        check("The first probe sets the anchor to the reset it learned", first.Anchor == now.AddDays(3));
        // The same reset seen again is not news and must not shift anything.
        var again = first.WithProbe(new KeyProbeResult(true, now.AddDays(3), "7d", ""), now.AddHours(1));
        check("Seeing the same reset again changes nothing", again.NextReset(now.AddHours(1)) == now.AddDays(3) && again.IntervalMinutes == first.IntervalMinutes);
        // A later reset measures the period: the gap between the two is the interval.
        var second = first.WithProbe(new KeyProbeResult(true, now.AddDays(3).AddHours(5), "7d", ""), now.AddHours(2));
        check("A later reset teaches the period", second.IntervalMinutes == 300 && second.PreviousResetAt == now.AddDays(3));
        check("The row counts down to the newest reset", second.NextReset(now.AddHours(2)) == now.AddDays(3).AddHours(5));
        // A failed probe leaves the times alone, so a network blip costs nothing.
        var failed = second.WithProbe(KeyProbeResult.Failed("offline"), now.AddHours(3));
        check("A failed probe keeps the times", failed.NextReset(now.AddHours(3)) == second.NextReset(now.AddHours(3)));
        check("A failed probe still records when it ran", failed.ProbedAt == now.AddHours(3));

        // A probed reset that has passed falls back to the schedule rather than showing a time in the past.
        var passed = second.WithProbe(new KeyProbeResult(true, now.AddDays(4), "7d", ""), now.AddDays(5));
        check("A passed reset falls back to the schedule", passed.NextReset(now.AddDays(5)) is { } next && next > now.AddDays(5));

        // Hourly cadence: due when nothing has run, and not due again within the hour.
        var fresh = new KeyReset { ProbeEnabled = true };
        check("A key that never probed is due", KeyProber.Due(fresh, now));
        var probed = fresh.Copy(); probed.ProbedAt = now;
        check("A key probed just now is not due", !KeyProber.Due(probed, now.AddMinutes(30)));
        check("A key probed an hour ago is due", KeyProber.Due(probed, now.AddHours(1)));
        probed.ProbeEnabled = false;
        check("A key with probing off is never due", !KeyProber.Due(probed, now.AddHours(5)));

        // The cadence follows the clock, so probes land on the hour whatever time the panel started.
        check("The next probe is the coming hour", KeyProber.NextHourly(now.AddMinutes(37)) == now.AddHours(1));

        // Probe settings survive a save and load, which is what keeps the learned times across a restart.
        var settings = new DeckSettings { KeyResets = [second] };
        settings.Normalize();
        var copy = settings.Copy();
        check("Learned reset times survive a copy", copy.KeyResets[0].ProbedResetAt == second.ProbedResetAt);
        check("Learned intervals survive a copy", copy.KeyResets[0].IntervalMinutes == second.IntervalMinutes);
        check("The probe kind survives a copy", copy.KeyResets[0].Probe == second.Probe);
    }
}
