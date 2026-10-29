using Velopack;

namespace WinClip;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Pending updates are applied after App acquires the single-instance mutex.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
