using System.Windows.Input;
using WinClip.Core;
using static WinClip.Native.NativeMethods;

namespace WinClip.Native;

internal sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 1;

    private readonly MessageWindow _window;
    private readonly Action _onPressed;

    private GlobalHotkey(MessageWindow window, Action onPressed)
    {
        _window = window;
        _onPressed = onPressed;
        window.MessageReceived += OnMessage;
    }

    /// <summary>Registers the hotkey, or returns null when the key name is unknown or another app owns the combination.</summary>
    public static GlobalHotkey? TryRegister(MessageWindow window, HotkeyGesture gesture, Action onPressed)
    {
        if (!Enum.TryParse<Key>(gesture.Key, ignoreCase: true, out var key))
            return null;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || !RegisterHotKey(window.Handle, HotkeyId, (uint)gesture.Modifiers | MOD_NOREPEAT, (uint)virtualKey))
            return null;
        return new GlobalHotkey(window, onPressed);
    }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        UnregisterHotKey(_window.Handle, HotkeyId);
    }

    private void OnMessage(int message, IntPtr wParam)
    {
        if (message == WM_HOTKEY && wParam == HotkeyId)
            _onPressed();
    }
}
