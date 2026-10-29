using System.Runtime.InteropServices;
using static WinClip.Native.NativeMethods;

namespace WinClip.Native;

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
    /// Focuses <paramref name="target"/> and sends Ctrl+V. Returns false when the target is gone or never
    /// took focus. Pasting into an elevated app fails silently unless WinClip is elevated too (UIPI).
    /// </summary>
    public static async Task<bool> PasteIntoAsync(IntPtr target)
    {
        if (!IsWindow(target))
            return false;

        BringToForeground(target);
        if (!await WaitUntil(() => GetForegroundWindow() == target, TimeSpan.FromMilliseconds(500)))
            return false;

        // A still-held Alt or Shift from the user's keystroke would turn Ctrl+V into a different shortcut.
        await WaitUntil(() => Modifiers.All(vk => GetAsyncKeyState(vk) >= 0), TimeSpan.FromSeconds(1));

        INPUT[] inputs =
        [
            Key(VK_CONTROL, keyUp: false),
            Key(VK_V, keyUp: false),
            Key(VK_V, keyUp: true),
            Key(VK_CONTROL, keyUp: true),
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
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
