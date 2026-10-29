using System.Windows.Interop;

namespace WinClip.Native;

/// <summary>Hidden message-only window that receives clipboard and hotkey notifications.</summary>
internal sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;

    public MessageWindow()
    {
        _source = new HwndSource(new HwndSourceParameters("WinClipMessages") { ParentWindow = NativeMethods.HWND_MESSAGE });
        _source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            MessageReceived?.Invoke(msg, wParam);
            return IntPtr.Zero;
        });
    }

    public IntPtr Handle => _source.Handle;

    public event Action<int, IntPtr>? MessageReceived;

    public void Dispose() => _source.Dispose();
}
