using BrimDeck.Core;
using MailKit;
using MimeKit;

// The two things that made a configured mailbox show nothing, kept as tests so they cannot come back:
//
//  - A message that is not multipart has one part, and that part's specifier is the empty string. The reader used
//    to skip every part with no specifier, which threw away exactly those messages.
//  - A poll that finds nothing new must not empty the page. The first read fills it, every later poll finds only what
//    came after the newest UID, and replacing the list with that left the page blank two minutes later.
internal static class MailReadTests
{
    public static void Run(Action<string, bool> check)
    {
        // A single-part message describes the whole body as one part with no specifier. It is the body, not a part
        // to skip.
        var single = new BodyPartText(new ContentType("text", "html"), "");
        var found = MailReader.TextPartsOf(single).ToList();
        check("A single-part message's part is read", found.Count == 1);
        check("That part is the one the server described", found.Count == 1 && found[0].PartSpecifier == "");
        check("The part is shown as the body, whatever type it is", found.Count == 1 && found[0].ContentType?.MimeType == "text/html");

        // A multipart message's text parts are found through the tree, at every depth.
        var plain = new BodyPartText(new ContentType("text", "plain"), "1.1");
        var markup = new BodyPartText(new ContentType("text", "html"), "1.2");
        var picture = new BodyPartBasic(new ContentType("image", "png"), "2");
        var alternative = new BodyPartMultipart(new ContentType("multipart", "alternative"), "");
        alternative.BodyParts.Add(plain);
        alternative.BodyParts.Add(markup);
        var mixed = new BodyPartMultipart(new ContentType("multipart", "mixed"), "");
        mixed.BodyParts.Add(alternative);
        mixed.BodyParts.Add(picture);
        var parts = MailReader.TextPartsOf(mixed).ToList();
        check("A multipart message's text parts are found", parts.Count == 2);
        check("The plain part is among them", parts.Any(part => part.PartSpecifier == "1.1"));
        check("The markup part is among them", parts.Any(part => part.PartSpecifier == "1.2"));
        check("The picture is not fetched", parts.All(part => part.PartSpecifier != "2"));

        // An attachment is not the body, even when it is text: a .txt file sent with the mail is not what the panel
        // shows, and downloading it is what the reader promises not to do.
        var attached = new BodyPartText(new ContentType("text", "plain"), "3");
        attached.ContentDisposition = new ContentDisposition("attachment");
        var withAttachment = new BodyPartMultipart(new ContentType("multipart", "mixed"), "");
        withAttachment.BodyParts.Add(plain);
        withAttachment.BodyParts.Add(attached);
        check("An attached text file is not read as the body", MailReader.TextPartsOf(withAttachment).All(part => part.PartSpecifier != "3"));

        // A forwarded mail: the text inside the nested message is found too.
        var nestedBody = new BodyPartText(new ContentType("text", "plain"), "2.1");
        var forwarded = new BodyPartMessage(new ContentType("message", "rfc822"), "2");
        forwarded.Body = nestedBody;
        var outer = new BodyPartMultipart(new ContentType("multipart", "mixed"), "");
        outer.BodyParts.Add(plain);
        outer.BodyParts.Add(forwarded);
        check("A forwarded mail's own text is found", MailReader.TextPartsOf(outer).Any(part => part.PartSpecifier == "2.1"));

        // A message with no structure at all yields nothing, rather than throwing.
        check("A message with no structure yields no parts", !MailReader.TextPartsOf(null).Any());

        // The mail the page shows is the mail every mailbox last read. A poll that finds nothing new adds nothing and
        // removes nothing: the mailbox's own list is what is kept between polls, and the merged list is built from it.
        // This is the second bug — the first read filled the page and each later poll found only the mail after the
        // newest UID, so the page went blank two minutes later.
        var newer = new MailItem("me@example.com", "a@b.c", "newer", DateTimeOffset.Now, "body", null, 8);
        var older = new MailItem("me@example.com", "a@b.c", "older", DateTimeOffset.Now.AddHours(-3), "body", null, 7);
        check("One mailbox's mail is on the page", AgendaService.MergeMail([[older, newer]], 60).Count == 2);
        check("The newest mail comes first", AgendaService.MergeMail([[older, newer]], 60)[0].Subject == "newer");
        // Two mailboxes are put together rather than one replacing the other.
        var other = new MailItem("you@example.com", "c@d.e", "other", DateTimeOffset.Now.AddHours(-1), "body", null, 3);
        var mergedMail = AgendaService.MergeMail([[older, newer], [other]], 60);
        check("The mail of two mailboxes is put together", mergedMail.Count == 3);
        check("The merge is in the order the mail arrived", mergedMail.Select(item => item.Subject).SequenceEqual(["newer", "other", "older"]));
        // A poll that found nothing new contributes nothing, and the mailbox that did read still shows what it read.
        check("A mailbox that found nothing new does not empty the page", AgendaService.MergeMail([[older, newer], []], 60).Count == 2);
        // The page shows the newest few, not every message ever read.
        check("Only the newest few are kept", AgendaService.MergeMail([[older, newer], [other]], 2).Count == 2);
        // With nothing read at all the page is empty, which is the one case where it should be.
        check("No mail read means an empty page", AgendaService.MergeMail([], 60).Count == 0);

        // A mailbox that was removed must not keep its mail on the page.
        var agenda = NewAgenda();
        agenda.ForgetMail(Guid.NewGuid());
        check("Forgetting a mailbox by an id it never had changes nothing", agenda.Mail.Count == 0);

        // What a mail shows: the summary is one flat line of the first two hundred characters, and a verification
        // code is taken out of the body.
        var body = "您的验证码是 483920，五分钟内有效。";
        check("A verification code is read out of a mail", MailReader.CodeFrom(body) == "483920");
        var text = MailReader.HtmlToText("<div>hello<br/>world</div>");
        check("A markup body is read as text", text.Contains("hello") && text.Contains("world"));
        check("A mail with no body reads as no text", MailReader.HtmlToText("") == "");
    }

    private sealed class StubSecrets : IProviderSecrets
    {
        public string Read(Guid instanceId) => "";
        public void Write(Guid instanceId, string secret) { }
        public void Delete(Guid instanceId) { }
    }

    // A service with nothing configured and nothing reachable: the tests here are about what it holds, not about what
    // it can read from a server.
    private static AgendaService NewAgenda() =>
        new(new CalDavClient(new HttpClient()), new EwsCalendarClient(new HttpClient()), new MailReader(new StubSecrets()), new StubSecrets());
}
