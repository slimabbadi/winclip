using System.Text.Json;

namespace WinClip.Core;

public sealed record AppSettings
{
    public string Hotkey { get; init; } = "Ctrl+Alt+V";

    /// <summary>History size cap. Pinned items do not count toward it.</summary>
    public int MaxItems { get; init; } = 500;

    /// <summary>Copies longer than this many characters are not recorded.</summary>
    public int MaxItemLength { get; init; } = 1_000_000;

    /// <summary>Whether to check GitHub Releases for a newer version about once a day.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Process names (with or without ".exe") whose copies are never recorded.</summary>
    public string[] IgnoredApps { get; init; } = ["KeePass", "KeePassXC", "1Password", "Bitwarden", "Dashlane"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Loads settings, writing defaults when the file is missing.</summary>
    /// <exception cref="JsonException">The file exists but is not valid settings JSON.</exception>
    public static AppSettings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            defaults.Save(path);
            return defaults;
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Settings file is empty.");
        if (settings.Validate() is { } error)
            throw new JsonException(error);
        return settings;
    }

    public void Save(string path)
    {
        // Write, flush to disk, then swap, so a crash mid-write cannot leave a truncated settings file.
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary); // No-op after a successful move.
        }
    }

    /// <summary>A user-facing description of the first invalid value, or null when all are valid.</summary>
    public string? Validate()
    {
        if (!HotkeyGesture.TryParse(Hotkey, out _))
            return $"\"{Hotkey}\" is not a valid shortcut. Use a modifier and a key, like Ctrl+Alt+V.";
        if (MaxItems < 1)
            return "History size must be at least 1.";
        if (MaxItemLength < 1)
            return "The copy size limit must be at least 1 character.";
        return null;
    }

    public bool IsIgnoredApp(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
            return false;
        var name = Path.GetFileNameWithoutExtension(processName);
        return IgnoredApps.Any(ignored =>
            string.Equals(Path.GetFileNameWithoutExtension(ignored), name, StringComparison.OrdinalIgnoreCase));
    }
}
