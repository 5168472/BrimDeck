using BrimDeck.Core;

internal static class VerificationCodeTests
{
    public static void Run(Action<string, bool> check)
    {
        // The ordinary case: the word that names the code is right next to it.
        check("A code after the word that names it is found",
            VerificationCode.Find("您的验证码是 123456，5 分钟内有效。") == "123456");
        check("A code before the word that names it is found",
            VerificationCode.Find("123456 是您的验证码") == "123456");
        check("The English wording works too",
            VerificationCode.Find("Your verification code is 839201") == "839201");

        // A code in the subject line is a code.
        check("A code in the subject is found",
            VerificationCode.Find("【某网站】登录验证码：556677") == "556677");

        // What must not be taken for a code: the numbers that appear in a mail for other reasons.
        check("An order number is not a code",
            VerificationCode.Find("您的订单号是 20260303，我们已发货") is null);
        check("An amount is not a code",
            VerificationCode.Find("本次消费金额 ¥1234.56 元，感谢惠顾") is null);
        check("A phone number is not a code",
            VerificationCode.Find("客服电话 13800138000，服务时间为 9:00-18:00") is null);
        check("A date is not a code",
            VerificationCode.Find("您的订单将于 2026-03-05 出货") is null);
        check("A bare number with no word is not a code",
            VerificationCode.Find("本次共 5 件商品，合计 200 元") is null);

        // A code written in full width is still a code, and is read in its half-width form.
        check("A code in full width is found",
            VerificationCode.Find("验证码：１２３４５６") == "123456");

        // A code with a space in it is one code.
        check("A code written in two groups is found",
            VerificationCode.Find("verification code: 123 456") == "123 456");

        // Six digits on their own line, with the word above: that is the usual shape of these mails.
        check("A code on its own line under the word is found",
            VerificationCode.Find("您的验证码\n887766\n请勿泄露") == "887766");

        // Nothing in the text at all.
        check("Text without a number has no code", VerificationCode.Find("hello") is null);
        check("Empty text has no code", VerificationCode.Find("") is null);

        // The normalization is what makes the full-width and spaced forms readable.
        var normalized = VerificationCode.Normalize("ＡＢ　１２３");
        check("Full-width letters and digits are folded", normalized == "AB 123");
        check("An invisible character is dropped", VerificationCode.Normalize("12\u200B34") == "1234");
    }
}
