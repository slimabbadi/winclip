using System.Text.Json;
using WinClip.Core;

namespace WinClip.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData("Ctrl+Alt+V", HotkeyModifiers.Control | HotkeyModifiers.Alt, "V")]
    [InlineData(" shift + win + Space ", HotkeyModifiers.Shift | HotkeyModifiers.Windows, "Space")]
    public void Hotkey_parses_modifiers_and_key(string value, HotkeyModifiers modifiers, string key)
    {
        Assert.True(HotkeyGesture.TryParse(value, out var gesture));
        Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
    }

    [Theory]
    [InlineData("V")]
    [InlineData("Ctrl+Ctrl+V")]
    [InlineData("Hyper+V")]
    [InlineData("")]
    public void Hotkey_rejects_invalid_gestures(string value)
    {
        Assert.False(HotkeyGesture.TryParse(value, out _));
    }

    [Theory]
    [InlineData("KeePassXC")]
    [InlineData("keepassxc.exe")]
    public void Ignored_apps_match_with_or_without_exe(string processName)
    {
        Assert.True(new AppSettings().IsIgnoredApp(processName));
    }

    [Fact]
    public void Missing_settings_file_is_created_with_defaults()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("winclip-tests-").FullName, "settings.json");

        var settings = AppSettings.LoadOrCreate(path);

        Assert.Equal(new AppSettings().Hotkey, settings.Hotkey);
        Assert.Equal(settings.MaxItems, AppSettings.LoadOrCreate(path).MaxItems);
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("winclip-tests-").FullName, "settings.json");
        var settings = new AppSettings { Hotkey = "Ctrl+Shift+Space", MaxItems = 42, IgnoredApps = ["Secret.exe"] };

        settings.Save(path);
        var loaded = AppSettings.LoadOrCreate(path);

        Assert.Equal(settings.Hotkey, loaded.Hotkey);
        Assert.Equal(42, loaded.MaxItems);
        Assert.Equal(["Secret.exe"], loaded.IgnoredApps);
    }

    [Theory]
    [InlineData("V", 500)]
    [InlineData("Ctrl+V", 0)]
    public void Validate_reports_the_invalid_value(string hotkey, int maxItems)
    {
        Assert.NotNull(new AppSettings { Hotkey = hotkey, MaxItems = maxItems }.Validate());
    }

    [Fact]
    public void Invalid_settings_are_rejected()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("winclip-tests-").FullName, "settings.json");
        File.WriteAllText(path, """{ "MaxItems": 0 }""");

        Assert.Throws<JsonException>(() => AppSettings.LoadOrCreate(path));
    }
}
