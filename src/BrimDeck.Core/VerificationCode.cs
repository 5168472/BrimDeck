using System.Text;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// Finds a verification code in a mail body.
//
// This is deliberately cautious, because showing the wrong number is worse than showing none: a person would copy
// an order number or a price and be told it is a code. The rules that keep that from happening:
//
//  - The number must be near a word that names it (验证码, verification code, and the like), or stand on its own
//    line, or follow a colon. A bare six-digit number in a paragraph is not a code.
//  - A number that looks like something else is refused: an order number, an amount, a phone number, a date, a
//    time, an address that has a web link in it.
//  - The strongest words carry the most weight, and a shape on its own carries little: "123456" alone is weak, but
//    "验证码 123456" is not.
public static class VerificationCode
{
    private const int Threshold = 65;

    // The words that name a code outright. Near one of these, the number is a code.
    private static readonly string[] StrongBefore =
    [
        "验证码", "校验码", "确认码", "动态码", "动态密码", "安全码", "登录码", "激活码", "一次性密码", "一次性口令",
        "otp", "one-time code", "one time code", "one-time password", "one time password",
        "verification code", "security code", "authentication code", "auth code", "passcode"
    ];
    private static readonly string[] StrongAfter =
    [
        "验证码", "校验码", "确认码", "动态码", "动态密码", "安全码", "登录码", "激活码", "一次性密码", "一次性口令",
        "otp", "one-time code", "one time code", "one-time password", "one time password",
        "verification code", "security code", "authentication code", "auth code", "passcode", "code is", "code:"
    ];

    // The words that only hint at it. Near one of these the number might be a code, which is not enough on its own.
    private static readonly string[] WeakBefore =
    [
        "登录", "登陆", "验证", "校验", "身份", "确认", "安全",
        "sign in", "sign-in", "log in", "log-in", "login", "verify", "verification", "confirm", "authenticate"
    ];

    // The things a number in a mail is usually not. One of these near the number refuses it.
    private static readonly string[] NotBefore =
    [
        "订单", "单号", "快递", "运单", "物流", "包裹", "编号", "序号", "发票", "金额", "合计", "实付", "应付",
        "电话", "手机号", "会议号", "会议", "卡尾号", "尾号", "邮编",
        "order", "track", "invoice", "amount", "phone", "meeting", "card ending", "sku"
    ];
    private static readonly string[] NotAfter =
    [
        "订单", "单号", "快递", "运单", "物流", "包裹", "编号", "序号", "发票", "金额", "合计", "实付", "应付",
        "电话", "手机号", "会议号", "会议", "卡尾号", "尾号", "邮编",
        "order", "track", "invoice", "amount", "phone", "meeting", "card ending", "sku"
    ];
    private static readonly string[] Money =
        ["¥", "￥", "$", "€", "元", "价格", "金额", "合计", "实付", "应付"];

    // The shape of the candidate: a run of four to eight digits or letters, optionally in two groups of three or four
    // joined by a dash or a space.
    private static readonly Regex Candidate = new(
        @"(?:[A-Za-z]{1,3}-)?(?:\d{3,4}[- ]\d{3,4}|\d{4}-\d{4}|[A-Za-z0-9]{3,4}-[A-Za-z0-9]{3,4}|\d{4,8}|[A-Za-z0-9]{4,8})",
        RegexOptions.Compiled);

    public static string? Find(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var body = Normalize(text);
        string? best = null;
        int bestScore = 0;
        foreach (Match match in Candidate.Matches(body))
        {
            var token = match.Value;
            if (token.Length == 0) continue;
            int start = match.Index, end = start + token.Length;
            string before = start >= 40 ? body[..start] : body[..start];
            string after = end < body.Length ? body[end..Math.Min(body.Length, end + 40)] : "";
            string wider = end < body.Length ? body[end..Math.Min(body.Length, end + 60)] : "";
            string earlier = start >= 60 ? body[Math.Max(0, start - 60)..start] : body[..start];

            int score = Score(token, before, after, wider, earlier, body, start, end);
            if (score > bestScore) { bestScore = score; best = token; }
        }
        return bestScore >= Threshold ? best : null;
    }

    private static int Score(string token, string before, string after, string wider, string earlier, string body, int start, int end)
    {
        // The shape alone says little; the words around it say much more.
        int score = Shape(token);

        bool strongBefore = Has(before, StrongBefore);
        bool strongAfter = Has(after, StrongAfter);
        bool weakBefore = Has(earlier, WeakBefore);
        bool ownLine = start == 0 || body[start - 1] is '\n' or '\r';
        bool colon = start > 0 && body[start - 1] == ':' || start > 0 && body[start - 1] == '：';

        if (strongBefore) score += 40;
        else if (weakBefore) score += 15;
        if (strongAfter) score += 28;
        if (ownLine) score += 15;
        if (colon) score += 10;

        // Without one of these the number is not a code, however good its shape: this is what keeps an order number
        // out of the answer.
        if (!strongBefore && !strongAfter && !ownLine && !colon) return 0;

        // The things that mean the number is something else.
        if (token.Contains("http", StringComparison.OrdinalIgnoreCase) || token.Contains("www.", StringComparison.OrdinalIgnoreCase)
            || token.Contains('/') || token.Contains('@')) return 0;
        if (Has(wider, Money) || Has(earlier, Money)) return 0;
        if (Has(earlier, NotBefore) || Has(after, NotAfter)) return 0;
        // A date or a time near the number: a day, a month, or an hour and minute.
        if (Regex.IsMatch(earlier, @"\d{4}[-/年]\d{1,2}") || Regex.IsMatch(wider, @"\d{1,2}:\d{2}")) return 0;
        if (Regex.IsMatch(earlier, @"[年月]|有效期至")) return 0;
        // A bare year is a year, not a code.
        if (token.Length == 4 && int.TryParse(token, out int year) && year is >= 1900 and <= 2100) score -= 35;

        return score;
    }

    // What the shape of the number is worth on its own.
    private static int Shape(string token)
    {
        string plain = token.Replace("-", "").Replace(" ", "");
        bool digits = plain.Length > 0 && plain.All(char.IsDigit);
        bool separated = token.Contains('-') || token.Contains(' ');
        if (digits)
        {
            return plain.Length switch
            {
                6 => separated ? 28 : 30,
                7 or 8 => separated ? 22 : 18,
                5 => 20,
                4 => 16,
                _ => 10
            };
        }
        // Letters and digits mixed: only a run of a sensible length is considered at all.
        if (plain.Length < 5) return 0;
        return plain.Length switch { 6 => separated ? 22 : 20, _ => 14 };
    }

    // Whether one of the words appears in the text.
    private static bool Has(string text, string[] words)
    {
        foreach (var word in words)
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The text as it is read: full-width characters folded to half-width, the invisible ones removed, so a code
    // written with spaces between its digits is still one code.
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // The invisible characters are dropped: a code written with one between its digits is still one code.
            // They are named by their code points, because a zero-width character written in the source is
            // invisible to whoever reads it, and the compiler refuses some of them outright.
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF' or '\u2060') continue;
            // The full-width forms are folded to their half-width ones, so a code typed in full width is read.
            if (ch == '\u3000') { builder.Append(' '); continue; }
            if (ch >= '\uFF01' && ch <= '\uFF5E') { builder.Append((char)(ch - 0xFEE0)); continue; }
            builder.Append(ch);
        }
        var result = builder.ToString();
        return result.Length > 6000 ? result[..6000] : result;
    }
}
