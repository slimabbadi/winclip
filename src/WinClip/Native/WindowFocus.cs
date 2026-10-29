using System.Runtime.InteropServices;
using static WinClip.Native.NativeMethods;

namespace WinClip.Native;

/// <summary>How a paste attempt ended. Anything but <see cref="Pasted"/> leaves the item on the clipboard.</summary>
internal enum PasteOutcome
{
    /// <summary>Ctrl+V was sent to the target window.</summary>
    Pasted,

    /// <summary>The target window is gone or never took focus.</summary>
    NoFocus,

    /// <summary>A modifier was still held, so Ctrl+V would have meant something else.</summary>
    ModifiersHeld,

    /// <summary>Windows refused the synthetic keystrokes.</summary>
    NotSent,
}

/// <summary>Moving focus between our popup and the app the user was typing in, then pasting there.</summary>
internal static class WindowFocus
{
    private static readonly int[] Modifiers = [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN];

    /// <summary>
    /// Windows only lets the foreground process hand out focus. Attaching to its input queue makes
    /// us count as foreground for the call; without it the target merely flashes in the taskbar.
    /// </summary>
    public static void BringToForeground(IntPtr hwnd)
    {
        if (SetForegroundWindow(hwnd))
            return;

        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var ourThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == ourThread)
            return;

        AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(hwnd);
        }
        finally
        {
            AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    /// <summary>
    /// Focuses <paramref name="target"/> and sends Ctrl+V. Pasting into an elevated app fails silently unless
    /// WinClip is elevated too (UIPI).
    /// </summary>
    public static async Task<PasteOutcome> PasteIntoAsync(IntPtr target)
    {
        if (!IsWindow(target))
            return PasteOutcome.NoFocus;

        BringToForeground(target);
        if (!await WaitUntil(() => GetForegroundWindow() == target, TimeSpan.FromMilliseconds(500)))
            return PasteOutcome.NoFocus;

        // A still-held Alt or Shift from the user's keystroke would turn Ctrl+V into a different shortcut, so
        // no input is sent at all when they are still down: the caller leaves the item on the clipboard instead.
        if (!await WaitUntil(() => Modifiers.All(vk => GetAsyncKeyState(vk) >= 0), TimeSpan.FromSeconds(1)))
            return PasteOutcome.ModifiersHeld;

        INPUT[] inputs =
        [
            Key(VK_CONTROL, keyUp: false),
            Key(VK_V, keyUp: false),
            Key(VK_V, keyUp: true),
            Key(VK_CONTROL, keyUp: true),
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length
            ? PasteOutcome.Pasted
            : PasteOutcome.NotSent;
    }

    private static INPUT Key(ushort vk, bool keyUp) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = keyUp ? KEYEVENTF_KEYUP : 0 } },
    };

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(10);
        }
        return true;
    }
}
