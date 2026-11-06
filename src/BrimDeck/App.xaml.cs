using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _settingsEvent;
    private RegisteredWaitHandle? _listener;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public DeckSettings Settings { get; private set; } = new();
    public SettingsStore Store { get; private set; } = null!;
    public UsageService Usage { get; private set; } = null!;
    public ProviderSecrets Secrets { get; private set; } = null!;
    public Pricing Prices { get; private set; } = null!;
    // Reads the chat clients once the user turns message notices on. Built with the other services so the settings page
    // can ask a source whether it can run here even before the feature is on.
    public MessageWatcher Messages { get; private set; } = null!;
    // Probes the AI keys the user configured, hourly, and keeps each one's reset time current.
    public KeyProber Prober { get; private set; } = null!;
    // Reads the work calendars and the mailboxes.
    public AgendaService Agenda { get; private set; } = null!;
    public AppUpdates Updates { get; internal set; } = null!;
    // The main panel. It owns the tray icon, reads the usage data and takes the settings preview; every other chosen
    // monitor gets a panel that shows the same data.
    public MainWindow Deck { get; private set; } = null!;
    private readonly Dictionary<string, MainWindow> _otherDecks = [];
    public IEnumerable<MainWindow> Decks => _otherDecks.Values.Prepend(Deck);
    public IReadOnlyList<DisplayInfo> Displays { get; private set; } = [];
    private bool _arrangePending;
    // Every panel shows the one set of players, and their lyrics are looked up once.
    internal MediaSessions? Media { get; private set; }
    internal event Action? MediaChanged;
    internal LyricsService Lyrics { get; private set; } = null!;
    // A window being dragged by its title bar or edge; the panels do not open on it (see MainWindow.TrackPointer).
    internal WindowDrags WindowDrags { get; private set; } = null!;
    private readonly HashSet<MainWindow> _presentingMedia = [], _presentingSystemAudio = [];
    // The system monitor page's readings, shared by every panel; created when a panel first shows the page.
    internal SystemSampler? Monitor { get; private set; }
    internal event Action<MonitorSnapshot>? MonitorUpdated;
    private readonly HashSet<MainWindow> _presentingMonitor = [], _stripMonitor = [];
    // The panels whose open monitor page shows the power line (PresentMonitor): the power is read for those, and for a row that chose it.
    private readonly HashSet<MainWindow> _presentingPower = [];
    public CancellationTokenSource Lifetime { get; } = new();
    public bool Exiting { get; private set; }

    // Tooltip delays are not inherited, so the defaults are replaced for every element before any window exists.
    // The system hover time on some machines is a full second; every tooltip here opens after the same short pause.
    static App()
    {
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(120));
        System.Windows.Controls.ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(400));
        System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));
    }

    // The element whose tooltip opens next (OnStartup).
    private WeakReference<DependencyObject>? _toolTipOwner;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Menus and tooltips take the text rendering of what they open from as they open (PanelTheme.Text). A tooltip opened by
        // the service has no placement target, so its owner is noted as the service raises ToolTipOpening on it.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent,
            new System.Windows.Controls.ToolTipEventHandler((_, args) => _toolTipOwner = new WeakReference<DependencyObject>((DependencyObject)args.OriginalSource)), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) =>
        {
            var tip = (System.Windows.Controls.ToolTip)sender;
            PanelTheme.Text(tip, tip.PlacementTarget ?? (_toolTipOwner?.TryGetTarget(out var owner) == true ? owner : null));
        }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => PanelTheme.Text((System.Windows.Controls.ContextMenu)sender, ((System.Windows.Controls.ContextMenu)sender).PlacementTarget)));
        Resources[SystemParameters.VerticalScrollBarWidthKey] = 7.0;
        var suffix = Environment.UserName;
        var eventName = "Local\\BrimDeck.Settings." + suffix;
        _mutex = new Mutex(true, "Local\\BrimDeck.Instance." + suffix, out var first);
        if (!first)
        {
            try { using var existing = EventWaitHandle.OpenExisting(eventName); existing.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        SetCurrentProcessExplicitAppUserModelID("BrimDeck.Desktop");
        RecordCrashes();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
        Store = new SettingsStore(folder); Settings = Store.Load();
        // Before anything is read: a reading left unanswered when the last run ended stops now (SensorGuard).
        SensorGuard.Start(folder);
        UseLanguage();
        PanelTheme.Use(Settings);
        ScheduleDaylight();
        // "跟随系统" follows the Windows app theme while BrimDeck runs.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        // The language a first start takes from Windows is kept, even if the display language changes later.
        if (Store.IsFirstStart) FlushSettings();
        try
        {
            Settings.LaunchAtStartup = StartupRegistration.IsEnabled();
            // When moving from the old EXE to an installed copy, keep an enabled startup entry at the new stable path.
            if (Settings.LaunchAtStartup) StartupRegistration.SetEnabled(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        Prices = new Pricing(folder);
        Updates = new AppUpdates(BrimDeck.Updates.GitHubUpdates.Create(folder), folder);
        Secrets = new ProviderSecrets(folder);
        Lyrics = new LyricsService(appleLyricsFile: Path.Combine(folder, "apple-music-lyrics.json"));
        Usage = new UsageService(new DesktopSources(), secrets: Secrets, quotaCache: Path.Combine(folder, ClaudeQuotaCache.FileName),
            kimiQuotaCache: Path.Combine(folder, KimiQuotaCache.FileName));
        // The ledger starts from what the last run announced, so a message still on screen is not announced twice.
        var ledger = new MessageLedger();
        ledger.Seed(Settings.MessageSeen ?? []);
        Messages = new MessageWatcher(
            [new Native.UnreadCountSource(ChatApp.Feishu), new Native.UnreadCountSource(ChatApp.WeCom), new Native.UnreadCountSource(ChatApp.WeChat)],
            ledger);
        Prober = new KeyProber(KeyProbeHttp(), Secrets);
        Agenda = new AgendaService(new CalDavClient(AgendaHttp()), new EwsCalendarClient(AgendaHttp()), new MailReader(Secrets), Secrets);
        WindowDrags = new WindowDrags();
        Deck = new MainWindow(this); MainWindow = Deck;
        Displays = Native.Displays.Current(); AssignDisplays();
        Deck.Show();
        InitializeHotkeys();
        StartBackgroundUpdates();
        // Only for a user who has the monitor page with its power line: the rest never open it.
        if (Settings.MonitorPage && Settings.MonitorPower) PowerWarm.Start(Dispatcher);
        WarmSettingsWriter();
        DisplayPower.Install(Dispatcher);
        Trace("startup: reached the scheduling step");
        // A key read from an older file may carry an anchor off the hour, which Normalize puts on the hour in memory. The
        // file is written at once with the rounded value, so the countdown is on the hour from the first start and the
        // rounding is not repeated on every load. Written now rather than queued: a queued write is a timer away, and a
        // start that opened and closed the panel in between left the file as it was.
        if (Settings.KeyResetsMovedToHour) { FlushSettings(); Trace("startup: key anchors moved to the hour"); }
        ScheduleProbes();
        ScheduleAgenda();
        Trace("startup: agenda scheduled");
        _save.Tick += (_, _) => FlushSettings();
        ApplyTray();
        _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _listener = ThreadPool.RegisterWaitForSingleObject(_settingsEvent, (_, _) => Dispatcher.BeginInvoke(ShowFromRelaunch), null, Timeout.Infinite, false);
        if (e.Args.Contains("--settings")) OpenSettings();
        // A portable update that kept this version says why on the About page.
        else if (Updates.RestartFailed) OpenUpdateSettings();
    }
    // A resident app must not vanish without a trace. Unhandled errors are appended to crash.log in the data folder;
    // errors on the UI thread are logged and the app keeps running, and unobserved task errors are only logged.
    private void RecordCrashes()
    {
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "crash.log");
        void Write(object error)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                // The log keeps only recent entries.
                if (File.Exists(log) && new FileInfo(log).Length > 1_048_576) File.Delete(log);
                File.AppendAllText(log, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {ApplicationVersion.Current}\n{error}\n\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        DispatcherUnhandledException += (_, args) => { Write(args.Exception); args.Handled = !Exiting; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Write(args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Write(args.Exception); args.SetObserved(); };
    }
    // Names used by XAML templates follow the language through these resources.
    private void UseLanguage()
    {
        Loc.Use(Settings.Language);
        Resources["PinSourceName"] = Loc.T("固定显示", "Pin");
        Resources["UnpinSourceName"] = Loc.T("取消固定", "Unpin");
    }
    public void OpenSettings() => ShowSettings(false);
    // Starting BrimDeck while it runs shows the hidden panels as well as the settings, so the panels come back even without
    // the tray icon.
    private void ShowFromRelaunch()
    {
        if (Exiting) return;
        SetUserHidden(false); OpenSettings();
    }
    // Adds or removes the tray icon to match the settings.
    private void ApplyTray()
    {
        if (Settings.ShowTrayIcon && _tray is null) _tray = new TrayIcon(Deck, OpenSettings, ExitApplication, ToggleHidden, () => UserHidden);
        else if (!Settings.ShowTrayIcon && _tray is not null) { _tray.Dispose(); _tray = null; }
    }
    // The panel ends a collapsed preview when the pointer reaches it. The settings window clears its own preview state; the
    // panel is resumed either way, in case the two disagree (ResumeAfterPreview does nothing when no preview is on).
    internal void EndPreviewFromDeck()
    {
        _settingsWindow?.EndPreview();
        Deck.ResumeAfterPreview();
    }
    // The panel's update dot opens the About page with the update dialog.
    internal void OpenUpdateSettings() => ShowSettings(true);
    // After an update the gear's dot opens the About page with the What's new window.
    internal void OpenReleaseNotes() => ShowSettings(false, notes: true);
    private void ShowSettings(bool update, bool notes = false)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            // What the window built is garbage once it closes: given back like a panel's after it collapses.
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; RequestTrim(); };
            _settingsWindow.Show();
            if (Store.LoadWarning is { } warning) _settingsWindow.SaveStatus(warning, true);
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        _ = notes ? _settingsWindow.ShowReleaseNotesAsync() : update ? _settingsWindow.ShowUpdateNoticeAsync() : _settingsWindow.CheckUpdatesOnOpenAsync();
    }
    public void UpdateSettings(DeckSettings settings)
    {
        foreach (var previous in Settings.Apps)
        {
            var next = settings.Entry(previous.InstanceId);
            if (previous.SecretRevision != Guid.Empty && (next is null || next.QuotaSource != previous.QuotaSource))
            {
                try { Secrets.Delete(previous.InstanceId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { _settingsWindow?.SaveStatus(Loc.T("密钥文件无法更新，请检查本机设置目录后重试。", "The key file could not be updated. Check the local settings folder and try again."), true); return; }
            }
        }
        bool dataChanged = settings.IncludeWsl != Settings.IncludeWsl || !settings.RequiredProviders.SetEquals(Settings.RequiredProviders) ||
            !settings.EnabledApps.Select(e => e.ConfigurationKey).ToHashSet().SetEquals(Settings.EnabledApps.Select(e => e.ConfigurationKey));
        bool syncEnabled = settings.UsageAutoSync && !Settings.UsageAutoSync;
        // A new collapsed style morphs every panel's surface into its form (MainWindow.MorphShape).
        bool styleChanged = settings.Style != Settings.Style;
        settings.Normalize(); Settings = settings;
        ApplyHotkeys();
        ApplyTray();
        // A panel whose collapsed row changes asks again as it is built (PresentMonitor).
        UpdateMonitorExtras();
        // Statuses and notes are worded when the data is read, so a new language reads the data again.
        bool languageChanged = Loc.Normalize(settings.Language) != Loc.Language;
        if (languageChanged) UseLanguage();
        PanelTheme.Use(settings);
        ScheduleDaylight();
        // A key added, removed or turned off changes when the next probe is due.
        ScheduleProbes();
        // The same for the calendars and the mailboxes.
        ScheduleAgenda();
        AssignDisplays();        foreach (var deck in Decks.ToList()) { if (styleChanged) deck.MorphShape(); else deck.ApplySettings(); }
        QueueSave();
        CollapsedChanged();
        if (dataChanged || syncEnabled || languageChanged) _ = Deck.RefreshAutomaticallyAsync(queueIfBusy: true);
    }
    // The settings window's size group follows changes made on the panel (a drag, or content that changed the auto width).
    internal void CollapsedChanged() => _settingsWindow?.CollapsedChanged();
    private void SystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Exiting) return;
            // High contrast turned on or off, or its scheme changed, shows the system's colors on the panel (PanelTheme.HighContrast).
            if (PanelThemes.NormalizeId(Settings.PanelTheme) == PanelThemes.SystemId || SystemParameters.HighContrast || PanelTheme.HighContrast)
                ApplyPanelTheme(systemChanged: true);
            // The theme page's "System" card shows the Windows mode whichever theme is chosen.
            else _settingsWindow?.PanelThemeChanged(onlyThemePage: true);
        });
    }
    // Makes the chosen panel theme current (reading the theme folder again when asked) and repaints every panel if it changed.
    // When Windows switches "system" between dark and light, a preset clock gradient carries over as it does when a theme is chosen.
    internal void ApplyPanelTheme(bool reload = false, bool systemChanged = false)
    {
        var previous = PanelTheme.Current;
        bool wasHigh = PanelTheme.HighContrast;
        bool changed = PanelTheme.Use(Settings, reload, systemChanged);
        ScheduleDaylight();
        if (!changed) return;
        // Into or out of high contrast the theme itself has not changed, so the clock's gradient stays as the user left it.
        if (systemChanged && !wasHigh && !PanelTheme.HighContrast && PanelThemes.CarryGradient(previous, PanelTheme.Current, Settings)) QueueSave();
        foreach (var deck in Decks.ToList()) deck.ApplySettings();
        if (!reload) _settingsWindow?.PanelThemeChanged();
    }
    // The hourly probe of the configured AI keys. The timer is set for the coming hour rather than a fixed interval, so the
    // probes follow the clock, and a machine that slept past the hour catches up on waking. A failed probe is retried on
    // the next hour rather than sooner: the services rate-limit, and the schedule keeps answering in the meantime.
    private DispatcherTimer? _probe;
    internal void ScheduleProbes()
    {
        var keys = Settings.KeyResets.Where(key => key.Enabled && key.ProbeEnabled).ToList();
        if (keys.Count == 0) { _probe?.Stop(); return; }
        if (_probe is null)
        {
            _probe = new DispatcherTimer(DispatcherPriority.Background);
            _probe.Tick += async (_, _) => { _probe.Stop(); await ProbeKeysAsync().ConfigureAwait(true); ScheduleProbes(); };
        }
        // Due now (a key that has never been probed, or one whose hour has come) is done at once; otherwise the timer
        // waits for the coming hour, at most a minute ahead so a slept-through hour is caught on the next tick.
        var now = DateTimeOffset.Now;
        bool due = keys.Any(key => KeyProber.Due(key, now));
        var wait = due ? TimeSpan.FromSeconds(1) : KeyProber.NextHourly(now) - now;
        _probe.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds + 500, 1000, TimeSpan.FromMinutes(61).TotalMilliseconds));
        _probe.Stop(); _probe.Start();
    }
    // Probes every key that is due and writes back what each one learned. The requests run on the pool, so a slow service
    // never stalls the panel, and the write-back is marshalled to this thread: UpdateSettings touches the panels and the
    // display assignment, both of which belong to the UI thread, and continuing on the pool after an await (which
    // ConfigureAwait(false) does) would otherwise reach them from the wrong thread.
    private async Task ProbeKeysAsync()
    {
        var keys = Settings.KeyResets.Where(key => KeyProber.Due(key, DateTimeOffset.Now)).ToList();
        if (keys.Count == 0) return;
        var results = new List<(Guid Id, KeyProbeResult Result, DateTimeOffset At)>();
        foreach (var key in keys)
        {
            KeyProbeResult result;
            try { result = await Prober.ProbeAsync(key, Lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result = KeyProbeResult.Failed(ex.GetType().Name); }
            results.Add((key.InstanceId, result, DateTimeOffset.Now));
        }
        if (results.Count == 0) return;
        // Back on the UI thread before anything is written: the settings are read and changed here, and UpdateSettings
        // lays the panels out, which is work only this thread may do.
        await Dispatcher.InvokeAsync(() =>
        {
            // A probe can take seconds; the settings may have been edited meanwhile, so what is written back is applied to
            // the current rows by identity rather than replacing the list with the one that was probed.
            bool changed = false;
            foreach (var (id, result, at) in results)
                changed |= Settings.UpdateKey(id, key => key.WithProbe(result, at));
            if (changed) UpdateSettings(Settings);
        });
    }
    // The client the key probes use. Probes are hourly and tiny, so a short timeout is enough and a hung service is
    // dropped rather than waited on; the handler is not shared with the quota readers, whose timeouts differ.
    // The client the calendar and mail readers use. A calendar server can be slow to answer a large query, so the
// timeout is generous; it is not the probe client, whose requests are tiny and must not wait.
    private static System.Net.Http.HttpClient AgendaHttp() => new(new System.Net.Http.SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private static System.Net.Http.HttpClient KeyProbeHttp() => new(new System.Net.Http.SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // A trace of what the calendar reader did, in the data folder. A running panel can be asked what it did this way
    // without a debugger attached, which is the only practical way to see why a page is empty.
    internal static void Trace(string line)
    {
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "agenda.log"),
            Path.Combine(Path.GetTempPath(), "brimdeck-agenda.log")
        })
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
    }

    // The work calendars and the mailboxes, read on their own cadence. The read happens on the pool; only the
    // drawing comes back to this thread.
    private DispatcherTimer? _agenda;
    // Whether the timer was last told to run. Every settings update asks again, and a read that saves what it learned
    // causes one, so the answer is compared rather than the timer restarted.
    private bool _agendaWanted;
    internal void ScheduleAgenda()
    {
        var state = Settings.Agenda;
        // A subscription is a calendar too: with only holidays set up there is still something to read, and leaving the
        // subscriber out of this test is why a machine with a holiday feed showed no meetings at all.
        bool wanted = state.AutoSync && (state.Accounts.Any(account => account.Enabled)
                                      || state.Subscriptions.Any(subscription => subscription.Enabled && subscription.Url.Length > 0)
                                      || state.MailAccounts.Any(account => account.Enabled));
        // Nothing to do when the answer has not changed: this is called on every settings update, and a read that
        // saves what it learned causes one, so an unconditional restart would schedule itself in a loop.
        if (wanted == _agendaWanted && (_agenda is null) == !wanted) { if (wanted) _agenda!.Start(); return; }
        _agendaWanted = wanted;
        if (!wanted) { _agenda?.Stop(); return; }
        if (_agenda is null)
        {
            _agenda = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
            _agenda.Tick += async (_, _) => { await ReadAgendaAsync().ConfigureAwait(true); };
            // The first read is asked for now rather than at the first tick: a page opened right after the panel starts
            // would otherwise show nothing for half a minute.
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await ReadAgendaAsync().ConfigureAwait(true); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Trace("first read failed: " + ex.GetType().Name + ": " + ex.Message); }
            }), DispatcherPriority.Background);
        }
        _agenda.Stop(); _agenda.Start();
    }

    // Reads the calendars and the mail, and tells the panels when something changed. A meeting that is about to
    // start is announced in the island, once, whether it came from a calendar or from a mail.
    private async Task ReadAgendaAsync()
    {
        var state = Settings.Agenda;
        try
        {
            // A trace of what the read found, in the data folder, which is the only place a running panel can be asked what
            // it did without a debugger attached.
            if (state.Accounts.Count == 0 && state.Subscriptions.Count == 0)
                Trace($"no calendar configured: accounts={state.Accounts.Count} subscriptions={state.Subscriptions.Count}");
            bool changed = await Agenda.RefreshAsync(state, DateTimeOffset.Now, Lifetime.Token).ConfigureAwait(false);
            Trace($"read: changed={changed} events={Agenda.Events.Count} error='{Agenda.CalendarError}'");
            // What the page will draw, so a page that shows nothing can be told from a read that found nothing.
            foreach (var meeting in Agenda.Events.Take(10))
                Trace($"  {meeting.Start.LocalDateTime:MM-dd HH:mm} [{meeting.Source}] {meeting.Title}");
            if (changed)
            {
                // What the read learned is kept: the calendar collection that was found, and the mail already seen.
                var copy = Settings.Copy();
                copy.Agenda = state;
                UpdateSettings(copy);
                // The pages are redrawn: a read that found meetings has to show them, and the calendar page is built
                // from what this service holds rather than from the settings.
                foreach (var deck in Decks.ToList()) deck.RefreshAgendaPages();
            }
            // The meetings that are about to start, and have not been announced.
            var due = Agenda.DueForReminder(state, DateTimeOffset.Now, TimeSpan.FromMinutes(Settings.ReminderLead));
            foreach (var meeting in due)
            {
                state.Announced.Add(meeting.DedupeKey);
                if (state.Announced.Count > 200) state.Announced.RemoveRange(0, state.Announced.Count - 200);
                foreach (var deck in Decks.ToList())
                    deck.ShowMessage(ChatApp.Feishu, meeting.Title, meeting.When(DateTimeOffset.Now));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    // A theme with a night look changes look at its hours (PanelThemes.IsNight): the timer is set for the next change, and at
    // most ten minutes ahead, so a clock changed by hand or a machine that slept past the hour catches up soon after. The switch
    // carries a preset clock gradient over, as Windows switching "system" between dark and light does.
    private DispatcherTimer? _daylight;
    internal void ScheduleDaylight()
    {
        if (PanelTheme.NextDaylightChange(Settings) is not { } next) { _daylight?.Stop(); return; }
        if (_daylight is null)
        {
            _daylight = new DispatcherTimer(DispatcherPriority.Background);
            _daylight.Tick += (_, _) => { _daylight.Stop(); if (!Exiting) ApplyPanelTheme(systemChanged: true); };
        }
        var wait = next - DateTime.Now;
        _daylight.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds + 500, 1000, TimeSpan.FromMinutes(10).TotalMilliseconds));
        _daylight.Stop(); _daylight.Start();
    }
    // A panel reports display changes (monitors added or removed, the primary changed, a work area or scale changed).
    // Each panel receives the same messages, so they are handled once, after Windows has moved the windows to the new layout.
    internal void DisplaysChanged()
    {
        if (_arrangePending || Exiting) return;
        _arrangePending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _arrangePending = false;
            if (Exiting) return;
            Displays = Native.Displays.Current(); AssignDisplays();
            foreach (var deck in Decks.ToList()) deck.ApplySettings();
            _settingsWindow?.DisplaysChanged();
        });
    }
    // Gives the main panel its monitor and opens or closes the panels of the other monitors. A panel that keeps its
    // monitor is kept, with its state.
    internal void AssignDisplays()
    {
        var plan = DisplayLayout.Arrange(Settings.Display, Settings.DisplayMonitors.Select(d => d.Id).ToList(), Displays.Select(d => (d.Id, d.Primary)).ToList());
        Deck.Display = Displays.FirstOrDefault(d => d.Id == plan.Main);
        foreach (var id in _otherDecks.Keys.Except(plan.Others).ToList())
        { var deck = _otherDecks[id]; _otherDecks.Remove(id); deck.Retire(); }
        // Panels are opened only once the main panel runs, so that they can take its data.
        if (!Deck.IsLoaded) return;
        foreach (var id in plan.Others)
        {
            var display = Displays.First(d => d.Id == id);
            if (_otherDecks.TryGetValue(id, out var deck)) { deck.Display = display; continue; }
            deck = new MainWindow(this, Deck) { Display = display };
            _otherDecks[id] = deck; deck.Show();
        }
    }
    internal void StartMedia()
    {
        bool wanted = Settings.MediaWanted;
        if (wanted && Media is null) { Media = new MediaSessions(Dispatcher); Media.Changed += RaiseMediaChanged; Media.SetPresentationActive(_presentingMedia.Count > 0, _presentingSystemAudio.Count > 0); _ = Media.StartAsync(); }
        else if (!wanted && Media is not null) { Media.Changed -= RaiseMediaChanged; Media.Dispose(); Media = null; RaiseMediaChanged(); }
        // Switching the setting off leaves full control on until BrimDeck restarts.
        if (Settings.AppleMusicFullControl && Media is { AppleMusicFullControl: false } media) media.EnableAppleMusicFullControl();
        if (Media is not null) { Media.NeteaseAutoFullControl = Settings.NeteaseAutoFullControl; Media.NeteaseFileControlWanted = Settings.NeteaseFullControl; Media.KugouFullControlWanted = Settings.KugouFullControl; }
    }
    private void RaiseMediaChanged() => MediaChanged?.Invoke();
    // Audio levels are read while any panel shows music: the player's while it shows a song's bars, the whole system's while
    // it shows collapsed bars at all times and no song plays.
    internal void PresentMedia(MainWindow deck, bool active, bool system)
    {
        if (active) _presentingMedia.Add(deck); else _presentingMedia.Remove(deck);
        if (system) _presentingSystemAudio.Add(deck); else _presentingSystemAudio.Remove(deck);
        Media?.SetPresentationActive(_presentingMedia.Count > 0, _presentingSystemAudio.Count > 0);
    }
    // The system is read while any panel shows the monitor page open (and briefly after; see SystemSampler) or shows readings in
    // its collapsed row. Processes are listed only for an open page.
    internal void PresentMonitor(MainWindow deck, bool page, bool strip, bool power = false)
    {
        if (page) _presentingMonitor.Add(deck); else _presentingMonitor.Remove(deck);
        if (power) _presentingPower.Add(deck); else _presentingPower.Remove(deck);
        if (strip) _stripMonitor.Add(deck); else _stripMonitor.Remove(deck);
        bool wanted = _presentingMonitor.Count + _stripMonitor.Count > 0;
        if (Exiting || Monitor is null && !wanted) return;
        // The sampler reads the choice of graphics adapter on its own thread, where each part is read whole.
        if (Monitor is null)
        {
            Monitor = new SystemSampler(Dispatcher, () => (Settings.MonitorGpu, Settings.MonitorGpuName));
            Monitor.Updated += snapshot => MonitorUpdated?.Invoke(snapshot);
            // A choice saved under an older key is kept under the adapter's key from now on (GpuChoice.Resolve).
            Monitor.GpuKeyUpgraded += (old, key) => { if (Settings.MonitorGpu == old) { Settings.MonitorGpu = key; QueueSave(); } };
        }
        // What is read besides the usage is set before the sampler starts, so its first collection already holds it.
        UpdateMonitorExtras();
        Monitor.SetDemand(wanted, Settings.MonitorInterval, _presentingMonitor.Count > 0);
    }
    // Temperatures and drives are read for an open page with them on; also for a collapsed row showing them: the processor's and
    // the graphics adapter's temperatures for a ring showing its own, the drives for the drive reading, and their temperatures when
    // it shows those.
    internal void UpdateMonitorExtras()
    {
        bool page = _presentingMonitor.Count > 0;
        bool strip = _stripMonitor.Count > 0;
        var shown = strip ? Core.CompactMonitor.Shown(Settings.CompactReadings) : CompactMonitorItems.None;
        bool Heat(CompactMonitorItems item) => (shown & item) != 0 && Settings.CompactReadings.FirstOrDefault(reading => reading.Item == item) is { } reading
            && (item == CompactMonitorItems.Storage || reading.ShowValue) && CompactFigures.Shows(reading, CompactFigures.Temperature);
        bool stripDrives = (shown & CompactMonitorItems.Storage) != 0;
        Monitor?.SetExtras(page && Settings.MonitorTemperatures || Heat(CompactMonitorItems.Cpu) || Heat(CompactMonitorItems.Gpu),
            page && Settings.MonitorStorage || stripDrives,
            page && Settings.MonitorStorage && Settings.MonitorTemperatures || Heat(CompactMonitorItems.Storage),
            // The watts (the counters, NVIDIA's helper) only for a page that shows the power line or a row that chose the reading; the battery
            // alone also for the row's charge reading.
            _presentingPower.Count > 0 && Settings.MonitorPower && PowerKnownToExist || (shown & CompactMonitorItems.Power) != 0,
            (shown & CompactMonitorItems.Charge) != 0,
            // The frame rate for a row that shows it, or an open page with room for its line.
            (shown & CompactMonitorItems.Frames) != 0 || Settings.MonitorFrames && _presentingMonitor.Any(deck => deck.PageShowsFrames),
            // The graphics adapters for the page and the row's GPU reading (the power asks for them itself).
            page || (shown & CompactMonitorItems.Gpu) != 0);
    }
    // The page reads the machine's power only where there may be something to read: not once the sampler knows there is neither a power
    // source nor a battery (a desktop with nothing), so such a page does not keep asking.
    private bool PowerKnownToExist => Monitor is null || Monitor.PowerSensor != SensorState.None || Monitor.BatterySensor != SensorState.None || Native.BatteryProvider.SystemHasBattery;
    // The first save of a session took about 115 ms on the UI thread (the serializer's first use), which landed 400 ms after the first opening
    // of a page (the last page is saved then) as a stall in its animation. Once the app is idle a pool thread serializes a copy of the
    // settings once, so the first real save finds the serializer ready.
    private void WarmSettingsWriter()
    {
        var copy = Settings.Copy();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ThreadPool.QueueUserWorkItem(_ =>
        {
            try { copy.Normalize(); System.Text.Json.JsonSerializer.SerializeToNode(copy); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        }));
    }
    // Settings changed outside UpdateSettings are written after the same short pause instead of immediately.
    public void QueueSave() { _save.Stop(); _save.Start(); }
    // The settings are turned into text here, where they live, and written on the thread pool: writing the file (and the virus
    // scan that follows a write) took 60 ms and more, which showed as a stall after every page switch. Writes run one after
    // another in the order they were asked for, so the newest settings always land last.
    private Task _writing = Task.CompletedTask;
    public void FlushSettings()
    {
        _save.Stop();
        string text = Store.Serialize(Settings);
        _writing = _writing.ContinueWith(_ =>
        {
            string? error = null;
            try { Store.Write(text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = Loc.T("保存失败，请检查设置目录权限。", "Saving failed. Check the permissions of the settings folder."); }
            Dispatcher.BeginInvoke(() => { if (error is null) _settingsWindow?.SaveStatus(""); else _settingsWindow?.SaveStatus(error, true); });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    // Before the process ends, the last write must have reached the disk.
    private void FinishWriting() { try { _writing.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { } }
    // The settings window records its size before the final save; closing it during shutdown would be too late.
    // Every panel powers off first (MainWindow.Lifecycle.cs): the islands draw back into dots that sink down through the floor, then the process ends. The
    // settings window goes at once, so the exit answers immediately. A panel that never reports back cannot hold the exit up:
    // the process ends anyway once the longest power-off would have finished.
    public void ExitApplication()
    {
        if (Exiting) return;
        Exiting = true; Lifetime.Cancel(); _settingsWindow?.RememberSize(); FlushSettings();
        _settingsWindow?.Hide();
        var decks = Decks.ToList();
        int waiting = decks.Count;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BrimDeck.MainWindow.PowerOffLength(Settings.AnimationDuration) + 500) };
        bool done = false;
        void End() { if (done) return; done = true; fallback.Stop(); FinishWriting(); Shutdown(); }
        fallback.Tick += (_, _) => End();
        fallback.Start();
        foreach (var deck in decks) deck.PowerOff(() => { if (--waiting <= 0) End(); });
        if (decks.Count == 0) End();
    }
    internal void InstallUpdate()
    {
        if (Updates.PrepareRestart(() => { FinishWriting(); Store.Save(Settings); })) ExitApplication();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true; Lifetime.Cancel(); _save.Stop(); FinishWriting(); StopBackgroundUpdates(); DisplayPower.Uninstall(); Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged; _tray?.Dispose(); DisposeHotkeys(); WindowDrags?.Dispose(); _listener?.Unregister(null); _settingsEvent?.Dispose();
        Media?.Dispose(); Monitor?.Dispose(); SensorGuard.Finish(); Lyrics?.Dispose(); Prices?.Dispose(); _mutex?.Dispose(); base.OnExit(e);
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}
