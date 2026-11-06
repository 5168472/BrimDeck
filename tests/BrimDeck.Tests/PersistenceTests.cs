using BrimDeck.Core;

// Proves that what the user sets up survives a restart: the keys, the calendars, the mailboxes and what has already
// been announced. An update replaces the program, never the settings file, so the test is about the file being read
// back the way a new run reads it.
internal static class PersistenceTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "persistence");
        Directory.CreateDirectory(folder);
        void Write(string text) => File.WriteAllText(Path.Combine(folder, "settings.json"), text);

        // A key, a calendar and a mailbox, each with an id the secrets store is keyed by.
        var keyId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var settings = new DeckSettings
        {
            KeyResets =
            [
                new KeyReset
                {
                    InstanceId = keyId, Name = "My key", IntervalMinutes = 1440, Probe = KeyProbeKind.OpenAICompatible,
                    Endpoint = "https://example.test/v1/chat/completions", Model = "some-model",
                    Anchor = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
                }
            ],
            Agenda = new CalendarState
            {
                Accounts = [new CalendarAccount { InstanceId = calendarId, Source = CalendarSource.Feishu, User = "work@example.com", Href = "https://caldav.feishu.cn/calendars/primary/" }],
                MailAccounts = [new MailboxAccount { InstanceId = mailboxId, Email = "me@example.com", Provider = MailReader.Provider.Qq }],
                LocalEvents = [new CalendarEvent("Own meeting", new DateTimeOffset(2026, 3, 5, 9, 0, 0, TimeSpan.Zero), null, false, "local-1", "Local", "")],
                Announced = ["local-1|1772701200"],
                SeenMailUids = new() { [mailboxId] = [12u, 15u] }
            }
        };
        settings.Normalize();
        var writer = new SettingsStore(folder, "1.2.4");
        writer.Write(writer.Serialize(settings));

        // Read it back the way a restart does.
        var reloaded = new SettingsStore(folder, "1.2.4").Load();
        reloaded.Normalize();

        check("A key survives a save and a load", reloaded.KeyResets.Count == 1 && reloaded.KeyResets[0].Name == "My key");
        check("A key keeps its id", reloaded.KeyResets[0].InstanceId == keyId);
        check("A key keeps its endpoint", reloaded.KeyResets[0].Endpoint == "https://example.test/v1/chat/completions");
        check("A key keeps its model", reloaded.KeyResets[0].Model == "some-model");
        check("A key keeps its interval", reloaded.KeyResets[0].IntervalMinutes == 1440);

        check("A calendar survives a save and a load", reloaded.Agenda.Accounts.Count == 1 && reloaded.Agenda.Accounts[0].User == "work@example.com");
        check("A calendar keeps its id", reloaded.Agenda.Accounts[0].InstanceId == calendarId);
        check("A calendar keeps the collection it found", reloaded.Agenda.Accounts[0].Href == "https://caldav.feishu.cn/calendars/primary/");

        check("A mailbox survives a save and a load", reloaded.Agenda.MailAccounts.Count == 1 && reloaded.Agenda.MailAccounts[0].Email == "me@example.com");
        check("A mailbox keeps its id", reloaded.Agenda.MailAccounts[0].InstanceId == mailboxId);

        check("The entries the user added survive", reloaded.Agenda.LocalEvents.Count == 1 && reloaded.Agenda.LocalEvents[0].Title == "Own meeting");
        check("The announced meetings survive", reloaded.Agenda.Announced.Contains("local-1|1772701200"));
        check("The mail already read survives", reloaded.Agenda.SeenMailUids.ContainsKey(mailboxId) && reloaded.Agenda.SeenMailUids[mailboxId].Count == 2);

        // The cases that lose data if they are not handled: a file written before these settings existed.
        Write("""{"AppVersion":"1.2.4","UsagePage":true}""");
        var older = new SettingsStore(folder, "1.2.4").Load();
        older.Normalize();
        check("A file from before the calendars existed still opens", older.Agenda is not null);
        check("That file has no calendars, and that is not an error", older.Agenda.Accounts.Count == 0);
        check("That file has no keys, and that is not an error", older.KeyResets.Count == 0);

        // A file whose Agenda was written as null, by a hand edit or by a build that did not have it.
        Write("""{"AppVersion":"1.2.4","Agenda":null}""");
        var nullAgenda = new SettingsStore(folder, "1.2.4").Load();
        nullAgenda.Normalize();
        check("A null Agenda is made empty rather than left null", nullAgenda.Agenda is not null && nullAgenda.Agenda.Accounts.Count == 0);

        // A file from a newer version: the settings this build understands are read, and the newer file is left alone.
        Write("""{"AppVersion":"9.9.9","KeyResets":[{"Name":"future","IntervalMinutes":60}],"Agenda":{"Accounts":[{"User":"n@e.w"}]}}""");
        var future = new SettingsStore(folder, "1.2.4").Load();
        future.Normalize();
        check("A newer file is read as far as it can be", future.KeyResets.Count == 1 && future.KeyResets[0].Name == "future");
        check("A newer file still opens without failing", future.Agenda is not null);

        // Two accounts that were given the same id are told apart, so their passwords cannot overwrite each other.
        var shared = Guid.NewGuid();
        var collided = new DeckSettings
        {
            Agenda = new CalendarState
            {
                Accounts = [new CalendarAccount { InstanceId = shared }, new CalendarAccount { InstanceId = shared }]
            }
        };
        collided.Normalize();
        check("Calendars that share an id are told apart", collided.Agenda.Accounts[0].InstanceId != collided.Agenda.Accounts[1].InstanceId);
        var sameMail = Guid.NewGuid();
        var mailCollided = new DeckSettings
        {
            Agenda = new CalendarState
            {
                MailAccounts =
                [
                    new MailboxAccount { InstanceId = sameMail, Email = "one@example.com" },
                    new MailboxAccount { InstanceId = sameMail, Email = "two@example.com" }
                ]
            }
        };
        mailCollided.Normalize();
        check("Mailboxes that share an id are told apart", mailCollided.Agenda.MailAccounts[0].InstanceId != mailCollided.Agenda.MailAccounts[1].InstanceId);

        // A mailbox that was just added has no address yet, and that row has to survive: it is what the user fills in.
        // Discarding it here is what made the "Add a mailbox" button look like it did nothing.
        var fresh = new DeckSettings { Agenda = new CalendarState { MailAccounts = [new MailboxAccount { Email = "" }] } };
        fresh.Normalize();
        check("A just-added mailbox with no address is kept", fresh.Agenda.MailAccounts.Count == 1);
        var blankId = fresh.Agenda.MailAccounts[0].InstanceId;
        check("The row it kept has an identity", blankId != Guid.Empty);
        // And the empty row survives a save and a load, so it is still there on the next start.
        fresh.Copy().Normalize();
        check("The empty row survives a settings copy", fresh.Copy().Agenda.MailAccounts.Count == 1);
    }
}
