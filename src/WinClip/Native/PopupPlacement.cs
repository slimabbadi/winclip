using System.Diagnostics;
using System.Runtime.InteropServices;
using static WinClip.Native.NativeMethods;

namespace WinClip.Native;

internal static class PopupPlacement
{
    private const int Gap = 6;

    /// <summary>
    /// Screen position (physical pixels) of the text caret in <paramref name="foreground"/>, falling back to
    /// the mouse cursor. Apps that draw their own caret (Chromium, Electron, most terminals) take the fallback.
    /// </summary>
    public static POINT FindAnchor(IntPtr foreground)
    {
        var info = new GUITHREADINFO { Size = Marshal.SizeOf<GUITHREADINFO>() };
        var thread = GetWindowThreadProcessId(foreground, out _);
        if (thread != 0 && GetGUIThreadInfo(thread, ref info) && info.Caret != IntPtr.Zero
            && info.CaretRect.Bottom > info.CaretRect.Top)
        {
            var point = new POINT { X = info.CaretRect.Left, Y = info.CaretRect.Bottom };
            if (ClientToScreen(info.Caret, ref point))
                return point;
        }

        GetCursorPos(out var cursor);
        return cursor;
    }

    /// <summary>
    /// Positions the window just below <paramref name="anchor"/>, flipping above it or sliding left when it
    /// would leave the monitor's work area. Sizes are converted with that monitor's DPI.
    /// </summary>
    public static void Place(IntPtr hwnd, POINT anchor, double widthDips, double heightDips)
    {
        var monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        var work = info.Work;
        var width = (int)Math.Ceiling(widthDips * scale);
        var height = (int)Math.Ceiling(heightDips * scale);

        var x = Math.Clamp(anchor.X, work.Left, Math.Max(work.Left, work.Right - width));
        var y = anchor.Y + Gap;
        if (y + height > work.Bottom)
            y = anchor.Y - height - Gap;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        GetWindowRect(hwnd, out var actual);
        Trace.TraceInformation(
            $"Popup anchor=({anchor.X},{anchor.Y}) work=({work.Left},{work.Top},{work.Right},{work.Bottom}) " +
            $"scale={scale} wanted=({x},{y},{width}x{height}) actual=({actual.Left},{actual.Top},{actual.Right - actual.Left}x{actual.Bottom - actual.Top})");
    }
}
