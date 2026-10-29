using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using WinClip.Core;
using WinClip.Native;

namespace WinClip;

public partial class App : Application
{
    // Clipboard history can contain sensitive text, so it lives in non-roaming local app data.
    private static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinClip");
    private static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");

    // Launching WinClip again while it runs signals this event, and the running copy opens Settings.
    private const string OpenSettingsEventName = @"Local\WinClip.OpenSettings";

    // Bigger images (say, an 8K screenshot) would bloat history and stall encryption; they are skipped.
    private const int MaxImageBytes = 32 * 1024 * 1024;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\WinClip.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (EventWaitHandle.TryOpenExisting(OpenSettingsEventName, out var signal))
                using (signal)
                    signal.Set();
            Shutdown();
            return;
        }
        _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEventName);
        _openSettingsWait = ThreadPool.RegisterWaitForSingleObject(_openSettingsSignal,
            (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        Directory.CreateDirectory(DataDirectory);
        Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(DataDirectory, "winclip.log")));
        Trace.AutoFlush = true;
        DispatcherUnhandledException += (_, args) =>
        {
            Trace.TraceError($"Unhandled: {args.Exception}");
            args.Handled = true;
        };

        string? settingsError = null;
        try
        {
            _settings = AppSettings.LoadOrCreate(SettingsPath);
        }
        catch (JsonException ex)
        {
            settingsError = $"Your settings file is invalid, so defaults are in use. {ex.Message}";
        }

        _store = new HistoryStore(Path.Combine(DataDirectory, "history.db"), _settings.MaxItems, new DpapiProtector());
        _messages = new MessageWindow();
        _monitor = new ClipboardMonitor(_messages);
        _monitor.Captured += OnCaptured;
        _popup = new PopupWindow(_store);
        _popup.ItemChosen += OnItemChosen;
        _tray = new TrayIcon(
            showHistory: () => TogglePopup(IntPtr.Zero),
            setPaused: paused => _monitor!.Paused = paused,
            openSettings: OpenSettings,
            clearHistory: ClearHistory,
            quit: Shutdown);

        var hotkeyRegistered = TryRegisterHotkey(_settings.Hotkey);
        if (settingsError is not null)
            _tray.Notify("Settings reset", settingsError, warning: true);
        else if (!hotkeyRegistered)
            _tray.Notify("Shortcut unavailable",
                $"{Describe(_settings.Hotkey)} is used by another app. Choose a different one in Settings.", warning: true);
        else if (!e.Args.Contains(StartupRegistration.BackgroundArgument))
            _tray.Notify("WinClip is running",
                $"Press {Describe(_settings.Hotkey)} to open your clipboard history. Settings are in the tray icon menu.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

    /// <summary>Applies and saves settings from the settings window. Returns an error message on failure.</summary>
    private string? ApplySettings(AppSettings next)
    {
        if (next.Validate() is { } invalid)
            return invalid;

        if (!TryRegisterHotkey(next.Hotkey))
        {
            TryRegisterHotkey(_settings.Hotkey);
            return $"{Describe(next.Hotkey)} is used by another app. Choose a different shortcut.";
        }

        try
        {
            next.Save(SettingsPath);
        }
        catch (IOException ex)
        {
            TryRegisterHotkey(_settings.Hotkey);
            return $"Settings couldn't be saved: {ex.Message}";
        }

        _settings = next;
        _store!.MaxUnpinned = next.MaxItems;
        return null;
    }

    /// <summary>Replaces the global shortcut. On failure no shortcut is registered.</summary>
    private bool TryRegisterHotkey(string hotkey)
    {
        _hotkey?.Dispose();
        _hotkey = HotkeyGesture.TryParse(hotkey, out var gesture)
            ? GlobalHotkey.TryRegister(_messages!, gesture, () => TogglePopup(NativeMethods.GetForegroundWindow()))
            : null;
        _tray!.SetHotkey(_hotkey is null ? null : Describe(hotkey));
        return _hotkey is not null;
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
            TryRegisterHotkey(_settings.Hotkey);
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettings, PauseHotkey);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                if (_hotkey is null)
                    TryRegisterHotkey(_settings.Hotkey);
            };
            _settingsWindow.Show();
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
        _store!.Add(content, captured.SourceApp, DateTimeOffset.Now);
    }

    private void TogglePopup(IntPtr target)
    {
        if (_popup!.IsVisible)
            _popup.Hide();
        else
            _popup.ShowFor(target);
    }

    private async void OnItemChosen(ClipItem item, IntPtr pasteTarget, bool plainText)
    {
        try
        {
            var formats = _store!.GetFormats(item.Id);
            if (!await ClipboardAccess.TryWriteAsync(_messages!.Handle, item, formats, plainText))
            {
                _tray!.Notify("Couldn't copy", "Another app is holding the clipboard. Try again.", warning: true);
                return;
            }
            _store.Touch(item.Id, DateTimeOffset.Now);
            if (pasteTarget != IntPtr.Zero && !await WindowFocus.PasteIntoAsync(pasteTarget))
                Trace.TraceWarning("Paste target did not take focus; the item is on the clipboard.");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Paste failed: {ex}");
        }
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show("Delete all clipboard history except pinned items?", "Clear history",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
            _store!.ClearUnpinned();
    }

    private static string Describe(string hotkey) =>
        HotkeyGesture.TryParse(hotkey, out var gesture) ? HotkeyRecorder.Describe(gesture) : hotkey;
}
