using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinClip.Core;
using WinClip.Native;

namespace WinClip;

/// <summary>
/// Shows the current shortcut as key caps. Click it (or press Enter/Space) and press a new combination
/// to replace it; Esc or moving focus away cancels.
/// </summary>
internal sealed partial class HotkeyRecorder : UserControl
{
    private static readonly Key[] ModifierKeys =
        [Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin];

    private HotkeyGesture _gesture = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "V");
    private bool _recording;

    public HotkeyRecorder()
    {
        InitializeComponent();
        MouseLeftButtonUp += (_, _) => StartRecording();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += (_, e) =>
        {
            if (_recording)
            {
                ShowHeldModifiers();
                e.Handled = true;
            }
        };
        LostKeyboardFocus += (_, _) => StopRecording();
        GotKeyboardFocus += (_, _) => Frame.BorderBrush = Brush("Accent");
        Render();
    }

    /// <summary>
    /// True while waiting for a key press. The app's global hotkey must be released meanwhile, otherwise
    /// Windows delivers the current shortcut to it and the recorder never sees it.
    /// </summary>
    public event Action<bool>? RecordingChanged;

    public HotkeyGesture Gesture
    {
        get => _gesture;
        set
        {
            _gesture = value;
            Render();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!_recording)
        {
            if (key is Key.Enter or Key.Space)
            {
                StartRecording();
                e.Handled = true;
            }
            return;
        }

        var modifiers = (HotkeyModifiers)Keyboard.Modifiers;
        if (key == Key.Tab && modifiers is HotkeyModifiers.None or HotkeyModifiers.Shift)
        {
            StopRecording(); // Not handled, so focus still moves on.
            return;
        }

        e.Handled = true;
        if (key == Key.Escape && modifiers == HotkeyModifiers.None)
        {
            StopRecording();
            return;
        }
        if (ModifierKeys.Contains(key) || key is Key.ImeProcessed or Key.DeadCharProcessed)
        {
            ShowHeldModifiers();
            return;
        }
        if (modifiers == HotkeyModifiers.None)
        {
            ShowPrompt("Hold Ctrl, Alt, Shift or Win with the key", Brush("Error"));
            return;
        }

        _gesture = new HotkeyGesture(modifiers, key.ToString());
        StopRecording();
    }

    private void StartRecording()
    {
        if (_recording)
            return;
        if (!IsKeyboardFocused && !Focus())
            return;
        _recording = true;
        AutomationProperties.SetHelpText(this, "Recording. Press the new shortcut, or Esc to cancel.");
        RecordingChanged?.Invoke(true);
        Hint.Text = "Esc to cancel";
        ShowHeldModifiers();
    }

    private void StopRecording()
    {
        if (!_recording)
            return;
        _recording = false;
        AutomationProperties.SetHelpText(this, "");
        RecordingChanged?.Invoke(false);
        Render();
    }

    private void Render()
    {
        Keys.ItemsSource = KeyCaps(_gesture.Modifiers).Append(DisplayName(_gesture.Key)).ToList();
        Keys.Visibility = Visibility.Visible;
        Prompt.Visibility = Visibility.Collapsed;
        Hint.Text = "Change";
        AutomationProperties.SetName(this, $"Shortcut {Describe(_gesture)}. Press Enter to change.");
        Frame.BorderBrush = Brush(IsKeyboardFocused ? "Accent" : "Hairline");
    }

    private void ShowHeldModifiers()
    {
        var held = KeyCaps((HotkeyModifiers)Keyboard.Modifiers).ToList();
        Frame.BorderBrush = Brush("Accent");
        if (held.Count == 0)
        {
            ShowPrompt("Press the new shortcut", Brush("Muted"));
            return;
        }
        Keys.ItemsSource = held;
        Keys.Visibility = Visibility.Visible;
        Prompt.Visibility = Visibility.Collapsed;
    }

    private void ShowPrompt(string text, Brush color)
    {
        Keys.Visibility = Visibility.Collapsed;
        Prompt.Text = text;
        Prompt.Foreground = color;
        Prompt.Visibility = Visibility.Visible;
    }

    private static IEnumerable<string> KeyCaps(HotkeyModifiers modifiers)
    {
        if (modifiers.HasFlag(HotkeyModifiers.Control)) yield return "Ctrl";
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) yield return "Alt";
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) yield return "Shift";
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) yield return "Win";
    }

    /// <summary>Readable form for menus and messages, such as "Ctrl+Alt+1".</summary>
    public static string Describe(HotkeyGesture gesture) =>
        string.Join('+', KeyCaps(gesture.Modifiers).Append(DisplayName(gesture.Key)));

    /// <summary>The label printed on the key: "1" rather than "D1", "ß" rather than "OemOpenBrackets".</summary>
    public static string DisplayName(string keyName)
    {
        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key))
            return keyName;
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return $"Num {key - Key.NumPad0}";

        // Punctuation keys differ per keyboard layout; ask the active layout what they print.
        var character = NativeMethods.MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(key), NativeMethods.MAPVK_VK_TO_CHAR) & 0x7FFF;
        return character > ' ' && !char.IsLetterOrDigit((char)character)
            ? char.ToUpperInvariant((char)character).ToString()
            : key.ToString();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
}
