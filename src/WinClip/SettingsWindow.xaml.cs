using System.Windows;
using System.Windows.Interop;
using WinClip.Core;
using WinClip.Native;

namespace WinClip;

internal sealed partial class SettingsWindow : Window
{
    private readonly Func<AppSettings, string?> _apply;

    /// <param name="apply">Saves and applies settings; returns an error message when they can't be applied.</param>
    /// <param name="pauseHotkey">Releases (true) or re-registers (false) the global shortcut while recording.</param>
    public SettingsWindow(AppSettings current, Func<AppSettings, string?> apply, Action<bool> pauseHotkey)
    {
        _apply = apply;
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();

        if (HotkeyGesture.TryParse(current.Hotkey, out var gesture))
            Recorder.Gesture = gesture;
        Recorder.RecordingChanged += pauseHotkey;
        MaxItems.Text = current.MaxItems.ToString();
        MaxItemLength.Text = current.MaxItemLength.ToString();
        IgnoredApps.Text = string.Join(Environment.NewLine, current.IgnoredApps);
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxItems.Text.Trim(), out var maxItems) || !int.TryParse(MaxItemLength.Text.Trim(), out var maxLength))
        {
            ShowError("History size and longest copy must be whole numbers.");
            return;
        }

        var settings = new AppSettings
        {
            Hotkey = Recorder.Gesture.ToString(),
            MaxItems = maxItems,
            MaxItemLength = maxLength,
            IgnoredApps = IgnoredApps.Text
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

        if (_apply(settings) is { } error)
        {
            ShowError(error);
            return;
        }
        StartupRegistration.IsEnabled = StartWithWindows.IsChecked == true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void UseDarkTitleBar()
    {
        var enabled = 1;
        NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
            NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }
}
