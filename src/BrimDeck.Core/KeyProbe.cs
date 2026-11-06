using System.Globalization;
using System.Net;

namespace BrimDeck.Core;

// Which API a key belongs to. The difference that matters is how the service states its reset: Anthropic gives an
// absolute instant in a rate-limit header, OpenAI-compatible services give a duration from the response, and a
// service with neither leaves the schedule to be learned from the times a reset is seen to pass.
public enum KeyProbeKind { Anthropic, OpenAI, OpenAICompatible }

// One probe's outcome: what the service said about the next reset, if it said anything. Ok is false when the request
// never reached the service (no network, a rejected key), which is different from reaching it and learning nothing.
// Status is the HTTP code the service answered with, which is what a person testing a key wants to see: 200 means the
// key works, 401 means it does not, 429 means it works but the allowance is spent.
public sealed record KeyProbeResult(bool Ok, DateTimeOffset? ResetAt, string Window, string Note, int? Status = null)
{
    public static KeyProbeResult Failed(string note, int? status = null) => new(false, null, "", note, status);
    public static KeyProbeResult Empty(string note = "") => new(true, null, "", note);
    // What the settings page says about the answer: the code and the reason, so a failure names itself.
    public string StatusText => Status is { } code ? $"HTTP {code}" : "";
}

// Reads a reset instant out of a probe response. Kept apart from the request so it can be tested against the exact
// headers the services send, and so a service that changes its headers only costs a parser, not a rewrite.
//
// The headers are the documented ones:
//  - Anthropic sends "anthropic-ratelimit-<window>-reset" as an RFC 3339 instant, and, for its unified limits,
//    "anthropic-ratelimit-unified-reset" as whole seconds since the epoch. A 429 carries "retry-after", which is a
//    duration in seconds or an HTTP date; that is when the window reopens, so it serves as the reset time.
//  - OpenAI and compatible services send "x-ratelimit-reset-requests" and "x-ratelimit-reset-tokens" as durations
//    ("1s", "6m0s", "1h2m3s"). Those are relative to the response, so the reset is the response time plus that.
//
// Nothing here guesses: a service that sends none of these returns no instant, and the caller falls back to the
// schedule it learned rather than to an invented time.
public static class KeyProbeParser
{
    // The windows, longest first: a weekly limit is the one a countdown should show, and a per-minute one would make it
    // flicker. Anthropic's own names are mapped to a readable window.
    private static readonly string[] AnthropicWindows = ["7d", "5h", "1d", "1h", "1m"];

    public static KeyProbeResult Parse(KeyProbeKind kind, HttpStatusCode status, IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
    {
        // A rejected key is a real answer: the service was reached and refused. It is reported as a failure so the
        // settings page can say the key is wrong instead of showing an old reset time forever.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return KeyProbeResult.Failed(Loc.T("密钥被拒绝", "Key rejected"), (int)status);

        // "retry-after" is the service's own statement of when the window reopens, so it wins over anything else, on a
        // 429 and equally on a plain success where the service volunteers it.
        if (Header(headers, "retry-after") is { Length: > 0 } retry && RetryAfter(retry, now) is { } reopened)
            return new KeyProbeResult(true, reopened, Loc.T("限流窗口", "Rate limit window"), "", (int)status);

        return kind switch
        {
            KeyProbeKind.Anthropic => Anthropic(headers, now, (int)status),
            _ => OpenAi(headers, now, (int)status)
        };
    }

    private static KeyProbeResult Anthropic(IReadOnlyDictionary<string, string> headers, DateTimeOffset now, int status)
    {
        // The unified limit is the account-wide window and the most useful one, so it is read first.
        if (Header(headers, "anthropic-ratelimit-unified-reset") is { Length: > 0 } unified &&
            long.TryParse(unified, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) && seconds > 0)
        {
            if (FromUnixSeconds(seconds) is { } at)
                return new KeyProbeResult(true, at, Loc.T("账户额度", "Account limit"), "", status);
        }
        foreach (var window in AnthropicWindows)
        {
            if (Header(headers, $"anthropic-ratelimit-{window}-reset") is not { Length: > 0 } text) continue;
            if (Instant(text) is not { } at) continue;
            // A reset already behind us is stale ("0" or a past instant): no window of this length is running.
            if (at <= now) continue;
            return new KeyProbeResult(true, at, window, "", status);
        }
        return KeyProbeResult.Empty() with { Status = status };
    }

    private static KeyProbeResult OpenAi(IReadOnlyDictionary<string, string> headers, DateTimeOffset now, int status)
    {
        foreach (var (name, window) in new[] { ("x-ratelimit-reset-requests", "requests"), ("x-ratelimit-reset-tokens", "tokens") })
        {
            if (Header(headers, name) is not { Length: > 0 } text) continue;
            if (Duration(text) is not { } span || span <= TimeSpan.Zero) continue;
            return new KeyProbeResult(true, now + span, window, "", status);
        }
        return KeyProbeResult.Empty() with { Status = status };
    }

    // A header lookup that ignores case, because HTTP header names are case-insensitive and the services differ in how
    // they spell theirs. A dictionary built with OrdinalIgnoreCase already answers this; the fallback covers the rest.
    private static string? Header(IReadOnlyDictionary<string, string> headers, string name)
    {
        if (headers.TryGetValue(name, out var direct)) return direct;
        foreach (var (key, value) in headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    // "120" seconds, or an HTTP date, as Retry-After allows.
    private static DateTimeOffset? RetryAfter(string text, DateTimeOffset now)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0 && seconds < 31_536_000)
            return now.AddSeconds(seconds);
        return Instant(text) is { } at && at > now ? at : null;
    }

    private static DateTimeOffset? Instant(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        // A plain number is seconds since the epoch, as Anthropic's unified header sends it.
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) && seconds > 0)
            return FromUnixSeconds(seconds);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at : null;
    }

    private static DateTimeOffset? FromUnixSeconds(long seconds)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // "1s", "6m0s", "1h2m3s", "500ms", and a bare number of seconds. Times the services send are short; anything past a
    // year is a parsing accident and is refused rather than shown as a reset decades away.
    public static TimeSpan? Duration(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double bare))
            return bare > 0 && bare < 31_536_000 ? TimeSpan.FromSeconds(bare) : null;
        double total = 0; int index = 0; bool any = false;
        while (index < text.Length)
        {
            int start = index;
            while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.')) index++;
            if (index == start) return null;
            if (!double.TryParse(text[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
            int unitStart = index;
            while (index < text.Length && !char.IsDigit(text[index]) && text[index] != '.') index++;
            string unit = text[unitStart..index].Trim().ToLowerInvariant();
            double factor = unit switch
            {
                "ms" => 0.001, "s" or "" => 1, "m" => 60, "h" => 3600, "d" => 86400, _ => double.NaN
            };
            if (double.IsNaN(factor)) return null;
            total += value * factor; any = true;
        }
        if (!any || total <= 0 || total >= 31_536_000) return null;
        return TimeSpan.FromSeconds(total);
    }
}
