using WinClip.Core;

namespace WinClip.Tests;

public sealed class LocalDataTests
{
    [Fact]
    public void Delete_removes_pinned_history_settings_logs_and_sqlite_sidecars()
    {
        var directory = Directory.CreateTempSubdirectory("winclip-delete-").FullName;
        using (var store = new HistoryStore(Path.Combine(directory, "history.db"), 10, new TestProtector()))
        {
            var clip = store.Add(ClipContent.PlainText("private"), "test", DateTimeOffset.Now);
            store.SetPinned(clip.Id, true);
        }
        foreach (var name in new[] { "settings.json", "winclip.log", "settings.json.interrupted.tmp", "history.db-wal", "history.db-shm" })
            File.WriteAllText(Path.Combine(directory, name), "private");

        LocalData.Delete(directory);
        LocalData.Delete(directory);

        Assert.False(Directory.Exists(directory));
    }

    private sealed class TestProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    [Fact]
    public void Delete_preserves_unrelated_files_and_directories()
    {
        var directory = Directory.CreateTempSubdirectory("winclip-delete-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "backup.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(directory, "backups"));

            LocalData.Delete(directory);

            Assert.False(File.Exists(Path.Combine(directory, "settings.json")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(directory, "backup.txt")));
            Assert.True(Directory.Exists(Path.Combine(directory, "backups")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
