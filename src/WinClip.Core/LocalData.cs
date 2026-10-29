namespace WinClip.Core;

public static class LocalData
{
    /// <summary>Deletes owned files after capture, database connections and logging have stopped. Leaves unrelated files intact.</summary>
    public static void Delete(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The data directory is a link. Remove its contents manually.");

        foreach (var name in new[] { "history.db", "history.db-wal", "history.db-shm", "settings.json", "winclip.log" })
            File.Delete(Path.Combine(directory, name));
        foreach (var temporary in Directory.EnumerateFiles(directory, "settings.json.*.tmp"))
            File.Delete(temporary);
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
