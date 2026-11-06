using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BrimDeck.Core;

// Probes the AI keys the user configured and keeps each one's reset time up to date.
//
// One probe every hour on the hour, which is what makes the times precise: a probe that reaches the service learns
// the real reset from its headers (KeyProbeParser), and a probe that only reaches it while the window is running
// learns the period from the gap between two resets. Between probes nothing needs to run: the panel counts down from
// the last reset it learned, so a probe missed while the machine was asleep costs nothing.
//
// A probe sends a deliberately tiny request to the service's own usage endpoint. For Anthropic that is a one-token
// message, which is billed as a rounding error and is the only way its headers can be seen; a key with no credit and
// no quota answers 400 or 429 and is read the same way, because both carry the reset headers. Nothing about the
// user's prompts or history is sent: the request holds a single token and nothing else.
public sealed class KeyProber(HttpClient http, IProviderSecrets secrets)
{
    // The requirement is a probe per hour; a request that is still running an hour later is abandoned rather than
    // allowed to pile up.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    // The body Anthropic needs for a request to reach its rate-limit layer. One token in, no token out.
    private static readonly string AnthropicBody = """{"model":"claude-haiku-4-5-20251001","max_tokens":1,"messages":[{"role":"user","content":"."}]}""";
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Reads one key. The key is fetched from secrets.dat by the caller's instance id, so it never sits in the settings
    // file and never travels with a copy of the settings.
    //
    // Two questions are answered separately, because they are separate in practice: does the key work (the service
    // accepted it and did not reject the credential), and did it say when the allowance comes back. OpenAI-compatible
    // gateways, MiniMax among them, answer a correct key with 200 and send no rate-limit headers at all, so a key can
    // pass while saying nothing about a reset. Reporting that as a failure would be wrong: the key is fine, the service
    // simply does not publish the time, and the panel's learned interval carries the countdown instead.
    public async Task<KeyProbeResult> ProbeAsync(KeyReset key, CancellationToken cancellation)
    {
        var secret = secrets.Read(key.InstanceId);
        if (secret.Length == 0) return KeyProbeResult.Failed(Loc.T("未设置密钥", "No key set"));
        // One probe at a time: the services rate-limit, and an hourly cadence has no reason to run them in parallel.
        await _gate.WaitAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(Timeout);
            var (status, headers, body) = await SendAsync(key, secret, timeout.Token);
            var result = KeyProbeParser.Parse(key.Probe, status, headers, DateTimeOffset.Now);
            int code = (int)status;

            // Refused for a reason about the key itself: that is a real failure and must say so.
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return KeyProbeResult.Failed(Loc.T("密钥被拒绝", "Key rejected"), code);

            // Reached, but the service reports a problem in its body instead of its status. MiniMax answers 200 with
            // {"base_resp":{"status_code":2069,"status_msg":"..."}} for a model it no longer serves, which is exactly the
            // case a person testing a key needs to be told about.
            if (BodyError(body) is { } complaint)
                return KeyProbeResult.Failed(complaint, code);

            // A client error that is not about the key (a bad endpoint, a bad request body) still means no connection
            // to the service's own accounting, so it is a failure with the code it came with.
            if ((int)status >= 400 && status is not HttpStatusCode.TooManyRequests)
                return KeyProbeResult.Failed(Loc.T($"服务返回 {code}", $"The service answered {code}"), code);

            // The key works. What it could or could not say about the reset is reported as a note, never as a failure.
            if (result.ResetAt is null)
            {
                string note = status is HttpStatusCode.TooManyRequests
                    ? Loc.T("已被限流，但服务没有说明何时恢复", "Rate limited, and the service did not say when it reopens")
                    : Loc.T("连接正常，但服务没有返回重置时间", "Connected, but the service returns no reset time");
                return result with { Note = note, Status = code };
            }
            return result with { Status = code };
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return KeyProbeResult.Failed(Loc.T("探测超时", "Probe timed out"));
        }
        catch (HttpRequestException ex)
        {
            return KeyProbeResult.Failed(Loc.T("无法连接", "Cannot reach") + ": " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    // Sends the probe and hands back what came with the answer: the code, every header, and the body (a service that
    // reports errors in JSON needs it, and it is a few hundred bytes at most).
    private async Task<(HttpStatusCode Status, Dictionary<string, string> Headers, string Body)> SendAsync(
        KeyReset key, string secret, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, key.ProbeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (key.Probe == KeyProbeKind.Anthropic)
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(AnthropicBody, Encoding.UTF8, "application/json");
        }
        else request.Content = new StringContent(OpenAiBody(key), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers) headers[name] = string.Join(",", values);
        foreach (var (name, values) in response.Content.Headers) headers[name] = string.Join(",", values);
        // A body is read only up to a small limit: a long one is never needed and would only be held in memory.
        string body = "";
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var buffer = new byte[2048];
            int read = await stream.ReadAsync(buffer, cancellation);
            if (read > 0) body = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException) { }
        return (response.StatusCode, headers, body);
    }

    // The model an OpenAI-compatible request names. These gateways refuse a model they no longer serve, and the names
    // change (MiniMax retired M2 while serving M3.1), so the name is a setting rather than a constant, with a default
    // that the newest family uses.
    private static string OpenAiBody(KeyReset key) =>
        $$"""{"model":"{{(key.Model.Length > 0 ? key.Model : "gpt-4o-mini")}}","max_tokens":1,"messages":[{"role":"user","content":"."}]}""";

    // A service that answers 200 and states the problem in the body. The two shapes seen in practice: OpenAI-style
    // {"error":{"message":...}} and MiniMax's {"base_resp":{"status_code":...,"status_msg":...}}. A zero status_code and
    // a success-shaped body are both silence, not a complaint.
    private static string? BodyError(string body)
    {
        if (body.Length == 0) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var error = JsonValue.Get(root, "error");
            if (error.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var message = error.Get("message").Text();
                if (message.Length > 0) return message;
            }
            var baseResp = JsonValue.Get(root, "base_resp");
            if (baseResp.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var code = baseResp.Get("status_code").Number();
                var message = baseResp.Get("status_msg").Text();
                // 0 is success for this API; anything else is the service refusing the request.
                if (code is { } value && value != 0 && message.Length > 0) return message;
            }
            return null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // The next time a probe is due: the coming hour, so the cadence follows the clock rather than the start of the run.
    public static DateTimeOffset NextHourly(DateTimeOffset now)
    {
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(1);
        return next;
    }

    // A test the user asked for, run at once instead of on the hour. It is the same request and the same parser as the
    // scheduled probe, so a passing test means the hourly probes will work; the only difference is that it happens now
    // and its answer is returned rather than saved by the caller.
    //
    // It is deliberately not rate-shed by Due(): pressing the button is an explicit request, and the answer to "is this
    // key good" has to be current. The one-per-hour rule belongs to the background cadence.
    public Task<KeyProbeResult> TestAsync(KeyReset key, CancellationToken cancellation) => ProbeAsync(key, cancellation);

    // Whether a key is due for a probe: hourly, unless one ran within the hour already, so a restart in the same hour
    // does not spend a request twice.
    public static bool Due(KeyReset key, DateTimeOffset now) =>
        key.ProbeEnabled && key.Enabled && (key.ProbedAt is not { } last || now - last >= TimeSpan.FromHours(1));
}
