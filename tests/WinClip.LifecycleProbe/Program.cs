using Velopack;
using System.IO;
using Velopack.Locators;
using WinClip;
using WinClip.Core;
using System.Windows.Automation;

internal static class Program
{
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinClip");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
                throw new InvalidOperationException("Lifecycle probe runs only on a fresh GitHub-hosted runner.");
            switch (args[0])
            {
                case "seed":
                    Seed();
                    break;
                case "verify":
                    Verify();
                    break;
                case "update":
                    await Update(args[1], args[2], args[3]);
                    break;
                case "delete-ui":
                    await DeleteUi();
                    break;
                default:
                    throw new ArgumentException("Unknown lifecycle command");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Seed()
    {
        if (Directory.Exists(DataDirectory))
            throw new InvalidOperationException("Refusing to overwrite existing user data.");
        Directory.CreateDirectory(DataDirectory);
        new AppSettings { MaxItems = 37, CheckForUpdates = false }.Save(Path.Combine(DataDirectory, "settings.json"));
        using var store = OpenStore();
        var pinned = store.Add(ClipContent.PlainText("lifecycle pinned"), "probe", DateTimeOffset.Now);
        store.SetPinned(pinned.Id, true);
        store.Add(ClipContent.PlainText("lifecycle regular"), "probe", DateTimeOffset.Now);
        Console.WriteLine("PASS: seeded encrypted history, pinned item and settings");
    }

    private static void Verify()
    {
        using var store = OpenStore();
        var clips = store.GetAll();
        if (!clips.Any(c => c.Text == "lifecycle pinned" && c.Pinned)
            || !clips.Any(c => c.Text == "lifecycle regular") || store.UnreadableItemCount != 0)
            throw new InvalidOperationException("History or pins did not survive.");
        if (AppSettings.LoadOrCreate(Path.Combine(DataDirectory, "settings.json")).MaxItems != 37)
            throw new InvalidOperationException("Settings did not survive.");
        Console.WriteLine("PASS: history, pins and settings preserved and decryptable");
    }

    private static HistoryStore OpenStore() => new(Path.Combine(DataDirectory, "history.db"), 37, new DpapiProtector());

    private static async Task<AutomationElement> FindElement(AutomationElement root, AutomationProperty property, string text)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            var element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(property, text));
            if (element is not null) return element;
            await Task.Delay(200);
        }
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement window in windows)
            Console.Error.WriteLine($"Visible automation window: {window.Current.Name} (PID {window.Current.ProcessId})");
        throw new TimeoutException($"UI element not found: {text}");
    }

    private static async Task DeleteUi()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinClip.App");
        using var second = System.Diagnostics.Process.Start(Path.Combine(root, "current", "WinClip.exe"))!;
        var settings = await FindElement(AutomationElement.RootElement, AutomationElement.NameProperty, "WinClip settings");
        var delete = await FindElement(settings, AutomationElement.AutomationIdProperty, "DeleteData");
        var invoke = (InvokePattern)delete.GetCurrentPattern(InvokePattern.Pattern);
        var click = Task.Run(invoke.Invoke);
        var dialog = await FindElement(AutomationElement.RootElement, AutomationElement.NameProperty, "Delete all local data");
        var cancel = await FindElement(dialog, AutomationElement.AutomationIdProperty, "2");
        ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await click.WaitAsync(TimeSpan.FromSeconds(10));
        Verify();
        Console.WriteLine("PASS: cancelling data deletion preserves history and settings");
        click = Task.Run(invoke.Invoke);
        dialog = await FindElement(AutomationElement.RootElement, AutomationElement.NameProperty, "Delete all local data");
        var confirm = await FindElement(dialog, AutomationElement.AutomationIdProperty, "1");
        ((InvokePattern)confirm.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await click.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task Update(string root, string version, string feed)
    {
        var locator = new TestVelopackLocator("WinClip.App", version, Path.Combine(root, "packages"),
            Path.Combine(root, "current"), root, Path.Combine(root, "Update.exe"), channel: "win");
        var updater = new Updater(new UpdateManager(feed, locator: locator));
        if (await updater.CheckAndDownloadAsync() != UpdateCheckResult.Ready || !updater.ScheduleRestart())
            throw new InvalidOperationException("Update was not downloaded and scheduled.");
        Console.WriteLine($"PASS: production Updater downloaded {updater.ReadyVersion} and scheduled restart");
    }
}
