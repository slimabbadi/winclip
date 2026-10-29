using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using WinClip.Core;
using WinClip.Native;

namespace WinClip;

/// <summary>An item plus exactly the formats its clipboard write needs.</summary>
internal sealed record PasteRequest(ClipItem Item, IReadOnlyDictionary<string, byte[]> Formats);

public partial class App : Application
{
    // Clipboard history can contain sensitive text, so it lives in non-roaming local app data.
    private static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinClip");
    private static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");
    private static readonly string LogPath = Path.Combine(DataDirectory, "winclip.log");
    private static readonly IReadOnlyDictionary<string, byte[]> NoFormats = new Dictionary<string, byte[]>();

    // Launching WinClip again while it runs signals this event, and the running copy opens Settings.
    private const string OpenSettingsEventName = @"Local\WinClip.OpenSettings";

    // Bigger images (say, an 8K screenshot) would bloat history and stall encryption; they are skipped.
    private const int MaxImageBytes = 32 * 1024 * 1024;

    private readonly Lock _writeGate = new();
    private readonly SemaphoreSlim _pasteGate = new(1, 1);

    private Mutex? _singleInstance;
    private EventWaitHandle? _openSettingsSignal;
    private RegisteredWaitHandle? _openSettingsWait;
    private AppSettings _settings = new();
    private HistoryStore? _store;
    private MessageWindow? _messages;
    private ClipboardMonitor? _monitor;
    private GlobalHotkey? _hotkey;
    private PopupWindow? _popup;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;
    private Updater? _updater;
    private DispatcherTimer? _updateTimer;
    private bool _checkingForUpdates;
    private Task _storeWrites = Task.CompletedTask;
    private bool _startupCompleted;
    private bool _startupFailureShown;
    private bool _unreadableNotified;
    private bool _deletingData;
    private TextWriterTraceListener? _logListener;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            Start(e);
        }
        catch (Exception ex)
        {
            // Without this the app would keep running invisibly, holding the single-instance mutex.
            ShowStartupFailure(ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateTimer?.Stop();
        // Queued history writes are short; letting them finish keeps the last copy the user made.
        try
        {
            _storeWrites.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The write already logged its own failure.
        }
        _openSettingsWait?.Unregister(null);
        _openSettingsSignal?.Dispose();
        _tray?.Dispose();
        _hotkey?.Dispose();
        _monitor?.Dispose();
        _messages?.Dispose();
        _store?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void Start(StartupEventArgs e)
    {
        // The event exists before the mutex is taken, so a launch during startup can always deliver its request.
        _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEventName);
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\WinClip.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _openSettingsSignal.Set();
            Shutdown();
            return;
        }

        Directory.CreateDirectory(DataDirectory);
        _logListener = new TextWriterTraceListener(LogPath);
        Trace.Listeners.Add(_logListener);
        Trace.AutoFlush = true;

        _openSettingsWait = ThreadPool.RegisterWaitForSingleObject(_openSettingsSignal,
            (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        string? settingsError = null;
        try
        {
            _settings = AppSettings.LoadOrCreate(SettingsPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            settingsError = $"Your settings file is invalid, so defaults are in use. {ex.Message}";
        }

        _updater = new Updater();
        if (_updater.ReadyVersion is not null && _updater.ScheduleRestart())
        {
            Shutdown();
            return;
        }

        _store = new HistoryStore(Path.Combine(DataDirectory, "history.db"), _settings.MaxItems, new DpapiProtector());
        _messages = new MessageWindow();
        _monitor = new ClipboardMonitor(_messages, ClipboardLimitsFor(_settings));
        _monitor.Captured += OnCaptured;
        _popup = new PopupWindow(_store);
        _popup.ItemChosen += OnItemChosen;
        _tray = new TrayIcon(
            showHistory: () => TogglePopup(IntPtr.Zero),
            setPaused: paused =>
            {
                _monitor!.Paused = paused;
                _popup!.RecordingPaused = paused;
            },
            openSettings: OpenSettings,
            clearHistory: ClearHistory,
            checkForUpdates: () => _ = CheckForUpdatesAsync(true),
            restartToUpdate: RestartToUpdate,
            quit: Shutdown);

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromDays(1);
            if (_settings.CheckForUpdates)
                _ = CheckForUpdatesAsync(false);
        };
        if (_settings.CheckForUpdates)
            _updateTimer.Start();

        var hotkeyRegistered = TryRegisterHotkey(_settings.Hotkey);
        if (settingsError is not null)
            _tray.Notify("Settings reset", settingsError, warning: true);
        else if (!hotkeyRegistered)
            _tray.Notify("Shortcut unavailable",
                $"{Describe(_settings.Hotkey)} is used by another app. Choose a different one in Settings.", warning: true);
        else if (!e.Args.Contains(StartupRegistration.BackgroundArgument))
            _tray.Notify("WinClip is running",
                $"Press {Describe(_settings.Hotkey)} to open your clipboard history. Settings are in the tray icon menu.");

        _startupCompleted = true;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        LogFailure("Unhandled exception", args.Exception);
        args.Handled = true;
        // A failure while starting up leaves nothing running: no tray icon, no hotkey, no window to close the
        // app with, so it has to shut down rather than sit there holding the single-instance mutex.
        if (!_startupCompleted)
            ShowStartupFailure(args.Exception);
    }

    private void ShowStartupFailure(Exception ex)
    {
        LogFailure("Startup failed", ex);
        if (_startupFailureShown || Dispatcher.HasShutdownStarted)
            return;
        _startupFailureShown = true;

        var message = $"WinClip couldn't start: {ex.GetType().Name}: {ex.Message}\n\n"
            + $"Your clipboard history and settings are in:\n{DataDirectory}\n\n"
            + $"Details are in {LogPath}.";
        try
        {
            MessageBox.Show(message, "WinClip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception showError)
        {
            // There is nothing left to tell the user with; the log entry above is the record.
            LogFailure("Showing the startup error failed", showError);
        }
        Shutdown(1);
    }

    /// <summary>Applies and saves settings from the settings window. Returns an error message on failure.</summary>
    private string? ApplySettings(AppSettings next)
    {
        if (next.Validate() is { } invalid)
            return invalid;

        if (!TryRegisterHotkey(next.Hotkey))
        {
            EnsureHotkey();
            return $"{Describe(next.Hotkey)} is used by another app. Choose a different shortcut.";
        }

        try
        {
            next.Save(SettingsPath);
        }
        catch (Exception ex)
        {
            // Permissions, a full disk or a read-only profile all land here; the shortcut must go back with them.
            EnsureHotkey();
            LogFailure("Saving settings failed", ex);
            return $"Settings couldn't be saved: {ex.Message}";
        }

        _settings = next;
        if (next.CheckForUpdates)
            _updateTimer?.Start();
        else
            _updateTimer?.Stop();
        _store!.MaxUnpinned = next.MaxItems;
        if (_monitor is not null)
            _monitor.Limits = ClipboardLimitsFor(_settings);
        return null;
    }

    private async Task CheckForUpdatesAsync(bool userRequested)
    {
        if (_checkingForUpdates || _updater is null)
            return;
        _checkingForUpdates = true;
        try
        {
            var outcome = await _updater.CheckAndDownloadAsync();
            if (_deletingData || Dispatcher.HasShutdownStarted)
                return;
            if (outcome == UpdateCheckResult.Ready)
                _tray!.ShowUpdateReady(_updater.ReadyVersion!, RestartToUpdate);
            else if (userRequested)
                _tray!.Notify("WinClip updates", outcome == UpdateCheckResult.NotSupported
                    ? "Install WinClip using Setup.exe or the portable zip from GitHub Releases to receive updates."
                    : $"WinClip {_updater.CurrentVersion} is up to date.");
        }
        catch (Exception ex)
        {
            // This is the background task boundary: an update failure must not stop clipboard capture.
            LogFailure("Checking for updates failed", ex);
            if (userRequested && !Dispatcher.HasShutdownStarted)
                _tray?.Notify("Update check failed", "Couldn't check or download updates. Try again later; details are in the log.", warning: true);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    private void RestartToUpdate()
    {
        if (_deletingData)
            return;
        try
        {
            if (_updater?.ScheduleRestart() == true)
                Shutdown();
        }
        catch (Exception ex)
        {
            LogFailure("Scheduling an update failed", ex);
            _tray?.Notify("Couldn't restart to update", "Quit and start WinClip again to retry the downloaded update.", warning: true);
        }
    }

    /// <summary>Replaces the global shortcut. On failure no shortcut is registered.</summary>
    private bool TryRegisterHotkey(string hotkey)
    {
        _hotkey?.Dispose();
        _hotkey = HotkeyGesture.TryParse(hotkey, out var gesture)
            ? GlobalHotkey.TryRegister(_messages!, gesture, () => TogglePopup(NativeMethods.GetForegroundWindow()))
            : null;
        _tray?.SetHotkey(_hotkey is null ? null : Describe(hotkey));
        return _hotkey is not null;
    }

    /// <summary>Registers the configured shortcut again, telling the user when it can no longer be had.</summary>
    private void EnsureHotkey()
    {
        if (!TryRegisterHotkey(_settings.Hotkey))
            _tray?.Notify("Shortcut unavailable",
                $"{Describe(_settings.Hotkey)} can't be registered right now. Choose a different one in Settings.",
                warning: true);
    }

    private void PauseHotkey(bool paused)
    {
        if (paused)
        {
            _hotkey?.Dispose();
            _hotkey = null;
        }
        else
        {
            EnsureHotkey();
        }
    }

    private void OpenSettings()
    {
        if (_deletingData)
            return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettings, PauseHotkey);
            _settingsWindow.DeleteDataRequested += DeleteLocalData;
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                if (_hotkey is null && !_deletingData)
                    EnsureHotkey();
            };
            _settingsWindow.Show();
        }
        else if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }
        _settingsWindow.Activate();
        WindowFocus.BringToForeground(new System.Windows.Interop.WindowInteropHelper(_settingsWindow).Handle);
    }

    private void OnCaptured(CapturedClip captured)
    {
        var content = captured.Content;
        if (_settings.IsIgnoredApp(captured.SourceApp)
            || (content.Kind == ClipKind.Text && content.Text.Length > _settings.MaxItemLength)
            || (content.Formats.TryGetValue(ClipFormat.Png, out var png) && png.Length > MaxImageBytes))
            return;

        if (_store is not { } store)
            return;

        // DPAPI plus a SQLite write can take a second for a large image, and this runs on the capture thread.
        // Writes are chained so items still land in the order they were copied.
        var now = DateTimeOffset.Now;
        lock (_writeGate)
        {
            if (_deletingData)
                return;
            _storeWrites = _storeWrites.ContinueWith(
                _ => AddToStore(store, content, captured.SourceApp, now), TaskScheduler.Default);
        }
    }

    private static void AddToStore(HistoryStore store, ClipContent content, string? sourceApp, DateTimeOffset now)
    {
        try
        {
            store.Add(content, sourceApp, now);
        }
        catch (Exception ex)
        {
            LogFailure("Saving a clipboard item failed", ex);
        }
    }

    private void TogglePopup(IntPtr target)
    {
        if (_popup is not { } popup)
            return;

        if (popup.IsVisible)
        {
            popup.Hide();
            return;
        }

        try
        {
            popup.ShowFor(target);
            NotifyUnreadableOnce();
        }
        catch (Exception ex)
        {
            LogFailure("Opening the history popup failed", ex);
            _tray?.Notify("Clipboard history",
                "The history window couldn't be opened. Restart WinClip; details are in the log file.", warning: true);
        }
    }

    private async void OnItemChosen(ClipItem item, IntPtr pasteTarget, bool plainText)
    {
        // Two quick choices must not interleave their clipboard writes and their keystrokes.
        await _pasteGate.WaitAsync();
        try
        {
            if (_deletingData)
                return;
            var store = _store;
            if (store is null)
                return;

            var prepared = await Task.Run(() => PreparePaste(store, item, plainText));
            if (prepared is not { } paste)
            {
                _tray?.Notify("Couldn't copy", "That item is no longer in your history.", warning: true);
                return;
            }

            if (!await ClipboardAccess.TryWriteAsync(_messages!.Handle, paste.Item, paste.Formats, plainText))
            {
                _tray?.Notify("Couldn't copy", "Another app is holding the clipboard. Try again.", warning: true);
                return;
            }
            await Task.Run(() => store.Touch(item.Id, DateTimeOffset.Now));

            if (pasteTarget == IntPtr.Zero)
                return;
            var outcome = await WindowFocus.PasteIntoAsync(pasteTarget);
            if (outcome == PasteOutcome.ModifiersHeld)
                _tray?.Notify("Copied, not pasted",
                    "Release Shift, Alt or the Windows key, then paste with Ctrl+V.", warning: true);
            else if (outcome != PasteOutcome.Pasted)
                Trace.TraceWarning("Paste target did not take focus; the item is on the clipboard.");
        }
        catch (Exception ex)
        {
            LogFailure("Paste failed", ex);
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    /// <summary>
    /// Loads what writing <paramref name="item"/> needs. List rows may carry shortened text, so text and files
    /// come from the store; formats are only fetched when the write will actually use them.
    /// </summary>
    private static PasteRequest? PreparePaste(HistoryStore store, ClipItem item, bool plainText)
    {
        var text = item.Text;
        if (item.Kind is ClipKind.Text or ClipKind.Files)
        {
            if (store.GetFullText(item.Id) is not { } full)
                return null;
            text = full;
        }

        var formats = NoFormats;
        if (item.Kind == ClipKind.Image || (item.Kind == ClipKind.Text && !plainText))
            formats = store.GetFormats(item.Id);
        return new PasteRequest(item with { Text = text }, formats);
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show("Delete all clipboard history except pinned items?", "Clear history",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
            _store!.ClearUnpinned();
    }

    private async void DeleteLocalData()
    {
        if (_deletingData)
            return;
        var answer = MessageBox.Show(_settingsWindow!,
            "Delete all clipboard history, including pinned items, settings and logs?\n\n"
            + "WinClip will turn off Start with Windows and quit. This cannot be undone. "
            + "Your current Windows clipboard will not be cleared.",
            "Delete all local data", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
            return;

        Task writes;
        lock (_writeGate)
        {
            _deletingData = true;
            writes = _storeWrites;
        }
        _settingsWindow!.IsEnabled = false;
        _hotkey?.Dispose();
        _hotkey = null;
        _tray?.Dispose();
        _tray = null;
        _updateTimer?.Stop();
        _monitor!.Paused = true;
        _popup!.Hide();
        await _pasteGate.WaitAsync();
        try
        {
            await writes;
            StartupRegistration.IsEnabled = false;
            _store!.Dispose();
            _store = null;
            Trace.Listeners.Remove(_logListener!);
            _logListener!.Dispose();
            await Task.Run(() => LocalData.Delete(DataDirectory));
        }
        catch (Exception ex)
        {
            // Report partial deletion without reopening the store or creating another log file.
            MessageBox.Show($"Some data could not be removed ({ex.GetType().Name}). WinClip will quit.\n\n"
                + $"Check this folder before restarting:\n{DataDirectory}", "Data deletion incomplete",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _pasteGate.Release();
            Shutdown();
        }
    }

    /// <summary>
    /// Tells the user once per session when history entries couldn't be decrypted, for example after a Windows
    /// password reset by an administrator, which makes DPAPI keys unusable. The entries are kept, not deleted.
    /// </summary>
    private void NotifyUnreadableOnce()
    {
        if (_unreadableNotified || _store is not { UnreadableItemCount: > 0 and var count })
            return;
        _unreadableNotified = true;
        _tray?.Notify("Some history can't be read",
            $"{count} {(count == 1 ? "item" : "items")} couldn't be decrypted with your Windows account and are hidden.",
            warning: true);
    }

    private static ClipboardLimits ClipboardLimitsFor(AppSettings settings) =>
        new(settings.MaxItemLength, MaxImageBytes);

    /// <summary>
    /// Logs the exception type and stack only: messages can quote clipboard-derived text, and the log is a
    /// plain-text file next to the history.
    /// </summary>
    private static void LogFailure(string context, Exception ex)
    {
        try
        {
            Trace.TraceError($"{context}: {ex.GetType().FullName}{Environment.NewLine}{ex.StackTrace}");
        }
        catch (Exception)
        {
            // Logging must never be what takes the app down.
        }
    }

    private static string Describe(string hotkey) =>
        HotkeyGesture.TryParse(hotkey, out var gesture) ? HotkeyRecorder.Describe(gesture) : hotkey;
}
