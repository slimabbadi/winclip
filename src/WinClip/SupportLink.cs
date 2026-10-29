using System.Diagnostics;

namespace WinClip;

internal static class SupportLink
{
    public const string Url = "https://buymeacoffee.com/slimabbadi";

    public static void Open() => Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
}
